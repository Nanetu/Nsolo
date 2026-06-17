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
        [SerializeField] private GameObject[] holes = new GameObject[48];
        [SerializeField] private Transform storeP1;
        [SerializeField] private Transform storeP2;

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

        [Header("Store Placement")]
        [SerializeField] private float storeBoundsPadding = 0.18f;
        [SerializeField] private float storeSurfaceYOffset = 0.045f;
        [SerializeField] private int maxVisualStonesPerStore = 48;

        [Header("Pile Stacking")]
        [Tooltip("How many stones settle into a pit's bottom layer before later stones start piling on top.")]
        [SerializeField] private int pitBottomLayerStoneCount = 4;
        [Tooltip("How many stones settle into a store's bottom layer before later stones start piling on top.")]
        [SerializeField] private int storeBottomLayerStoneCount = 12;
        [Tooltip("Fraction of the pit/store footprint radius used for stone spread within each layer.")]
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
        private readonly List<GameObject>[,] stonesByPit = new List<GameObject>[4, 12];
        private readonly List<GameObject> storeP1Stones = new List<GameObject>();
        private readonly List<GameObject> storeP2Stones = new List<GameObject>();

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
                for (int c = 0; c < 12; c++)
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
                StoreBoundsPadding = storeBoundsPadding,
                StoreSurfaceYOffset = storeSurfaceYOffset,
                RandomSeed = randomSeed,
                RandomRotationDegrees = randomRotationDegrees,
                StoneScale = stoneScale,
                PitBottomLayerStoneCount = pitBottomLayerStoneCount,
                StoreBottomLayerStoneCount = storeBottomLayerStoneCount,
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
                AudioClip generated = animator.CreateStoneHitClip();
                animator.SetAudio(audioSource, generated);
            }
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

            Log($"SetHoles() complete. Assigned holes: {CountAssignedHoles()}/48.");
        }

        public void Refresh(GameBoard board, int capturedP1, int capturedP2)
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

            Log($"Board total stones before spawn: {CountBoardStones(board)}. Captured P1={capturedP1}, Captured P2={capturedP2}.");
            Log($"Assigned holes before spawn: {CountAssignedHoles()}/48.");

            ClearStones();

            int spawnedBeforeStores = 0;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 12; c++)
                {
                    Transform pit = GetPitTransform(r, c);
                    if (pit == null)
                    {
                        Debug.LogWarning($"PitStoneVisualizer: Skipping Hole_{r}_{c} because no Transform was found.");
                        continue;
                    }

                    int spawned = SpawnPitStoneGroup(pit, board.Get(r, c), maxVisualStonesPerPit, r, c, stonesByPit[r, c]);
                    spawnedBeforeStores += spawned;
                }
            }

            Log($"Pit loop complete. Spawned {spawnedBeforeStores} pit stones.");

            if (storeP1 != null)
                SpawnStoreStoneGroup(storeP1, capturedP1, maxVisualStonesPerStore, storeP1Stones);

            if (storeP2 != null)
                SpawnStoreStoneGroup(storeP2, capturedP2, maxVisualStonesPerStore, storeP2Stones);

            Log($"Refresh() complete. Total runtime stones now tracked: {spawnedStones.Count}.");
        }

        public IEnumerator PlayMoveAnimation(GameBoard startingBoard, MoveResult moveResult)
        {
            if (startingBoard == null || moveResult == null)
                yield break;

            yield return animator.PlayMoveAnimation(startingBoard, moveResult);
        }

        internal Transform GetPitTransform(int row, int col)
        {
            int index = row * 12 + col;
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
                spawnedStones.Add(stone);
                targetList.Add(stone);
            }

            Log($"SpawnPitStoneGroup({center.name}) requested={count}, spawned={visualCount}.");
            return visualCount;
        }

        private int SpawnStoreStoneGroup(Transform center, int count, int maxVisualCount, List<GameObject> targetList)
        {
            int visualCount = Mathf.Min(count, maxVisualCount);
            if (count <= 0)
            {
                Log($"SpawnStoreStoneGroup({center.name}) count is 0. No stones spawned.");
                return 0;
            }

            System.Random random = new System.Random(randomSeed + count * 7);

            for (int i = 0; i < visualCount; i++)
            {
                Vector3 position = layout.GetStoreStonePosition(center, i, visualCount);
                Quaternion rotation = layout.RandomRotation(random);

                GameObject stone = Instantiate(stonePrefab, position, rotation, spawnedStoneRoot);
                stone.transform.localScale = stone.transform.localScale * stoneScale;
                spawnedStones.Add(stone);
                targetList.Add(stone);
            }

            Log($"SpawnStoreStoneGroup({center.name}) requested={count}, spawned={visualCount}.");
            return visualCount;
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
                for (int c = 0; c < 12; c++)
                    stonesByPit[r, c]?.Clear();

            storeP1Stones.Clear();
            storeP2Stones.Clear();
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
                for (int c = 0; c < 12; c++)
                    total += board.Get(r, c);

            return total;
        }

        private void Log(string message)
        {
            if (enableBreadcrumbLogs)
                Debug.Log($"[PitStoneVisualizer] {message}", this);
        }
    }
}
