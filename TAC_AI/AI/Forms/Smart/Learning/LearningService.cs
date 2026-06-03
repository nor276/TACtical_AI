using System;
using System.IO;
using System.Threading;
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
        /// Phase 5 (FIX-PLAN.md) — AUDIT-R2 §1.R2-E: derive a stable per-player identifier
        /// for the profile path. Phase 11 (verify-build): the assumed
        /// <c>NetPlayer.PlayerName</c> property does not exist on the engine type, and
        /// the existing codebase has no verified read of any stable name field on
        /// NetPlayer. Until the canonical accessor surfaces, MP and SP both fall back
        /// to <see cref="DefaultPlayerId"/> — TerraTech doesn't support multi-player
        /// on one installation, so a single profile per installation is correct for
        /// v0.1.0. v0.2: revisit when NetPlayer's stable-identity field is identified
        /// (likely a Steam ID accessor or session player-slot index).
        /// </summary>
        private static string ResolvePlayerId()
        {
            return DefaultPlayerId;
        }

        public static OpponentIntentClassifier Intent { get; private set; }
        public static ActionValueEstimator ActionValue { get; private set; }
        public static TrajectoryResidualModel Residual { get; private set; }
        public static ThreatAssessmentModel Threat { get; private set; }

        private static int _running;
        private static string _profileDir;
        private static string _currentPlayerId = DefaultPlayerId;
        // Phase 4 (FIX-PLAN.md): modifiedSinceLoad was a plain bool read on shutdown and
        // written from OnDamageObserved on a different thread. R1 §3.9 + R2 1.R2-N. Now
        // an int accessed via Interlocked.Exchange/CompareExchange so the shutdown read
        // sees writes from any thread.
        private static int _modifiedSinceLoad;
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

            LoadProfile(_currentPlayerId);

            // Spin up one trainer worker per model. EnqueueLongRunning spawns dedicated
            // threads so trainer workers (which block their thread forever in tight loops)
            // cannot consume worker-pool slots needed for short-task RunMPCWork dispatches.
            pool.EnqueueLongRunning(new TrainerWorker(Intent).RunLoop, "Trainer-Intent");
            pool.EnqueueLongRunning(new TrainerWorker(ActionValue).RunLoop, "Trainer-ActionValue");
            pool.EnqueueLongRunning(new TrainerWorker(Residual).RunLoop, "Trainer-Residual");
            pool.EnqueueLongRunning(new TrainerWorker(Threat).RunLoop, "Trainer-Threat");

            // Event subscriptions. Only DamageObserved is wired to a substantive handler
            // at v0.1.0; others are documented stubs per the class summary.
            _damageHandler = OnDamageObserved;
            WorldEventBus.Subscribe(_damageHandler);
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
                    // No file, no baseline — Glorot-initialized weights stand (per LEARNING §7.1).
                    DebugTAC_AI.Log("Smart.Learning: no profile for " + playerId + "; using Glorot init.");
                    Interlocked.Exchange(ref _modifiedSinceLoad, 0);
                    return;
                }
                ApplyProfile(profile);
                DebugTAC_AI.Log("Smart.Learning: loaded profile for " + playerId
                                + " (schema=" + profile.SchemaVersion
                                + ", fromBaseline=" + profile.FromBaseline + ").");
                Interlocked.Exchange(ref _modifiedSinceLoad, 0);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.Load: " + ex.Message);
            }
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
                var features = new float[ThreatAssessmentModel.FeatureDim];
                // Normalize damage to [0,1] as the empirical threat observation (provisional).
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
