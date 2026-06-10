using System;
using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// L-049: canonical-roster scanner that respawns missing long-running daemons.
    /// Distinct from <see cref="WorkerHealthMonitor"/> (L-047) in two ways:
    ///   - The roster is HARD-CODED to the 18 long-running daemon label strings (matches
    ///     <see cref="WorkerPool.EnqueueLongRunning"/>'s label parameter exactly). A
    ///     missing daemon here means a process-correctness regression.
    ///   - The respawn factory is registered at SmartRuntime.Init time, not later — so
    ///     this scanner can act the moment the runtime is up.
    ///
    /// Scan is debounced to 1000ms via Interlocked.CompareExchange on _lastDaemonWatchdogTickMs.
    /// Per-daemon respawn capped at 5 per session to prevent thrash on a deterministic crash
    /// loop; manual recovery via smart.workers.respawn beyond that.
    ///
    /// IsTearingDown gate (L-002) suppresses scans during shutdown so the watchdog doesn't
    /// fight CancelAllAndJoin.
    /// </summary>
    public static class DaemonWatchdog
    {
        public const int ScanDebounceMs = 1000;
        public const int RespawnAttemptCap = 5;

        // Canonical roster — LITERAL strings matching EnqueueLongRunning label parameters.
        // Verifier note (§9): names must match exactly; paraphrased names break the
        // WorkerHealthMonitor.IsAliveByLabel prefix match.
        // BUG-025 + BUG-036: added StrategicStateExtractor + TeamReaper + Autosave +
        // TechLeakWatchdog — all four were enqueued as long-running daemons but missing
        // from the roster, so DaemonWatchdog.DoScan never checked their liveness.
        private static readonly string[] CanonicalRoster = new[]
        {
            "AetherFuser",
            "GlobalPlanner",
            "GlobalCoordinator",
            "ThreatFieldRebuild",
            "PathSolve",
            "Trainer-Intent",
            "Trainer-ActionValue",
            "Trainer-Residual",
            "Trainer-Threat",
            "StrategicStateExtractor",
            "TeamReaper",
            "Autosave",
            "TechLeakWatchdog",
            // Director phase 1 — TrainingDirector ships its factory; DirectorInbox factory
            // arrives in Phase 9. Until then ScanAndRespawn logs factory=missing once
            // per session for DirectorInbox (dedup'd by name).
            "TrainingDirector",
            "DirectorInbox",
            // Scenario planner + chunk regen. Both spin at Director.Init; idle until
            // ActiveScenario is non-zero.
            "ScenarioWorker",
            "ChunkRegen",
            // L3 guard worker. 1 Hz tick, idle when no Smart-driven techs or paused.
            "GuardWorker",
        };

        private static int _lastDaemonWatchdogTickMs;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _respawnAttempts
            = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        private static readonly HashSet<string> _previouslyMissing = new HashSet<string>(System.StringComparer.Ordinal);
        private static readonly object _missingLock = new object();

        // Factories — populated by RegisterCanonical from SmartRuntime.Init (L-066 pattern).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<string>> _factories
            = new System.Collections.Concurrent.ConcurrentDictionary<string, Func<string>>();

        /// <summary>Register a respawn factory for a canonical daemon name.</summary>
        public static void RegisterCanonical(string daemonName, Func<string> respawnFactory)
        {
            if (string.IsNullOrEmpty(daemonName) || respawnFactory == null) return;
            _factories[daemonName] = respawnFactory;
        }

        public static void Reset()
        {
            _lastDaemonWatchdogTickMs = 0;
            _respawnAttempts.Clear();
            lock (_missingLock) _previouslyMissing.Clear();
            _factories.Clear();
        }

        /// <summary>
        /// Called from SmartForm.Operations (L-049's hook site mirrors L-072
        /// WorkerHealthMonitor.Tick pattern). Cheap fast-path no-op when debounce window
        /// hasn't elapsed.
        /// </summary>
        public static void ScanAndRespawn()
        {
            if (WorkerLifecycleRegistry.IsTearingDown) return;

            int now = System.Environment.TickCount;
            int prev = System.Threading.Volatile.Read(ref _lastDaemonWatchdogTickMs);
            if (prev != 0 && unchecked(now - prev) < ScanDebounceMs) return;
            if (System.Threading.Interlocked.CompareExchange(ref _lastDaemonWatchdogTickMs, now, prev) != prev) return;

            DoScan();
        }

        private static void DoScan()
        {
            var live = WorkerLifecycleRegistry.SnapshotLive();
            var aliveNames = new HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < live.Count; i++)
            {
                var h = live[i];
                if (h != null) aliveNames.Add(h.Name);
            }

            var missingNow = new List<string>();
            for (int i = 0; i < CanonicalRoster.Length; i++)
            {
                var name = CanonicalRoster[i];
                if (!IsAliveByLabel(name, aliveNames)) missingNow.Add(name);
            }

            if (missingNow.Count == 0)
            {
                lock (_missingLock) { if (_previouslyMissing.Count > 0) _previouslyMissing.Clear(); }
                return;
            }

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
                DebugTAC_AI.LogWarnFileOnly("daemon-watchdog-missing-" + string.Join(",", newlyMissing),
                    "[DAEMON-RESPAWN] missing=" + string.Join(",", newlyMissing) + " — attempting respawn");
                for (int i = 0; i < newlyMissing.Count; i++) AttemptRespawn(newlyMissing[i]);
            }
        }

        private static bool IsAliveByLabel(string daemonName, HashSet<string> aliveNames)
        {
            string prefix = "SmartLR-" + daemonName + "-";
            foreach (var name in aliveNames)
            {
                if (name != null && name.StartsWith(prefix, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void AttemptRespawn(string daemonName)
        {
            int prior = _respawnAttempts.AddOrUpdate(daemonName, 1, (_, v) => v + 1);
            if (prior > RespawnAttemptCap)
            {
                DebugTAC_AI.LogWarnFileOnly("daemon-respawn-capped-" + daemonName,
                    "[DAEMON-RESPAWN] daemon=" + daemonName + " capped (attempts=" + prior
                    + " > " + RespawnAttemptCap + ") — manual smart.workers.respawn required");
                return;
            }
            if (!_factories.TryGetValue(daemonName, out var factory) || factory == null)
            {
                DebugTAC_AI.LogWarnFileOnly("daemon-respawn-no-factory-" + daemonName,
                    "[DAEMON-RESPAWN] daemon=" + daemonName + " factory=missing (not registered with RegisterCanonical)");
                return;
            }
            try
            {
                string newName = factory();
                if (!string.IsNullOrEmpty(newName))
                {
                    DebugTAC_AI.LogWarnFileOnly("daemon-respawn-ok-" + daemonName,
                        "[DAEMON-RESPAWN] daemon=" + daemonName + " factory=ok newThread=" + newName);
                    lock (_missingLock) _previouslyMissing.Remove(daemonName);
                    // Phase 9 promotes this to a typed DaemonRespawned event on WorldEventBus.
                    // Until then, the digest §6.2 TRIG=respawn:<daemon> source is the
                    // file-only tag the line above carries.
                    try
                    {
                        DebugTAC_AI.LogWarnFileOnly("daemon-respawned-event-" + daemonName,
                            "[DAEMON-RESPAWNED] name=" + daemonName
                            + " tickMono=" + TAC_AI.AI.Forms.Smart.World.MonoClock.Now()
                            + " reason=respawn-success");
                    }
                    catch { /* defensive */ }
                }
                else
                {
                    DebugTAC_AI.LogWarnFileOnly("daemon-respawn-null-" + daemonName,
                        "[DAEMON-RESPAWN] daemon=" + daemonName + " factory=null-return (pool shutdown?)");
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("daemon-respawn-threw-" + daemonName,
                    "[DAEMON-RESPAWN] daemon=" + daemonName + " factory=threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static int CanonicalCount => CanonicalRoster.Length;
        public static int FactoriesRegistered => _factories.Count;
    }
}
