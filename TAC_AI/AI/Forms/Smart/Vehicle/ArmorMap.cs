using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>Result of an armor-face query.</summary>
    public readonly struct ArmorQueryResult
    {
        public readonly Vector3 FaceCenterLocal;
        public readonly Vector3 FaceNormalLocal;
        public readonly float TotalHP;

        public ArmorQueryResult(Vector3 center, Vector3 normal, float totalHP)
        {
            FaceCenterLocal = center;
            FaceNormalLocal = normal;
            TotalHP = totalHP;
        }

        public static readonly ArmorQueryResult Empty = new ArmorQueryResult(Vector3.zero, Vector3.up, 0f);
    }

    /// <summary>
    /// Voxelized armor / vulnerability map per VEHICLE-CONTRACT §6.
    /// 3D grid aligned to tech-local axes; each cell stores accumulated HP.
    ///
    /// Resolution is adaptive to tech extents — 6×6×6 to 16×16×16 typical.
    /// Face-direction queries walk the slab nearest to the query direction and
    /// return the lowest-HP face center.
    /// </summary>
    public sealed class ArmorMap
    {
        public Vector3Int GridResolution { get; }
        public Bounds LocalBounds { get; }
        private readonly float[] _hp;        // length = nx * ny * nz; row-major (x, y, z) → index = x*nynz + y*nz + z

        public static ArmorMap Empty { get; } = new ArmorMap(new Vector3Int(1, 1, 1), new Bounds(Vector3.zero, Vector3.one), new float[1]);

        public ArmorMap(Vector3Int resolution, Bounds localBounds, float[] hpCells)
        {
            // Phase 6 (FIX-PLAN.md) — AUDIT-R2 §2.R2.F: zero-dimension resolution causes
            // QueryWeakFace's cellSize divisions to produce infinities. Reject explicitly.
            if (resolution.x <= 0 || resolution.y <= 0 || resolution.z <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(resolution),
                    "ArmorMap resolution components must be positive.");
            GridResolution = resolution;
            LocalBounds = localBounds;
            _hp = hpCells ?? new float[resolution.x * resolution.y * resolution.z];
        }

        public float CellHP(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 ||
                x >= GridResolution.x || y >= GridResolution.y || z >= GridResolution.z) return 0f;
            return _hp[(x * GridResolution.y + y) * GridResolution.z + z];
        }

        /// <summary>
        /// Query the weakest face given an attack direction in tech-local coords.
        /// Walks the slab facing the attacker; returns the lowest-HP face.
        /// </summary>
        public ArmorQueryResult QueryWeakFace(Vector3 attackDirectionLocal)
        {
            if (_hp.Length <= 1) return ArmorQueryResult.Empty;

            // Determine which axis the attack predominantly comes from.
            Vector3 nrm = attackDirectionLocal.normalized;
            float ax = Mathf.Abs(nrm.x), ay = Mathf.Abs(nrm.y), az = Mathf.Abs(nrm.z);
            int axis = 0;
            if (ay > ax && ay > az) axis = 1;
            else if (az > ax && az > ay) axis = 2;

            // Which side of the axis: the slab facing the attacker is opposite to attack direction.
            int sign = 1;
            if (axis == 0 && nrm.x < 0) sign = -1;
            else if (axis == 1 && nrm.y < 0) sign = -1;
            else if (axis == 2 && nrm.z < 0) sign = -1;
            // The slab is the layer with sign-extremes index on that axis.
            int slabIndex = sign > 0 ? GetExtent(axis) - 1 : 0;

            // Sum HP across the slab and find the cell with min HP (for the weakest face).
            float totalHP = 0f;
            float minCellHP = float.MaxValue;
            int minA = 0, minB = 0;
            int otherAxis1 = (axis + 1) % 3;
            int otherAxis2 = (axis + 2) % 3;
            for (int a = 0; a < GetExtent(otherAxis1); a++)
            {
                for (int b = 0; b < GetExtent(otherAxis2); b++)
                {
                    var idx = new int[3];
                    idx[axis] = slabIndex;
                    idx[otherAxis1] = a;
                    idx[otherAxis2] = b;
                    float cellHP = CellHP(idx[0], idx[1], idx[2]);
                    totalHP += cellHP;
                    if (cellHP > 0 && cellHP < minCellHP)
                    {
                        minCellHP = cellHP;
                        minA = a; minB = b;
                    }
                }
            }

            // Convert the slab cell coordinates back to local-space face center.
            Vector3 cellSize = new Vector3(
                LocalBounds.size.x / GridResolution.x,
                LocalBounds.size.y / GridResolution.y,
                LocalBounds.size.z / GridResolution.z);
            Vector3 origin = LocalBounds.min;
            var center = new int[3];
            center[axis] = slabIndex;
            center[otherAxis1] = minA;
            center[otherAxis2] = minB;
            Vector3 faceCenter = new Vector3(
                origin.x + cellSize.x * (center[0] + 0.5f),
                origin.y + cellSize.y * (center[1] + 0.5f),
                origin.z + cellSize.z * (center[2] + 0.5f));

            // Face normal: along chosen axis, pointing outward.
            Vector3 faceNormal = Vector3.zero;
            if (axis == 0) faceNormal.x = sign;
            else if (axis == 1) faceNormal.y = sign;
            else faceNormal.z = sign;

            return new ArmorQueryResult(faceCenter, faceNormal, totalHP);
        }

        private int GetExtent(int axis)
        {
            if (axis == 0) return GridResolution.x;
            if (axis == 1) return GridResolution.y;
            return GridResolution.z;
        }

        /// <summary>
        /// v0.1 path: build an armor map by binning blocks into voxels and accumulating
        /// <c>b.CurrentMass</c> as the per-cell HP proxy. Bit-identical to v0.1 behavior;
        /// retained as the default path so face-weakness ranking is unchanged when
        /// <see cref="ArmorMapPolicy.UseRealSpecHP"/> is false.
        /// </summary>
        public static ArmorMap Compute(BlockInstancePose[] poses, Vector3Int desiredResolution)
        {
            if (poses == null || poses.Length == 0) return Empty;

            Vector3 minP, maxP, size;
            Bounds bounds;
            int nx, ny, nz;
            float[] cells;
            BuildVoxelGrid(poses, desiredResolution, out minP, out maxP, out size, out bounds,
                           out nx, out ny, out nz, out cells);

            for (int i = 0; i < poses.Length; i++)
            {
                var b = poses[i];
                int x = Mathf.Clamp((int)((b.LocalPosition.x - minP.x) / size.x * nx), 0, nx - 1);
                int y = Mathf.Clamp((int)((b.LocalPosition.y - minP.y) / size.y * ny), 0, ny - 1);
                int z = Mathf.Clamp((int)((b.LocalPosition.z - minP.z) / size.z * nz), 0, nz - 1);
                cells[(x * ny + y) * nz + z] += b.CurrentMass; // mass-as-HP placeholder
            }

            return new ArmorMap(new Vector3Int(nx, ny, nz), bounds, cells);
        }

        /// <summary>
        /// P3 Item 5: catalog-backed overload. Accumulates per-voxel HP as
        /// <c>archetype.SpecHP * pose.HpFraction</c> — real per-block HP from ModuleDamage
        /// modulated by per-instance damage state. Falls back gracefully when the catalog
        /// has no entry for a block's TypeKey: uses <c>b.CurrentMass</c> for that block
        /// (matches v0.1 mass-as-HP semantics so unprobed blocks don't poison the map).
        ///
        /// Gated by <see cref="ArmorMapPolicy.UseRealSpecHP"/> at the call site
        /// (SmartRuntime.cs:238).
        /// </summary>
        public static ArmorMap Compute(BlockInstancePose[] poses, TypedBlockCatalog catalog,
                                       Vector3Int desiredResolution)
        {
            if (poses == null || poses.Length == 0) return Empty;
            if (catalog == null) return Compute(poses, desiredResolution); // graceful degrade

            Vector3 minP, maxP, size;
            Bounds bounds;
            int nx, ny, nz;
            float[] cells;
            BuildVoxelGrid(poses, desiredResolution, out minP, out maxP, out size, out bounds,
                           out nx, out ny, out nz, out cells);

            for (int i = 0; i < poses.Length; i++)
            {
                var b = poses[i];
                int x = Mathf.Clamp((int)((b.LocalPosition.x - minP.x) / size.x * nx), 0, nx - 1);
                int y = Mathf.Clamp((int)((b.LocalPosition.y - minP.y) / size.y * ny), 0, ny - 1);
                int z = Mathf.Clamp((int)((b.LocalPosition.z - minP.z) / size.z * nz), 0, nz - 1);

                // Per-block HP via archetype lookup. Unknown archetype → fall back to
                // b.CurrentMass (v0.1 path) so a single missed probe doesn't zero the voxel.
                var arch = catalog.TryGet(b.TypeKey);
                float hp;
                if (arch != null && arch.SpecHP > 0f)
                    hp = arch.SpecHP * Mathf.Clamp01(b.HpFraction);
                else
                    hp = b.CurrentMass;

                cells[(x * ny + y) * nz + z] += hp;
            }

            return new ArmorMap(new Vector3Int(nx, ny, nz), bounds, cells);
        }

        // Shared bounds + grid setup for both Compute overloads.
        private static void BuildVoxelGrid(BlockInstancePose[] poses, Vector3Int desiredResolution,
            out Vector3 minP, out Vector3 maxP, out Vector3 size, out Bounds bounds,
            out int nx, out int ny, out int nz, out float[] cells)
        {
            minP = poses[0].LocalPosition;
            maxP = poses[0].LocalPosition;
            for (int i = 1; i < poses.Length; i++)
            {
                Vector3 p = poses[i].LocalPosition;
                if (p.x < minP.x) minP.x = p.x; else if (p.x > maxP.x) maxP.x = p.x;
                if (p.y < minP.y) minP.y = p.y; else if (p.y > maxP.y) maxP.y = p.y;
                if (p.z < minP.z) minP.z = p.z; else if (p.z > maxP.z) maxP.z = p.z;
            }
            size = maxP - minP;
            if (size.x < 0.1f) size.x = 0.1f;
            if (size.y < 0.1f) size.y = 0.1f;
            if (size.z < 0.1f) size.z = 0.1f;
            bounds = new Bounds(minP + size * 0.5f, size);
            nx = Mathf.Max(1, desiredResolution.x);
            ny = Mathf.Max(1, desiredResolution.y);
            nz = Mathf.Max(1, desiredResolution.z);
            cells = new float[nx * ny * nz];
        }
    }
}
