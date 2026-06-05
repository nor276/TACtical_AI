#if SMART_DEV
using System;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Tests
{
    /// <summary>
    /// L-068: SMART_DEV gate locking every invariant in §3.7 (terrain + path-cache world
    /// reset). 6 tests; SmartTestSuite.RunAll lists them inside #if SMART_DEV.
    /// </summary>
    public static class PathingResetTests
    {
        /// <summary>WorldResetRegistry: lambda registration fires on ResetAll.</summary>
        public static void WorldResetRegistry_LambdaFires()
        {
            // Snapshot count + register a local lambda; ResetAll must invoke it.
            int before = WorldResetRegistry.Count;
            int hits = 0;
            WorldResetRegistry.RegisterLambda("UnitTest.LambdaFires", () => hits++);
            int ok = WorldResetRegistry.ResetAll();
            if (hits != 1) throw new Exception("Lambda not invoked, hits=" + hits);
            if (ok < 1) throw new Exception("ResetAll returned ok=" + ok);
            if (WorldResetRegistry.Count <= before) throw new Exception("Registration count regressed");
        }

        /// <summary>WorldResetRegistry: a throwing hook does not abort the rest.</summary>
        public static void WorldResetRegistry_ThrowingHookContained()
        {
            int after = 0;
            WorldResetRegistry.RegisterLambda("UnitTest.ThrowingHook",
                () => { throw new Exception("intentional"); });
            WorldResetRegistry.RegisterLambda("UnitTest.AfterHook", () => after++);
            WorldResetRegistry.ResetAll();
            if (after != 1) throw new Exception("Subsequent hook didn't run after throwing predecessor");
        }

        /// <summary>TerrainMap.IsFreshlyAllocated starts true; flips false after MarkRefreshed.</summary>
        public static void TerrainMap_IsFreshlyAllocated_FlipsOnRefresh()
        {
            var tm = new TerrainMap();
            if (!tm.IsFreshlyAllocated) throw new Exception("New TerrainMap should report freshly allocated");
            tm.MarkRefreshed();
            if (tm.IsFreshlyAllocated) throw new Exception("MarkRefreshed did not clear flag");
        }

        /// <summary>TerrainPublication: atomic swap returns prior reference.</summary>
        public static void TerrainPublication_SwapAtomic()
        {
            var pub = new TerrainPublication();
            var first = new TerrainMap();
            var second = new TerrainMap();
            var prior0 = pub.Publish(first);
            if (prior0 != null) throw new Exception("Initial publish should return null");
            var prior1 = pub.Publish(second);
            if (!ReferenceEquals(prior1, first)) throw new Exception("Second publish should return the first instance");
            if (!ReferenceEquals(pub.Read(), second)) throw new Exception("Read should return latest");
            if (pub.Generation < 2) throw new Exception("Generation should be >= 2 after 2 swaps");
        }

        /// <summary>PathingService.ForgetTeam tolerates a non-running service silently.</summary>
        public static void PathingService_ForgetTeam_TolerantOfNotRunning()
        {
            // Service may be stopped during tests; no throw expected.
            PathingService.ForgetTeam(new TeamId(int.MaxValue));
        }

        /// <summary>PathRequestBackpressure.ResetForWorldChange clears hysteresis + reservoir.</summary>
        public static void PathRequestBackpressure_ResetClears()
        {
            var bp = new TAC_AI.AI.Forms.Smart.Threading.PathRequestBackpressure();
            for (int i = 0; i < 10; i++) bp.RecordSolveLatency(5f);
            bp.ResetForWorldChange();
            if (bp.SolveLatencyMsP50 != 0f) throw new Exception("Reset did not clear latency reservoir");
            if (bp.ShedActive) throw new Exception("Reset did not clear shed state");
        }
    }
}
#endif
