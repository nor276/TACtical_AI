using System;
using System.IO;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning.Features;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// Smart's process-wide learning orchestrator. Per LEARNING-CONTRACT §9.
    ///
    /// Owns:
    /// - The four model instances (Intent, ActionValue, Residual, Threat).
    /// - Per-model trainer workers running on the shared WorkerPool.
    /// - WorldEventBus subscriptions that translate events → training events.
    /// - The per-player profile load/save lifecycle.
    ///
    /// v0.1.0 player-ID handling: a single fixed "local" profile is used since OQ-9
    /// (player join/leave events) is not yet verified. When verified, the per-player
    /// cache + Harmony patches per §7.2 plug in here.
    ///
    /// Event-handler completeness: the threat model's <see cref="World.DamageObserved"/>
    /// path produces real training events end-to-end. ActionValue events are produced
    /// at the ContinuousController decision boundary (§7.2 + R2-06 — one-tick deferred
    /// (s, a, r, s') tuples enqueued into <c>ActionValueEstimator.EventQueue</c> after
    /// goal arbitration). Intent / Residual producers are wired through the observation-
    /// sequence buffer and LeadResidualRecorder respectively.
    /// </summary>
    public static class LearningService
    {
        public const string DefaultPlayerId = "local";

        /// <summary>
        /// P7 Item 20 (REV 7): delegate to <see cref="PlayerIdResolver"/>. Tries reflection
        /// against the candidate Steam-ID field names on <c>ManNetwork.inst.MyPlayer</c>;
        /// falls back to <see cref="DefaultPlayerId"/> on every failure path. SP and MP
        /// both safe — null player / missing field / null SteamID all degrade to the
        /// default ID (preserves v0.1 single-profile-per-install semantics).
        /// </summary>
        private static string ResolvePlayerId()
        {
            return PlayerIdResolver.Resolve();
        }

        public static OpponentIntentClassifier Intent { get; private set; }
        public static ActionValueEstimator ActionValue { get; private set; }
        public static TrajectoryResidualModel Residual { get; private set; }
        public static ThreatAssessmentModel Threat { get; private set; }

        // P7 sidecars (Item 16/18/21). Created in Init, cleared in Shutdown. Lifecycle
        // mirrors the model fields above.
        public static TargetObservationSequenceBuffer ObservationSequence { get; private set; }   // Item 16
        public static IdentityOutcomeConsumer IdentityOutcomes { get; private set; }              // Item 21

        private static int _running;
        private static string _profileDir;
        private static string _currentPlayerId = DefaultPlayerId;
        // Phase 4 (FIX-PLAN.md): modifiedSinceLoad was a plain bool read on shutdown and
        // written from OnDamageObserved on a different thread. R1 §3.9 + R2 1.R2-N. Now
        // an int accessed via Interlocked.Exchange/CompareExchange so the shutdown read
        // sees writes from any thread.
        private static int _modifiedSinceLoad;
        /// <summary>L-060: read-only dirty bit for AutosaveWorker.</summary>
        public static bool HasUnsavedTraining => Volatile.Read(ref _modifiedSinceLoad) == 1;
        private static Action<DamageObserved> _damageHandler;

        public static bool IsRunning => Volatile.Read(ref _running) == 1;
        public static string CurrentPlayerId => _currentPlayerId;

        /// <summary>
        /// Fired after AutosaveWorker.TickOnce successfully saves the profile (host-only via
        /// BUG-055 SaveProfile gate). The Director piggy-backs its checkpoint sibling JSON
        /// writes here so the disk-tier auto-checkpoint shares the profile save's I/O.
        /// </summary>
        public static event Action ProfileSaved;

        /// <summary>Raised by AutosaveWorker after a successful save. Swallows handler faults.</summary>
        public static void RaiseProfileSaved()
        {
            var h = ProfileSaved;
            if (h == null) return;
            try { h(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("learning-profilesaved-handler",
                    "[LEARNING] ProfileSaved handler threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Start the learning subsystem. Idempotent.</summary>
        public static void Init(WorkerPool pool, string modDirectory)
        {
            if (pool == null) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

            _profileDir = Path.Combine(modDirectory ?? ".", "SmartAI", "Profiles");
            // Phase 5: resolve a stable per-player identifier rather than hardcoding "local".
            _currentPlayerId = ResolvePlayerId();
            Intent = new OpponentIntentClassifier();
            ActionValue = new ActionValueEstimator();
            Residual = new TrajectoryResidualModel();
            Threat = new ThreatAssessmentModel();

            // L-080: round-trip-fixture self-test BEFORE first LoadProfile. On failure,
            // we log [PROFILE-SELFTEST-FAIL] and refuse to load the player's profile (Glorot
            // weights retained). A deserializer regression that would corrupt the player's
            // disk image now produces no save instead of silent corruption.
            bool selfTestPassed = true;
            try { ProfileSelfTest.Run(modDirectory, new ILearnedModel[] { Intent, ActionValue, Residual, Threat }); }
            catch (Exception ex)
            {
                selfTestPassed = false;
                DebugTAC_AI.LogError("[PROFILE-SELFTEST-FAIL] " + ex.GetType().Name + ": " + ex.Message);
            }
            if (selfTestPassed)
            {
                LoadProfile(_currentPlayerId);
            }
            else
            {
                DebugTAC_AI.LogWarning("[PROFILE-SELFTEST-FAIL] LoadProfile suppressed; Glorot-init weights retained for player=" + _currentPlayerId);
            }

            // Spin up one trainer worker per model. EnqueueLongRunning spawns dedicated
            // threads so trainer workers (which block their thread forever in tight loops)
            // cannot consume worker-pool slots needed for short-task RunMPCWork dispatches.
            // L-066: enqueue trainers AND register their respawn factories with both
            // DaemonWatchdog (canonical roster) and WorkerHealthMonitor (live count). The
            // factories capture the TrainerWorker instance so a respawn re-enters the same
            // RunLoop without losing model identity.
            void EnqueueWithFactory(string name, ILearnedModel model)
            {
                var worker = new TrainerWorker(model);
                pool.EnqueueLongRunning(worker.RunLoop, name);
                Func<string> factory = () => pool.EnqueueLongRunning(new TrainerWorker(model).RunLoop, name);
                TAC_AI.AI.Forms.Smart.Threading.DaemonWatchdog.RegisterCanonical(name, factory);
                TAC_AI.AI.Forms.Smart.Threading.WorkerHealthMonitor.RegisterCanonical(name, factory);
            }
            EnqueueWithFactory("Trainer-Intent", Intent);
            EnqueueWithFactory("Trainer-ActionValue", ActionValue);
            EnqueueWithFactory("Trainer-Residual", Residual);
            EnqueueWithFactory("Trainer-Threat", Threat);

            // Director lifecycle plug — fires AFTER the 4 trainers are enqueued so
            // the canonical roster watchdog sees the daemon name immediately.
            try { TAC_AI.AI.Forms.Smart.Director.TrainingDirector.Init(pool, modDirectory); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("LearningService.Init: TrainingDirector.Init threw: " + ex.Message);
            }

            // Hand the identity lookup to IdentityReplayBank so tee sites can
            // stamp the source-tech's SmartIdentity without walking teams themselves.
            TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.SetIdentityLookup(
                id => SmartRuntime.TryGetSmartIdentity(id));

            // L-060: AutosaveWorker — 30s dirty-only autosave.
            AutosaveWorker.Reset();
            AutosaveWorker.Enqueue(pool);

            // Event subscriptions. P7 wires three new substantive consumers:
            // Item 16 ObservationSequence buffer (per-tech rolling features for Intent),
            // Item 18 LeadResidualRecorder (per-target fire/observe matching for Residual),
            // Item 21 IdentityOutcomeConsumer (per-identity outcome counters).
            // Generic-classified techs now appear in IdentityOutcome routing per P6 Item 27.
            ObservationSequence = new TargetObservationSequenceBuffer();
            LeadResidualRecorder.Install(new LeadResidualRecorder());
            // L-028: wire the 2 learning-side sidecars into TechLifecycleRegistry so the
            // Wave-2 L-055 Deregister refactor + L-056 TechLeakWatchdog cover them.
            TAC_AI.AI.Forms.Smart.World.TechLifecycleRegistry.Register(ObservationSequence);
            TAC_AI.AI.Forms.Smart.World.TechLifecycleRegistry.Register(LeadResidualRecorder.Instance);
            IdentityOutcomes = new IdentityOutcomeConsumer();
            IdentityOutcomeConsumer.Install(IdentityOutcomes);
            // Hand the consumer reference to the ConstraintEngine so 0.2 Hz constraint
            // evaluations can query GetRatePerMin / GetWeightedRatePerMin.
            TAC_AI.AI.Forms.Smart.Director.ConstraintEngine.SetOutcomeConsumer(IdentityOutcomes);

            _damageHandler = OnDamageObserved;
            WorldEventBus.Subscribe(_damageHandler);

            // L-070: subscribe to HostChanged so we can checkpoint on host-loss + reload on
            // host-gain. The handler keeps a reference so Shutdown's unsubscribe finds it.
            _hostChangedHandler = OnHostChanged;
            WorldEventBus.Subscribe(_hostChangedHandler);
        }

        private static System.Action<HostChanged> _hostChangedHandler;
        private static long _profileLoadedAtMs;

        private static void OnHostChanged(HostChanged ev)
        {
            try
            {
                if (!ev.IsHost) OnHostLost(ev);
                else OnHostGained(ev);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("LearningService.OnHostChanged: " + ev.PhaseSource + " " + ex.Message);
            }
        }

        /// <summary>
        /// L-070: IsHost went true→false. Wait for every trainer to reach Paused (500ms
        /// budget), then save the profile so a subsequent host-takeover by another client
        /// has a fresh disk image to load from.
        /// </summary>
        private static void OnHostLost(HostChanged ev)
        {
            const int BarrierWaitMs = 500;
            // L-059: ref-counted Inc so a concurrent rollback's Dec/Inc pair doesn't
            // race a clobber-write.
            SmartRuntime.AcceptingTrainingEvents_Inc();

            bool allParked = TAC_AI.AI.Forms.Smart.Coordination.TrainerBarrier.WaitAll(BarrierWaitMs);
            if (!allParked)
            {
                DebugTAC_AI.LogWarnFileOnly("learning-hostchange-pause-timeout",
                    "[LEARNING-HOSTCHANGE] OnHostLost barrier timeout — saving with possibly-stale params");
            }
            try
            {
                // L-070 / BUG-055: SmartRuntime.IsHost is already false by the time HostChanged
                // fires (SmartForm.Operations writes it before notifying). The host-loss
                // checkpoint MUST still write so a takeover client has fresh weights, so we
                // bypass the host gate here by routing through SaveProfileInternal directly.
                if (IsRunning) SaveProfileInternal(_currentPlayerId);
                DebugTAC_AI.LogWarnFileOnly("learning-hostchange-lost",
                    "[LEARNING-HOSTCHANGE] OnHostLost player=" + _currentPlayerId
                    + " barrierParked=" + allParked + " phase=" + ev.PhaseSource);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("LearningService.OnHostLost.Save: " + ex.Message);
            }
        }

        /// <summary>
        /// L-070: IsHost went false→true. Reload disk profile IFF it's newer than what
        /// we loaded at Init; for each model, wrap LoadParameters in try/catch to handle
        /// architecture-mismatch (host wrote v=N+1 while we're at v=N). Re-arm trainer
        /// barriers + flip AcceptingTrainingEvents back on.
        /// </summary>
        private static void OnHostGained(HostChanged ev)
        {
            try
            {
                if (IsRunning)
                {
                    string path = ProfilePath(_currentPlayerId);
                    long diskMs = System.IO.File.Exists(path)
                        ? new System.IO.FileInfo(path).LastWriteTimeUtc.Ticks / System.TimeSpan.TicksPerMillisecond
                        : 0L;
                    if (diskMs > _profileLoadedAtMs)
                    {
                        // BUG-075: include the baseline tier here too — if the host's
                        // disk image was corrupted between Init and host-handover, the
                        // baseline file is the last-ditch recovery.
                        var profile = ProfilePersistence.Load(path, baselineBytes: TryReadBaselineBytes());
                        if (profile != null)
                        {
                            // Per-model load: catch arch-mismatch + keep in-memory params.
                            ApplyProfileSafe(profile);
                            _profileLoadedAtMs = diskMs;
                            // BUG-023: refresh the UnknownTag preservation buffer with the
                            // newest disk image so the next Save re-emits forward-compat
                            // fields the takeover host's profile carried.
                            lock (_saveSyncRoot) _lastLoadedProfile = profile;
                        }
                    }
                }
                // L-058: re-arm trainer barriers for next OnHostLost cycle.
                TAC_AI.AI.Forms.Smart.Coordination.TrainerBarrier.Reset();
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("LearningService.OnHostGained: " + ex.Message);
            }
            finally
            {
                // L-059: pair the Inc from OnHostLost. Effective flag is
                // (disableCount == 0 && IsHost).
                SmartRuntime.AcceptingTrainingEvents_Dec();
                DebugTAC_AI.LogWarnFileOnly("learning-hostchange-gained",
                    "[LEARNING-HOSTCHANGE] OnHostGained player=" + _currentPlayerId
                    + " phase=" + ev.PhaseSource);
            }
        }

        // L-070 / BUG-074: per-model load with arch-mismatch guard. ApplyProfile's
        // per-section catch covers exceptions in the iteration loop, but the documented
        // "wrap LoadParameters in try/catch" contract requires PER-MODEL recovery so
        // partial-tolerance survives even if one model's blob is the wrong arch. This
        // path drives the load through the per-model Try helper so arch-mismatch on
        // any single model leaves the others' weights honored.
        private static void ApplyProfileSafe(LoadedProfile profile)
        {
            if (profile == null || profile.Sections == null) return;
            void Try(string name, ILearnedModel model, LoadedProfile.Section section)
            {
                if (model == null || section == null || section.Weights == null) return;
                // BUG-024: explicit on-disk vs live ArchitectureVersion check at the
                // per-model entry so a same-ParameterCount arch bump doesn't silently
                // load old weights into new arch. LoadParameters-only catches length
                // changes, not permutation/re-weighting.
                if (section.ArchitectureVersion != model.ArchitectureVersion)
                {
                    DebugTAC_AI.LogWarnFileOnly("learning-hostchange-archver-" + name,
                        "[LEARNING-HOSTCHANGE] reload-skip model=" + name
                        + " reason=arch-version-mismatch (disk=" + section.ArchitectureVersion
                        + " live=" + model.ArchitectureVersion + "); keeping in-memory params");
                    return;
                }
                try
                {
                    // BUG-047: serialise per-model load against in-flight TrainOneMinibatch
                    // (TrainerWorker locks SaveMutex around Adam.Step) so Array.Copy doesn't
                    // race element-wise writes during host promotion.
                    // BUG-046: restore Adam state under the same lock so the resumed model
                    // doesn't run a Glorot-fresh M/V/T against trained weights.
                    lock (model.SaveMutex)
                    {
                        model.LoadParameters(section.Weights);
                        if (section.AdamStatePayload != null)
                            ProfilePersistence.ApplyAdamStatePayload(model, section.AdamStatePayload);
                    }
                }
                catch (ArgumentException)
                {
                    DebugTAC_AI.LogWarnFileOnly("learning-hostchange-archmismatch-" + name,
                        "[LEARNING-HOSTCHANGE] reload-skip model=" + name
                        + " reason=arch-mismatch (param-count differs); keeping in-memory params");
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("LearningService.ApplyProfileSafe " + name + ": " + ex.Message);
                }
            }
            for (int i = 0; i < profile.Sections.Length; i++)
            {
                var s = profile.Sections[i];
                if (s == null) continue;
                switch (s.Id)
                {
                    case ModelId.Intent: Try("Intent", Intent, s); break;
                    case ModelId.ActionValue: Try("ActionValue", ActionValue, s); break;
                    case ModelId.TrajectoryResidual: Try("Residual", Residual, s); break;
                    case ModelId.ThreatAssessment: Try("Threat", Threat, s); break;
                }
            }
        }

        /// <summary>
        /// Save the current profile if modified, then mark stopped. Worker cancellation
        /// flows through <see cref="WorkerLifecycleRegistry"/>.
        /// </summary>
        public static void Shutdown()
        {
            if (Interlocked.Exchange(ref _running, 0) == 0) return;
            if (_damageHandler != null)
            {
                WorldEventBus.Unsubscribe(_damageHandler);
                _damageHandler = null;
            }
            // L-070: unsubscribe HostChanged so re-Init in same process doesn't double-fire.
            if (_hostChangedHandler != null)
            {
                try { WorldEventBus.Unsubscribe(_hostChangedHandler); } catch { }
                _hostChangedHandler = null;
            }
            try
            {
                // Phase 4 (FIX-PLAN.md) — Plan 8 insight #2: previous code read `_params`
                // directly (a torn-snapshot race with the trainer workers). Now Shutdown is
                // called AFTER CancelAllAndJoin per SmartRuntime.Shutdown ordering, so
                // workers are guaranteed quiesced. SaveProfile reads via StoreParameters
                // which copies the post-step params; this is now race-free.
                if (Interlocked.Exchange(ref _modifiedSinceLoad, 0) == 1)
                    SaveProfile(_currentPlayerId);
            }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Learning.Shutdown: save failed: " + ex.Message); }

            // Drop the per-cell replay queues before the models are nulled so
            // dangling envelopes don't outlive their target trainers.
            TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.SetIdentityLookup(null);
            TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.Reset();

            // P7 sidecars: tear down before nulling the models (LeadResidualRecorder
            // enqueues into Residual.EventQueue, so the recorder must stop accepting
            // new commits before Residual is nulled). Unsubscribe IdentityOutcomeConsumer
            // and uninstall the singleton recorder.
            IdentityOutcomeConsumer.Uninstall();
            IdentityOutcomes?.Clear();
            IdentityOutcomes = null;
            LeadResidualRecorder.Instance?.Clear();
            LeadResidualRecorder.Uninstall();
            ObservationSequence?.Clear();
            ObservationSequence = null;

            // Phase 4 (FIX-PLAN.md) — R2 2.R2.E: null the model fields so a subsequent
            // Init creates fresh instances and the old DoubleBuffer / BoundedQueue /
            // float[] arrays are GC'd. Previously these leaked across every Init/Shutdown
            // cycle (~360 KB per cycle for the intent classifier alone).
            Intent = null;
            ActionValue = null;
            Residual = null;
            Threat = null;
            _profileDir = null;
        }

        // ---- Profile load / save ----

        private static string ProfilePath(string playerId)
        {
            string safe = Sanitize(playerId);
            return Path.Combine(_profileDir, safe + ".bin");
        }

        /// <summary>
        /// Director rollback disk-tier source — the current player's live profile path.
        /// Null when the profile dir isn't initialised yet.
        /// </summary>
        public static string RollbackProfilePath()
        {
            if (string.IsNullOrEmpty(_profileDir)) return null;
            return ProfilePath(_currentPlayerId);
        }

        // BUG-023: cache the LoadedProfile so Save can re-emit any UnknownTags the disk
        // contained (forward-compat TLV slots a newer client wrote that we don't know
        // about). _saveSyncRoot guards reads/writes of this field across Load/Save.
        private static LoadedProfile _lastLoadedProfile;
        private static readonly object _saveSyncRoot = new object();

        // BUG-075: convention baseline file location. Sibling of the per-player profile
        // dir; PretrainingPipeline.Run writes the cross-player baseline here so the load
        // chain's fourth tier (embedded baseline) actually has bytes to fall through to
        // when current + .previous + .penultimate are all corrupt.
        private static string BaselinePath()
        {
            if (string.IsNullOrEmpty(_profileDir)) return null;
            return Path.Combine(_profileDir, "baseline.bin");
        }

        private static byte[] TryReadBaselineBytes()
        {
            try
            {
                var bp = BaselinePath();
                if (!string.IsNullOrEmpty(bp) && File.Exists(bp))
                    return File.ReadAllBytes(bp);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.Baseline: read failed: " + ex.Message);
            }
            return null;
        }

        private static void LoadProfile(string playerId)
        {
            try
            {
                string path = ProfilePath(playerId);
                // BUG-075: surface the on-disk baseline file (if any) as the fourth-tier
                // fallback. Pre-fix every caller passed null, making the documented
                // four-tier chain functionally three-tier.
                var profile = ProfilePersistence.Load(path, baselineBytes: TryReadBaselineBytes());
                if (profile == null)
                {
                    // L-015: cold start (no primary, no .previous, no .penultimate, no
                    // baseline). Distinct tag so [PROFILE-LOAD-FAIL] is reserved for
                    // genuine fallback (one or more tiers were corrupt).
                    DebugTAC_AI.Log("[PROFILE-COLD-START] player=" + playerId + " — Glorot init");
                    Interlocked.Exchange(ref _modifiedSinceLoad, 0);
                    lock (_saveSyncRoot) _lastLoadedProfile = null;
                    return;
                }
                // L-015: tier-aware log. Primary success = quiet; anything else = a
                // [PROFILE-LOAD-FAIL] warning naming the recovered tier so the operator
                // sees that the primary was unrecoverable.
                var tier = ProfilePersistence.LastLoadTier;
                if (tier != ProfilePersistence.LoadTier.Primary)
                {
                    DebugTAC_AI.LogWarnFileOnly("profile-load-fail-" + playerId,
                        "[PROFILE-LOAD-FAIL] player=" + playerId + " tier=" + tier
                        + " (primary was missing or corrupt; recovered from named tier)");
                }
                ApplyProfile(profile);
                DebugTAC_AI.Log("Smart.Learning: loaded profile for " + playerId
                                + " (schema=" + profile.SchemaVersion
                                + ", fromBaseline=" + profile.FromBaseline
                                + ", tier=" + tier + ").");
                Interlocked.Exchange(ref _modifiedSinceLoad, 0);
                // BUG-023: stash the in-memory profile for UnknownTag re-emission on Save.
                lock (_saveSyncRoot) _lastLoadedProfile = profile;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.Load: " + ex.Message);
            }
        }

        // P11 T2 Items 50 + 52: periodic-tick state. Two cadences:
        //   - DrainObservationsIntervalMs: drain TargetObservationSequenceBuffer rings into
        //     OpponentIntentClassifier.EventQueue at ~1 Hz so the BPTT trainer actually
        //     receives labeled (sequence, label) tuples from the heuristic labeler.
        //   - OutcomesLogIntervalMs: emit an [OUTCOMES] file-only log line summarizing the
        //     top per-(identity, kind) IdentityOutcome counters once per 60s.
        private const int DrainObservationsIntervalMs = 1000;
        private const int OutcomesLogIntervalMs = 60000;
        private static int _lastDrainMs = int.MinValue;
        private static int _lastOutcomesLogMs = int.MinValue;

        /// <summary>
        /// P11 T2 Items 50 + 52: main-thread periodic tick. Called from
        /// <c>SmartForm.Operations</c> each frame; internal interval guards keep the work
        /// to two cheap cadences (1 Hz observation drain, 1/60 Hz outcomes log). No-op when
        /// <see cref="IsRunning"/> is false.
        /// </summary>
        public static void PeriodicTick(UnityEngine.Vector3 ownAnchorWorld)
        {
            if (!IsRunning) return;
            int now = Environment.TickCount;

            int lastDrain = Volatile.Read(ref _lastDrainMs);
            if (lastDrain == int.MinValue || unchecked(now - lastDrain) >= DrainObservationsIntervalMs)
            {
                if (Interlocked.CompareExchange(ref _lastDrainMs, now, lastDrain) == lastDrain)
                {
                    try
                    {
                        var obs = ObservationSequence;
                        var classifier = Intent;
                        if (obs != null && classifier != null)
                        {
                            int n = obs.DrainAndEnqueue(classifier.EventQueue, ownAnchorWorld);
                            if (n > 0)
                                DebugTAC_AI.LogWarnFileOnly("learning-drain",
                                    "[LEARNING-DRAIN] enqueued " + n + " IntentEvents (anchor="
                                    + ownAnchorWorld.ToString("F0") + ")");
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarnFileOnly("learning-drain-err",
                            "PeriodicTick.DrainObservations threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            int lastOutcomes = Volatile.Read(ref _lastOutcomesLogMs);
            if (lastOutcomes == int.MinValue || unchecked(now - lastOutcomes) >= OutcomesLogIntervalMs)
            {
                if (Interlocked.CompareExchange(ref _lastOutcomesLogMs, now, lastOutcomes) == lastOutcomes)
                {
                    try { EmitOutcomesLogLine(); }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarnFileOnly("learning-outcomes-err",
                            "PeriodicTick.EmitOutcomes threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// P11 T2 Item 52: emit one [OUTCOMES] file-only log line per period. Iterates the 8
        /// known SmartIdentity × 3 OutcomeKind grid; only logs cells with non-zero counters.
        /// Public so the new <c>smart.outcomes.dump</c> console command can call it on-demand.
        /// </summary>
        public static void EmitOutcomesLogLine()
        {
            var outcomes = IdentityOutcomes;
            if (outcomes == null) return;
            var sb = new System.Text.StringBuilder(128);
            sb.Append("[OUTCOMES] ");
            int rows = 0;
            foreach (Identity.SmartIdentity ident in Enum.GetValues(typeof(Identity.SmartIdentity)))
            {
                foreach (OutcomeKind kind in Enum.GetValues(typeof(OutcomeKind)))
                {
                    long c = outcomes.GetCount(ident, kind);
                    if (c <= 0) continue;
                    if (rows > 0) sb.Append(' ');
                    sb.Append(ident).Append(':').Append(kind).Append('=').Append(c);
                    rows++;
                }
            }
            if (rows == 0) sb.Append("(no outcomes recorded yet)");
            DebugTAC_AI.LogWarnFileOnly("learning-outcomes", sb.ToString());
        }

        /// <summary>
        /// BUG-055 (root): host gate at the entry — clients never own training state, so
        /// they must never overwrite the per-player profile with model state that received
        /// no training events. Closes BUG-018 (QuitSaveCoordinator), BUG-019 (SmartForm
        /// OnEngineSave), BUG-078 (AutosaveWorker host-loss race), and BUG-054
        /// (SnapshotManager client-restore overwrites) in one place. The internal entry
        /// (<see cref="SaveProfileInternal"/>) bypasses this gate for the OnHostLost
        /// checkpoint, which fires AFTER SmartRuntime.IsHost has already flipped false.
        /// </summary>
        public static void SaveProfile(string playerId)
        {
            if (!SmartRuntime.IsHost)
            {
                DebugTAC_AI.LogWarnFileOnly("profile-save-nonhost",
                    "[PROFILE-SAVE-SKIP] non-host — refusing to overwrite profile for player=" + playerId);
                return;
            }
            SaveProfileInternal(playerId);
        }

        // BUG-055 bypass entry — only OnHostLost should call this directly. Shutdown's
        // last-save also routes through the public gate so a non-host process doesn't
        // poison disk on quit (training never accumulates locally on clients).
        private static void SaveProfileInternal(string playerId)
        {
            // BUG-022 + BUG-023 + BUG-076: serialise the entire Save against concurrent
            // Load calls via _saveSyncRoot, and re-emit UnknownTags from the most-recent
            // load so forward-compat TLV slots survive a save-after-load roundtrip.
            lock (_saveSyncRoot)
            {
                try
                {
                    string path = ProfilePath(playerId);
                    var models = new ILearnedModel[] { Intent, ActionValue, Residual, Threat };
                    ProfilePersistence.Save(path, models, _lastLoadedProfile);
                    Interlocked.Exchange(ref _modifiedSinceLoad, 0);
                    DebugTAC_AI.Log("Smart.Learning: saved profile for " + playerId + " → " + path);
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Learning.Save: " + ex.Message);
                }
            }
        }

        private static void ApplyProfile(LoadedProfile profile)
        {
            for (int i = 0; i < profile.Sections.Length; i++)
            {
                var section = profile.Sections[i];
                if (section == null) continue;
                try
                {
                    // BUG-024: arch-version guard at ApplyProfile entry. LoadParameters only
                    // catches ParameterCount mismatch; a same-count arch bump (permutation /
                    // re-weighting) would silently load stale weights into new arch.
                    ILearnedModel target = null;
                    switch (section.Id)
                    {
                        case ModelId.Intent: target = Intent; break;
                        case ModelId.ActionValue: target = ActionValue; break;
                        case ModelId.TrajectoryResidual: target = Residual; break;
                        case ModelId.ThreatAssessment: target = Threat; break;
                    }
                    if (target == null) continue;
                    if (section.ArchitectureVersion != target.ArchitectureVersion)
                    {
                        DebugTAC_AI.LogWarnFileOnly("profile-arch-version-skip-" + section.Id,
                            "[PROFILE-ARCH-VERSION-SKIP] model=" + section.Id
                            + " disk=" + section.ArchitectureVersion
                            + " live=" + target.ArchitectureVersion
                            + " — keeping Glorot/in-memory params");
                        continue;
                    }
                    // BUG-047: lock SaveMutex so LoadParameters' Array.Copy doesn't race
                    // against an in-flight TrainerWorker Adam.Step (which the worker takes
                    // under the same mutex). At Init this is uncontended; at OnHostGained
                    // the workers are live and concurrently writing to _params.
                    // BUG-046: also restore Adam state under the same lock so the model
                    // resumes optimization at the saved T/M/V instead of re-pinging Adam
                    // bias-correction (which would multiply effective LR ~10× on the first
                    // post-load step against trained weights).
                    lock (target.SaveMutex)
                    {
                        target.LoadParameters(section.Weights);
                        if (section.AdamStatePayload != null)
                            ProfilePersistence.ApplyAdamStatePayload(target, section.AdamStatePayload);
                    }
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.Learning.Apply[" + section.Id + "]: " + ex.Message);
                }
            }
        }

        private static string Sanitize(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return DefaultPlayerId;
            var chars = playerId.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok) chars[i] = '_';
            }
            return new string(chars);
        }

        // ---- Event handlers ----

        /// <summary>
        /// DamageObserved → ThreatAssessment training event. Builds the 48-slot
        /// ThreatEvent features per FEATURE-EXPANSION-PLAN §3.5 (ThreatSlots constants
        /// in StrategicStateVector.cs) and enqueues for the Threat trainer. Runs on
        /// the main thread (SmartEventBridge.OnTankDamage dispatches WorldEventBus
        /// inline), so per-tech SelfStateProbe snapshots are fresh and the F-32
        /// opaque-attacker pattern is observed for non-Smart-team attackers (composition
        /// + weapon + energy + cargo zeroed when no probe exists).
        /// </summary>
        private static void OnDamageObserved(DamageObserved evt)
        {
            try
            {
                if (Threat == null) return;
                // L-059: producer gate. During HostChanged(IsHost=false) drain the
                // coordinator flips this off; trainer workers transition through Paused
                // and the queue would otherwise accumulate events for a host that no
                // longer trains. Edge-coalesced log so a non-host session doesn't spam.
                if (!SmartRuntime.AcceptingTrainingEvents)
                {
                    DebugTAC_AI.LogWarnFileOnly("trainer-dropped-nonhost",
                        "[TRAINER-DROPPED-NONHOST] LearningService.OnDamageObserved dropping events while !AcceptingTrainingEvents");
                    return;
                }

                float[] features = BuildThreatEventFeatures(evt);
                float observedThreat = Mathf.Clamp01(evt.Damage.Damage / 100f);
                var threatEvent = new ThreatEvent(evt.Id, features, observedThreat);
                Threat.EventQueue.Enqueue(threatEvent);
                // Tee into IdentityReplayBank. Identity is the victim's (training is about
                // this tech's threat surface).
                var threatIdentity = TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.ResolveIdentity(evt.Id);
                TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.TeeThreat(threatIdentity, threatEvent);
                Interlocked.Exchange(ref _modifiedSinceLoad, 1);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.OnDamage: " + ex.Message);
            }
        }

        // Local AnchorState code (mirrors StrategicStateExtractor.AnchorCode private
        // helper). 0 floating, 0.5 anchored, 1 sky-anchored. byte source on the probe.
        private static float AnchorCodeFromProbe(byte anchorState)
        {
            if (anchorState == (byte)AnchorStateCode.SkyAnchored) return 1f;
            if (anchorState == (byte)AnchorStateCode.Anchored)    return 0.5f;
            return 0f;
        }

        /// <summary>
        /// Builds the 48-slot ThreatEvent feature vector per FEATURE-EXPANSION-PLAN
        /// §3.5 / ThreatSlots. Mirrors the StrategicStateExtractor.FillThreatSlots
        /// discipline: every Smart-team-only field reads from the per-tech
        /// SelfStateProbe snapshot via SmartRuntime.LookupSelfStateProbe; kinematic +
        /// engagement-geometry fields read from the BeliefSnapshot. Opaque-attacker
        /// (non-Smart-team) composition/weapon/energy/cargo slots stay zero per F-32.
        /// </summary>
        private static float[] BuildThreatEventFeatures(DamageObserved evt)
        {
            var features = new float[ThreatAssessmentModel.FeatureDim];
            long nowMono = MonoClock.Now();
            long from1s = nowMono - (long)(1.0 / MonoClock.TickFreq);
            long from5s = nowMono - (long)(5.0 / MonoClock.TickFreq);

            // Beliefs. World may be null very early in lifecycle — leave zeros.
            BeliefState victimBelief = null;
            BeliefState attackerBelief = null;
            try
            {
                var world = SmartRuntime.World;
                if (world != null)
                {
                    var vbuf = world.GetPerTechBuffer(evt.Id);
                    if (vbuf != null) victimBelief = vbuf.Read();
                    if (evt.Damage.HasAttacker)
                    {
                        var abuf = world.GetPerTechBuffer(evt.Damage.AttackerIfKnown);
                        if (abuf != null) attackerBelief = abuf.Read();
                    }
                }
            }
            catch { /* belief refs stay null - dependent slots stay 0 */ }

            // Probes. F-32 opaque-attacker: probe is null when attacker isn't on a
            // Smart-driven team → leave composition / weapon-aggregate / energy / cargo
            // slots zero. Victim probe likewise gated.
            SelfProbeSnapshot attackerProbe = null;
            SelfProbeSnapshot victimProbe = null;
            try
            {
                if (evt.Damage.HasAttacker)
                {
                    var ap = SmartRuntime.LookupSelfStateProbe(evt.Damage.AttackerIfKnown);
                    if (ap != null) attackerProbe = ap.TryRead();
                }
                var vp = SmartRuntime.LookupSelfStateProbe(evt.Id);
                if (vp != null) victimProbe = vp.TryRead();
            }
            catch { /* probes stay null - dependent slots stay 0 */ }

            // [0..7] Attacker composition. Smart-team probe only; opaque → zero.
            if (attackerProbe != null)
            {
                features[ThreatSlots.AttackerBlockCount]    = attackerProbe.BlockCount;
                features[ThreatSlots.AttackerMass]          = attackerProbe.Mass / 1000f;
                features[ThreatSlots.AttackerWeaponCount]   = attackerProbe.WeaponAggregate.WeaponCount;
                int aw = attackerProbe.WeaponAggregate.WeaponCount;
                features[ThreatSlots.AttackerMeleeFraction] = aw > 0
                    ? (float)attackerProbe.WeaponAggregate.MeleeWeaponCount / aw : 0f;
                features[ThreatSlots.AttackerHpFraction]    = attackerProbe.HpFraction;
                features[ThreatSlots.AttackerAnchorState]   = AnchorCodeFromProbe(attackerProbe.AnchorState);
                features[ThreatSlots.AttackerRoleHintCode]  = (int)attackerProbe.SmartIdentity / 8f;
                features[ThreatSlots.AttackerProvokedFlag]  = attackerProbe.ProvokedCountdown > 0 ? 1f : 0f;
            }
            // HpFraction fallback from HealthSidecar when attacker is opaque but the
            // sidecar still tracks its HP (engine TankDamage path mirrors hp irrespective
            // of Smart-team registration). Keeps slot 4 useful even when probe is absent.
            else if (evt.Damage.HasAttacker && SmartRuntime.Health != null)
            {
                features[ThreatSlots.AttackerHpFraction] = SmartRuntime.Health.Get(evt.Damage.AttackerIfKnown);
            }

            // [8..15] Attacker weapon aggregate. Probe-only; opaque → zero.
            if (attackerProbe != null)
            {
                var agg = attackerProbe.WeaponAggregate;
                features[ThreatSlots.AttackerWeaponRangeMax]          = agg.RangeMax;
                features[ThreatSlots.AttackerWeaponRangeMean]         = agg.RangeMean;
                features[ThreatSlots.AttackerWeaponFireRateMean]      = agg.FireRateMean;
                features[ThreatSlots.AttackerWeaponDamagePerShotMean] = agg.DamagePerShotMean;
                features[ThreatSlots.AttackerWeaponMuzzleVelMean]     = agg.MuzzleVelMean;
                features[ThreatSlots.AttackerEnergyWeaponFraction]    = agg.EnergyWeaponFraction;
                features[ThreatSlots.AttackerWeaponKindMixGun]        = agg.KindMixGun;
                features[ThreatSlots.AttackerWeaponKindMixBeamMelee]  = agg.KindMixBeamMelee;
            }

            // [16..23] Attacker kinematic. Speed + accel come from belief (available
            // for any observed tech); slope/height query terrain at attacker.xz;
            // waterlogged + cargo + ready-fire + energy are probe-only.
            if (attackerBelief != null)
            {
                features[ThreatSlots.AttackerSpeed]            = attackerBelief.VelocityMean.magnitude;
                features[ThreatSlots.AttackerMaxAccelEstimate] = attackerBelief.MaxAccelerationEstimate / 20f;

                // Terrain reads — gated on a terrain map past the FreshlyAllocated race.
                // Daemon does the same gate.
                TerrainMap terrain = PathingService.CurrentTerrain;
                if (terrain != null && !terrain.IsFreshlyAllocated)
                {
                    Vector2 axz = new Vector2(attackerBelief.PositionMean.x, attackerBelief.PositionMean.z);
                    features[ThreatSlots.AttackerSlopeUnder]         = terrain.SlopeAt(axz);
                    features[ThreatSlots.AttackerHeightAboveTerrain] = attackerBelief.PositionMean.y - terrain.HeightAt(axz);
                }
            }
            if (attackerProbe != null)
            {
                // Waterlogged: clamp((WaterHeight - y)/5, 0, 1). -9001 = no WaterMod.
                features[ThreatSlots.AttackerWaterloggedFrac] =
                    attackerProbe.WaterHeight > -9000f
                    ? Mathf.Clamp01((attackerProbe.WaterHeight - attackerProbe.PositionWorld.y) / 5f) : 0f;
                int cap = attackerProbe.CargoCapacity;
                features[ThreatSlots.AttackerCargoFill] = cap > 0
                    ? (float)attackerProbe.CargoNumContents / cap : 0f;
                int aw2 = attackerProbe.WeaponAggregate.WeaponCount;
                features[ThreatSlots.AttackerReadyFireFraction] = aw2 > 0
                    ? (float)attackerProbe.WeaponAggregate.ReadyToFireCount / aw2 : 0f;
                features[ThreatSlots.AttackerEnergyStored] = attackerProbe.Electric.CurrentAmount / 10000f;
            }

            // [24..31] Engagement geometry. Needs both beliefs; LOS direction is
            // attacker→victim per §3.5 ("DistanceToVictim", "HeadingTowardVictimDot").
            if (attackerBelief != null && victimBelief != null)
            {
                Vector3 toVictim = victimBelief.PositionMean - attackerBelief.PositionMean;
                float dist = toVictim.magnitude;
                features[ThreatSlots.DistanceToVictim] = dist;

                if (dist > 1e-3f)
                {
                    Vector3 losDir = toVictim / dist;
                    Vector3 aForward = attackerBelief.ForwardXZ;
                    if (aForward.sqrMagnitude > 1e-6f)
                    {
                        features[ThreatSlots.AttackerHeadingTowardVictimDot] = Vector3.Dot(aForward, losDir);
                    }
                    // VictimWeakFaceTowardAttackerDot: rotated face normal vs -los.
                    // Victim probe carries the world-frame weak-face normal.
                    if (victimProbe != null)
                    {
                        features[ThreatSlots.VictimWeakFaceTowardAttackerDot] =
                            Vector3.Dot(victimProbe.WeakFaceNormalWorld, -losDir);
                    }
                }

                // LOS-blocked between attacker and victim. Same terrain-fresh gate.
                TerrainMap terrain = PathingService.CurrentTerrain;
                if (terrain != null && !terrain.IsFreshlyAllocated)
                {
                    features[ThreatSlots.LosBlockedToVictim] =
                        terrain.RaycastSegment(attackerBelief.PositionMean, victimBelief.PositionMean) ? 1f : 0f;
                }

                // WeaponInRange / AimableFlag — need attacker weapon range from probe.
                // Opaque attacker → both stay 0 (we don't know its range).
                if (attackerProbe != null)
                {
                    float rmax = attackerProbe.WeaponAggregate.RangeMax;
                    bool inRange = rmax > 0f && dist <= rmax;
                    features[ThreatSlots.AttackerWeaponInRange] = inRange ? 1f : 0f;
                    // Aimable proxy: in range AND LOS clear AND face-in-arc (using
                    // attacker forward dot as a coarse arc proxy — mirrors the extractor).
                    bool losClear = features[ThreatSlots.LosBlockedToVictim] < 0.5f;
                    bool facing = features[ThreatSlots.AttackerHeadingTowardVictimDot] > 0.5f;
                    features[ThreatSlots.AttackerAimableFlag] = (inRange && losClear && facing) ? 1f : 0f;
                }
            }

            // VictimWeakFaceHp + VictimHpFraction. Smart-team victim → probe; opaque
            // victim falls back to HealthSidecar for HpFraction. WeakFaceHp is probe-
            // armor-only (can't query a chassis without ArmorMap).
            if (victimProbe != null && victimProbe.Armor != null && victimProbe.Armor != Vehicle.ArmorMap.Empty)
            {
                features[ThreatSlots.VictimWeakFaceHp] = victimProbe.Armor.QueryWeakFace(Vector3.forward).TotalHP;
            }
            if (victimProbe != null)
            {
                features[ThreatSlots.VictimHpFraction] = victimProbe.HpFraction;
            }
            else if (SmartRuntime.Health != null)
            {
                features[ThreatSlots.VictimHpFraction] = SmartRuntime.Health.Get(evt.Id);
            }
            else
            {
                features[ThreatSlots.VictimHpFraction] = 1f;
            }

            // [32..37] Recent-damage windows.
            // Attacker-side dealt (slot 32/33 = magnitude, 35 = dominant type) from
            // RecentDamageDealtAccumulator. Sidecar tracks ALL attackers (opaque too),
            // so these are usable irrespective of Smart-team membership.
            if (evt.Damage.HasAttacker && SmartRuntime.DealtAccumulator != null)
            {
                var dealt1 = SmartRuntime.DealtAccumulator.SumWithin(evt.Damage.AttackerIfKnown, from1s, nowMono);
                var dealt5 = SmartRuntime.DealtAccumulator.SumWithin(evt.Damage.AttackerIfKnown, from5s, nowMono);
                features[ThreatSlots.RecentDamageDealt1s]       = dealt1.TotalMagnitude;
                features[ThreatSlots.RecentDamageDealt5s]       = dealt5.TotalMagnitude;
                features[ThreatSlots.RecentDamageDealtTypeCode] = dealt1.DominantTypeCode / 8f;
            }
            // Slot 34 — RecentShotsFired1s. WeaponFireBuffer keyed by attacker.
            if (evt.Damage.HasAttacker && SmartRuntime.WeaponFires != null)
            {
                features[ThreatSlots.RecentShotsFired1s] =
                    SmartRuntime.WeaponFires.CountWithin(evt.Damage.AttackerIfKnown, from1s, nowMono);
            }
            // Victim-side taken (slot 36/37) from DamageHintBuffer. Sidecar tracks ALL
            // victims; usable irrespective of Smart-team membership.
            if (SmartRuntime.DamageHints != null)
            {
                var taken1 = SmartRuntime.DamageHints.SumWithin(evt.Id, from1s, nowMono);
                var taken5 = SmartRuntime.DamageHints.SumWithin(evt.Id, from5s, nowMono);
                features[ThreatSlots.RecentDamageTakenByVictim1s] = taken1.TotalMagnitude;
                features[ThreatSlots.RecentDamageTakenByVictim5s] = taken5.TotalMagnitude;
            }

            // [38..47] Reserved — mission-state / team-context / shield-charge.
            // Per §3.6 reserved-slot ownership: no source identified yet, slots stay 0.

            return features;
        }
    }
}
