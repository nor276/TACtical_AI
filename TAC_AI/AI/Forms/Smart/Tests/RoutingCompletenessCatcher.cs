using System;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Tests
{
    /// <summary>
    /// L-081: 5s-cadence walker that asserts every live TankAIHelper has
    /// <see cref="TankAIHelper.Routing"/>.FormId != null (or its age &lt; 1s — fresh
    /// spawn still in the DelayedSubscribe window). On orphan detection, logs
    /// <c>[ROUTING-ORPHAN]</c> via popup-once-per-tank surface AND self-heals by
    /// calling <see cref="AIFormRegistry.RouteTech"/> with Reason=Reclaimed.
    ///
    /// Default-ON. Called from SmartForm.Operations (debounced 5s) — same hook pattern
    /// as WorkerHealthMonitor.Tick (L-072) + DaemonWatchdog.ScanAndRespawn (L-049).
    /// </summary>
    public static class RoutingCompletenessCatcher
    {
        public const int ScanIntervalMs = 5000;
        public const int SpawnGraceMs = 1000;   // helpers younger than this are still in DelayedSubscribe

        private static int _lastScanMs;

        public static void Reset()
        {
            _lastScanMs = 0;
        }

        public static void MainThreadTick()
        {
            int now = Environment.TickCount;
            int prev = System.Threading.Volatile.Read(ref _lastScanMs);
            if (prev != 0 && unchecked(now - prev) < ScanIntervalMs) return;
            if (System.Threading.Interlocked.CompareExchange(ref _lastScanMs, now, prev) != prev) return;

            try { DoScan(now); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("RoutingCompletenessCatcher: " + ex.Message);
            }
        }

        private static void DoScan(int nowMs)
        {
            int orphanCount = 0;
            int healedCount = 0;
            foreach (var helper in AIECore.IterateAllHelpers())
            {
                if (helper == null || helper.tank == null) continue;
                var r = helper.Routing;
                if (r.FormId != null) continue;

                int age = r.TimestampMs == 0 ? int.MaxValue : unchecked(nowMs - r.TimestampMs);
                if (age < SpawnGraceMs) continue;   // still in DelayedSubscribe window

                orphanCount++;
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "routing-orphan-" + helper.GetInstanceID(),
                    "[ROUTING-ORPHAN] tech='" + (helper.tank ? helper.tank.name : "<null>")
                    + "' has no FormId routing (age=" + age + "ms) — self-healing",
                    null);

                // Self-heal: re-route via funnel.
                try
                {
                    AIFormRegistry.RouteTech(helper, "Reclaimed");
                    healedCount++;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("routing-orphan-heal-fail-" + helper.GetInstanceID(),
                        "[ROUTING-ORPHAN] self-heal failed for tech='"
                        + (helper.tank ? helper.tank.name : "<null>") + "': " + ex.Message);
                }
            }

            if (orphanCount > 0)
            {
                DebugTAC_AI.LogWarnFileOnly("routing-orphan-batch",
                    "[ROUTING-ORPHAN] scan: " + orphanCount + " orphan(s) found, " + healedCount + " self-healed");
            }
        }
    }
}
