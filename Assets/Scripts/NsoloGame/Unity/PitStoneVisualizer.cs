using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using NsoloGame.Core;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Runtime-only stone renderer for the Nsolo board.
    /// Destroys and rebuilds spawned stones from the authoritative GameBoard state.
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
        [SerializeField] private bool usePitFormationLayout = true;
        [SerializeField] private float pitFormationSpacing = 0.09f;
        [SerializeField] private int stonesPerPileLayer = 7;
        [SerializeField] private float pileLayerHeight = 0.028f;
        [SerializeField] private float stoneYOffset = 0.04f;
        [SerializeField] private float stoneScale = 1f;
        [SerializeField] private int maxVisualStonesPerPit = 24;

        [Header("Store Placement")]
        [SerializeField] private float storeBoundsPadding = 0.18f;
        [SerializeField] private float storeSurfaceYOffset = 0.045f;
        [SerializeField] private float storePileHeightStep = 0.018f;
        [SerializeField] private int maxVisualStonesPerStore = 48;

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

        [Tooltip("Radius (world units) used to keep stones from overlapping each other. 0 = auto-detect from the stone prefab's mesh bounds.")]
        [SerializeField] private float stoneFootprintRadius = 0f;

        private float effectiveStoneRadius = 0.02f;

        private readonly List<GameObject> spawnedStones = new List<GameObject>();
        private readonly List<GameObject>[,] stonesByPit = new List<GameObject>[4, 12];
        private readonly List<GameObject> storeP1Stones = new List<GameObject>();
        private readonly List<GameObject> storeP2Stones = new List<GameObject>();

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
            {
                for (int c = 0; c < 12; c++)
                {
                    stonesByPit[r, c] = new List<GameObject>();
                }
            }

            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
                if (audioSource == null)
                {
                    audioSource = gameObject.AddComponent<AudioSource>();
                }
            }

            if (stoneHitClip == null)
            {
                stoneHitClip = CreateStoneHitClip();
            }

            effectiveStoneRadius = stoneFootprintRadius > 0f
                ? stoneFootprintRadius
                : ComputeStoneFootprintRadius();

            Log($"Effective stone footprint radius = {effectiveStoneRadius:F4}.");
        }

        private float ComputeStoneFootprintRadius()
        {
            const float fallback = 0.02f;

            if (stonePrefab == null)
                return fallback * Mathf.Abs(stoneScale);

            MeshFilter meshFilter = stonePrefab.GetComponentInChildren<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
                return fallback * Mathf.Abs(stoneScale);

            Bounds localBounds = meshFilter.sharedMesh.bounds;
            Vector3 lossyScale = meshFilter.transform.lossyScale;
            float radiusX = localBounds.extents.x * Mathf.Abs(lossyScale.x);
            float radiusZ = localBounds.extents.z * Mathf.Abs(lossyScale.z);
            float footprint = Mathf.Max(radiusX, radiusZ) * Mathf.Abs(stoneScale);

            return Mathf.Max(footprint, 0.005f);
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
            {
                holes[i] = sceneHoles[i];
            }

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
            {
                SpawnStoreStoneGroup(storeP1, capturedP1, maxVisualStonesPerStore, storeP1Stones);
            }

            if (storeP2 != null)
            {
                SpawnStoreStoneGroup(storeP2, capturedP2, maxVisualStonesPerStore, storeP2Stones);
            }

            Log($"Refresh() complete. Total runtime stones now tracked: {spawnedStones.Count}.");
        }

        private Transform GetPitTransform(int row, int col)
        {
            int index = row * 12 + col;
            if (holes[index] == null)
            {
                holes[index] = GameObject.Find($"Hole_{row}_{col}");
            }

            return holes[index] == null ? null : holes[index].transform;
        }

        public IEnumerator PlayMoveAnimation(GameBoard startingBoard, MoveResult moveResult)
        {
            if (startingBoard == null || moveResult == null)
                yield break;

            Refresh(startingBoard, startingBoard.CapturedP1, startingBoard.CapturedP2);

            foreach (SowingSegment segment in moveResult.SowingSegments)
            {
                yield return AnimateSowingSegment(segment);
            }

            Refresh(moveResult.Board, moveResult.Board.CapturedP1, moveResult.Board.CapturedP2);
        }

        private IEnumerator AnimateSowingSegment(SowingSegment segment)
        {
            List<GameObject> pickedStones = PickUpStones(segment.Source.r, segment.Source.c);
            if (pickedStones.Count == 0)
                yield break;

            Vector3 sourcePosition = GetPitPosition(segment.Source.r, segment.Source.c);
            Vector3 hoverCenter = sourcePosition + Vector3.up * pickupLiftHeight;

            for (int i = 0; i < pickedStones.Count; i++)
            {
                GameObject stone = pickedStones[i];
                if (stone == null)
                    continue;

                Vector3 target = hoverCenter + GetCarryOffset(i, pickedStones.Count);
                StartCoroutine(MoveStone(stone.transform, stone.transform.position, target, 0.14f, 0f));
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
                    Destroy(pickedStones[i]);
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
                Vector3 position = GetPitStonePosition(center, i, visualCount, pitRadius, centeredPitRadius, random, targetList);
                Quaternion rotation = Quaternion.Euler(
                    RandomRange(random, -randomRotationDegrees, randomRotationDegrees),
                    RandomRange(random, 0f, 360f),
                    RandomRange(random, -randomRotationDegrees, randomRotationDegrees));

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
                Vector3 position = GetStoreStonePosition(center, i);
                Quaternion rotation = Quaternion.Euler(
                    RandomRange(random, -randomRotationDegrees, randomRotationDegrees),
                    RandomRange(random, 0f, 360f),
                    RandomRange(random, -randomRotationDegrees, randomRotationDegrees));

                GameObject stone = Instantiate(stonePrefab, position, rotation, spawnedStoneRoot);
                stone.transform.localScale = stone.transform.localScale * stoneScale;
                spawnedStones.Add(stone);
                targetList.Add(stone);
            }

            Log($"SpawnStoreStoneGroup({center.name}) requested={count}, spawned={visualCount}.");
            return visualCount;
        }

        private Vector3 GetPitPosition(int row, int col)
        {
            Transform pit = GetPitTransform(row, col);
            if (pit == null)
                return transform.position;

            return GetPitBasePosition(pit);
        }

        /// <summary>
        /// Computes the landing position for a stone sowed into a pit, avoiding collisions up front
        /// rather than relying on a later Refresh() to spread stones apart.
        ///
        /// When the centered pile formation layout is in use, the formation slots for the
        /// existing stones depend on the pile's total stone count. Adding a stone changes that
        /// total, so the existing stones are smoothly re-slotted to the formation for the new
        /// total at the same time the incoming stone animates into the one remaining slot -
        /// keeping the pile non-overlapping throughout the animation, matching what Refresh()
        /// would produce.
        /// </summary>
        private Vector3 GetIncomingStonePosition(int row, int col, List<GameObject> existingStonesInDestination, GameObject incomingStone)
        {
            Transform pit = GetPitTransform(row, col);
            if (pit == null)
                return transform.position;

            if (useCenteredStonePiles)
            {
                int newTotal = existingStonesInDestination.Count + 1;

                for (int j = 0; j < existingStonesInDestination.Count; j++)
                {
                    GameObject existing = existingStonesInDestination[j];
                    if (existing == null)
                        continue;

                    System.Random existingRandom = new System.Random(randomSeed + row * 97 + col * 13 + j * 23);
                    Vector3 newSlot = GetPitStonePosition(pit, j, newTotal, pitRadius, centeredPitRadius, existingRandom);

                    if ((existing.transform.position - newSlot).sqrMagnitude > 0.0001f)
                    {
                        StartCoroutine(MoveStone(existing.transform, existing.transform.position, newSlot, secondsPerStone * 0.6f, 0f));
                    }
                }

                System.Random incomingRandom = new System.Random(randomSeed + row * 97 + col * 13 + existingStonesInDestination.Count * 23);
                return GetPitStonePosition(pit, existingStonesInDestination.Count, newTotal, pitRadius, centeredPitRadius, incomingRandom);
            }

            return GetPitStonePositionWithSeparationAndBounds(row, col, existingStonesInDestination, incomingStone);
        }

        /// <summary>
        /// Gets a stone position for a destination pit during sowing, with separation checking
        /// against all stones already in the destination pit (including those already landed in this segment)
        /// and ensures the position stays within collider bounds.
        /// </summary>
        private Vector3 GetPitStonePositionWithSeparationAndBounds(int row, int col, List<GameObject> existingStonesInDestination, GameObject stoneBeingPlaced)
        {
            Transform pit = GetPitTransform(row, col);
            if (pit == null)
                return transform.position;

            // Get collider bounds to enforce containment
            Collider pitCollider = pit.GetComponentInChildren<Collider>();
            Bounds pitBounds = pitCollider != null ? pitCollider.bounds : new Bounds(pit.position, Vector3.one * 0.5f);
            
            int visualIndex = existingStonesInDestination.Count;
            System.Random random = new System.Random(randomSeed + row * 97 + col * 13 + visualIndex * 23);
            
            // Run separation check loop
            float stoneRadius = effectiveStoneRadius;
            float minDistanceSqr = (stoneRadius * 2f) * (stoneRadius * 2f);
            int maxAttempts = 10;
            Vector3 lastPosition = GetPitBasePosition(pit);
            
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                float radius = useCenteredStonePiles ? centeredPitRadius : pitRadius;
                int layer = useCenteredStonePiles ? visualIndex / Mathf.Max(stonesPerPileLayer, 1) : 0;
                Vector3 basePosition = GetPitBasePosition(pit);
                
                Vector2 offset;
                if (useCenteredStonePiles)
                {
                    offset = GetCenteredPileOffset(visualIndex, Mathf.Max(visualIndex + 1, 1), radius);
                    if (attempt > 0)
                    {
                        Vector2 jitter = new Vector2(
                            RandomRange(random, -stoneRadius * 0.5f, stoneRadius * 0.5f),
                            RandomRange(random, -stoneRadius * 0.5f, stoneRadius * 0.5f));
                        offset += jitter;
                    }
                }
                else
                {
                    offset = GetScatterOffset(visualIndex, Mathf.Max(visualIndex + 1, 1), radius, random);
                }
                
                lastPosition = basePosition
                    + pit.right * offset.x
                    + pit.forward * offset.y
                    + Vector3.up * (layer * pileLayerHeight);
                
                // Check 1: Is the position non-overlapping with existing stones?
                if (IsOverlapping(lastPosition, existingStonesInDestination, minDistanceSqr))
                    continue;
                
                // Check 2: Is the position within the pit collider bounds?
                if (pitCollider != null && !pitBounds.Contains(lastPosition))
                    continue;
                
                // Position is valid
                return lastPosition;
            }
            
            // Fallback: nudge up and clamp to bounds
            lastPosition = lastPosition + Vector3.up * 0.01f;
            if (pitCollider != null && !pitBounds.Contains(lastPosition))
            {
                // Clamp position to collider bounds
                lastPosition.x = Mathf.Clamp(lastPosition.x, pitBounds.min.x + stoneRadius, pitBounds.max.x - stoneRadius);
                lastPosition.y = Mathf.Clamp(lastPosition.y, pitBounds.min.y + stoneRadius, pitBounds.max.y - stoneRadius);
                lastPosition.z = Mathf.Clamp(lastPosition.z, pitBounds.min.z + stoneRadius, pitBounds.max.z - stoneRadius);
            }
            
            return lastPosition;
        }

        private Vector3 GetPitStonePosition(Transform center, int index, int total, float scatterRadius, float centeredRadius, System.Random random)
        {
            float radius = useCenteredStonePiles ? centeredRadius : scatterRadius;
            Vector2 offset = useCenteredStonePiles
                ? GetCenteredPileOffset(index, total, radius)
                : GetScatterOffset(index, total, radius, random);

            int layer = useCenteredStonePiles ? index / Mathf.Max(stonesPerPileLayer, 1) : 0;
            Vector3 basePosition = GetPitBasePosition(center);

            return basePosition
                + center.right * offset.x
                + center.forward * offset.y
                + Vector3.up * (layer * pileLayerHeight);
        }

        private Vector3 GetPitStonePosition(Transform center, int index, int total, float scatterRadius, float centeredRadius, System.Random random, List<GameObject> existingStones)
        {
            float radius = useCenteredStonePiles ? centeredRadius : scatterRadius;
            int layer = useCenteredStonePiles ? index / Mathf.Max(stonesPerPileLayer, 1) : 0;
            Vector3 basePosition = GetPitBasePosition(center);
            Vector3 lastPosition = basePosition;

            float stoneRadius = effectiveStoneRadius;
            float minDistanceSqr = (stoneRadius * 2f) * (stoneRadius * 2f);
            int maxAttempts = 10;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                Vector2 offset;
                if (useCenteredStonePiles)
                {
                    offset = GetCenteredPileOffset(index, total, radius);
                    if (attempt > 0)
                    {
                        Vector2 jitter = new Vector2(
                            RandomRange(random, -stoneRadius, stoneRadius),
                            RandomRange(random, -stoneRadius, stoneRadius)) * 0.5f;
                        offset += jitter;
                    }
                }
                else
                {
                    offset = GetScatterOffset(index, total, radius, random);
                }

                lastPosition = basePosition
                    + center.right * offset.x
                    + center.forward * offset.y
                    + Vector3.up * (layer * pileLayerHeight);

                if (!IsOverlapping(lastPosition, existingStones, minDistanceSqr))
                {
                    return lastPosition;
                }
            }

            return lastPosition + Vector3.up * 0.01f;
        }

        private bool IsOverlapping(Vector3 position, List<GameObject> existingStones, float minDistanceSqr)
        {
            if (existingStones == null || existingStones.Count == 0)
                return false;

            for (int i = 0; i < existingStones.Count; i++)
            {
                GameObject existing = existingStones[i];
                if (existing == null)
                    continue;

                if ((existing.transform.position - position).sqrMagnitude < minDistanceSqr)
                    return true;
            }

            return false;
        }

        private Vector3 GetPitBasePosition(Transform pit)
        {
            if (!usePitBoundsBottom)
                return pit.position + Vector3.up * stoneYOffset;

            Bounds bounds = GetWorldBounds(pit);
            return new Vector3(bounds.center.x, bounds.min.y + pitBottomYOffset, bounds.center.z);
        }

        private Vector2 GetCenteredPileOffset(int index, int total, float radius)
        {
            if (usePitFormationLayout)
            {
                return GetPitFormationOffset(index, total, radius);
            }

            if (index == 0)
                return Vector2.zero;

            int layerIndex = index % Mathf.Max(stonesPerPileLayer, 1);
            int layerCount = Mathf.Min(total, Mathf.Max(stonesPerPileLayer, 1));
            float angle = (Mathf.PI * 2f * layerIndex) / Mathf.Max(layerCount, 1);
            float ring = Mathf.Min(radius, radius * (0.45f + 0.12f * (index / Mathf.Max(stonesPerPileLayer, 1))));

            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * ring;
        }

        private Vector2 GetPitFormationOffset(int index, int total, float radius)
        {
            float spacing = Mathf.Min(pitFormationSpacing, radius);

            if (total <= 1)
                return Vector2.zero;

            if (total == 2)
            {
                return index == 0
                    ? new Vector2(-spacing * 0.5f, 0f)
                    : new Vector2(spacing * 0.5f, 0f);
            }

            if (total == 3)
            {
                switch (index)
                {
                    case 0: return new Vector2(0f, spacing * 0.52f);
                    case 1: return new Vector2(-spacing * 0.58f, -spacing * 0.42f);
                    default: return new Vector2(spacing * 0.58f, -spacing * 0.42f);
                }
            }

            if (total == 4)
            {
                switch (index)
                {
                    case 0: return new Vector2(-spacing * 0.55f, spacing * 0.45f);
                    case 1: return new Vector2(spacing * 0.55f, spacing * 0.45f);
                    case 2: return new Vector2(-spacing * 0.55f, -spacing * 0.45f);
                    default: return new Vector2(spacing * 0.55f, -spacing * 0.45f);
                }
            }

            if (total == 5)
            {
                if (index == 0)
                    return Vector2.zero;

                float angle = Mathf.PI * 0.25f + (index - 1) * Mathf.PI * 0.5f;
                return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * spacing * 0.85f;
            }

            if (total == 6)
            {
                if (index < 5)
                {
                    float angle = Mathf.PI * 0.5f + index * Mathf.PI * 2f / 5f;
                    return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * spacing * 0.9f;
                }

                return Vector2.zero;
            }

            int layerIndex = index % Mathf.Max(stonesPerPileLayer, 1);
            int layer = index / Mathf.Max(stonesPerPileLayer, 1);
            int layerCount = Mathf.Min(total - layer * Mathf.Max(stonesPerPileLayer, 1), Mathf.Max(stonesPerPileLayer, 1));
            float layerAngle = Mathf.PI * 0.5f + layerIndex * Mathf.PI * 2f / Mathf.Max(layerCount, 1);
            float layerRadius = Mathf.Min(radius, spacing * (0.78f + layer * 0.18f));

            return new Vector2(Mathf.Cos(layerAngle), Mathf.Sin(layerAngle)) * layerRadius;
        }

        /// <summary>
        /// Places a store stone on a grid sized to the stone's footprint so adjacent stones
        /// never overlap, with small per-cell jitter for an organic look. The grid spans the
        /// store's box collider bounds (minus padding) and stacks into layers once a layer fills up.
        /// </summary>
        private Vector3 GetStoreStonePosition(Transform store, int index)
        {
            Bounds bounds = GetWorldBounds(store);
            Vector3 right = store.right;
            Vector3 forward = store.forward;

            GetProjectedExtents(bounds, right, out float rightMin, out float rightMax);
            GetProjectedExtents(bounds, forward, out float forwardMin, out float forwardMax);

            float rawRightLength = rightMax - rightMin;
            float rawForwardLength = forwardMax - forwardMin;
            float edgePadding = Mathf.Min(storeBoundsPadding, effectiveStoneRadius);
            float rightPadding = Mathf.Min(edgePadding, rawRightLength * 0.4f);
            float forwardPadding = Mathf.Min(edgePadding, rawForwardLength * 0.4f);

            float rightLength = Mathf.Max(0.01f, rawRightLength - rightPadding * 2f);
            float forwardLength = Mathf.Max(0.01f, rawForwardLength - forwardPadding * 2f);

            float cellSize = effectiveStoneRadius * 2.2f;

            int columns = Mathf.Max(1, Mathf.FloorToInt(rightLength / cellSize));
            int rows = Mathf.Max(1, Mathf.FloorToInt(forwardLength / cellSize));
            int perLayer = Mathf.Max(1, columns * rows);

            int layer = index / perLayer;
            int indexInLayer = index % perLayer;
            int col = indexInLayer % columns;
            int row = indexInLayer / columns;

            float rightStart = rightMin + rightPadding + cellSize * 0.5f;
            float forwardStart = forwardMin + forwardPadding + cellSize * 0.5f;

            System.Random random = new System.Random(randomSeed + index * 31 + layer * 17);
            float jitter = cellSize * 0.08f;
            float rightValue = rightStart + col * cellSize + RandomRange(random, -jitter, jitter);
            float forwardValue = forwardStart + row * cellSize + RandomRange(random, -jitter, jitter);

            float y = bounds.max.y + storeSurfaceYOffset + layer * storePileHeightStep;

            Vector3 horizontal = right * rightValue + forward * forwardValue;
            Vector3 storeCenterProjection = right * Vector3.Dot(bounds.center, right) + forward * Vector3.Dot(bounds.center, forward);

            return bounds.center
                + (horizontal - storeCenterProjection)
                + Vector3.up * (y - bounds.center.y);
        }

        private Bounds GetWorldBounds(Transform target)
        {
            Collider targetCollider = target.GetComponentInChildren<Collider>();
            if (targetCollider != null)
                return targetCollider.bounds;

            Renderer targetRenderer = target.GetComponentInChildren<Renderer>();
            if (targetRenderer != null)
                return targetRenderer.bounds;

            return new Bounds(target.position, Vector3.one * 0.25f);
        }

        private void GetProjectedExtents(Bounds bounds, Vector3 axis, out float min, out float max)
        {
            axis.Normalize();
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;

            min = float.MaxValue;
            max = float.MinValue;

            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 corner = center + new Vector3(extents.x * x, extents.y * y, extents.z * z);
                        float projected = Vector3.Dot(corner, axis);
                        min = Mathf.Min(min, projected);
                        max = Mathf.Max(max, projected);
                    }
                }
            }
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
            {
                audioSource.PlayOneShot(stoneHitClip, stoneHitVolume);
            }
        }

        private AudioClip CreateStoneHitClip()
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

        private Vector2 GetScatterOffset(int index, int total, float radius, System.Random random)
        {
            float goldenAngle = 137.508f * Mathf.Deg2Rad;
            float normalizedIndex = (index + 0.5f) / Mathf.Max(total, 1);
            float distance = Mathf.Sqrt(normalizedIndex) * radius * RandomRange(random, 0.35f, 0.95f);
            float angle = index * goldenAngle + RandomRange(random, -0.45f, 0.45f);

            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
        }

        private float RandomRange(System.Random random, float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }

        private void ClearStones()
        {
            Log($"ClearStones() destroying {spawnedStones.Count} old stones.");

            for (int i = spawnedStones.Count - 1; i >= 0; i--)
            {
                if (spawnedStones[i] != null)
                {
                    Destroy(spawnedStones[i]);
                }
            }

            spawnedStones.Clear();
            ClearStoneLists();
        }

        private void ClearStonesImmediate()
        {
            for (int i = spawnedStones.Count - 1; i >= 0; i--)
            {
                if (spawnedStones[i] != null)
                {
                    DestroyImmediate(spawnedStones[i]);
                }
            }

            spawnedStones.Clear();
            ClearStoneLists();
        }

        private void ClearStoneLists()
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 12; c++)
                {
                    if (stonesByPit[r, c] != null)
                    {
                        stonesByPit[r, c].Clear();
                    }
                }
            }

            storeP1Stones.Clear();
            storeP2Stones.Clear();
        }

        private int CountAssignedHoles()
        {
            int count = 0;
            for (int i = 0; i < holes.Length; i++)
            {
                if (holes[i] != null)
                {
                    count++;
                }
            }

            return count;
        }

        private int CountBoardStones(GameBoard board)
        {
            int total = 0;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 12; c++)
                {
                    total += board.Get(r, c);
                }
            }

            return total;
        }

        private void Log(string message)
        {
            if (enableBreadcrumbLogs)
            {
                Debug.Log($"[PitStoneVisualizer] {message}", this);
            }
        }
    }
}
