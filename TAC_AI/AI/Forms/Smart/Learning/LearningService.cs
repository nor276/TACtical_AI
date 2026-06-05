using System;
using System.IO;
using System.Threading;
using UnityEngine;
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
    /// v0.1.0 event-handler completeness: the threat model's <see cref="World.DamageObserved"/>
    /// path produces real training events end-to-end. Intent / ActionValue / Residual
    /// handlers subscribe but emit only when the per-tech state plumbing they depend on
    /// (target observation sequence buffers, plan-transition events, weapon-fire outcome
    /// tracking) is wired — TODO v0.2 — they otherwise sit dormant. This still satisfies
    /// step 1.10's verification gate via the threat pipeline; the others go live with
    /// the corresponding subsystem integrations.
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
            try { ProfileSelfTest.Run(modDirectory); }
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
            // L-059: stop accepting new training events while we drain.
            SmartRuntime.AcceptingTrainingEvents = false;

            bool allParked = TAC_AI.AI.Forms.Smart.Coordination.TrainerBarrier.WaitAll(BarrierWaitMs);
            if (!allParked)
            {
                DebugTAC_AI.LogWarnFileOnly("learning-hostchange-pause-timeout",
                    "[LEARNING-HOSTCHANGE] OnHostLost barrier timeout — saving with possibly-stale params");
            }
            try
            {
                if (IsRunning) SaveProfile(_currentPlayerId);
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
                        var profile = ProfilePersistence.Load(path, baselineBytes: null);
                        if (profile != null)
                        {
                            // Per-model load: catch arch-mismatch + keep in-memory params.
                            ApplyProfileSafe(profile);
                            _profileLoadedAtMs = diskMs;
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
                // L-059: producers may enqueue again.
                SmartRuntime.AcceptingTrainingEvents = true;
                DebugTAC_AI.LogWarnFileOnly("learning-hostchange-gained",
                    "[LEARNING-HOSTCHANGE] OnHostGained player=" + _currentPlayerId
                    + " phase=" + ev.PhaseSource);
            }
        }

        // L-070: per-model load with arch-mismatch guard. ApplyProfile (existing) calls
        // LoadParameters which throws on length-mismatch; we want partial-tolerance here.
        private static void ApplyProfileSafe(LoadedProfile profile)
        {
            void Try(string name, ILearnedModel model, byte[] bytes)
            {
                if (model == null || bytes == null) return;
                try
                {
                    var floats = new float[bytes.Length / 4];
                    System.Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
                    model.LoadParameters(floats);
                }
                catch (ArgumentException)
                {
                    DebugTAC_AI.LogWarnFileOnly("learning-hostchange-archmismatch-" + name,
                        "[LEARNING-HOSTCHANGE] reload-skip model=" + name
                        + " reason=arch-mismatch (host schema differs); keeping in-memory params");
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("LearningService.ApplyProfileSafe " + name + ": " + ex.Message);
                }
            }
            // ApplyProfile's existing structure is sufficient when arches match; the safe
            // path above only kicks in when there's a mismatch on a particular model.
            try { ApplyProfile(profile); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("learning-hostchange-applyprofile-fail",
                    "[LEARNING-HOSTCHANGE] ApplyProfile threw — falling back to per-model safe loads: " + ex.Message);
                // ApplyProfile failed wholesale; we still have in-memory params, which is
                // the correct fallback per the plan invariant.
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

        private static void LoadProfile(string playerId)
        {
            try
            {
                string path = ProfilePath(playerId);
                var profile = ProfilePersistence.Load(path, baselineBytes: null);
                if (profile == null)
                {
                    // L-015: cold start (no primary, no .previous, no .penultimate, no
                    // baseline). Distinct tag so [PROFILE-LOAD-FAIL] is reserved for
                    // genuine fallback (one or more tiers were corrupt).
                    DebugTAC_AI.Log("[PROFILE-COLD-START] player=" + playerId + " — Glorot init");
                    Interlocked.Exchange(ref _modifiedSinceLoad, 0);
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

        public static void SaveProfile(string playerId)
        {
            try
            {
                string path = ProfilePath(playerId);
                var models = new ILearnedModel[] { Intent, ActionValue, Residual, Threat };
                ProfilePersistence.Save(path, models);
                Interlocked.Exchange(ref _modifiedSinceLoad, 0);
                DebugTAC_AI.Log("Smart.Learning: saved profile for " + playerId + " → " + path);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.Save: " + ex.Message);
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
                    switch (section.Id)
                    {
                        case ModelId.Intent: Intent.LoadParameters(section.Weights); break;
                        case ModelId.ActionValue: ActionValue.LoadParameters(section.Weights); break;
                        case ModelId.TrajectoryResidual: Residual.LoadParameters(section.Weights); break;
                        case ModelId.ThreatAssessment: Threat.LoadParameters(section.Weights); break;
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
        /// DamageObserved → ThreatAssessment training event. The damage observation gives
        /// us a noisy single-sample threat estimate for the attacker. v0.1.0 fills the
        /// 25-element feature vector with zeros except the few we know from the damage
        /// payload (placeholder normalization). When VehicleModelSnapshot for the attacker
        /// is plumbed, the feature vector gets real values.
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
                var features = new float[ThreatAssessmentModel.FeatureDim];

                // P7 (REV 7): real feature population from belief state.
                // Slot layout (v0.2):
                //   [0] damage magnitude normalized
                //   [1] HasAttacker flag (0/1)
                //   [2] attacker velocity magnitude (if known)
                //   [3] distance victim<-attacker (if both beliefs available)
                //   [4] attacker Sight enum cast to float (0=Lost..2=Fresh) (if known)
                //   [5] victim velocity magnitude (if known)
                //   [6] elapsed seconds since attacker last observed (if known)
                //   [7..n] reserved for v0.3 VehicleModelSnapshot plumbing.
                features[0] = UnityEngine.Mathf.Clamp01(evt.Damage.Damage / 100f);
                features[1] = evt.Damage.HasAttacker ? 1f : 0f;

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
                catch { /* leave belief refs null - features stay 0 in those slots */ }

                if (attackerBelief != null)
                {
                    features[2] = attackerBelief.VelocityMean.magnitude;
                    features[4] = (float)(int)attackerBelief.Sight;
                    long now = MonoClock.Now();
                    features[6] = MonoClock.Seconds(attackerBelief.LastObservedTickMono, now);
                }
                if (victimBelief != null)
                {
                    features[5] = victimBelief.VelocityMean.magnitude;
                    if (attackerBelief != null)
                    {
                        features[3] = (attackerBelief.PositionMean - victimBelief.PositionMean).magnitude;
                    }
                }

                // P11 T4 Item 54: populate slots 7-22 from already-wired v0.2 sources.
                // Plan-verified per Agent 4 sweep + decompile. Slots 23-24 (victim chassis
                // mass + weapon count) require victim's own VehicleModelSnapshot which only
                // exists for Smart-driven teams via TeamRuntime.GetTechRuntime → VehicleBuffer.
                // Filled when available; left zero with explicit comment when victim isn't Smart.
                long nowMono = MonoClock.Now();
                if (victimBelief != null)
                {
                    features[7]  = victimBelief.AgeSeconds;
                    features[8]  = victimBelief.UncertaintyMeters;
                    features[10] = (float)(int)victimBelief.Sight;
                    features[11] = victimBelief.SpeedAtObserve;
                    features[13] = victimBelief.MaxAccelerationEstimate;
                    Vector3 vForward = victimBelief.ForwardXZ;

                    if (attackerBelief != null)
                    {
                        features[9]  = attackerBelief.UncertaintyMeters;
                        features[12] = attackerBelief.SpeedAtObserve;
                        features[14] = attackerBelief.MaxAccelerationEstimate;

                        // Slot 15: closing rate (relative velocity projected onto victim→attacker LOS).
                        Vector3 los = attackerBelief.PositionMean - victimBelief.PositionMean;
                        float losLen = los.magnitude;
                        if (losLen > 1e-3f)
                        {
                            Vector3 losDir = los / losLen;
                            Vector3 relVel = attackerBelief.VelocityMean - victimBelief.VelocityMean;
                            features[15] = Vector3.Dot(relVel, losDir);

                            // Slot 16: bearing victim→attacker vs victim Forward (rear-shot indicator).
                            if (vForward.sqrMagnitude > 1e-6f)
                            {
                                features[16] = Vector3.Dot(losDir, vForward);
                            }
                            // Slot 17: attacker heading vs LOS (rear-attack vs face-attack).
                            Vector3 aForward = attackerBelief.ForwardXZ;
                            if (aForward.sqrMagnitude > 1e-6f)
                            {
                                features[17] = Vector3.Dot(aForward, -losDir);   // 1.0 = attacker facing victim
                            }
                        }

                        // Slot 18: same-team flag (1 when attacker and victim share team).
                        features[18] = (attackerBelief.Team.Value == victimBelief.Team.Value) ? 1f : 0f;
                    }
                }

                // Slot 19: victim HpFraction from HealthSidecar (1.0 if not yet populated).
                if (SmartRuntime.Health != null)
                {
                    features[19] = SmartRuntime.Health.Get(evt.Id);
                }
                else
                {
                    features[19] = 1f;
                }

                // Slots 20-22: recent-damage context from DamageHintBuffer (count, sum, distinct attackers).
                if (SmartRuntime.DamageHints != null)
                {
                    var hintBuf = new DamageHint[16];
                    int hintCount = SmartRuntime.DamageHints.TryGetRecent(evt.Id, hintBuf);
                    features[20] = hintCount;
                    float dmgSum = 0f;
                    var attackerSet = new System.Collections.Generic.HashSet<int>();
                    for (int i = 0; i < hintCount; i++)
                    {
                        dmgSum += hintBuf[i].Magnitude;
                        if (hintBuf[i].HasAttacker)
                            attackerSet.Add(hintBuf[i].AttackerIfKnown.Value);
                    }
                    features[21] = dmgSum;
                    features[22] = attackerSet.Count;
                }

                // Slots 23-24: victim's own chassis mass + weapon count, ONLY when victim is
                // on a Smart-driven team (we own its VehicleModelSnapshot). Looked up via the
                // team-runtime registry; left zero with a documented blocker for enemy victims
                // (TeamRuntime.EnemyVehicleSnapshots() => null is a stated v0.2 cost-bound,
                // NOT a v0.3 deferment).
                try
                {
                    if (victimBelief != null)
                    {
                        var ownTeam = SmartRuntime.GetTeam(victimBelief.Team);
                        if (ownTeam != null)
                        {
                            var vbuf2 = SmartRuntime.GetSmartPerTechVehicleBuffer(evt.Id);
                            if (vbuf2 != null)
                            {
                                var vsnap = vbuf2.Read();
                                features[23] = vsnap.Mass.TotalMass;
                                features[24] = vsnap.Weapons != null ? vsnap.Weapons.Count : 0;
                            }
                        }
                    }
                }
                catch { /* victim not on Smart team — features 23-24 stay zero. */ }

                float observedThreat = UnityEngine.Mathf.Clamp01(evt.Damage.Damage / 100f);
                Threat.EventQueue.Enqueue(new ThreatEvent(evt.Id, features, observedThreat));
                Interlocked.Exchange(ref _modifiedSinceLoad, 1);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.OnDamage: " + ex.Message);
            }
        }
    }
}
