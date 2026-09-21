using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using NsoloGame.Core;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Runtime-only stone renderer for the Nsolo board.
    /// Destroys and rebuilds spawned stones from the authoritative GameBoard state.
    /// Animation is delegated to PitStoneAnimator; position math to StoneLayoutProvider.
    /// </summary>
    public class PitStoneVisualizer : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private GameObject[] holes = new GameObject[32];

        [Header("Stone Prefab")]
        [SerializeField] private GameObject stonePrefab;
        [SerializeField] private Transform spawnedStoneRoot;

        [Header("Pit Placement")]
        [SerializeField] private float pitRadius = 0.33f;
        [SerializeField] private bool usePitBoundsBottom = true;
        [SerializeField] private float pitBottomYOffset = 0.035f;
        [SerializeField] private bool useCenteredStonePiles = true;
        [SerializeField] private float centeredPitRadius = 0.075f;
        [SerializeField] private float stoneYOffset = 0.04f;
        [SerializeField] private float stoneScale = 1f;
        [SerializeField] private int maxVisualStonesPerPit = 12;

        [Header("Pile Stacking")]
        [Tooltip("How many stones settle into a pit's bottom layer (as spot/line/triangle/square) before later stones start stacking on top.")]
        [SerializeField] private int pitBottomLayerStoneCount = 4;
        [Tooltip("Fraction of the pit footprint radius used for stone spread within each layer.")]
        [SerializeField] private float layerSpreadFraction = 0.15f;

        [Header("Scatter Randomness")]
        [SerializeField] private int randomSeed = 2371;
        [SerializeField] private float randomRotationDegrees = 35f;

        [Header("Animation")]
        [SerializeField] private float secondsPerStone = 0.5f;
        [SerializeField] private float pickupLiftHeight = 0.28f;
        [SerializeField] private float moveArcHeight = 0.18f;
        [SerializeField] private float pickupStaggerSeconds = 0.025f;
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip stoneHitClip;
        [SerializeField] private float stoneHitVolume = 0.45f;

        [Header("Debug")]
        [SerializeField] private bool enableBreadcrumbLogs = false;

        [Tooltip("Radius (world units) used to keep stones from overlapping. 0 = auto-detect from stone prefab mesh bounds.")]
        [SerializeField] private float stoneFootprintRadius = 0f;

        private float effectiveStoneRadius = 0.02f;

        private readonly List<GameObject> spawnedStones = new List<GameObject>();
        private readonly List<GameObject>[,] stonesByPit = new List<GameObject>[4, 8];

        // ── Legal-move glow ──────────────────────────────────────────────────
        //
        // Which pits are currently playable, and the stone renderers that say so. Kept here rather
        // than in UIManager because the stones are spawned and destroyed on every Refresh: the
        // renderers UIManager would be holding are dead objects one board update later. Storing the
        // pits instead means the glow can be put back on the new stones at the end of Refresh,
        // which is what makes it survive a board update at all.
        private readonly HashSet<int> glowPits = new HashSet<int>();
        private readonly Dictionary<int, List<Renderer>> glowRenderers = new Dictionary<int, List<Renderer>>();

        /// <summary>The pit a hint flash currently owns, or -1. Skipped by the pulse so the two
        /// effects don't write the same emission colour on alternate frames.</summary>
        private int hintFlashPit = -1;

        private static readonly Color GlowColor = new Color(1f, 0.78f, 0.18f);
        private const float GlowPulseSpeed = 3.2f;
        private const float GlowPulseMin = 0.55f;
        private const float GlowPulseMax = 1.5f;

        private StoneLayoutProvider layout;
        private PitStoneAnimator animator;

        private void Awake()
        {
            Log("Awake()");

            if (spawnedStoneRoot == null)
            {
                GameObject root = new GameObject("Spawned Stones");
                root.transform.SetParent(transform, false);
                spawnedStoneRoot = root.transform;
                Log("Created Spawned Stones container.");
            }
            else
            {
                Log($"Using assigned Spawned Stones container: {spawnedStoneRoot.name}.");
            }

            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 8; c++)
                    stonesByPit[r, c] = new List<GameObject>();

            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
                if (audioSource == null)
                    audioSource = gameObject.AddComponent<AudioSource>();
            }

            layout = new StoneLayoutProvider
            {
                PitRadius = pitRadius,
                CenteredPitRadius = centeredPitRadius,
                StoneYOffset = stoneYOffset,
                UsePitBoundsBottom = usePitBoundsBottom,
                PitBottomYOffset = pitBottomYOffset,
                UseCenteredStonePiles = useCenteredStonePiles,
                RandomSeed = randomSeed,
                RandomRotationDegrees = randomRotationDegrees,
                StoneScale = stoneScale,
                PitBottomLayerStoneCount = pitBottomLayerStoneCount,
                LayerSpreadFraction = layerSpreadFraction,
            };

            effectiveStoneRadius = stoneFootprintRadius > 0f
                ? stoneFootprintRadius
                : layout.ComputeStoneFootprintRadius(stonePrefab, stoneScale);

            layout.EffectiveStoneRadius = effectiveStoneRadius;

            Log($"Effective stone footprint radius = {effectiveStoneRadius:F4}.");

            animator = new PitStoneAnimator(
                this,
                this,
                stonesByPit,
                spawnedStones,
                layout,
                secondsPerStone,
                pickupLiftHeight,
                moveArcHeight,
                pickupStaggerSeconds,
                audioSource,
                stoneHitClip,
                stoneHitVolume);

            if (stoneHitClip == null)
            {
                // Prefer whatever Stone Drop is set to on AudioManager, so the clip is chosen in one
                // place. The synthesised click is the last resort, not the default — it used to be
                // reached whenever this slot was empty, which is why assigning Stone Drop appeared
                // to change nothing.
                AudioClip fromManager = AudioManager.StoneDropClip;
                animator.SetAudio(audioSource, fromManager != null ? fromManager : animator.CreateStoneHitClip());
            }
        }

        private void OnEnable()
        {
            // MenuManager raises this when the SFX slider moves. Nothing had ever subscribed, so
            // the slider wrote a pref and changed nothing audible — the stone sounds were really
            // being scaled by the *music* slider via AudioListener.volume.
            MenuManager.SFXVolumeChanged += ApplySfxVolume;
            ApplySfxVolume(PlayerPrefs.GetFloat(AudioManager.SfxVolumeKey, 0.85f));
        }

        private void OnDisable()
        {
            MenuManager.SFXVolumeChanged -= ApplySfxVolume;
        }

        private void ApplySfxVolume(float value)
        {
            if (audioSource != null)
                audioSource.volume = Mathf.Clamp01(value);
        }

        private void OnDestroy()
        {
            Log("OnDestroy() clearing spawned stones immediately.");
            ClearStonesImmediate();
        }

        public void SetHoles(GameObject[] sceneHoles)
        {
            Log($"SetHoles() received {(sceneHoles == null ? 0 : sceneHoles.Length)} entries.");

            if (sceneHoles == null)
                return;

            for (int i = 0; i < holes.Length && i < sceneHoles.Length; i++)
                holes[i] = sceneHoles[i];

            Log($"SetHoles() complete. Assigned holes: {CountAssignedHoles()}/32.");
        }

        public void Refresh(GameBoard board)
        {
            Log("Refresh() called.");

            if (board == null)
            {
                Debug.LogWarning("PitStoneVisualizer: Refresh stopped because GameBoard is null.");
                return;
            }

            if (stonePrefab == null)
            {
                Debug.LogWarning("PitStoneVisualizer: Refresh stopped because Stone Prefab is not assigned.");
                return;
            }

            Log($"Board total stones before spawn: {CountBoardStones(board)}.");
            Log($"Assigned holes before spawn: {CountAssignedHoles()}/32.");

            ClearStones();

            int spawnedCount = 0;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    Transform pit = GetPitTransform(r, c);
                    if (pit == null)
                    {
                        Debug.LogWarning($"PitStoneVisualizer: Skipping Hole_{r}_{c} because no Transform was found.");
                        continue;
                    }

                    int spawned = SpawnPitStoneGroup(pit, board.Get(r, c), maxVisualStonesPerPit, r, c, stonesByPit[r, c]);
                    spawnedCount += spawned;
                }
            }

            Log($"Pit loop complete. Spawned {spawnedCount} pit stones.");
            Log($"Refresh() complete. Total runtime stones now tracked: {spawnedStones.Count}.");

            // The stones the glow was attached to were destroyed at the top of this method, so it
            // is re-attached to the ones that replaced them. Without this a highlight set before a
            // board update would silently go out.
            RebuildGlowRenderers();
        }

        public IEnumerator PlayMoveAnimation(GameBoard startingBoard, MoveResult moveResult)
        {
            if (startingBoard == null || moveResult == null)
                yield break;

            yield return animator.PlayMoveAnimation(startingBoard, moveResult);
        }

        /// <summary>Hands GameController a per-segment hook into the sowing animation.</summary>
        public void SetSegmentListener(System.Action<SowingSegment, int> listener)
        {
            animator?.SetSegmentListener(listener);
        }

        /// <summary>Fast-forwards a move in progress. False when nothing is animating.</summary>
        public bool TrySkipAnimation()
        {
            return animator != null && animator.TryRequestSkip();
        }

        public bool IsAnimating => animator != null && animator.IsAnimating;

        /// <summary>Abandons a move in progress. Used when undo interrupts the AI.</summary>
        public void CancelAnimation()
        {
            animator?.CancelAnimation();
        }

        internal Transform GetPitTransform(int row, int col)
        {
            int index = row * 8 + col;
            if (holes[index] == null)
                holes[index] = GameObject.Find($"Hole_{row}_{col}");

            return holes[index] == null ? null : holes[index].transform;
        }

        internal Vector3 GetPitPosition(int row, int col)
        {
            Transform pit = GetPitTransform(row, col);
            if (pit == null)
                return transform.position;

            return layout.GetPitBasePosition(pit);
        }

        private int SpawnPitStoneGroup(Transform center, int count, int maxVisualCount, int row, int col, List<GameObject> targetList)
        {
            int visualCount = Mathf.Min(count, maxVisualCount);
            if (count <= 0)
            {
                Log($"SpawnPitStoneGroup({center.name}) count is 0. No stones spawned.");
                return 0;
            }

            System.Random random = new System.Random(randomSeed + row * 97 + col * 13 + count * 7);

            for (int i = 0; i < visualCount; i++)
            {
                Vector3 position = layout.GetPitStonePosition(center, i, visualCount, pitRadius, centeredPitRadius, random, targetList);
                Quaternion rotation = layout.RandomRotation(random);

                GameObject stone = Instantiate(stonePrefab, position, rotation, spawnedStoneRoot);
                stone.transform.localScale = stone.transform.localScale * stoneScale;
                DisableColliders(stone);
                spawnedStones.Add(stone);
                targetList.Add(stone);
            }

            Log($"SpawnPitStoneGroup({center.name}) requested={count}, spawned={visualCount}.");
            return visualCount;
        }

        /// <summary>
        /// Spawns a single loose stone for the animator. Pit piles are visually capped at
        /// maxVisualStonesPerPit, so a large pit can hold fewer stone GameObjects than the
        /// engine actually sowed — the animator tops up with these so every landing in a
        /// sowing segment gets a visible stone. The stone is tracked in spawnedStones (so
        /// Refresh/Clear still owns its lifetime) but belongs to no pit until it lands.
        /// </summary>
        internal GameObject SpawnStoneForAnimation(Vector3 position)
        {
            GameObject stone = Instantiate(stonePrefab, position, Quaternion.identity, spawnedStoneRoot);
            stone.transform.localScale = stone.transform.localScale * stoneScale;
            DisableColliders(stone);
            spawnedStones.Add(stone);
            return stone;
        }

        /// <summary>
        /// Spawned stones must never intercept the board's pit-click raycasts — a stone sitting
        /// on top of a pit can otherwise occlude the raycast to pits further from the camera
        /// (e.g. the inner row sitting behind the outer row's stones).
        /// </summary>
        private void DisableColliders(GameObject stone)
        {
            foreach (Collider collider in stone.GetComponentsInChildren<Collider>())
                collider.enabled = false;
        }

        private void ClearStones()
        {
            Log($"ClearStones() destroying {spawnedStones.Count} old stones.");

            for (int i = spawnedStones.Count - 1; i >= 0; i--)
            {
                if (spawnedStones[i] != null)
                    Destroy(spawnedStones[i]);
            }

            spawnedStones.Clear();
            ClearStoneLists();
        }

        private void ClearStonesImmediate()
        {
            for (int i = spawnedStones.Count - 1; i >= 0; i--)
            {
                if (spawnedStones[i] != null)
                    DestroyImmediate(spawnedStones[i]);
            }

            spawnedStones.Clear();
            ClearStoneLists();
        }

        private void ClearStoneLists()
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 8; c++)
                    stonesByPit[r, c]?.Clear();
        }

        private int CountAssignedHoles()
        {
            int count = 0;
            for (int i = 0; i < holes.Length; i++)
                if (holes[i] != null)
                    count++;

            return count;
        }

        private int CountBoardStones(GameBoard board)
        {
            int total = 0;
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 8; c++)
                    total += board.Get(r, c);

            return total;
        }

        /// <summary>
        /// Makes the given pits glow for as long as they stay playable.
        ///
        /// The board already had a highlight call, and it did nothing visible: it tinted the
        /// Hole_r_c renderers, which on this board are the invisible click targets sitting under
        /// the painted board art, so "select one of your highlighted pits" pointed at pits that
        /// looked exactly like every other pit. The hint glow was the one highlight players could
        /// actually see, because it lights the stones rather than the hole. This lights the same
        /// thing, held rather than flashed, and slowly pulsed so that a board full of playable pits
        /// reads as an invitation rather than as sixteen hints at once.
        /// </summary>
        public void SetLegalMoveGlow(IEnumerable<(int row, int col)> pits)
        {
            ClearLegalMoveGlow();
            if (pits == null) return;

            foreach (var pit in pits)
            {
                if (pit.row < 0 || pit.row >= 4 || pit.col < 0 || pit.col >= 8) continue;
                glowPits.Add(pit.row * 8 + pit.col);
            }

            RebuildGlowRenderers();
        }

        /// <summary>Puts every glowing pit back to its normal, unlit stones.</summary>
        public void ClearLegalMoveGlow()
        {
            foreach (var entry in glowRenderers)
                ExtinguishGlow(entry.Value);

            glowRenderers.Clear();
            glowPits.Clear();
        }

        /// <summary>
        /// Collects the stone renderers for the currently glowing pits and switches their emission
        /// on. Called both when the highlight changes and after every Refresh, since a Refresh
        /// replaces the stone objects the previous pass collected.
        /// </summary>
        private void RebuildGlowRenderers()
        {
            glowRenderers.Clear();
            if (glowPits.Count == 0) return;

            foreach (int key in glowPits)
            {
                List<GameObject> pitStones = stonesByPit[key / 8, key % 8];
                if (pitStones == null || pitStones.Count == 0) continue;

                var renderers = new List<Renderer>(pitStones.Count);
                foreach (GameObject stone in pitStones)
                {
                    if (stone == null) continue;
                    Renderer r = stone.GetComponentInChildren<Renderer>();
                    if (r == null) continue;

                    r.material.EnableKeyword("_EMISSION");
                    renderers.Add(r);
                }

                if (renderers.Count > 0) glowRenderers[key] = renderers;
            }
        }

        private static void ExtinguishGlow(List<Renderer> renderers)
        {
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                r.material.SetColor("_EmissionColor", Color.black);
                r.material.DisableKeyword("_EMISSION");
            }
        }

        private void Update()
        {
            if (glowRenderers.Count == 0) return;

            // Unscaled, so the glow keeps breathing while a pause menu has frozen the game clock.
            float wave = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * GlowPulseSpeed);
            Color emission = GlowColor * Mathf.Lerp(GlowPulseMin, GlowPulseMax, wave);

            foreach (var entry in glowRenderers)
            {
                if (entry.Key == hintFlashPit) continue;

                List<Renderer> renderers = entry.Value;
                for (int i = 0; i < renderers.Count; i++)
                {
                    if (renderers[i] != null) renderers[i].material.SetColor("_EmissionColor", emission);
                }
            }
        }

        /// <summary>
        /// Pulses a warm gold glow on the stones in the given pit once, then fades out.
        /// Called by UIManager when a hint result arrives.
        /// </summary>
        public IEnumerator FlashHintGlow(int row, int col)
        {
            List<GameObject> pitStones = stonesByPit[row, col];
            if (pitStones == null || pitStones.Count == 0) yield break;

            var renderers = new List<Renderer>();
            foreach (var stone in pitStones)
            {
                if (stone == null) continue;
                Renderer r = stone.GetComponentInChildren<Renderer>();
                if (r != null) renderers.Add(r);
            }
            if (renderers.Count == 0) yield break;

            Color glowColor = GlowColor;

            // Claimed for the duration so the legal-move pulse leaves this pit alone — otherwise
            // the hinted pit, which is a legal move by definition, would have both effects writing
            // its emission colour and the flash would be lost under the pulse.
            hintFlashPit = row * 8 + col;

            // Enable emission
            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.material.EnableKeyword("_EMISSION");
                r.material.SetColor("_EmissionColor", glowColor * 2.5f);
            }

            yield return new WaitForSeconds(0.45f);

            // Fade out over 0.4s
            float fadeTime = 0.4f;
            float elapsed = 0f;
            while (elapsed < fadeTime)
            {
                elapsed += Time.deltaTime;
                float t = 1f - Mathf.Clamp01(elapsed / fadeTime);
                Color c = glowColor * (2.5f * t);
                foreach (var r in renderers)
                {
                    if (r != null) r.material.SetColor("_EmissionColor", c);
                }
                yield return null;
            }

            // Clear
            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.material.SetColor("_EmissionColor", Color.black);
                r.material.DisableKeyword("_EMISSION");
            }

            // Handed back. If the pit is still a legal move the pulse picks it up again on the next
            // frame, which needs its emission keyword back on.
            hintFlashPit = -1;
            if (glowRenderers.TryGetValue(row * 8 + col, out var stillGlowing))
            {
                foreach (Renderer r in stillGlowing)
                    if (r != null) r.material.EnableKeyword("_EMISSION");
            }
        }

        private void Log(string message)
        {
            if (enableBreadcrumbLogs)
                Debug.Log($"[PitStoneVisualizer] {message}", this);
        }
    }
}
