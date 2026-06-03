using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: per-tank single-pass block walker. Reads cachedLocalPosition + Quaternion
    /// cachedLocalRotation.rot + CurrentMass per block; resolves each block-type via the
    /// catalog (GetOrProbe lazily); emits a BlockInstancePose[].
    ///
    /// Main-thread only -- called from SmartPerTechState.RebuildVehicleSnapshotInternal
    /// behind the parallel-run gate in Step 5. Zero GetComponent calls on instances after
    /// catalog warmup (per Decision summary).
    /// </summary>
    public static class ChassisCapture
    {
        /// <summary>
        /// Walk every block on <paramref name="tank"/>, probe the catalog for each block type,
        /// and emit a per-instance pose array suitable for ThrustField/MassDistribution/etc.
        /// </summary>
        public static BlockInstancePose[] Capture(Tank tank, TypedBlockCatalog catalog)
        {
            if (tank == null || tank.blockman == null || catalog == null)
                return System.Array.Empty<BlockInstancePose>();

            // Eagerly collect into a List, then ToArray. The intermediate growth is small
            // (most techs are < 100 blocks) and the per-rebuild cost is one allocation.
            var poses = new List<BlockInstancePose>(64);

            foreach (TankBlock block in tank.blockman.IterateBlocks())
            {
                if (block == null) continue;
                // Trigger probe lazily; we don't need the result here (ThrustField.Compute
                // looks it up again via TryGet by typeKey). Calling GetOrProbe also covers
                // the first-sighting cost amortization.
                catalog.GetOrProbe(block);

                int typeKey = (int)block.BlockType;
                Vector3 pos;
                Quaternion rot;
                float mass = 1f;
                try { pos = block.cachedLocalPosition; }
                catch { pos = Vector3.zero; }
                // block.cachedLocalRotation is OrthoRotation (TerraTech's discretized rotation
                // type); its `.rot` field is OrthoRotation.r-typed and doesn't implicitly
                // convert to Quaternion. block.transform.localRotation gives the same
                // chassis-frame rotation as a native Quaternion (transform is in chassis-local
                // coords inside Tank.blockman). Step 3.5 fixture verifies this matches the
                // OrthoRotation-source pattern within float-eps.
                try { rot = block.transform.localRotation; }
                catch { rot = Quaternion.identity; }
                try { mass = block.CurrentMass; }
                catch { /* keep default */ }
                if (mass <= 0f) mass = 1f;

                poses.Add(new BlockInstancePose(typeKey, pos, rot, mass));
            }

            var arr = poses.ToArray();

            // Chassis Step 3.5 hard-gate: emit fixture verification once per session on the
            // first non-trivial probed tech. No-op after the first interesting tech.
            ChassisFixtureGate.TryEmitOnce(tank, catalog, arr);

            return arr;
        }
    }
}
