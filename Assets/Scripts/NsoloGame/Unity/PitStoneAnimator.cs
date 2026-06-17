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

        public IEnumerator PlayMoveAnimation(GameBoard startingBoard, MoveResult moveResult)
        {
            if (startingBoard == null || moveResult == null)
                yield break;

            visualizer.Refresh(startingBoard, startingBoard.CapturedP1, startingBoard.CapturedP2);

            foreach (SowingSegment segment in moveResult.SowingSegments)
                yield return AnimateSowingSegment(segment);

            visualizer.Refresh(moveResult.Board, moveResult.Board.CapturedP1, moveResult.Board.CapturedP2);
        }

        private IEnumerator AnimateSowingSegment(SowingSegment segment)
        {
            List<GameObject> pickedStones = PickUpStones(segment.Source.r, segment.Source.c);
            if (pickedStones.Count == 0)
                yield break;

            Vector3 sourcePosition = visualizer.GetPitPosition(segment.Source.r, segment.Source.c);
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
                GameObject stone = pickedStones[i];
                if (stone == null)
                    continue;

                var landing = segment.Landings[i];
                Vector3 start = stone.transform.position;
                List<GameObject> destinationPitStones = stonesByPit[landing.r, landing.c];

                Vector3 end = GetIncomingStonePosition(landing.r, landing.c, destinationPitStones, stone);

                yield return MoveStone(stone.transform, start, end, secondsPerStone, moveArcHeight);

                destinationPitStones.Add(stone);
                PlayStoneHit();
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

        private IEnumerator MoveStone(Transform stone, Vector3 start, Vector3 end, float duration, float arcHeight)
        {
            if (stone == null)
                yield break;

            float elapsed = 0f;
            duration = Mathf.Max(0.01f, duration);

            while (elapsed < duration)
            {
                float t = elapsed / duration;
                float eased = Mathf.SmoothStep(0f, 1f, t);
                Vector3 position = Vector3.Lerp(start, end, eased);
                position.y += Mathf.Sin(eased * Mathf.PI) * arcHeight;
                stone.position = position;
                stone.Rotate(Vector3.up, 220f * Time.deltaTime, Space.World);

                elapsed += Time.deltaTime;
                yield return null;
            }

            stone.position = end;
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

                for (int j = 0; j < existingStonesInDestination.Count; j++)
                {
                    GameObject existing = existingStonesInDestination[j];
                    if (existing == null)
                        continue;

                    System.Random existingRandom = new System.Random(layout.RandomSeed + row * 97 + col * 13 + j * 23);
                    Vector3 newSlot = layout.GetPileStonePosition(basePosition, pit.right, pit.forward, layout.CenteredPitRadius, layout.CenteredPitRadius, j, existingRandom, layout.PitBottomLayerStoneCount);

                    if ((existing.transform.position - newSlot).sqrMagnitude > 0.0001f)
                        mono.StartCoroutine(MoveStone(existing.transform, existing.transform.position, newSlot, secondsPerStone * 0.6f, 0f));
                }

                System.Random incomingRandom = new System.Random(layout.RandomSeed + row * 97 + col * 13 + existingStonesInDestination.Count * 23);
                return layout.GetPileStonePosition(basePosition, pit.right, pit.forward, layout.CenteredPitRadius, layout.CenteredPitRadius, existingStonesInDestination.Count, incomingRandom, layout.PitBottomLayerStoneCount);
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
