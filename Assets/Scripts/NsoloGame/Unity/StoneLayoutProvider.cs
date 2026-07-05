using UnityEngine;
using System.Collections.Generic;

namespace NsoloGame.Unity
{
    /// <summary>
    /// Pure position/layout math for pit stones. No MonoBehaviour, no coroutines.
    /// Initialized by PitStoneVisualizer in Awake with the serialized settings.
    /// </summary>
    public class StoneLayoutProvider
    {
        public float PitRadius;
        public float CenteredPitRadius;
        public float StoneYOffset;
        public bool UsePitBoundsBottom;
        public float PitBottomYOffset;
        public bool UseCenteredStonePiles;
        public int RandomSeed;
        public float RandomRotationDegrees;
        public float StoneScale;
        public int PitBottomLayerStoneCount;
        public float LayerSpreadFraction;
        public float EffectiveStoneRadius;

        public float ComputeStoneFootprintRadius(GameObject stonePrefab, float stoneScale)
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

        public Vector3 GetPitBasePosition(Transform pit)
        {
            if (!UsePitBoundsBottom)
                return pit.position + Vector3.up * StoneYOffset;

            Bounds bounds = GetWorldBounds(pit);
            return new Vector3(bounds.center.x, bounds.min.y + PitBottomYOffset, bounds.center.z);
        }

        public Bounds GetWorldBounds(Transform target)
        {
            Collider c = target.GetComponentInChildren<Collider>();
            if (c != null)
                return c.bounds;

            Renderer r = target.GetComponentInChildren<Renderer>();
            if (r != null)
                return r.bounds;

            return new Bounds(target.position, Vector3.one * 0.25f);
        }

        public void GetProjectedExtents(Bounds bounds, Vector3 axis, out float min, out float max)
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

        public Vector3 GetPitStonePosition(Transform center, int index, int total, float scatterRadius, float centeredRadius, System.Random random, List<GameObject> existingStones)
        {
            Vector3 basePosition = GetPitBasePosition(center);

            if (UseCenteredStonePiles)
                return GetPileStonePosition(basePosition, center.right, center.forward, centeredRadius, centeredRadius, index, total, random, PitBottomLayerStoneCount);

            Vector2 offset = GetScatterOffset(index, total, scatterRadius, random);
            return basePosition + center.right * offset.x + center.forward * offset.y;
        }

        /// <summary>
        /// Places stone <paramref name="index"/> in an explicit layer stack.
        /// Every layer holds up to <paramref name="bottomLayerCount"/> stones (default 4) in the same
        /// deterministic formation, keyed off how many stones actually occupy that layer: 1 = a single
        /// centered spot, 2 = a line, 3 = a triangle, 4 = a square. So a filling pit repeats the bottom
        /// layer's spot→line→triangle→square growth on each successive layer stacked on top, rather than
        /// piling later stones into a single centered column.
        /// </summary>
        public Vector3 GetPileStonePosition(Vector3 basePosition, Vector3 right, Vector3 forward, float radiusX, float radiusZ, int index, int totalStones, System.Random random, int bottomLayerCount)
        {
            bottomLayerCount = Mathf.Max(1, bottomLayerCount);

            float diameter = EffectiveStoneRadius * 2f;
            float layerStep = diameter * 0.82f;

            int layer = index / bottomLayerCount;
            int indexInLayer = index % bottomLayerCount;

            // How many stones actually occupy this specific layer (1-bottomLayerCount). The top,
            // partially filled layer grows spot→line→triangle→square just like the bottom one.
            int stonesInThisLayer = Mathf.Clamp(totalStones - layer * bottomLayerCount, 1, bottomLayerCount);
            Vector2 offset = GetLayerFormationOffset(indexInLayer, stonesInThisLayer, radiusX, radiusZ);

            float jx = (float)(random.NextDouble() - 0.5) * diameter * 0.08f;
            float jz = (float)(random.NextDouble() - 0.5) * diameter * 0.08f;
            float jy = (float)random.NextDouble() * diameter * 0.03f;

            return basePosition
                + right * (offset.x + jx)
                + forward * (offset.y + jz)
                + Vector3.up * (layer * layerStep + jy);
        }

