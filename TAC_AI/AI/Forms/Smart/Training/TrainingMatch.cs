#if SMART_DEV
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using TAC_AI.Templates;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Planning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Training
{
    /// <summary>
    /// One self-play (or benchmark) match. Per TRAINING-CONTRACT §2.2.
    ///
    /// v0.1.0 IMPLEMENTATION SHAPE (post step-1.11 + OQ-5/spawn + hp-tuning + coroutine driver):
    ///   - Tech spawn and despawn LIVE via <see cref="RawTechLoader"/>.
    ///   - Movement actuator LIVE via <see cref="ContinuousController.OnControlFrame"/>.
    ///   - Hyperparameter write-through LIVE (16 mutable static fields).
    ///   - <see cref="SimulateCoroutineFull"/> LIVE — drives a real Unity frame loop with
    ///     <see cref="WaitForFixedUpdate"/> and per-tick termination checks against the
    ///     verified alive predicate (<c>tank.visible.isActive &amp;&amp; tank.blockman.blockCount &gt; 0</c>).
    ///   - <see cref="Run"/> is the synchronous variant retained for CMA-ES math testing
    ///     without a host MonoBehaviour — its sim is a zero-duration stalemate by design.
    ///   - TerrainSpec carries authored terrain hints but no programmatic seeding hook
    ///     ships — matches reuse whatever world is currently loaded. The dead BuildTerrain
    ///     stub was removed; TerrainSpec is informational on the scenario file only.
    ///   - Render suppression still partial (Time.timeScale only).
    /// </summary>
    public sealed class TrainingMatch
    {
        public Scenario Scenario { get; }
        public MatchMode Mode { get; }
        public HyperParams Candidate { get; }
        public bool Headless { get; }
        public float TimeScaleHeadless { get; set; } = 15f;
        public float StalemateNoDamageWindowSeconds { get; set; } = 60f;

        private readonly CancellationToken _cancellation;
        private readonly List<Tank> _spawnedTanks = new List<Tank>();
        private readonly Dictionary<int, int> _instanceIdToTeam = new Dictionary<int, int>(); // tank.GetInstanceID() → 1 or 2
        private Action<DamageObserved> _damageHandler;

        private MatchOutcome _outcome;
        private float _simulationTime;
        private float _lastDamageTime;
        private int _shotsFired;
        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6: separate Them shot counter so
        // OutcomeScorer.DeriveWinner's symmetric swap can compute Them's
        // WeaponEfficiency instead of zeroing it.
        private int _themShotsFired;
        private Action<ProjectileFired> _projectileHandler;

        public TrainingMatch(Scenario scenario, MatchMode mode, HyperParams candidate, bool headless, CancellationToken ct)
        {
            Scenario = scenario;
            Mode = mode;
            Candidate = candidate;
            Headless = headless;
            _cancellation = ct;
        }

        // ====================== ENTRY POINTS ======================

        /// <summary>
        /// Synchronous run — performs setup + cleanup but NO simulation. Useful for
        /// exercising CMA-ES math without a host MonoBehaviour. The returned outcome is
        /// a zero-duration stalemate.
        /// </summary>
        public MatchOutcome Run()
        {
            var outcome = SetupAndAllocateOutcome();
            if (outcome == null) return CreateFailedOutcome();
            try { /* simulation skipped */ }
            finally { Teardown(); }
            // Surface the spawned counts as alive — caller sees a no-fight stalemate.
            outcome.OurAliveAtEnd = outcome.OurStartingCount;
            outcome.ThemAliveAtEnd = outcome.ThemStartingCount;
            outcome.OurEndHpTotal = outcome.OurStartingHpTotal;
            outcome.ThemEndHpTotal = outcome.ThemStartingHpTotal;
            outcome.Stalemate = true;
            return outcome;
        }

        /// <summary>
        /// Full match coroutine: setup → simulate (yields per FixedUpdate) → cleanup →
        /// onComplete(outcome). Must be driven from a host MonoBehaviour's StartCoroutine.
        /// Per TRAINING-CONTRACT §2.3 termination conditions are all checked each tick.
        /// </summary>
        public IEnumerator SimulateCoroutineFull(Action<MatchOutcome> onComplete)
        {
            var outcome = SetupAndAllocateOutcome();
            if (outcome == null)
            {
                onComplete?.Invoke(CreateFailedOutcome());
                yield break;
            }

            // Damage + projectile telemetry subscriptions, scoped to the match.
            _damageHandler = OnDamageEvent;
            _projectileHandler = OnProjectileEvent;
            WorldEventBus.Subscribe(_damageHandler);
            WorldEventBus.Subscribe(_projectileHandler);

            Exception simulationError = null;
            try
            {
                _simulationTime = 0f;
                _lastDamageTime = 0f;
            }
            catch (Exception ex) { simulationError = ex; }

            // The actual yielding loop — we can't yield inside try{} in C# 7.3, so we
            // structure the cleanup as a finally-equivalent after the loop.
            // Phase 4 (FIX-PLAN.md) — AUDIT-R2 §1.R2-L: an exception thrown inside the
            // loop body (e.g., CountAliveAndHp NREs because a Tank was recycled mid-
            // tick) used to bypass the cleanup section below — leaving WorldEventBus
            // subscribers leaked, Time.timeScale stuck at 15×, and spawned tanks not
            // despawned. The inner try sets simulationError + finished and lets the
            // normal exit path run cleanup.
            bool finished = false;
            while (!finished)
            {
                if (_cancellation.IsCancellationRequested) break;
                if (simulationError != null) break;

                yield return new WaitForFixedUpdate();

                bool inLoopFailed = false;
                Exception inLoopError = null;
                try
                {
                    _simulationTime += Time.fixedDeltaTime;

                    int ourAlive, themAlive;
                    float ourHp, themHp;
                    CountAliveAndHp(out ourAlive, out themAlive, out ourHp, out themHp);
                    _outcome.OurAliveAtEnd = ourAlive;
                    _outcome.ThemAliveAtEnd = themAlive;
                    _outcome.OurEndHpTotal = ourHp;
                    _outcome.ThemEndHpTotal = themHp;

                    // §2.3 conditions.
                    if (ourAlive == 0 || themAlive == 0) { finished = true; }
                    else if (_simulationTime >= _outcome.DurationLimitSeconds) { finished = true; }
                    else if (_simulationTime - _lastDamageTime > StalemateNoDamageWindowSeconds)
                    {
                        _outcome.Stalemate = true;
                        finished = true;
                    }
                }
                catch (Exception ex)
                {
                    inLoopFailed = true;
                    inLoopError = ex;
                }
                if (inLoopFailed)
                {
                    simulationError = inLoopError;
                    _outcome.Stalemate = true;
                    break;
                }
            }

            _outcome.DurationSeconds = _simulationTime;
            _outcome.OurShotsFired = _shotsFired;
            _outcome.ThemShotsFired = _themShotsFired;

            // Cleanup — even on cancellation / exception path.
            try
            {
                WorldEventBus.Unsubscribe(_damageHandler);
                WorldEventBus.Unsubscribe(_projectileHandler);
                _damageHandler = null;
                _projectileHandler = null;
            }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Training.UnsubFail: " + ex.Message); }

            Teardown();

            if (simulationError != null)
                DebugTAC_AI.LogWarning("Smart.Training.SimError: " + simulationError.Message);

            onComplete?.Invoke(_outcome);
        }

        // ====================== SETUP / TEARDOWN ======================

        private MatchOutcome SetupAndAllocateOutcome()
        {
            _outcome = new MatchOutcome
            {
                ScenarioId = Scenario.Id,
                Mode = Mode,
                DurationLimitSeconds = Scenario.MatchDurationLimit,
            };
            try
            {
                ApplyHyperParameters(Candidate);
                // Terrain is whatever's loaded; Scenario.Terrain is informational.
                SpawnTechs(Scenario.Spawns, _outcome);
                if (Headless) EnableHeadlessMode();
                return _outcome;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Training.Match.Setup: " + ex.Message);
                Teardown();
                return null;
            }
        }

        private MatchOutcome CreateFailedOutcome()
        {
            return new MatchOutcome
            {
                ScenarioId = Scenario?.Id ?? "unknown",
                Mode = Mode,
                DurationLimitSeconds = Scenario?.MatchDurationLimit ?? 0f,
                Stalemate = true,
            };
        }

        private void Teardown()
        {
            try { if (Headless) DisableHeadlessMode(); } catch { /* swallow */ }
            CleanupSpawnedTechs();
            _instanceIdToTeam.Clear();
        }

        // ====================== TELEMETRY HOOKS ======================

        private void OnDamageEvent(DamageObserved evt)
        {
            try
            {
                int victimId = evt.Id.Value;
                if (!_instanceIdToTeam.TryGetValue(victimId, out int victimTeam)) return;
                float damage = evt.Damage.Damage;
                int attackerTeam = -1;
                if (evt.Damage.AttackerIfKnown.Value != 0)
                    _instanceIdToTeam.TryGetValue(evt.Damage.AttackerIfKnown.Value, out attackerTeam);

                if (victimTeam == 1)
                {
                    _outcome.OurDamageTaken += damage;
                    if (attackerTeam == 2) _outcome.ThemDamageDealt += damage;
                }
                else if (victimTeam == 2)
                {
                    _outcome.ThemDamageTaken += damage;
                    if (attackerTeam == 1) _outcome.OurDamageDealt += damage;
                }
                _lastDamageTime = _simulationTime;
            }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Training.OnDamage: " + ex.Message); }
        }

        private void OnProjectileEvent(ProjectileFired evt)
        {
            try
            {
                if (_instanceIdToTeam.TryGetValue(evt.Id.Value, out int firerTeam))
                {
                    if (firerTeam == 1) _shotsFired++;
                    else if (firerTeam == 2) _themShotsFired++;
                }
            }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Training.OnFire: " + ex.Message); }
        }

        // ====================== HYPERPARAMETER APPLICATION ======================

        private void ApplyHyperParameters(HyperParams hp)
        {
            // Subsystem hyperparameter targets are mutable static fields per CMA-ES tuning
            // surface (Training §5.1). Reads of each tunable happen on the next tick of the
            // owning subsystem, so write-here / take-effect-next-tick is the active semantics.
            if (hp == null || hp.Values == null || hp.Names == null) return;
            int n = Mathf.Min(hp.Values.Length, hp.Names.Length);
            for (int i = 0; i < n; i++)
            {
                float v = hp.Values[i];
                switch (hp.Names[i])
                {
                    // ---- Control ----
                    case "MPC_sample_count":
                        SamplingMPC.SampleCount = Mathf.Clamp(Mathf.RoundToInt(v), 8, 512); break;
                    case "MPC_horizon_seconds":
                        SamplingMPC.HorizonSteps = Mathf.Clamp(Mathf.RoundToInt(v / Mathf.Max(SamplingMPC.StepDt, 1e-3f)), 1, 200); break;
                    case "MPC_temperature":
                        SamplingMPC.Lambda = Mathf.Max(v, 1e-3f); break;
                    case "Tactical_learning_rate":
                        TacticalOptimizer.LearningRate = Mathf.Max(v, 1e-4f); break;
                    case "Salvo_threshold_damage":
                        WeaponFireController.SalvoDamageThreshold = Mathf.Max(v, 0.1f); break;
                    case "Sticky_target_ticks":
                        WeaponFireController.StickyTicks = Mathf.Max(Mathf.RoundToInt(v), 1); break;

                    // ---- Planning ----
                    case "PUCT_exploration_constant":
                        PUCTSearch.CPuct = Mathf.Max(v, 1e-3f); break;
                    case "MCTS_node_budget":
                        PUCTSearch.NodeBudget = Mathf.Clamp(Mathf.RoundToInt(v), 100, 100000); break;
                    case "Rollout_depth_seconds":
                        StrategicRollout.RolloutHorizonSeconds = Mathf.Max(v, 0.1f); break;

                    // ---- Cost-function weights ----
                    case "w_health_value": StrategicValueFunction.WHealth = v; break;
                    case "w_threat_value": StrategicValueFunction.WThreat = v; break;
                    case "w_reach_cost":   CostFunction.WReach = v; break;
                    case "w_effort_cost":  CostFunction.WEffort = v; break;
                    case "w_threat_cost":  CostFunction.WThreat = v; break;
                    case "w_weapon_cost":  CostFunction.WWeapon = v; break;

                    // ---- Pathing ----
                    case "CHOMP_step_count":
                        TrajectoryOptimizer.GradientSteps = Mathf.Clamp(Mathf.RoundToInt(v), 1, 200); break;

                    // Phase 8 (FIX-PLAN.md): additional tunables surfaced to CMA-ES.
                    case "Energy_refill_per_second":
                        WeaponFireController.EnergyRefillPerSecond = Mathf.Clamp(v, 0.01f, 5f); break;
                    case "MPC_sigma_throttle":
                        SamplingMPC.SigmaThrottle = Mathf.Clamp(v, 0.01f, 1.5f); break;
                    case "MPC_sigma_steer":
                        SamplingMPC.SigmaSteer = Mathf.Clamp(v, 0.01f, 1.5f); break;
                    case "MPC_sigma_brake":
                        SamplingMPC.SigmaBrake = Mathf.Clamp(v, 0.01f, 1.5f); break;
                    case "w_position_value":
                        StrategicValueFunction.WPosition = v; break;
                    case "w_speed_value":
                        StrategicValueFunction.WSpeed = v; break;
                    case "w_coverage_value":
                        StrategicValueFunction.WCoverage = v; break;
                }
            }
        }

        // ====================== SPAWN / CLEANUP ======================

        private void SpawnTechs(IReadOnlyList<SpawnSpec> spawns, MatchOutcome outcome)
        {
            var filter = RawTechPopParams.Default;
            int ours = 0, theirs = 0;
            float ourStartingHp = 0f;
            float themStartingHp = 0f;
            for (int i = 0; i < spawns.Count; i++)
            {
                var spec = spawns[i];
                Vector3 forwards = spec.Rotation * Vector3.forward;
                Tank spawned = null;
                try
                {
                    spawned = RawTechLoader.SpawnRandomTechAtPosHead(spec.Position, forwards, spec.Team.Value, filter, nullOnErrorTech: true);
                }
                catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Training.SpawnTechs[" + i + "]: " + ex.Message); }
                if (spawned == null) continue;

                _spawnedTanks.Add(spawned);
                _instanceIdToTeam[spawned.GetInstanceID()] = spec.Team.Value;
                if (spec.Team.Value == 1)
                {
                    ours++;
                    ourStartingHp += EstimateTechHp(spawned);
                }
                else if (spec.Team.Value == 2)
                {
                    theirs++;
                    themStartingHp += EstimateTechHp(spawned);
                }
            }
            outcome.OurStartingCount = ours;
            outcome.ThemStartingCount = theirs;
            outcome.OurStartingHpTotal = ourStartingHp;
            outcome.ThemStartingHpTotal = themStartingHp;
        }

        private static float EstimateTechHp(Tank tank)
        {
            if (tank == null || tank.blockman == null) return 100f;
            return Mathf.Max(tank.blockman.blockCount * 50f, 50f);
        }

        // Phase 5 (FIX-PLAN.md) — AUDIT-R2 §1.R2-F: Time.timeScale is process-global and
        // NOT network-replicated. Setting it to 15 during MP would desync every client.
        // Also Plan 3 insight #9: respect the user's prior timeScale (could be 0 paused).
        private float _capturedTimeScale = 1f;
        private void EnableHeadlessMode()
        {
            if (IsMultiplayer())
            {
                DebugTAC_AI.LogWarning("Smart.Training: refusing to set Time.timeScale during MP session.");
                return;
            }
            _capturedTimeScale = Time.timeScale;
            Time.timeScale = TimeScaleHeadless;
        }
        private void DisableHeadlessMode()
        {
            // Restore whatever timeScale was set before we touched it — not a blind 1f.
            Time.timeScale = _capturedTimeScale;
        }

        private static bool IsMultiplayer()
        {
            try { return ManNetwork.inst != null && ManNetwork.inst.IsMultiplayer; }
            catch { return false; }
        }

        private void CleanupSpawnedTechs()
        {
            for (int i = 0; i < _spawnedTanks.Count; i++)
            {
                var t = _spawnedTanks[i];
                if (t == null || t.visible == null) continue;
                try { t.visible.RemoveFromGame(); }
                catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Training.Cleanup: " + ex.Message); }
            }
            _spawnedTanks.Clear();
        }

        // ====================== ALIVE PREDICATE ======================

        /// <summary>
        /// Verified TerraTech alive predicate (per AIECore.cs:139 / 226 + AIERepair.cs:839
        /// usage pattern): tank.visible.isActive AND tank.blockman.blockCount &gt; 0.
        /// </summary>
        private static bool IsTankAlive(Tank tank)
        {
            return tank != null
                && tank.visible != null
                && tank.visible.isActive
                && tank.blockman != null
                && tank.blockman.blockCount > 0;
        }

        private void CountAliveAndHp(out int ourAlive, out int themAlive, out float ourHp, out float themHp)
        {
            ourAlive = 0; themAlive = 0; ourHp = 0f; themHp = 0f;
            for (int i = 0; i < _spawnedTanks.Count; i++)
            {
                var t = _spawnedTanks[i];
                if (!IsTankAlive(t)) continue;
                if (!_instanceIdToTeam.TryGetValue(t.GetInstanceID(), out int team)) continue;
                if (team == 1)
                {
                    ourAlive++;
                    ourHp += EstimateTechHp(t);
                }
                else if (team == 2)
                {
                    themAlive++;
                    themHp += EstimateTechHp(t);
                }
            }
        }
    }
}
#endif
