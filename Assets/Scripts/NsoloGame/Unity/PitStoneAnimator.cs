using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using NsoloGame.Core;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Handles sowing move animations for the Nsolo board.
    /// Plain C# class — coroutines are started on the PitStoneVisualizer MonoBehaviour.
    /// Initialized by PitStoneVisualizer.Awake().
    /// </summary>
    public class PitStoneAnimator
    {
        private readonly MonoBehaviour mono;
        private readonly PitStoneVisualizer visualizer;
        private readonly List<GameObject>[,] stonesByPit;
        private readonly List<GameObject> spawnedStones;
        private readonly StoneLayoutProvider layout;

        private float secondsPerStone;
        private float pickupLiftHeight;
        private float moveArcHeight;
        private float pickupStaggerSeconds;
        private AudioSource audioSource;
        private AudioClip stoneHitClip;
        private float stoneHitVolume;

        public PitStoneAnimator(
            MonoBehaviour mono,
            PitStoneVisualizer visualizer,
            List<GameObject>[,] stonesByPit,
            List<GameObject> spawnedStones,
            StoneLayoutProvider layout,
            float secondsPerStone,
            float pickupLiftHeight,
            float moveArcHeight,
            float pickupStaggerSeconds,
            AudioSource audioSource,
            AudioClip stoneHitClip,
            float stoneHitVolume)
        {
            this.mono = mono;
            this.visualizer = visualizer;
            this.stonesByPit = stonesByPit;
            this.spawnedStones = spawnedStones;
            this.layout = layout;
            this.secondsPerStone = secondsPerStone;
            this.pickupLiftHeight = pickupLiftHeight;
            this.moveArcHeight = moveArcHeight;
            this.pickupStaggerSeconds = pickupStaggerSeconds;
            this.audioSource = audioSource;
            this.stoneHitClip = stoneHitClip;
            this.stoneHitVolume = stoneHitVolume;
        }

        public void SetAudio(AudioSource source, AudioClip clip)
        {
            audioSource = source;
            stoneHitClip = clip;
        }

        private System.Action<SowingSegment, int> segmentListener;

        /// <summary>
        /// Called as each sowing segment begins, with the segment and its index. Index 0 is the
        /// opening sow; a later segment carrying ExtraSources is a capture, and any other later
        /// segment is a relay. GameController listens so the explanations land at the moment the
        /// mechanic happens rather than after the whole chain has played out.
        /// </summary>
        public void SetSegmentListener(System.Action<SowingSegment, int> listener)
        {
            segmentListener = listener;
        }

        /// <summary>True while a move is mid-flight, so a skip request has something to act on.</summary>
        public bool IsAnimating { get; private set; }

        private bool skipRequested;

        /// <summary>
        /// Fast-forwards the rest of the move to the settled board. Returns false when nothing is
        /// animating, which lets the caller treat the gesture as unhandled.
        /// </summary>
        public bool TryRequestSkip()
        {
            if (!IsAnimating) return false;
            skipRequested = true;
            return true;
        }

        /// <summary>
        /// Abandons a move mid-flight, for when undo cuts in during the AI's reply. The coroutine
        /// is stopped from outside and never reaches its own cleanup, so the flags are cleared here
        /// instead — otherwise IsAnimating would stay true forever and poison every later skip.
        /// The stranded stones are dealt with by the Refresh that follows the undo.
        /// </summary>
        public void CancelAnimation()
        {
            IsAnimating = false;
            skipRequested = false;
        }

        public IEnumerator PlayMoveAnimation(GameBoard startingBoard, MoveResult moveResult)
        {
            if (startingBoard == null || moveResult == null)
                yield break;

            skipRequested = false;
            IsAnimating = true;
            visualizer.Refresh(startingBoard);

            for (int i = 0; i < moveResult.SowingSegments.Count; i++)
            {
                if (skipRequested) break;

                segmentListener?.Invoke(moveResult.SowingSegments[i], i);

                // One frame so a tip raised by that callback can freeze the board (timeScale 0)
                // before any stone leaves its pit. Frame-based, so it still ticks while frozen.
                yield return null;

                yield return AnimateSowingSegment(moveResult.SowingSegments[i]);
            }

            IsAnimating = false;
            skipRequested = false;

            // Rebuilds every pit from the final board, which also cleans up whatever was left
            // mid-flight when the move was skipped.
            visualizer.Refresh(moveResult.Board);
        }

        private IEnumerator AnimateSowingSegment(SowingSegment segment)
        {
            // Most segments pick up from a single pit. Captures pick up from the landing pit
            // plus both of the opponent's same-column pits — gather all of them into one group
            // so every captured stone actually has a GameObject to fly into the resow.
            List<GameObject> pickedStones = PickUpStones(segment.Source.r, segment.Source.c);
            foreach (var extraSource in segment.ExtraSources)
                pickedStones.AddRange(PickUpStones(extraSource.r, extraSource.c));

            Vector3 sourcePosition = visualizer.GetPitPosition(segment.Source.r, segment.Source.c);

            // Pit piles are visually capped (maxVisualStonesPerPit), so a big pit can hold
            // fewer stone GameObjects than the engine actually sowed from it. Without topping
            // up, the trailing landings of this segment would get no stone: pits that really
            // hold a stone would look empty, and the next relay pickup would appear to jump
            // to an unrelated pit. Spawn extras at the source so every landing is animated.
            while (pickedStones.Count < segment.Landings.Count)
                pickedStones.Add(visualizer.SpawnStoneForAnimation(sourcePosition));

            if (pickedStones.Count == 0)
                yield break;
            Vector3 hoverCenter = sourcePosition + Vector3.up * pickupLiftHeight;

            for (int i = 0; i < pickedStones.Count; i++)
            {
                GameObject stone = pickedStones[i];
                if (stone == null)
                    continue;

                Vector3 target = hoverCenter + GetCarryOffset(i, pickedStones.Count);
                mono.StartCoroutine(MoveStone(stone.transform, stone.transform.position, target, 0.14f, 0f));
                yield return new WaitForSeconds(pickupStaggerSeconds);
            }

            yield return new WaitForSeconds(0.12f);

            int movingCount = Mathf.Min(pickedStones.Count, segment.Landings.Count);
            for (int i = 0; i < movingCount; i++)
            {
                if (skipRequested) break;

                GameObject stone = pickedStones[i];
                if (stone == null)
                    continue;

                var landing = segment.Landings[i];
                Vector3 start = stone.transform.position;
                List<GameObject> destinationPitStones = stonesByPit[landing.r, landing.c];

                Vector3 end = GetIncomingStonePosition(landing.r, landing.c, destinationPitStones, stone);

                // The hit fires slightly before the move finishes, not after it. MoveStone eases
                // with SmoothStep, so the stone covers the last tenth of the distance in the last
                // fifth of the time — it looks landed well before the coroutine returns, and a
                // sound played on return arrives audibly late. Firing at the point the eye reads as
                // contact puts them back together.
                yield return MoveStone(stone.transform, start, end, secondsPerStone, moveArcHeight,
                                       PlayStoneHit, HitCuePoint);

                destinationPitStones.Add(stone);
            }

            for (int i = movingCount; i < pickedStones.Count; i++)
            {
                if (pickedStones[i] != null)
                {
                    Object.Destroy(pickedStones[i]);
                    spawnedStones.Remove(pickedStones[i]);
                }
            }
        }

        private List<GameObject> PickUpStones(int row, int col)
        {
            List<GameObject> pitStones = stonesByPit[row, col];
            List<GameObject> pickedStones = new List<GameObject>(pitStones);
            pitStones.Clear();
            return pickedStones;
        }

        /// <summary>
        /// How far through a move the stone reads as having landed. SmoothStep has it 90% of the
        /// way there by this point and barely moving, so this is where the eye says "contact".
        /// </summary>
        private const float HitCuePoint = 0.82f;

        /// <summary>
        /// Moves one stone, optionally firing <paramref name="cue"/> partway through.
        ///
        /// The cue exists for audio. Playing a sound when this coroutine returns puts it after the
        /// visual landing by the tail of the easing curve plus a frame — small numbers that add up
        /// to an audible lag. The cue fires once, at <paramref name="cueAt"/>, and is skipped
        /// entirely if the move is cut short, so a skipped animation stays silent rather than
        /// firing a burst of hits at once.
        /// </summary>
        private IEnumerator MoveStone(Transform stone, Vector3 start, Vector3 end, float duration, float arcHeight,
                                      System.Action cue = null, float cueAt = 1f)
        {
            if (stone == null)
                yield break;

            float elapsed = 0f;
            duration = Mathf.Max(0.01f, duration);
            bool cueFired = false;

            while (elapsed < duration)
            {
                // Refresh() destroys every stone when a move is skipped or restarted, and some of
                // these coroutines are fire-and-forget, so the transform can vanish mid-flight.
                if (stone == null)
                    yield break;
                if (skipRequested)
                    break;

                float t = elapsed / duration;

                if (!cueFired && cue != null && t >= cueAt)
                {
                    cueFired = true;
                    cue();
                }

                float eased = Mathf.SmoothStep(0f, 1f, t);
                Vector3 position = Vector3.Lerp(start, end, eased);
                position.y += Mathf.Sin(eased * Mathf.PI) * arcHeight;
                stone.position = position;
                stone.Rotate(Vector3.up, 220f * Time.deltaTime, Space.World);

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (stone != null)
                stone.position = end;

            // A move that ran to completion without reaching the cue point — a single-frame move on
            // a slow frame, say — still owes its sound.
            if (!cueFired && cue != null && !skipRequested)
                cue();
        }

        /// <summary>
        /// Computes the landing position for a stone sowed into a pit, avoiding collisions up front.
        /// When using centered pile formation, existing stones are re-slotted to the new total formation
        /// at the same time the incoming stone animates in — keeping the pile non-overlapping.
        /// </summary>
        private Vector3 GetIncomingStonePosition(int row, int col, List<GameObject> existingStonesInDestination, GameObject incomingStone)
        {
            Transform pit = visualizer.GetPitTransform(row, col);
            if (pit == null)
                return mono.transform.position;

            if (layout.UseCenteredStonePiles)
            {
                Vector3 basePosition = layout.GetPitBasePosition(pit);
                int totalAfterLanding = existingStonesInDestination.Count + 1;

                for (int j = 0; j < existingStonesInDestination.Count; j++)
                {
                    GameObject existing = existingStonesInDestination[j];
                    if (existing == null)
                        continue;

                    System.Random existingRandom = new System.Random(layout.RandomSeed + row * 97 + col * 13 + j * 23);
                    Vector3 newSlot = layout.GetPileStonePosition(basePosition, pit.right, pit.forward, layout.CenteredPitRadius, layout.CenteredPitRadius, j, totalAfterLanding, existingRandom, layout.PitBottomLayerStoneCount);

                    if ((existing.transform.position - newSlot).sqrMagnitude > 0.0001f)
                        mono.StartCoroutine(MoveStone(existing.transform, existing.transform.position, newSlot, secondsPerStone * 0.6f, 0f));
                }

                System.Random incomingRandom = new System.Random(layout.RandomSeed + row * 97 + col * 13 + existingStonesInDestination.Count * 23);
                return layout.GetPileStonePosition(basePosition, pit.right, pit.forward, layout.CenteredPitRadius, layout.CenteredPitRadius, existingStonesInDestination.Count, totalAfterLanding, incomingRandom, layout.PitBottomLayerStoneCount);
            }

            return layout.GetPitStonePositionWithSeparationAndBounds(row, col, existingStonesInDestination, incomingStone, pit);
        }

        private Vector3 GetCarryOffset(int index, int total)
        {
            float angle = total <= 1 ? 0f : (Mathf.PI * 2f * index) / total;
            float radius = total <= 1 ? 0f : Mathf.Min(0.18f, 0.035f * total);
            return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }

        private void PlayStoneHit()
        {
            if (audioSource != null && stoneHitClip != null)
                audioSource.PlayOneShot(stoneHitClip, stoneHitVolume);
        }

        public AudioClip CreateStoneHitClip()
        {
            const int sampleRate = 22050;
            const float duration = 0.07f;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)sampleRate;
                float envelope = Mathf.Exp(-55f * t);
                float click = Mathf.Sin(2f * Mathf.PI * 920f * t) * envelope;
                float knock = Mathf.Sin(2f * Mathf.PI * 210f * t) * envelope * 0.55f;
                samples[i] = (click + knock) * 0.45f;
            }

            AudioClip clip = AudioClip.Create("Generated Stone Hit", sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
