using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// L-047: snapshot-based liveness watchdog. Tracks an "expected" canonical roster of
    /// long-running workers + their respawn factories; <see cref="Tick"/> walks the
    /// <see cref="WorkerLifecycleRegistry"/> snapshot, detects names from the roster that
    /// are no longer present, emits a one-shot <c>[SMART-WORKERS-DEAD]</c> log per drop
    /// event with the missing names, and (if a factory is registered) invokes the factory
    /// to respawn the worker.
    ///
    /// Designed for SmartForm.Operations (L-072) to call at the main-thread per-frame
    /// cadence with a 1s debounce so we don't burn CPU walking the registry every frame.
    ///
    /// BeginShutdown (L-067) is called from SmartRuntime.Shutdown so the watchdog stops
    /// alerting on workers it KNOWS are about to exit. <see cref="WorkerLifecycleRegistry.IsTearingDown"/>
    /// is the other gate — together they cover both the "Shutdown was called" path AND
    /// the "CancelAllAndJoin is in flight without Shutdown" path.
    /// </summary>
    public static class WorkerHealthMonitor
    {
        private const int TickDebounceMs = 1000;
        private const int RespawnAttemptCap = 5;   // per-session, per-worker

        private static readonly ConcurrentDictionary<string, Func<string>> _factories
            = new ConcurrentDictionary<string, Func<string>>();
        private static readonly ConcurrentDictionary<string, int> _respawnAttempts
            = new ConcurrentDictionary<string, int>();
        // Names that were missing in the prior tick — used so we only emit one
        // [SMART-WORKERS-DEAD] line per drop event (not one per tick the worker stays dead).
        private static readonly HashSet<string> _previouslyMissing = new HashSet<string>();
        private static readonly object _missingLock = new object();

        private static int _lastTickMs;
        private static int _respawnSuccessTotal;
        private static int _respawnFailureTotal;
        private static bool _shuttingDown;

        /// <summary>
        /// Register an expected-worker name + a factory that respawns it. The factory MUST
        /// return the new worker's thread name (matching <see cref="WorkerPool.EnqueueLongRunning"/>
        /// signature) or null on failure. Idempotent: re-registering the same name overwrites
        /// the prior factory.
        /// </summary>
        public static void RegisterCanonical(string expectedName, Func<string> respawnFactory)
        {
            if (string.IsNullOrEmpty(expectedName)) return;
            _factories[expectedName] = respawnFactory;
        }

        /// <summary>
        /// Called by SmartRuntime.Shutdown (L-067). After BeginShutdown the watchdog stops
        /// emitting alerts and stops attempting respawns. Tick still runs because something
        /// else may still poll it, but it's effectively idle.
        /// </summary>
        public static void BeginShutdown()
        {
            _shuttingDown = true;
            lock (_missingLock) _previouslyMissing.Clear();
        }

        /// <summary>SMART_DEV / startup reset.</summary>
        public static void Reset()
        {
            _factories.Clear();
            _respawnAttempts.Clear();
            lock (_missingLock) _previouslyMissing.Clear();
            _lastTickMs = 0;
            _respawnSuccessTotal = 0;
            _respawnFailureTotal = 0;
            _shuttingDown = false;
        }

        /// <summary>
        /// Called from SmartForm.Operations (L-072) every frame. Internally debounced to 1s
        /// to keep the registry walk cost negligible.
        /// </summary>
        public static void Tick()
        {
            if (_shuttingDown) return;
            if (WorkerLifecycleRegistry.IsTearingDown) return;

            int now = System.Environment.TickCount;
            int prev = System.Threading.Interlocked.CompareExchange(ref _lastTickMs, now, _lastTickMs);
            if (prev != 0 && unchecked(now - prev) < TickDebounceMs) return;
            System.Threading.Volatile.Write(ref _lastTickMs, now);

            DoScan();
        }

        // ---- internal scan + respawn ----

        private static void DoScan()
        {
            // Build the alive set from the registry snapshot.
            var live = WorkerLifecycleRegistry.SnapshotLive();
            var aliveNames = new HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < live.Count; i++)
            {
                var h = live[i];
                if (h == null) continue;
                aliveNames.Add(h.Name);
            }

            // Walk expected roster — find names that are registered but missing.
            // EnqueueLongRunning shapes its thread name as "SmartLR-<label>-<counter>", so an
            // exact-name match isn't enough. We match by label prefix "SmartLR-<expected>-".
            var missingNow = new List<string>();
            foreach (var kv in _factories)
            {
                if (IsAliveByLabel(kv.Key, aliveNames)) continue;
                missingNow.Add(kv.Key);
            }

            if (missingNow.Count == 0)
            {
                // Recovery: any names previously missing that are now alive — clear them so
                // a future drop fires a fresh log.
                lock (_missingLock)
                {
                    if (_previouslyMissing.Count > 0) _previouslyMissing.Clear();
                }
                return;
            }

            // Diff: report names missing NOW that weren't missing before.
            List<string> newlyMissing = null;
            lock (_missingLock)
            {
                for (int i = 0; i < missingNow.Count; i++)
                {
                    var name = missingNow[i];
                    if (_previouslyMissing.Add(name))
                    {
                        if (newlyMissing == null) newlyMissing = new List<string>();
                        newlyMissing.Add(name);
                    }
                }
            }

            if (newlyMissing != null && newlyMissing.Count > 0)
            {
                DebugTAC_AI.LogWarnFileOnly("smart-workers-dead-" + string.Join(",", newlyMissing),
                    "[SMART-WORKERS-DEAD] expected=" + _factories.Count + " live=" + aliveNames.Count
                    + " missing=" + string.Join(",", newlyMissing) + " — attempting auto-respawn");

                for (int i = 0; i < newlyMissing.Count; i++)
                {
                    AttemptRespawn(newlyMissing[i]);
                }
            }
        }

        private static bool IsAliveByLabel(string expectedLabel, HashSet<string> aliveNames)
        {
            string prefix = "SmartLR-" + expectedLabel + "-";
            foreach (var name in aliveNames)
            {
                if (name != null && name.StartsWith(prefix, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void AttemptRespawn(string expectedName)
        {
            int prior = _respawnAttempts.AddOrUpdate(expectedName, 1, (_, v) => v + 1);
            if (prior > RespawnAttemptCap)
            {
                DebugTAC_AI.LogWarnFileOnly("smart-respawn-capped-" + expectedName,
                    "[SMART-WORKER-RESPAWN] name=" + expectedName + " factory=capped (attempts=" + prior
                    + " > cap=" + RespawnAttemptCap + ") — manual `smart.workers.respawn` required");
                return;
            }
            if (!_factories.TryGetValue(expectedName, out var factory) || factory == null)
            {
                DebugTAC_AI.LogWarnFileOnly("smart-respawn-no-factory-" + expectedName,
                    "[SMART-WORKER-RESPAWN] name=" + expectedName + " factory=missing");
                System.Threading.Interlocked.Increment(ref _respawnFailureTotal);
                return;
            }
            try
            {
                string newName = factory();
                if (!string.IsNullOrEmpty(newName))
                {
                    System.Threading.Interlocked.Increment(ref _respawnSuccessTotal);
                    DebugTAC_AI.LogWarnFileOnly("smart-respawn-ok-" + expectedName,
                        "[SMART-WORKER-RESPAWN] name=" + expectedName + " factory=ok newThread=" + newName);
                    // Clear the missing entry so the diff catches a future drop.
                    lock (_missingLock) _previouslyMissing.Remove(expectedName);
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref _respawnFailureTotal);
                    DebugTAC_AI.LogWarnFileOnly("smart-respawn-null-" + expectedName,
                        "[SMART-WORKER-RESPAWN] name=" + expectedName + " factory=null-return (pool shutdown?)");
                }
            }
            catch (System.Exception ex)
            {
                System.Threading.Interlocked.Increment(ref _respawnFailureTotal);
                DebugTAC_AI.LogWarnFileOnly("smart-respawn-throw-" + expectedName,
                    "[SMART-WORKER-RESPAWN] name=" + expectedName + " factory=threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Diagnostic counters surfaced by smart.runtime.status (L-076).</summary>
        public static int ExpectedCount => _factories.Count;
        public static int RespawnSuccessTotal => System.Threading.Volatile.Read(ref _respawnSuccessTotal);
        public static int RespawnFailureTotal => System.Threading.Volatile.Read(ref _respawnFailureTotal);
    }
}
