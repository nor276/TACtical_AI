using System;
using TAC_AI.AI.Engine;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart
{
    /// <summary>
    /// "Smart" — the advanced AI form (workflow step 1.3 scaffold).
    ///
    /// This is the v1.3 skeleton: all 25 IAIForm members implemented as no-ops or
    /// neutral defaults so the form registers via AIFormRegistry.ScanAndRegister,
    /// appears in the in-game form selector, and can be activated/deactivated
    /// without affecting Vanilla. No substantive AI work runs yet — every subsystem
    /// is pending its workflow step.
    ///
    /// Contract reference (see TAC_AI/AI/Forms/Smart/Docs/):
    ///   FORM-SPECIFICATION   project-level authority; §1.10 ownership boundary;
    ///                        §3.1 primary goals; §5 AI-collaborator directives.
    ///   ARCHITECTURE         threading model; §3.2 host-authority gating
    ///                        (OD-7 resolution); §3.3 attract behavior (OD-8).
    ///   SHELL-API-GUIDE      §2 the IAIForm contract this implements.
    ///
    /// As subsystems land at their workflow steps (Threading at 1.4,
    /// World+Vehicle at 1.5, Control at 1.6, Planning+Coordination at 1.7,
    /// Pathing at 1.8, weapons at 1.9, Learning at 1.10, Training at 1.11),
    /// the relevant methods below transition from no-ops to subsystem dispatches.
    /// </summary>
    public sealed class SmartForm : IAIForm
    {
        public string Id => "Smart";
        public string DisplayName => "Smart (advanced AI — in development)";

        // [TEMP DIAGNOSTIC] Counter for the first-N OnTechSpawn-timing log lines. Capped so
        // a 50-tech world-load doesn't flood the log. Remove this + the timing blocks below
        // once the init-freeze cause is identified.
        private static int _spawnTimingCounter = 0;
        private const int SpawnTimingCap = 10;

        // [TEMP DIAGNOSTIC] Same shape, for Operations sub-call timing. Captures the first
        // OperationsTimingCap Operations invocations across all techs. First-invocation
        // allocations (MPC sample buffers, controller warm-up, first PathingService.MainThreadTick)
        // are the candidate spawn-freeze cause if the freeze is per-tech-first rather than
        // session-first. Remove with the rest of the diagnostic instrumentation.
        private static int _opsTimingCounter = 0;
        private const int OpsTimingCap = 10;

        // -------- global (mod-wide) lifecycle --------

        public void InitGlobal()
        {
            // Start Smart's process-wide globals: worker pool + world model.
            // Per ARCHITECTURE §3.2: host-authority means workers may run regardless of
            // host/client state, but per-tick paths gate substantive work on host==true.
            SmartRuntime.Init();
            // Phase 3.3 (FIX-PLAN.md): wire the engine-side observable surface into
            // Smart's WorldEventBus.
            Integration.SmartEventBridge.Install();
            // Phase 3.4 (FIX-PLAN.md): subscribe to KickStart's save/load sinks so Smart
            // can flush the learning profile pre-save and clear per-mission state pre-load.
            // ManSafeSaves itself is already registered by KickStart with one set of
            // callbacks; subscribing to KickStart's static event avoids fighting that
            // registration. SHELL-API-GUIDE OQ-7 resolution + AUDIT-R2 §1.R2-D.
            KickStart.SaveSink += OnEngineSave;
            KickStart.LoadSink += OnEngineLoad;
#if SMART_DEV
            // Phase 10 (FIX-PLAN.md): bring the dev driver up so its OnGUI overlay (with
            // Run Tests / Begin Training buttons) is reachable the moment Smart is active.
            // GetOrCreate is idempotent and safe — it creates the host GameObject only if
            // not already present.
            try { Training.SmartTrainingDriver.GetOrCreate(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.InitGlobal: SmartTrainingDriver init failed: " + ex.Message); }
#endif
        }

        public void DeInitGlobal()
        {
            // Tear down workers, clear event bus subscribers, dispose world model.
            // Per THREADING §7 + ARCHITECTURE §5 I3 (all workers terminate within DeInitGlobal).
            KickStart.SaveSink -= OnEngineSave;
            KickStart.LoadSink -= OnEngineLoad;
            Integration.SmartEventBridge.Uninstall();

            // Phase 4 (FIX-PLAN.md) — AUDIT-R2 §2.R2.E: SHELL-API-GUIDE §6.3 NORMATIVE
            // requires DeInitGlobal to clear FormState on every live TankAIHelper whose
            // state is owned by this form. Without this, each Smart-driven helper retains
            // a SmartPerTechState transitively holding 1-10 MB of per-tech state (MPC
            // arrays, PUCT trees via OwnerTeam, etc.) — leaking across every form swap.
            try
            {
                foreach (var helper in AIECore.IterateAllHelpers())
                {
                    if (helper == null) continue;
                    if (helper.FormState is SmartPerTechState)
                    {
                        // Detach the per-tank damage subscription before we drop the state
                        // reference, mirroring OnTechRecycle's cleanup path.
                        try { Integration.SmartEventBridge.DetachPerTank(helper.tank); }
                        catch { /* tank may already be gone */ }
                        helper.FormState = null;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.DeInitGlobal: helper iteration failed: " + ex.Message);
            }

            SmartRuntime.Shutdown();
        }

        /// <summary>
        /// Phase 3.4 (FIX-PLAN.md): KickStart save-sink handler. <paramref name="doing"/>
        /// is true at pre-save (Smart flushes profile + publishes WorldSaving); false at
        /// post-save (Smart publishes WorldSaved). Exceptions caught by KickStart's catch.
        /// </summary>
        private void OnEngineSave(bool doing)
        {
            if (doing)
            {
                World.WorldEventBus.Publish(new World.WorldSaving());
                if (LearningService.IsRunning)
                    LearningService.SaveProfile(LearningService.CurrentPlayerId);
            }
            else
            {
                World.WorldEventBus.Publish(new World.WorldSaved());
            }
        }

        /// <summary>
        /// Phase 3.4 + 3.5 (FIX-PLAN.md): KickStart load-sink handler. Pre-load clears
        /// per-mission state (the prior mission's beliefs, threat fields, paths, terrain
        /// snapshot) so the new mission starts clean. Post-load reloads the per-player
        /// profile.
        /// </summary>
        private void OnEngineLoad(bool doing)
        {
            if (doing)
            {
                World.WorldEventBus.Publish(new World.WorldLoading());
                OnWorldReset();
            }
            else
            {
                World.WorldEventBus.Publish(new World.WorldLoaded());
                // The loaded save's profile is reloaded by LearningService — but at v0.1.0
                // the profile path is derived from a stable _currentPlayerId, so the next
                // SaveProfile/LoadProfile cycle picks up the right file automatically.
                // Phase 5 (FIX-PLAN.md) hardens the player-id resolution for MP.
            }
        }

        // -------- per-tech lifecycle --------

        public void OnTechSpawn(TankAIHelper helper)
        {
            // [TEMP DIAGNOSTIC] Distinguish "Smart was active but bailed" from "Smart wasn't
            // even called for this spawn." If this line is missing for a new tech, the active
            // form wasn't Smart at DelayedSubscribe time. If it logs "BAIL: ..." then we know
            // Smart got called but couldn't run.
            string tankName = helper?.tank != null ? helper.tank.name : "<null-tank>";
            if (helper == null || helper.tank == null)
            {
                DebugTAC_AI.Log("Smart.OnTechSpawn[ENTRY] '" + tankName + "' BAIL: null helper or tank.");
                return;
            }
            if (!SmartRuntime.IsRunning)
            {
                DebugTAC_AI.Log("Smart.OnTechSpawn[ENTRY] '" + tankName + "' BAIL: SmartRuntime.IsRunning=false (Pool="
                    + (SmartRuntime.Pool != null ? "ok" : "null") + ", World="
                    + (SmartRuntime.World != null ? "ok" : "null") + ").");
                return;
            }
            DebugTAC_AI.Log("Smart.OnTechSpawn[ENTRY] '" + tankName + "' proceeding.");

            // RunMovementBridge in TankAIHelper gates per-frame ControlFrame on
            // RunState == AIRunState.Advanced. The default is Advanced, but other forms
            // (notably Vanilla.SetHandback) can flip it to Default. When a player switches
            // from Vanilla to Smart, existing helpers retain RunState=Default and their
            // ControlFrame never fires - meaning every Smart goal/MPC computation is
            // silently discarded at the actuator boundary. Defensive write here ensures
            // any tech entering Smart's OnTechSpawn becomes Advanced. ForceVanillaAI gets
            // cleared too in case it was set.
            try
            {
                helper.RunState = AIRunState.Advanced;
                helper.ForceVanillaAI = false;
                // RunMovementBridge gates Player-aligned ControlFrame on SetToActive =
                // lastAITypeResolved && lastAIType != Idle. Smart never resolves AI type
                // through ModifiedOperations.OperationsBegin, so Player-aligned techs stay
                // gated out and never receive Smart's ControlFrame writes. Force-resolve
                // and set a non-Idle AI type so the bridge admits Smart's writes.
                helper.lastAITypeResolved = true;
                if (helper.lastAIType == AITreeType.AITypes.Idle)
                    helper.lastAIType = AITreeType.AITypes.Escort;
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.OnTechSpawn: RunState write failed: " + ex.Message);
            }

            // [TEMP DIAGNOSTIC] Per-sub-call timing for the first SpawnTimingCap spawns to
            // bisect the init freeze. Lazy team/per-tech allocation is the prime suspect.
            // Tagged with "[TIMING]" so the lines are greppable; remove this block + the
            // sw.Restart() calls below once the cause is identified.
            bool doTime = _spawnTimingCounter < SpawnTimingCap;
            int spawnIdx = -1;
            System.Diagnostics.Stopwatch swTotal = null;
            System.Diagnostics.Stopwatch sw = null;
            if (doTime)
            {
                spawnIdx = System.Threading.Interlocked.Increment(ref _spawnTimingCounter) - 1;
                swTotal = System.Diagnostics.Stopwatch.StartNew();
                sw = System.Diagnostics.Stopwatch.StartNew();
            }

            // Team id is derived from the engine's tank.Team (see GUINPTInteraction usages
            // for the canonical accessor). Lazy-creates the TeamRuntime + starts its
            // StrategicPlanner + Coordinator workers if this is the team's first tech.
            var teamId = new TeamId(helper.tank.Team);
            var teamRuntime = SmartRuntime.GetOrCreateTeam(teamId);
            if (doTime) { DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] GetOrCreateTeam(" + teamId.Value + "): " + sw.ElapsedMilliseconds + "ms"); sw.Restart(); }

            // Build per-tech state and install in FormState.
            var state = new SmartPerTechState(
                helper.tank, helper, teamId,
                SmartRuntime.Pool,
                () => SmartRuntime.World?.FusedBuffer.Read());
            helper.FormState = state;
            if (doTime) { DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] new SmartPerTechState: " + sw.ElapsedMilliseconds + "ms"); sw.Restart(); }

            // Join the team's coordination registry — TeamRuntime's planner + coordinator
            // workers see this tech on their next tick.
            teamRuntime?.RegisterTech(state);
            if (doTime) { DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] teamRuntime.RegisterTech: " + sw.ElapsedMilliseconds + "ms"); sw.Restart(); }

            // Smart's MPC owns avoidance via its threat-field cost term + friendly-fire
            // raycast; the engine's vanilla collision-avoidance would conflict with the
            // MPC's planned trajectory. Disable it for Smart-driven techs.
            //
            // Phase 5 (FIX-PLAN.md) — AUDIT-R2 §2.R2.B: gate this on host. On a client
            // the MPC does not run, so vanilla avoidance is the only avoidance available;
            // leaving it enabled keeps client-side rendered movement sensible.
            try
            {
                if (SmartRuntime.IsHost && helper.tank?.control?.m_Movement != null)
                    helper.tank.control.m_Movement.m_USE_AVOIDANCE = false;
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart: m_USE_AVOIDANCE write failed: " + ex.Message);
            }

            // Identity Phase 1: force a synchronous vehicle-snapshot rebuild so the classifier
            // (called after World.RegisterTech below) sees real composition data instead of
            // VehicleModelSnapshot.Empty. Also gives World.RegisterTech a real maxAccelEstimate
            // from the populated thrust map instead of the 5f fallback. See
            // Docs/SMART-IDENTITY-DESIGN.md sec 7.2.
            state.RebuildVehicleSnapshotNow();
            if (doTime) { DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] RebuildVehicleSnapshotNow: " + sw.ElapsedMilliseconds + "ms"); sw.Restart(); }

            // Register with World so belief tracking begins for this tech.
            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6: the previous formula
            // (VerticalAuthority * 9.81 + 5) is unrelated to the kinematic process noise
            // it feeds. maxAccelEstimate sets the Q-matrix bound in KalmanUpdate.Propagate
            // — using vertical thrust ratio means a flat-driving wheeled tech gets
            // Q≈25 m²/s⁴ regardless of how fast it can actually accelerate forward,
            // collapsing belief uncertainty wrong for fast techs and overstating it for
            // slow ones. Use the max-magnitude linear acceleration the tech can produce
            // (positive or negative, across all body axes) — the actual physical bound.
            var thrust = state.VehicleBuffer.Read().Thrust;
            float maxAccel = UnityEngine.Mathf.Max(
                thrust.MaxLinearAccelPositive.magnitude,
                thrust.MaxLinearAccelNegative.magnitude);
            if (float.IsNaN(maxAccel) || float.IsInfinity(maxAccel) || maxAccel < 1f) maxAccel = 5f;
            if (doTime) { DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] VehicleBuffer.Read().Thrust + maxAccel: " + sw.ElapsedMilliseconds + "ms"); sw.Restart(); }
            UnityEngine.Vector3 spawnForward = helper.tank.rootBlockTrans != null
                ? helper.tank.rootBlockTrans.forward
                : UnityEngine.Vector3.forward;
            SmartRuntime.World?.RegisterTech(
                state.TechId, state.TeamId,
                helper.tank.boundsCentreWorldNoCheck,
                helper.tank.rbody != null ? helper.tank.rbody.velocity : UnityEngine.Vector3.zero,
                forward: spawnForward,
                maxAccelEstimate: maxAccel,
                currentTick: World.MonoClock.Now());
            if (doTime) { DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] World.RegisterTech: " + sw.ElapsedMilliseconds + "ms"); sw.Restart(); }

            // Identity Phase 1: classify and stamp. The classifier is a Generic-returning stub
            // at v0.1 (Phases 2-5 layer concrete rules in). Runs AFTER World.RegisterTech so any
            // identity that wants to read beliefs has the BeliefState registered. Exception-
            // isolated - a misbehaving classifier MUST NOT prevent the spawn from completing.
            try
            {
                var stamp = Identity.SmartIdentityClassifier.Classify(helper, state.VehicleBuffer.Read(), helper.tank.Team);
                var src = Identity.SmartIdentityRegistry.For(stamp.Identity);
                state.StampIdentity(stamp, src);
                DebugTAC_AI.Log("Smart.Identity: '" + (helper.tank != null ? helper.tank.name : "<null>")
                    + "' = " + stamp.Identity + (stamp.FromAuthored ? " (authored)" : " (composition)"));
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.OnTechSpawn: SmartIdentityClassifier threw: " + ex.Message + " - falling back to Generic.");
            }
            if (doTime) { DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] SmartIdentityClassifier: " + sw.ElapsedMilliseconds + "ms"); sw.Restart(); }

            // Phase 3.3 (FIX-PLAN.md): subscribe Smart to this tank's DamageEvent. The
            // bridge translates each engine damage hit into a DamageObserved publish on
            // Smart's WorldEventBus, which Learning's threat trainer + TrainingMatch's
            // outcome scorer both consume. AUDIT-R2 §2.R2.J + Plan 6 #4.
            Integration.SmartEventBridge.AttachPerTank(helper.tank);
            if (doTime)
            {
                DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] SmartEventBridge.AttachPerTank: " + sw.ElapsedMilliseconds + "ms");
                DebugTAC_AI.Log("Smart.OnTechSpawn[TIMING #" + spawnIdx + "] TOTAL for tech '" + (helper.tank != null ? helper.tank.name : "<null>") + "': " + swTotal.ElapsedMilliseconds + "ms");
            }
        }

        public void OnTechRecycle(TankAIHelper helper)
        {
            if (helper == null) return;
            if (helper.FormState is SmartPerTechState state)
            {
                // Aether/T3: single unified cleanup fan-out (see SmartRuntime.Deregister).
                SmartRuntime.Deregister(state.TechId, state.TeamId, helper, helper.tank);
            }
            else
            {
                helper.FormState = null;
            }
        }

        // -------- per-tick hooks --------

        public void Directors(TankAIHelper helper, bool host)
        {
            // v1.3: no-op.
            // Future: per ARCHITECTURE §3.2 host-authority — when host==true,
            // recalibrate movement controller via TankAIHelper.Controller.cs
            // path; when host==false, no work.
        }

        public void Operations(TankAIHelper helper, bool host)
        {
            // Phase 5 (FIX-PLAN.md): track host authority for the worker host-gate.
            SmartRuntime.IsHost = host;
            // Phase 3.2 (FIX-PLAN.md): drain any worker-published events queued for
            // main-thread dispatch. The drain is per-tick and bounded; runs even when
            // FormState is missing so non-Smart techs still flush Smart's marshal queue.
            WorldEventBus.DrainMainThreadQueue();

            // World-observation sweep. Operations is called once per Smart-driven tech per
            // physics frame, so with N Smart techs this method runs N times/frame. The sweep
            // itself is per-session work — we only need it once per ~ObservationIntervalMs. The
            // CompareExchange below ensures exactly one Operations call per interval wins the
            // sweep, even when several Smart techs hit this line in the same physics step.
            // Without this sweep, BeliefSnapshot.ByTech never sees non-Smart techs and
            // EngagementUtility returns 0 forever — the previously-observed zero-movement bug.
            ObserveWorldTechsIfDue();

            if (!(helper.FormState is SmartPerTechState state)) return;

            // [TEMP DIAGNOSTIC] Time the first OpsTimingCap Operations invocations.
            bool doTime = _opsTimingCounter < OpsTimingCap;
            int opsIdx = -1;
            System.Diagnostics.Stopwatch swOpsTotal = null;
            System.Diagnostics.Stopwatch swOps = null;
            if (doTime)
            {
                opsIdx = System.Threading.Interlocked.Increment(ref _opsTimingCounter) - 1;
                swOpsTotal = System.Diagnostics.Stopwatch.StartNew();
                swOps = System.Diagnostics.Stopwatch.StartNew();
            }

            // Phase 4 (FIX-PLAN.md): per-tech exception isolation. AUDIT-R2 §1.R2-K — one
            // bad tech (NRE inside BlockCapture or a malformed snapshot) used to break
            // the shell's per-tech iteration, stopping every OTHER tech from ticking that
            // frame. Each substantive call is now individually wrapped; per-tech failures
            // log + continue. Subsequent techs are unaffected.
            try
            {
                // Main-thread observations (kinematic + vehicle rebuild on cadence).
                state.TickMainThread(0.033f);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Operations.TickMainThread[" + state.TechId + "]: " + ex.Message);
            }
            if (doTime) { DebugTAC_AI.Log("Smart.Operations[TIMING #" + opsIdx + "] TickMainThread: " + swOps.ElapsedMilliseconds + "ms"); swOps.Restart(); }

            try
            {
                // PathingService's TerrainMap refresh runs on the main thread.
                PathingService.MainThreadTick();
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Operations.PathingTick: " + ex.Message);
            }
            if (doTime) { DebugTAC_AI.Log("Smart.Operations[TIMING #" + opsIdx + "] PathingService.MainThreadTick: " + swOps.ElapsedMilliseconds + "ms"); swOps.Restart(); }

            try
            {
                // Belief read for the controller. World's perception worker maintains
                // beliefs asynchronously; the controller reads the latest snapshot.
                var ownBelief = SmartRuntime.World?.GetPerTechBuffer(state.TechId)?.Read();
                state.Controller.OnOperationsTick(host, ownBelief);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Operations.OnOperationsTick[" + state.TechId + "]: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
            }
            if (doTime)
            {
                DebugTAC_AI.Log("Smart.Operations[TIMING #" + opsIdx + "] Controller.OnOperationsTick: " + swOps.ElapsedMilliseconds + "ms");
                DebugTAC_AI.Log("Smart.Operations[TIMING #" + opsIdx + "] TOTAL for tech '" + (helper.tank != null ? helper.tank.name : "<null>") + "': " + swOpsTotal.ElapsedMilliseconds + "ms");
            }
        }

        public void PostUpdate(TankAIHelper helper)
        {
            // v1.3: no-op.
            // Future: post-tick brain — lock-on follow-through, block-hold state
            // updates.
        }

        // -------- per-frame control bridge --------

        public void ControlFrame(TankAIHelper helper, TankControl control)
        {
            if (!(helper.FormState is SmartPerTechState state)) return;
            // Phase 4 (FIX-PLAN.md): exception-isolate the actuator write. AUDIT-R2
            // §1.R2-K — ControlFrame runs per-frame for every Smart tech; an NRE on a
            // single bad ControlProfile would otherwise abort the frame's iteration and
            // freeze every other Smart tech's control surface that frame.
            try
            {
                state.Controller.OnControlFrame(control, 0L);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.ControlFrame[" + state.TechId + "]: " + ex.Message);
            }
        }

        // -------- v2 isolation seams --------

        public void OnWorldReset()
        {
            // Phase 3.5 (FIX-PLAN.md): SHELL-API §2.6 + WORLD §7 + PATHING §3.x mandate
            // clearing per-mission state so a second mission in the same process does
            // not see polluted beliefs/threat/path/terrain. Workers (Pool, Perception,
            // Pathing rebuild loops, Planner/Coordinator per-team) STAY ALIVE — only
            // per-mission caches are cleared.
            if (!SmartRuntime.IsRunning) return;
            try
            {
                // World: clear per-tech beliefs + fused snapshot. PerceptionWorker
                // continues running and will repopulate as techs spawn in the new world.
                SmartRuntime.World?.Clear();

                // Pathing: drop the terrain map snapshot and per-team threat-field
                // caches; the rebuild loops repopulate them in the new world.
                if (PathingService.IsRunning)
                    PathingService.OnWorldReset();
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.OnWorldReset: " + ex.Message);
            }
        }

        public void DrawPathingDebugGUI()
        {
            // Phase 2.5 (FIX-PLAN.md): minimal text-only diagnostic surface. Authoring the
            // full DIAGNOSTICS-CONTRACT debug viz (belief ellipses, threat field heatmap,
            // path samples, target arrows, MPC rollout traces) is deferred to v0.2; the
            // text overlay below is enough for a developer to verify "is anything running"
            // without needing to wire up Smart.Diagnostics externally.
            if (!SmartRuntime.IsRunning) return;
            const int W = 280;
            int y = 142;
            UnityEngine.GUI.Box(new UnityEngine.Rect(4, 138, W, 110), UnityEngine.GUIContent.none);
            UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                "Smart: pool=" + (SmartRuntime.Pool != null ? SmartRuntime.Pool.WorkerCount : 0)
                + " workers, world="
                + (SmartRuntime.World != null ? "active" : "null"));
            y += 18;
            int teamCount = 0;
            int techCount = 0;
            foreach (var t in SmartRuntime.EnumerateTeams())
            {
                teamCount++;
                techCount += t.TechCount;
            }
            UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                "Teams: " + teamCount + "   Smart-driven techs: " + techCount);
            y += 18;
            if (PathingService.IsRunning)
            {
                UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                    "Pathing: running   terrain=" +
                    (PathingService.CurrentTerrain != null ? "alive" : "null"));
                y += 18;
            }
            if (LearningService.IsRunning)
            {
                UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                    "Learning: " + LearningService.CurrentPlayerId
                    + "   threatQ=" + (LearningService.Threat?.EventQueue.ApproximateCount ?? 0));
                y += 18;
            }
        }

        public IMovementAIController CreateMovementController(TankAIHelper helper, MovementContainerKind kind, EnemyMind mind)
        {
            // Per CONTROL-CONTRACT §8: thin shell, MonoBehaviour added to
            // helper.gameObject. Pattern mirrors VanillaForm.CreateMovementController.
            // Smart's MPC bypasses the movement controller's Drive* callbacks
            // (which write to EControlCoreSet) and writes TankControl directly
            // in ControlFrame (OQ-6 resolution); the controller is here only to
            // satisfy the shell's non-null IMovementAIController invariant.
            if (helper.MovementController is SmartMovementController existing)
            {
                existing.Initiate(helper.tank, helper, mind);
                return existing;
            }
            IMovementAIController previous = helper.MovementController;
            var built = helper.gameObject.AddComponent<SmartMovementController>();
            built.Initiate(helper.tank, helper, mind);
            if (previous != null) previous.Recycle();
            return built;
        }

        // Smart is not a grounded airplane controller; status text gets 0.
        public int GetAirDiveState(TankAIHelper helper) => 0;
        public int GetAirUTurnState(TankAIHelper helper) => 0;

        // -------- combat policy + per-tick combat dispatch --------
        // v1.3: inert defaults matching Vanilla's pattern. The substantive logic
        // arrives with the Planning + Control subsystems (workflow 1.6, 1.7, 1.9).

        public EAttackMode SelectEnemyAttackStrat(Tank tank, EnemyMind mind) => EAttackMode.Chase;
        public EAttackMode SelectAlliedAttackStrat(TankAIHelper helper) => EAttackMode.Chase;
        public bool EnemyHasArtillery(BlockManager bm) => false;

        public void RunEnemyCombatChecking(TankAIHelper helper, Tank tank, EnemyMind mind) { }
        public void RunEnemyMonitor(TankAIHelper helper, Tank tank, EnemyMind mind) { }
        public void RunEnemyBolts(TankAIHelper helper, Tank tank, EnemyMind mind) { }
        public void RunEnemyRepairStep(TankAIHelper helper, Tank tank, EnemyMind mind, bool venPower) { }
        public void RunEnemyRTSCombat(TankAIHelper helper, Tank tank, EnemyMind mind) { }

        // -------- per-tech combat/role dispatch --------

        public void RunEnemy(AIContext ctx, EnemyMind mind)
        {
            // v1.3: no-op.
            // Future: per ARCHITECTURE §3.2 host-authority, only run when host;
            // dispatch to Planning + Coordination + Control via AIContext bus.
        }

        public void RunAllied(AIContext ctx)
        {
            // v1.3: no-op.
            // Future: same as RunEnemy but for friendly techs.
        }

        // -------- world observation sweep --------

        // Throttle for ObserveWorldTechsIfDue. ~33 ms gives ~30 Hz observation cadence which
        // matches the engine's fixed-update rate; KalmanUpdate.Propagate will fold each
        // observation into the per-tech belief and decay variance accordingly. Faster would
        // multiply observation work for no Kalman benefit (the propagation step is what
        // expands the variance between observations).
        private const int ObservationIntervalMs = 33;
        private static int _lastObservationTickMs = int.MinValue;
        // Reused buffer to avoid per-tick allocation when calling RegisterExternalTech for
        // newly-seen techs. PerceptionWorker's main loop is the hot path; allocation here
        // would compound across thousands of frames.
        private static readonly System.Collections.Generic.List<Tank> _observationScratch =
            new System.Collections.Generic.List<Tank>(32);
        // Reused buffers for the orphan-sweep. Live-tech IDs gathered in the observation
        // pass; world-side orphans collected once per sweep then deregistered.
        private static readonly System.Collections.Generic.HashSet<World.TechId> _liveTechIds =
            new System.Collections.Generic.HashSet<World.TechId>();
        private static readonly System.Collections.Generic.List<World.TechId> _worldOrphansBuffer =
            new System.Collections.Generic.List<World.TechId>(8);

        /// <summary>
        /// Walks ManTechs.IterateTechs() and records a position observation for every tech
        /// the world model knows about. Throttled by <see cref="ObservationIntervalMs"/>;
        /// only the first Operations call inside each interval window does the work, even
        /// when several Smart-driven techs invoke Operations on the same physics frame.
        ///
        /// Also opportunistically registers techs the bridge hasn't seen yet — typically
        /// won't fire (TankPostSpawnEvent + the Install-time sweep already cover spawn) but
        /// protects against a race window where a tech exists in ManTechs.IterateTechs but
        /// hasn't yet been registered by the bridge.
        /// </summary>
        private static void ObserveWorldTechsIfDue()
        {
            int now = System.Environment.TickCount;
            int last = System.Threading.Volatile.Read(ref _lastObservationTickMs);
            // The TickCount wrap (~25 days) makes (now - last) potentially negative; treat
            // that as "due" since the interval has definitely elapsed.
            if (last != int.MinValue && unchecked(now - last) >= 0 && unchecked(now - last) < ObservationIntervalMs) return;
            // Race guard: only one caller per interval wins. Others see CompareExchange fail
            // (because someone else already advanced _lastObservationTickMs) and return.
            if (System.Threading.Interlocked.CompareExchange(ref _lastObservationTickMs, now, last) != last) return;

            try
            {
                if (SmartRuntime.World == null) return;
                if (ManTechs.inst == null) return;

                _observationScratch.Clear();
                foreach (var tank in ManTechs.inst.IterateTechs())
                {
                    if (tank == null) continue;
                    _observationScratch.Add(tank);
                }

                // Build a set of currently-live tech IDs while we iterate so we can detect
                // orphans below without a second engine walk.
                _liveTechIds.Clear();
                foreach (var tank in _observationScratch)
                {
                    var techId = World.TechId.FromTank(tank);
                    _liveTechIds.Add(techId);
                    // Lazy-register if the bridge hasn't seen this tech yet (shouldn't normally
                    // happen — TankPostSpawnEvent + the Install-time sweep cover spawn — but
                    // safe to handle the race).
                    if (!SmartRuntime.World.IsRegistered(techId))
                    {
                        Integration.SmartEventBridge.RegisterExternalTech(tank);
                    }
                    UnityEngine.Vector3 pos = tank.boundsCentreWorldNoCheck;
                    UnityEngine.Vector3 vel = tank.rbody != null ? tank.rbody.velocity : UnityEngine.Vector3.zero;
                    UnityEngine.Vector3 fwd = tank.rootBlockTrans != null
                        ? tank.rootBlockTrans.forward
                        : UnityEngine.Vector3.forward;
                    World.Observer.Submit(
                        SmartRuntime.World.Intake, techId, pos, vel, fwd, World.MonoClock.Now());
                }

                // Orphan sweep. Techs purged by the engine (PurgeHost, fall-out-of-world,
                // scene unload) don't always fire TankDestroyedEvent, so the bridge's
                // OnTankDestroyed cleanup misses them. Without periodic reclamation, World
                // and TeamRuntime accumulate dead-tech entries; Coordinator's BuildTeamSnapshot
                // pass grows linearly, MPC threat-field queries spend cycles on corpses,
                // and over a play session the system "goes stale" - new techs get crowded
                // out of the worker pool's effective throughput.
                _worldOrphansBuffer.Clear();
                var perTechCopy = SmartRuntime.World.SnapshotPerTechState();
                foreach (var kv in perTechCopy)
                {
                    if (!_liveTechIds.Contains(kv.Key))
                        _worldOrphansBuffer.Add(kv.Key);
                }
                for (int i = 0; i < _worldOrphansBuffer.Count; i++)
                {
                    var orphanId = _worldOrphansBuffer[i];
                    // Aether/T3: single unified cleanup fan-out (see SmartRuntime.Deregister).
                    // Engine purged the GameObject so helper/tank are unsafe; pass nulls.
                    World.TeamId teamId = default(World.TeamId);
                    if (perTechCopy.TryGetValue(orphanId, out var entry) && entry.PriorBelief != null)
                        teamId = entry.PriorBelief.Team;
                    SmartRuntime.Deregister(orphanId, teamId, null, null);
                }
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.ObserveWorldTechs: " + ex.Message);
            }
            finally
            {
                _observationScratch.Clear();
            }
        }
    }
}
