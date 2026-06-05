using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// L-009: canonical source of truth for per-tech-state cleanup. Every sidecar that
    /// keys state by TechId self-registers a Forget delegate + Keys snapshot delegate.
    /// SmartRuntime.Deregister (L-055) fans out by enumerating the registry instead of the
    /// prior hard-coded linear list — adding a new sidecar means one Register call, not a
    /// PR-review-only convention.
    ///
    /// Sidecars implement <see cref="ITechSidecar"/>. The registry is process-global and
    /// idempotent across SmartRuntime.Init/Shutdown cycles (a registered sidecar instance
    /// stays registered until explicit Unregister). Concurrent reads + writes are safe.
    ///
    /// Diagnostic: TechLeakWatchdog (L-056) walks every registered sidecar's
    /// <see cref="ITechSidecar.SnapshotKeys"/> at 60s cadence and compares against the
    /// live-tech-id set; any sidecar key not in the live set produces `[TECH-LEAK]`.
    /// </summary>
    public interface ITechSidecar
    {
        /// <summary>Stable name for logging. Should match the type name for greppability.</summary>
        string Name { get; }

        /// <summary>
        /// Drop per-tech state for <paramref name="id"/>. MUST be idempotent (called from
        /// multiple fan-out paths during a single recycle). MUST NOT throw — wrap any
        /// internal logic in try/catch and log to <c>[TECH-LEAK-FORGET-FAIL]</c>.
        /// </summary>
        void Forget(TechId id);

        /// <summary>
        /// Snapshot of every TechId this sidecar currently holds state for. Used by the
        /// TechLeakWatchdog to detect leaks. Allocation-light — called at 60s cadence.
        /// </summary>
        IReadOnlyCollection<TechId> SnapshotKeys();
    }

    public static class TechLifecycleRegistry
    {
        // Keyed by ITechSidecar instance via reference equality (default for class keys).
        // ConcurrentDictionary supports concurrent Register/Unregister + ForgetAll iteration
        // without lock churn.
        private static readonly ConcurrentDictionary<ITechSidecar, byte> _sidecars
            = new ConcurrentDictionary<ITechSidecar, byte>();

        /// <summary>Register a sidecar. Idempotent — same instance registered twice is a no-op.</summary>
        public static void Register(ITechSidecar sidecar)
        {
            if (sidecar == null) return;
            _sidecars.TryAdd(sidecar, 0);
        }

        /// <summary>Drop a sidecar. Idempotent.</summary>
        public static void Unregister(ITechSidecar sidecar)
        {
            if (sidecar == null) return;
            _sidecars.TryRemove(sidecar, out _);
        }

        /// <summary>Diagnostic count.</summary>
        public static int Count => _sidecars.Count;

        /// <summary>
        /// Snapshot of currently-registered sidecars. Returned as an array so callers can
        /// enumerate without holding any internal lock. Used by ForgetAll (below) and by
        /// TechLeakWatchdog.
        /// </summary>
        public static ITechSidecar[] Snapshot()
        {
            var arr = new ITechSidecar[_sidecars.Count];
            int i = 0;
            foreach (var kv in _sidecars)
            {
                if (i >= arr.Length) break;
                arr[i++] = kv.Key;
            }
            // _sidecars may have shrunk between Count read and enumeration; trim.
            if (i != arr.Length)
            {
                var trimmed = new ITechSidecar[i];
                Array.Copy(arr, trimmed, i);
                return trimmed;
            }
            return arr;
        }

        /// <summary>
        /// Forget <paramref name="id"/> across every registered sidecar. Each Forget runs
        /// inside its own try/catch — one sidecar's exception cannot abort the rest.
        /// Failed sidecars produce <c>[TECH-LEAK-FORGET-FAIL]</c> log lines.
        /// </summary>
        public static void ForgetAll(TechId id)
        {
            var snap = Snapshot();
            for (int i = 0; i < snap.Length; i++)
            {
                var sc = snap[i];
                if (sc == null) continue;
                try
                {
                    sc.Forget(id);
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly(
                        "tech-leak-forget-fail-" + sc.Name + "-" + id.Value,
                        "[TECH-LEAK-FORGET-FAIL] sidecar='" + sc.Name + "' techId=" + id.Value
                        + " threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>SMART_DEV test hook: drop every registered sidecar for a clean test session.</summary>
        public static void Clear() => _sidecars.Clear();
    }
}
