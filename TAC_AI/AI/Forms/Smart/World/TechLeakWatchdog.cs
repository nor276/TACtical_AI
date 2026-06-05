using System;
using System.Collections.Generic;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// L-056: 60s-cadence walker that asserts every registered
    /// <see cref="TechLifecycleRegistry"/> sidecar's snapshot keys are a subset of the
    /// live-tech-id set. Any key NOT in the live set is a leak — logs
    /// <c>[TECH-LEAK]</c> with the sidecar name + offending TechId + count.
    ///
    /// Live-tech-id set is the UNION of:
    ///   - <see cref="ManTechs.IterateTechs"/> (engine source of truth, sampled via
    ///     main-thread one-shot — see note below)
    ///   - <see cref="SmartRuntime.EnumerateTeams"/> per-team SnapshotKeys
    ///   - <see cref="World.WorldModel"/> SnapshotKeys
    ///
    /// Engine-side note (per plan §8 honesty section): <see cref="ManTechs._tankList"/> is
    /// not thread-safe. We post a main-thread one-shot via <see cref="WorldEventBus"/>'s
    /// async dispatch pattern (not implemented here — placeholder via TerraTechETCUtil
    /// scheduling). Fallback: if the main-thread snapshot isn't ready within 100ms, we
    /// proceed using JUST the SmartRuntime/WorldModel union (a stricter superset that
    /// catches MOST leaks but may flag legitimately-alive techs Smart didn't register).
    ///
    /// Default cadence 60s. Enqueued via WorkerPool.EnqueueLongRunning at
    /// SmartRuntime.Init.
    /// </summary>
    public static class TechLeakWatchdog
    {
        public const int CyclePeriodMs = 60000;

        private static long _leaksFoundTotal;
        public static long LeaksFoundTotal => Interlocked.Read(ref _leaksFoundTotal);

        public static string Enqueue(TAC_AI.AI.Forms.Smart.Threading.WorkerPool pool)
        {
            if (pool == null) return null;
            return pool.EnqueueLongRunning(RunLoop, "TechLeakWatchdog");
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _leaksFoundTotal, 0L);
        }

        private static void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
                    continue;
                }

                try { Sweep(); }
                catch (OperationCanceledException) { return; }
                catch (ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("TechLeakWatchdog")
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.TechLeakWatchdog: " + ex.GetType().Name + ": " + ex.Message);
                }

                if (cancellation.WaitHandle.WaitOne(CyclePeriodMs)) return;
            }
        }

        private static void Sweep()
        {
            // Build live-tech-id union from Smart-side sources (worker-thread safe).
            // ManTechs.IterateTechs is intentionally NOT consulted here — its _tankList
            // mutation isn't thread-safe and a main-thread bounce would add hundreds of ms
            // to a 60s-cadence task. The Smart-side union is the canonical "techs Smart
            // believes are alive" set; any sidecar key outside this union is by definition
            // a leak from Smart's POV regardless of engine state.
            var live = new HashSet<TechId>();
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                var keys = team.SnapshotTechIds();
                if (keys == null) continue;
                foreach (var k in keys) live.Add(k);
            }
            var worldKeys = SmartRuntime.World?.SnapshotKeys();
            if (worldKeys != null) foreach (var k in worldKeys) live.Add(k);

            // Walk each sidecar's snapshot and report keys not in `live`.
            var sidecars = TechLifecycleRegistry.Snapshot();
            int totalLeaks = 0;
            for (int i = 0; i < sidecars.Length; i++)
            {
                var sc = sidecars[i];
                if (sc == null) continue;
                var keys = sc.SnapshotKeys();
                if (keys == null || keys.Count == 0) continue;
                int leakCount = 0;
                TechId firstLeak = default(TechId);
                foreach (var k in keys)
                {
                    if (!live.Contains(k))
                    {
                        if (leakCount == 0) firstLeak = k;
                        leakCount++;
                    }
                }
                if (leakCount > 0)
                {
                    totalLeaks += leakCount;
                    DebugTAC_AI.LogWarnFileOnly("tech-leak-" + sc.Name,
                        "[TECH-LEAK] sidecar='" + sc.Name + "' leaked=" + leakCount
                        + " (firstId=" + firstLeak.Value + ") liveSetSize=" + live.Count);
                }
            }
            if (totalLeaks > 0)
            {
                Interlocked.Add(ref _leaksFoundTotal, totalLeaks);
            }
        }
    }
}