        /// <summary>
        /// Returns a 2-D offset (right, forward) for stone <paramref name="indexInLayer"/> within a layer
        /// of <paramref name="layerSize"/> stones, spread across an ellipse of radii (radiusX, radiusZ).
        /// Layout: 1→center, 2→pair, 3→triangle, 4→2x2, N→grid. Incomplete rows are centred.
        /// </summary>
        public Vector2 GetLayerFormationOffset(int indexInLayer, int layerSize, float radiusX, float radiusZ)
        {
            if (layerSize <= 1)
                return Vector2.zero;

            int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(layerSize)));
            int rows = Mathf.CeilToInt((float)layerSize / cols);

            int row = indexInLayer / cols;
            int col = indexInLayer % cols;
            int stonesInRow = Mathf.Min(cols, layerSize - row * cols);

            float colFrac = stonesInRow > 1 ? (col / (float)(stonesInRow - 1) - 0.5f) : 0f;
            float rowFrac = rows > 1 ? (row / (float)(rows - 1) - 0.5f) : 0f;

            return new Vector2(
                colFrac * 2f * radiusX * LayerSpreadFraction,
                rowFrac * 2f * radiusZ * LayerSpreadFraction);
        }

        public bool IsOverlapping(Vector3 position, List<GameObject> existingStones, float minDistanceSqr)
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

        /// <summary>
        /// Gets a stone position for a destination pit during sowing, with separation checking
        /// and pit collider bounds containment.
        /// </summary>
        public Vector3 GetPitStonePositionWithSeparationAndBounds(int row, int col, List<GameObject> existingStonesInDestination, GameObject stoneBeingPlaced, Transform pit)
        {
            Collider pitCollider = pit.GetComponentInChildren<Collider>();
            Bounds pitBounds = pitCollider != null ? pitCollider.bounds : new Bounds(pit.position, Vector3.one * 0.5f);

            int visualIndex = existingStonesInDestination.Count;
            System.Random random = new System.Random(RandomSeed + row * 97 + col * 13 + visualIndex * 23);

            float stoneRadius = EffectiveStoneRadius;
            float minDistanceSqr = (stoneRadius * 2f) * (stoneRadius * 2f);
            int maxAttempts = 10;
            Vector3 basePosition = GetPitBasePosition(pit);
            Vector3 lastPosition = basePosition;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                Vector2 offset = GetScatterOffset(visualIndex, Mathf.Max(visualIndex + 1, 1), PitRadius, random);

                lastPosition = basePosition
                    + pit.right * offset.x
                    + pit.forward * offset.y;

                if (IsOverlapping(lastPosition, existingStonesInDestination, minDistanceSqr))
                    continue;

                if (pitCollider != null && !pitBounds.Contains(lastPosition))
                    continue;

                return lastPosition;
            }

            lastPosition = lastPosition + Vector3.up * 0.01f;
            if (pitCollider != null && !pitBounds.Contains(lastPosition))
            {
                lastPosition.x = Mathf.Clamp(lastPosition.x, pitBounds.min.x + stoneRadius, pitBounds.max.x - stoneRadius);
                lastPosition.y = Mathf.Clamp(lastPosition.y, pitBounds.min.y + stoneRadius, pitBounds.max.y - stoneRadius);
                lastPosition.z = Mathf.Clamp(lastPosition.z, pitBounds.min.z + stoneRadius, pitBounds.max.z - stoneRadius);
            }

            return lastPosition;
        }

        public Vector2 GetScatterOffset(int index, int total, float radius, System.Random random)
        {
            float goldenAngle = 137.508f * Mathf.Deg2Rad;
            float normalizedIndex = (index + 0.5f) / Mathf.Max(total, 1);
            float distance = Mathf.Sqrt(normalizedIndex) * radius * RandomRange(random, 0.35f, 0.95f);
            float angle = index * goldenAngle + RandomRange(random, -0.45f, 0.45f);

            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
        }

        public Quaternion RandomRotation(System.Random random)
        {
            return Quaternion.Euler(
                RandomRange(random, -RandomRotationDegrees, RandomRotationDegrees),
                RandomRange(random, 0f, 360f),
                RandomRange(random, -RandomRotationDegrees, RandomRotationDegrees));
        }

        public float RandomRange(System.Random random, float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }
    }
}
