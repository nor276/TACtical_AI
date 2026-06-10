using System;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Coordination;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// L2 supervisor daemon. Phase 1 ships the skeleton: 1 Hz aggregation +
    /// 0.2 Hz constraint tick. No verbs, no constraint registry, no publishers yet —
    /// those land in Phase 2+. The construction order and shutdown plug here are
    /// load-bearing for the rollback contract — singletons must exist before the
    /// daemon thread spins, and BeginShutdown must wait on in-flight rollbacks.
    /// </summary>
    public static class TrainingDirector
    {
        public const string CanonicalName = "TrainingDirector";
        public const int AggregationTickMs = 1000;        // 1 Hz
        public const int ConstraintTickEveryNAggregations = 5; // 0.2 Hz inside the 1 Hz loop
        public const int HeartbeatJournalEveryNAggregations = 60; // ~once/min while idle
        public const int ShutdownRollbackWaitMs = 10000;
        public const int InitWaitPollMs = 1000;

        // Set after singletons constructed; daemon RunLoop spins on this latch before
        // doing any work. Mirrors the GlobalPlannerDaemon defensive pattern.
        private static volatile bool _daemonInitComplete;

        // Catastrophic shutdown signal. Set by BeginShutdown; daemon checks at the top
        // of each loop iteration and exits cleanly.
        private static volatile bool _killSwitch;

        // L-004-style latch on shutdown entry so concurrent Shutdown hooks don't race.
        private static int _shutdownRequested;

        // Phase 7 rollback in-flight gauge. Always 0 in Phase 1 (no rollback machinery
        // yet) — the wait surface must exist for the BeginShutdown contract.
        private static int _rollbackInProgress;

        private static string _modDirectory;

        // AUTO-HEALTHY scheduler (§7.1 trigger 1). Captures an in-memory healthy ring trio
        // when all constraints have been healthy for the preceding 5 min. The disk-tier
        // auto-checkpoint piggy-backs on LearningService.ProfileSaved, gated to at most
        // once per 30 min wall-clock.
        public const float AutoHealthyAllGreenSec = 300f;
        public const double AutoCheckpointMinIntervalSec = 30.0 * 60.0;
        private static long _lastHealthyRingCaptureMono;
        private static long _lastDiskAutoCheckpointMono;
        private static Action _profileSavedHandler;

        // Mono stamp set in Init — drives AgeSinceInitSec for cold-start trigger suppression.
        private static long _initMono;

        public static bool IsActive
        {
            get { return _daemonInitComplete && !_killSwitch; }
        }

        public static bool IsKillSwitchSet
        {
            get { return _killSwitch; }
        }

        // DigestBuilder shutdown gate — no-op Emit once set.
        public static bool IsShuttingDown
        {
            get { return _killSwitch || Volatile.Read(ref _shutdownRequested) != 0; }
        }

        // Seconds since Init completed. Cold-start trigger suppression checks < 60.
        public static float AgeSinceInitSec
        {
            get
            {
                long im = Interlocked.Read(ref _initMono);
                if (im == 0L) return 0f;
                return World.MonoClock.Seconds(im, World.MonoClock.Now());
            }
        }

        public static int RollbackInProgress
        {
            get { return Volatile.Read(ref _rollbackInProgress); }
        }

        /// <summary>
        /// Idempotent. Called from LearningService.Init after the 4 trainers are enqueued.
        /// Construction order: singletons first, daemon last — RunLoop spins on
        /// _daemonInitComplete before doing any work.
        /// </summary>
        public static void Init(WorkerPool pool, string modDirectory)
        {
            if (pool == null) return;
            // Reset latches on a clean re-init (post-Shutdown). _daemonInitComplete acts
            // as the idempotency guard.
            if (_daemonInitComplete) return;

            _modDirectory = modDirectory;
            _killSwitch = false;
            Interlocked.Exchange(ref _shutdownRequested, 0);
            Interlocked.Exchange(ref _rollbackInProgress, 0);

            // (1) Touch DirectorState (cctor runs), load tuning, init journal.
            try { var _ = DirectorState.ActiveScenario; }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DirectorState touch threw: " + ex.Message);
            }
            try { DirectorTuning.Load(modDirectory); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DirectorTuning.Load threw: " + ex.Message);
            }
            try { DirectorJournal.Init(modDirectory); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DirectorJournal.Init threw: " + ex.Message);
            }
            try { DirectorTunables.Register(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DirectorTunables.Register threw: " + ex.Message);
            }
            try { DirectiveSurface.Init(modDirectory); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DirectiveSurface.Init threw: " + ex.Message);
            }
            // T3 checkpoint root: (1) directives/journal loaded above; (2)+(3) referenced-set
            // build + prune happen inside DirectorCheckpoint.Init; (4) daemon registers below.
            try { DirectorCheckpoint.Init(modDirectory); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DirectorCheckpoint.Init threw: " + ex.Message);
            }
            try { DigestBuilder.Init(modDirectory); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DigestBuilder.Init threw: " + ex.Message);
            }
            // Fresh ring on a clean (re-)init.
            try { DirectorParamRing.Clear(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DirectorParamRing.Clear threw: " + ex.Message);
            }
            // Disk-tier auto-checkpoint piggy-back on profile saves.
            if (_profileSavedHandler == null) _profileSavedHandler = OnProfileSaved;
            try { Learning.LearningService.ProfileSaved += _profileSavedHandler; }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: ProfileSaved subscribe threw: " + ex.Message);
            }
            Interlocked.Exchange(ref _lastHealthyRingCaptureMono, 0L);
            Interlocked.Exchange(ref _lastDiskAutoCheckpointMono, 0L);

            // (2) Now enqueue the long-running daemon AND register canonical factories.
            pool.EnqueueLongRunning(RunLoop, CanonicalName);
            var poolRef = pool;
            Func<string> factory = () => poolRef != null ? poolRef.EnqueueLongRunning(RunLoop, CanonicalName) : null;
            DaemonWatchdog.RegisterCanonical(CanonicalName, factory);
            WorkerHealthMonitor.RegisterCanonical(CanonicalName, factory);

            // (2b) Scenario planner + chunk regen daemons. They idle-wait until
            // ActiveScenario != 0 / ∈ {S2,S5}.
            try { Scenarios.ScenarioWorker.Init(pool); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: ScenarioWorker.Init threw: " + ex.Message);
            }
            try { Scenarios.ChunkRegenWorker.Init(pool); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: ChunkRegenWorker.Init threw: " + ex.Message);
            }
            try { DirectiveInboxWorker.Init(pool, modDirectory); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: DirectiveInboxWorker.Init threw: " + ex.Message);
            }
            // GuardWorker registers AFTER ScenarioWorker so its ctx reads the
            // ActiveScenario the planner mints first.
            try { Guards.BehaviorGuardWorker.Init(pool); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.Init: BehaviorGuardWorker.Init threw: " + ex.Message);
            }

            // (3) Latch — daemon may begin work.
            Interlocked.Exchange(ref _initMono, World.MonoClock.Now());
            _daemonInitComplete = true;

            DirectorJournal.Append("DIRECTOR-INIT",
                "modDirectory=" + (modDirectory ?? "<null>"));
        }

        /// <summary>
        /// L-004 patterned shutdown. Called from SmartRuntime.Shutdown between
        /// WorkerHealthMonitor.BeginShutdown() and TrainerBarrier.ClearAll(). NEVER
        /// uses Thread.Abort. Bound by 10 s wait for in-flight rollback.
        /// </summary>
        public static void BeginShutdown()
        {
            int prior = Interlocked.CompareExchange(ref _shutdownRequested, 1, 0);
            if (prior != 0) return; // loser
            _killSwitch = true;

            // Block until any in-flight digest Emit finishes its current tick.
            try { DigestBuilder.WaitForEmitLoopGate(2000); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: DigestBuilder gate wait threw: " + ex.Message);
            }

            // Drop the profile-save piggy-back subscription so a post-shutdown save doesn't
            // re-enter the checkpoint writer.
            if (_profileSavedHandler != null)
            {
                try { Learning.LearningService.ProfileSaved -= _profileSavedHandler; }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: ProfileSaved unsubscribe threw: " + ex.Message);
                }
            }

            // Halt scenario envelope emission immediately so stale envelopes don't drain
            // post-teardown.
            try { Scenarios.ScenarioWorker.BeginShutdown(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: ScenarioWorker.BeginShutdown threw: " + ex.Message);
            }
            try { Scenarios.ChunkRegenWorker.BeginShutdown(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: ChunkRegenWorker.BeginShutdown threw: " + ex.Message);
            }
            try { Guards.BehaviorGuardWorker.BeginShutdown(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: BehaviorGuardWorker.BeginShutdown threw: " + ex.Message);
            }
            try { DirectiveInboxWorker.BeginShutdown(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: DirectiveInboxWorker.BeginShutdown threw: " + ex.Message);
            }
            // Clear any in-flight hard-correct mailbox slots before queues drain.
            try { Guards.GuardCorrectiveActuator.ForceReleaseAll(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: GuardCorrectiveActuator.ForceReleaseAll threw: " + ex.Message);
            }
            // Reset executor CAS gates so a re-Init re-installs the bus subscriptions.
            try { Scenarios.SpawnEnvelopeExecutor.Reset(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: SpawnEnvelopeExecutor.Reset threw: " + ex.Message);
            }

            // Wait for any in-flight rollback to release its pause token. In Phase 1
            // _rollbackInProgress is always 0 — this loop returns immediately. The
            // surface must exist so Phase 7's rollback honours it.
            int waitedMs = 0;
            while (Volatile.Read(ref _rollbackInProgress) != 0 && waitedMs < ShutdownRollbackWaitMs)
            {
                Thread.Sleep(50);
                waitedMs += 50;
            }
            if (Volatile.Read(ref _rollbackInProgress) != 0)
            {
                DirectorJournal.Append("SHUTDOWN-FORCED-WHILE-ROLLBACK-INFLIGHT",
                    "waitedMs=" + waitedMs);
                DebugTAC_AI.LogWarnFileOnly("director-shutdown-forced",
                    "[DIRECTOR-SHUTDOWN] forced past in-flight rollback after " + waitedMs + "ms");
            }
            else
            {
                DirectorJournal.Append("DIRECTOR-SHUTDOWN", "clean");
            }

            // Stuck-token sweep: if some publisher acquired the token but never released,
            // give TrainerBarrier the chance to force-release. Phase 2 owns the watchdog;
            // here we just journal the leak.
            try { TrainerBarrier.TickPauseTokenWatchdog(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: TickPauseTokenWatchdog threw: " + ex.Message);
            }

            // Flush whatever is in the disk queue before the runtime tears down.
            try { DirectorJournal.DrainToDisk(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: DrainToDisk threw: " + ex.Message);
            }

            // Persist tuning so a restart picks up where we left off.
            try { DirectorTuning.Save(_modDirectory); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingDirector.BeginShutdown: DirectorTuning.Save threw: " + ex.Message);
            }

            _daemonInitComplete = false;
            Interlocked.Exchange(ref _initMono, 0L);
            ConstraintEngine.Reset();
            TrainerStats.ResetAll();
            DirectorParamRing.Clear();
            DigestBuilder.Reset();
            DirectiveSurface.Reset();
            DirectorJournal.Reset();
        }

        // §7.1 piggy-back: write tunables.json + director-config.json siblings + stamp
        // params.bin alongside the just-saved profile, at most once per 30 min. Host-only
        // (BUG-055 gates SaveProfile, so this only fires on the host). Skipped while paused
        // / Director-paused / mid-rollback so we never capture torn state.
        private static void OnProfileSaved()
        {
            if (!IsActive) return;
            if (SmartRuntime.IsPaused || !SmartRuntime.IsHost) return;
            if (SmartRuntime.IsDirectorPaused) return;
            if (Volatile.Read(ref _rollbackInProgress) != 0) return;

            long now = MonoClock.Now();
            long last = Interlocked.Read(ref _lastDiskAutoCheckpointMono);
            if (last != 0L && MonoClock.SecondsDouble(last, now) < AutoCheckpointMinIntervalSec) return;
            // Only auto-checkpoint to disk when the last 5 min were all-healthy.
            if (ConstraintEngine.SecondsAllHealthy() < AutoHealthyAllGreenSec) return;

            try
            {
                string dir = DirectorCheckpoint.Save("auto-healthy");
                if (dir != null) Interlocked.Exchange(ref _lastDiskAutoCheckpointMono, now);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-auto-checkpoint",
                    "[DIRECTOR-CHECKPOINT] auto-checkpoint threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Daemon body. Skeleton aggregation tick — Phase 2 fills in the ConstraintEngine
        /// call body and Phase 9 fills in the DigestBuilder + ActionPicker hooks.
        /// </summary>
        public static void RunLoop(CancellationToken cancellation)
        {
            // Wait for Init to finish populating singletons.
            while (!_daemonInitComplete && !_killSwitch)
            {
                if (cancellation.IsCancellationRequested) return;
                if (cancellation.WaitHandle.WaitOne(InitWaitPollMs)) return;
            }
            if (_killSwitch) return;

            int aggregationCount = 0;
            while (!cancellation.IsCancellationRequested)
            {
                if (_killSwitch) return;

                if (SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(AggregationTickMs)) return;
                    continue;
                }
                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(AggregationTickMs)) return;
                    continue;
                }

                try { AggregationTick(aggregationCount); }
                catch (OperationCanceledException) { return; }
                catch (ThreadAbortException)
                {
                    if (AbortGuard.Absorb(CanonicalName) == AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning(
                        "Smart.TrainingDirector: aggregation tick threw " + ex.GetType().Name + ": " + ex.Message);
                }

                if ((aggregationCount % ConstraintTickEveryNAggregations) == 0)
                {
                    try { ConstraintTick(); }
                    catch (OperationCanceledException) { return; }
                    catch (ThreadAbortException)
                    {
                        if (AbortGuard.Absorb(CanonicalName) == AbortGuard.AbortAction.ExitForRespawn) return;
                    }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarning(
                            "Smart.TrainingDirector: constraint tick threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                aggregationCount++;
                if (cancellation.WaitHandle.WaitOne(AggregationTickMs)) return;
            }
        }

        private static void AggregationTick(int aggregationCount)
        {
            // Drain journal disk queue every tick.
            DirectorJournal.DrainToDisk();

            // Wall-clock directive expiry sweep (for-duration only; until-expr is the
            // constraint pass's concern).
            DirectiveSurface.SweepExpired();

            // 1 Hz AllyProtected scanner (driven by the Director tick, not a daemon).
            Publishers.AllyProtectionPublisher.Tick();

            // Drive the digest cadence + coalesced out-of-band triggers. 600 s phase-aligned
            // periodic emission + trigger drain happen here on the 1 Hz tick.
            DigestBuilder.OnDirectorTick();

            // Stuck pause-token sweep.
            TrainerBarrier.TickPauseTokenWatchdog();

            // Drop expired (30-min) pre-action ring slots.
            DirectorParamRing.SweepExpiredPreAction();

            // AUTO-HEALTHY (§7.1 trigger 1): capture an in-memory healthy trio when all
            // constraints have been green for 5 min. Skipped while paused / Director-paused
            // / mid-rollback so the snapshot can't catch torn state.
            if (Volatile.Read(ref _rollbackInProgress) == 0
                && !SmartRuntime.IsDirectorPaused
                && ConstraintEngine.SecondsAllHealthy() >= AutoHealthyAllGreenSec)
            {
                long now = MonoClock.Now();
                long last = Interlocked.Read(ref _lastHealthyRingCaptureMono);
                // Re-capture at the constraint-tick cadence (~5 s) is wasteful; throttle the
                // healthy ring to once per minute so the 3 slots span ~3 min of green.
                if (last == 0L || MonoClock.Seconds(last, now) >= 60f)
                {
                    try
                    {
                        DirectorParamRing.CaptureHealthy();
                        Interlocked.Exchange(ref _lastHealthyRingCaptureMono, now);
                    }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarnFileOnly("director-healthy-capture",
                            "[PARAMRING] CaptureHealthy threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            // Heartbeat journal so we can see the daemon is alive even when no
            // constraint fires.
            if ((aggregationCount % HeartbeatJournalEveryNAggregations) == 0)
            {
                DirectorJournal.Append("DIRECTOR-HEARTBEAT",
                    "tick=" + aggregationCount
                    + " activeScenario=" + DirectorState.ActiveScenario
                    + " intensity=" + DirectorState.Intensity.ToString("F2",
                        System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private static void ConstraintTick()
        {
            // 0.2 Hz BaseHeld HP poll + window close.
            Publishers.BaseHeldPublisher.Tick();
            ConstraintEngine.EvaluateAll();
        }

        // Single-flight gate. RollbackAction CAS's _rollbackInProgress 0→1 to claim the
        // rollback; returns false if another rollback already holds it. The same field is
        // read by RollbackInProgress (watchdog + shutdown wait), so single-flight,
        // stuck-token watchdog and shutdown all agree on one source of truth.
        internal static bool TryEnterRollbackSingleFlight()
        {
            return Interlocked.CompareExchange(ref _rollbackInProgress, 1, 0) == 0;
        }

        internal static void ExitRollbackSingleFlight()
        {
            Interlocked.Exchange(ref _rollbackInProgress, 0);
        }

        // Active scenario id the picker hands to RollbackAction's scenario revert. The
        // rollback verb is dispatched from ConstraintEngine via ActionPicker.Dispatch.
        public static void DispatchRollbackVerb(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            ActionPicker.DispatchRollback(c, metric, deviation, severity, formattedBand);
        }

        // Operator scenario controls for the in-game DirectorPanel. Host + daemon-up gated;
        // the actual action + journaling lives in ActionPicker.
        public static bool OperatorSetScenario(int scenarioId, float intensity)
        {
            if (!IsActive) return false;
            return ActionPicker.OperatorSetScenario(scenarioId, intensity);
        }

        public static bool OperatorRespawnScenario()
        {
            if (!IsActive) return false;
            return ActionPicker.OperatorRespawnScenario();
        }

        public static bool OperatorStopScenario()
        {
            if (!IsActive) return false;
            return ActionPicker.OperatorStopScenario();
        }
    }

    /// <summary>
    /// Maps a violated Constraint row to one of the 6 verbs (lr_scale, temp_adjust,
    /// freeze_model, replay_bank, scenario_set, scenario_respawn) and invokes the
    /// corresponding IDirectorAction. rollback still journals ACTION-WOULD-HAVE-FIRED
    /// until that action lands. Severity = deviation/band-width x dependsOnPublisherWeight.
    /// </summary>
    public static class ActionPicker
    {
        // Stateless action singletons.
        private static readonly Actions.LrScaleAction _lrScale = new Actions.LrScaleAction();
        private static readonly Actions.TempAdjustAction _tempAdjust = new Actions.TempAdjustAction();
        private static readonly Actions.FreezeModelAction _freezeModel = new Actions.FreezeModelAction();
        private static readonly Actions.ReplayBankAction _replayBank = new Actions.ReplayBankAction();
        private static readonly Actions.ScenarioSetAction _scenarioSet = new Actions.ScenarioSetAction();
        private static readonly Actions.ScenarioRespawnAction _scenarioRespawn = new Actions.ScenarioRespawnAction();
        private static readonly Actions.RollbackAction _rollback = new Actions.RollbackAction();

        /// <summary>
        /// Constraint engine entry. Picks the verb from Constraint.PrimaryVerb and dispatches
        /// to the matching action with a DirectorActionContext computed from the constraint
        /// row + current metric. Unknown verbs (rollback / diagnostic) still journal
        /// ACTION-WOULD-HAVE-FIRED so telemetry stays observable until their actions exist.
        /// </summary>
        public static void Dispatch(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            string verb = c.PrimaryVerb ?? "diagnostic";
            switch (verb)
            {
                case "lr_scale":
                    DispatchLrScale(c, metric, deviation, severity, formattedBand);
                    break;
                case "temp_adjust":
                    DispatchTempAdjust(c, metric, deviation, severity, formattedBand);
                    break;
                case "freeze_model":
                    DispatchFreezeModel(c, metric, deviation, severity, formattedBand);
                    break;
                case "replay_bank":
                    DispatchReplayBank(c, metric, deviation, severity, formattedBand);
                    break;
                case "scenario_set":
                    DispatchScenarioSet(c, metric, deviation, severity, formattedBand);
                    break;
                case "scenario_respawn":
                    DispatchScenarioRespawn(c, metric, deviation, severity, formattedBand);
                    break;
                case "rollback":
                    DispatchRollback(c, metric, deviation, severity, formattedBand);
                    break;
                default:
                    JournalWouldHaveFired(c, verb, metric, deviation, severity, formattedBand);
                    break;
            }
        }

        // AUTO-PRE-ACTION (§7.1 trigger 2): capture an in-memory pre-action snapshot before
        // a destructive verb so a subsequent rollback can revert it. 30-min TTL inside the
        // ring. Best-effort — never blocks the action on a capture fault.
        private static void CapturePreActionFor(string verbLabel)
        {
            try { DirectorParamRing.CapturePreAction("pre-" + verbLabel); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-preaction-capture",
                    "[PARAMRING] pre-action capture for " + verbLabel + " threw "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void DispatchScenarioSet(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            CapturePreActionFor("scenario_set");
            // Picker default: S1 (open brawl) at intensity 1.0. Constraint row carries
            // no scenarioId today; S1 is the safe fallback brawl.
            int scenarioId = (int)Scenarios.ScenarioId.S1;
            float intensity = severity > 1.5f ? 1.25f : 1.0f;
            var ctx = new Actions.DirectorActionContext(c.Model, c.Identity, intensity, scenarioId, 0, c.Name);
            JournalEntry entry;
            if (!_scenarioSet.TryApply(ctx, out entry))
            {
                JournalSkipped(c, "scenario_set", metric, deviation, severity, formattedBand);
            }
        }

        private static void DispatchScenarioRespawn(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            var ctx = new Actions.DirectorActionContext(c.Model, c.Identity, 0f, 0, 0, c.Name);
            JournalEntry entry;
            if (!_scenarioRespawn.TryApply(ctx, out entry))
            {
                // Action body already journaled SKIPPED / RATE-LIMITED.
            }
        }

        // Operator entry points — driven by the in-game DirectorPanel. Same action +
        // journal path with constraint="operator" so the digest shows who asked. The
        // IsActive gate lives on the TrainingDirector wrapper; here we just apply.
        public static bool OperatorSetScenario(int scenarioId, float intensity)
        {
            CapturePreActionFor("scenario_set");
            var ctx = new Actions.DirectorActionContext(default(Learning.ModelId),
                default(Identity.SmartIdentity), intensity, scenarioId, 0, "operator");
            JournalEntry entry;
            return _scenarioSet.TryApply(ctx, out entry);
        }

        public static bool OperatorRespawnScenario()
        {
            var ctx = new Actions.DirectorActionContext(default(Learning.ModelId),
                default(Identity.SmartIdentity), 0f, 0, 0, "operator");
            JournalEntry entry;
            return _scenarioRespawn.TryApply(ctx, out entry);
        }

        public static bool OperatorStopScenario()
        {
            bool ok = Scenarios.ScenarioWorker.RequestStop();
            if (ok) DirectorJournal.Append("ACTION-SCENARIO-STOP", "by=operator");
            return ok;
        }

        private static void DispatchLrScale(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            // Factor pick: severity-proportional dampen below 1.0. Default plan factor 0.5
            // for variance-spike remediation; stronger violations push toward the floor.
            float factor = severity > 1.5f ? 0.25f : 0.5f;
            // Pre-action snapshot only for large LR moves (|log10(factor)| > 0.3).
            if (factor > 0f && Math.Abs(Math.Log10(factor)) > 0.3)
                CapturePreActionFor("lr_scale");
            var ctx = new Actions.DirectorActionContext(c.Model, c.Identity, factor, 0, 0, c.Name);
            JournalEntry entry;
            if (!_lrScale.TryApply(ctx, out entry))
            {
                JournalSkipped(c, "lr_scale", metric, deviation, severity, formattedBand);
            }
        }

        private static void DispatchTempAdjust(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            // Intent entropy band [0.30, 0.85]. Below band → entropy too low → raise tau
            // (+0.10 log-tau). Above band → raise entropy back down → lower tau (-0.10).
            float delta = metric < c.BandMin ? 0.10f : -0.10f;
            var ctx = new Actions.DirectorActionContext(c.Model, c.Identity, delta, 0, 0, c.Name);
            JournalEntry entry;
            if (!_tempAdjust.TryApply(ctx, out entry))
            {
                JournalSkipped(c, "temp_adjust", metric, deviation, severity, formattedBand);
            }
        }

        private static void DispatchFreezeModel(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            // Diagnostic mode at ship; soft mode wires once ttl_discard control needs it.
            int durationSec = 600;
            byte mode = Actions.FreezeModelAction.ModeDiagnostic;
            // Soft freeze discards queued events — capture a pre-action snapshot first.
            if (mode == Actions.FreezeModelAction.ModeSoft) CapturePreActionFor("freeze_model");
            var ctx = new Actions.DirectorActionContext(c.Model, c.Identity, 0f, durationSec,
                mode, c.Name);
            JournalEntry entry;
            if (!_freezeModel.TryApply(ctx, out entry))
            {
                JournalSkipped(c, "freeze_model", metric, deviation, severity, formattedBand);
            }
        }

        private static void DispatchReplayBank(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            int count = 128;
            // BankFullness + minReplay gating lives inside ReplayBankAction so each
            // constraint type uniformly journals SKIPPED-INSUFFICIENT when cold.
            var ctx = new Actions.DirectorActionContext(c.Model, c.Identity, 0f, count, 0, c.Name);
            JournalEntry entry;
            if (!_replayBank.TryApply(ctx, out entry))
            {
                // ReplayBankAction already journaled SKIPPED-INSUFFICIENT / SKIPPED itself.
            }
        }

        internal static void DispatchRollback(Constraint c, float metric, float deviation, float severity, string formattedBand)
        {
            // Auto-fired rollback targets the most recent healthy in-memory snapshot
            // (memory tier — fastest revert, no implicit pre-rollback disk checkpoint).
            var ctx = new Actions.DirectorActionContext(c.Model, c.Identity, 0f, 0,
                Actions.RollbackAction.TierMemory, c.Name);
            JournalEntry entry;
            if (_rollback.TryApply(ctx, out entry))
            {
                // Force-emit the post-rollback digest — bypasses IsPaused for the rollback trigger.
                try { DigestBuilder.ForceEmit("rollback"); }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("director-rollback-digest",
                        "[DIRECTOR-DIGEST] post-rollback emit threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            // else RollbackAction body already journaled REJECTED / RATE-LIMITED / ABORTED.
        }

        private static void JournalWouldHaveFired(Constraint c, string verb, float metric, float deviation, float severity, string formattedBand)
        {
            DirectorJournal.Append("ACTION-WOULD-HAVE-FIRED",
                "verb=" + verb
                + " constraint=" + c.Name
                + " metric=" + metric.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " band=" + formattedBand
                + " deviation=" + deviation.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " severity=" + severity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void JournalSkipped(Constraint c, string verb, float metric, float deviation, float severity, string formattedBand)
        {
            DirectorJournal.Append("ACTION-SKIPPED",
                "verb=" + verb
                + " constraint=" + c.Name
                + " metric=" + metric.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " band=" + formattedBand
                + " deviation=" + deviation.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " severity=" + severity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
