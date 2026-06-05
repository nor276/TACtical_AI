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
            // L-007: SmartLifecycleShim MUST install BEFORE SmartRuntime.Init so
            // OnApplicationPause hits a live runtime. Install is idempotent on SetActive
            // re-entry (form-swap → DeInitGlobal → InitGlobal); the DontDestroyOnLoad
            // GameObject is re-found by name and reused.
            try { SmartLifecycleShim.Install(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.InitGlobal: SmartLifecycleShim.Install failed: " + ex.Message); }
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
            // P10 Item 36 (REV 7): install the F8 live-tweaker panel alongside the dev
            // driver. Same idempotent shape — adds a DontDestroyOnLoad MonoBehaviour that
            // owns its IMGUI window. SmartConsoleCommands registers automatically via
            // [DevCommand] attribute scan (no Install needed for those).
            try { Tooling.LiveTweakerPanel.Install(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.InitGlobal: LiveTweakerPanel install failed: " + ex.Message); }
#endif
        }

        public void DeInitGlobal()
        {
            // Tear down workers, clear event bus subscribers, dispose world model.
            // Per THREADING §7 + ARCHITECTURE §5 I3 (all workers terminate within DeInitGlobal).
            KickStart.SaveSink -= OnEngineSave;
            KickStart.LoadSink -= OnEngineLoad;
            Integration.SmartEventBridge.Uninstall();
#if SMART_DEV
            // P10 Item 36 (REV 7): mirror the InitGlobal install. Uninstall is idempotent.
            try { Tooling.LiveTweakerPanel.Uninstall(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.DeInitGlobal: LiveTweakerPanel uninstall failed: " + ex.Message); }
#endif

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
            // L-007: SmartLifecycleShim Uninstall AFTER Shutdown so any pause that fires
            // mid-Uninstall is benign (Shutdown already nulled Pool / cancelled workers).
            try { SmartLifecycleShim.Uninstall(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.DeInitGlobal: SmartLifecycleShim.Uninstall failed: " + ex.Message); }
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
                {
                    // L-032: uniform tag so the manual-save path is greppable distinct from
                    // L-060 [PROFILE-AUTOSAVE] and L-078 [PROFILE-QUIT-SAVE].
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    LearningService.SaveProfile(LearningService.CurrentPlayerId);
                    DebugTAC_AI.LogWarnFileOnly("profile-manual-save",
                        "[PROFILE-MANUAL-SAVE] player=" + LearningService.CurrentPlayerId
                        + " elapsed=" + sw.ElapsedMilliseconds + "ms");
                }
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
            // L-082: the prior [TEMP DIAGNOSTIC] block is subsumed by the unified
            // [TECH-ROUTE] log emitted from AIFormRegistry.RouteTech (L-027). Per-form
            // BAIL distinctions are now visible via Routing.Reason on the helper +
            // LiveRoutings overlay (L-053). The runtime-down case below becomes a funnel
            // fallback (Reason=SmartRuntimeNotRunning) rather than a silent log line.
            if (helper == null || helper.tank == null) return;
            if (!SmartRuntime.IsRunning)
            {
                // Stamp the reason on the helper so the orphan catcher (L-081) sees this
                // tech is intentionally unrouted (not a dropped Invoke).
                helper.Routing = new TAC_AI.AI.Forms.RoutingDecision
                {
                    FormId = null,
                    TimestampMs = System.Environment.TickCount,
                    Reason = "SmartRuntimeNotRunning",
                };
                return;
            }

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
            // L-019: TeamRuntime.RegisterTech now returns bool; refuse on Draining/Disposed
            // teams (team-changed during this spawn cycle, or team reaped under us). On
            // refusal we drop FormState + DetachPerTank so the half-initialised state
            // doesn't leak — the engine's Modified form will pick up the next frame's
            // routing reclaim (TR-4 / L-051).
            bool registered = teamRuntime != null && teamRuntime.RegisterTech(state);
            if (!registered)
            {
                DebugTAC_AI.LogWarnFileOnly("smart-onspawn-team-refused-" + state.TechId.Value,
                    "Smart.OnTechSpawn: team refused RegisterTech (team="
                    + teamId.Value + " state="
                    + (teamRuntime != null ? teamRuntime.State.ToString() : "<null-runtime>")
                    + ") — aborting spawn");
                helper.FormState = null;
                try { Integration.SmartEventBridge.DetachPerTank(helper.tank); }
                catch { /* tank may already be partially recycled */ }
                return;
            }
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

            // Identity: classify and stamp. P11 Item 67 (doc honesty pass): the classifier
            // is no longer a Generic-returning stub — as of P6 it routes through 8 rules
            // (Base / Gatherer / AircraftSupport / AircraftHunter / Sniper / RepairSupport /
            // Patrol / Hunter / Generic) per SmartIdentityClassifier.cs. Runs AFTER
            // World.RegisterTech so any identity that wants to read beliefs has the
            // BeliefState registered. Exception-isolated — a misbehaving classifier MUST NOT
            // prevent the spawn from completing.
            try
            {
                var vehicleSnap = state.VehicleBuffer.Read();
                var stamp = Identity.SmartIdentityClassifier.Classify(helper, vehicleSnap, helper.tank.Team);
                var src = Identity.SmartIdentityRegistry.For(stamp.Identity);
                state.StampIdentity(stamp, src);
                DebugTAC_AI.Log("Smart.Identity: '" + (helper.tank != null ? helper.tank.name : "<null>")
                    + "' = " + stamp.Identity + (stamp.FromAuthored ? " (authored)" : " (composition)"));

                // P11 T1 Item 47: Identity parity gate. Re-classify with all SmartIdentityTuning
                // flags forced false to get the v0.1 baseline, then emit the (v0.1, v0.2) pair
                // to the gate. The gate is a no-op when the two identities match (most techs);
                // only flag-flip reroutes log. Per-tech dedup, so this only fires once per tech.
                // Only run the second classify when AT LEAST ONE flag is on — when all flags are
                // off, v0.1 == v0.2 trivially and we can skip the recompute entirely.
                if (Identity.SmartIdentityTuning.EnableRepairSupport
                    || Identity.SmartIdentityTuning.EnablePatrol
                    || Identity.SmartIdentityTuning.UseHarvesterModulesForGatherer)
                {
                    var v01Stamp = Identity.SmartIdentityClassifier.ClassifyWithFlags(
                        helper, vehicleSnap, helper.tank.Team,
                        enableRepairSupport: false, enablePatrol: false, useHarvesterModulesForGatherer: false);
                    Identity.IdentityClassifierParityGate.TryEmitOnce(
                        state.TechId.Value, v01Stamp.Identity, stamp.Identity,
                        helper.tank != null ? helper.tank.name : null);
                }
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
            // L-057: notify HostAuthorityCoordinator AFTER the SmartRuntime.IsHost write so
            // its edge-detect sees the new value on this same tick. Publishes HostChanged
            // via WorldEventBus when the debounced transition completes.
            Coordination.HostAuthorityCoordinator.Notify();
            // L-072: WorkerHealthMonitor tick (debounced internally to 1s) — surfaces
            // [SMART-WORKERS-DEAD] when canonical daemons go missing + auto-respawns.
            Threading.WorkerHealthMonitor.Tick();
            // L-049: DaemonWatchdog scan (debounced internally to 1s) — checks the 9-name
            // canonical roster + respawns missing daemons.
            Threading.DaemonWatchdog.ScanAndRespawn();
            // L-081: RoutingCompletenessCatcher tick (debounced internally to 5s) — finds
            // helpers with FormId=null + age >= 1s and self-heals via funnel.
            Tests.RoutingCompletenessCatcher.MainThreadTick();
            // Phase 3.2 (FIX-PLAN.md): drain any worker-published events queued for
            // main-thread dispatch. The drain is per-tick and bounded; runs even when
            // FormState is missing so non-Smart techs still flush Smart's marshal queue.
            WorldEventBus.DrainMainThreadQueue();

            // P5 Item 23: LOS producer tick. Default OFF (no-op); when Enabled, computes
            // round-robin per-team LOS via Physics.Raycast and publishes per-team snapshot
            // that TeamRuntime.BuildTeamSnapshot (worker side) reads via SnapshotForTeam.
            // Has its own internal interval guard (TickIntervalMs=150ms); safe to call
            // every Operations frame.
            Coordination.LineOfSightProducer.MainThreadTick();

            // P10 Item 41 (REV 7): periodic VEHICLE-PARITY regression sweep. Default OFF
            // (no-op); when Tests.VehicleParityCatcher.Enabled is flipped to true (e.g.
            // during a spawn-test campaign), walks every loaded tech at 30s cadence and
            // gives newly-spawned techs a parity verdict. Per-gate dedup means previously
            // checked techs cost nothing.
            Tests.VehicleParityCatcher.MainThreadTick();

            // P11 T2 Items 50 + 52: LearningService periodic tick. Internal interval guards
            // run the observation-buffer drain at 1 Hz and the [OUTCOMES] log line at 1/60 Hz.
            // ownAnchorWorld = local player's current tech position when available; lets the
            // heuristic intent labeler compute closing-rate-based Aggressing / Retreating
            // labels. Falls back to Vector3.zero (labeler then skips those two classes).
            try
            {
                UnityEngine.Vector3 ownAnchor = UnityEngine.Vector3.zero;
                var net = ManNetwork.inst;
                if (net != null && net.MyPlayer != null && net.MyPlayer.CurTech != null
                    && net.MyPlayer.CurTech.tech != null)
                {
                    ownAnchor = net.MyPlayer.CurTech.tech.boundsCentreWorldNoCheck;
                }
                LearningService.PeriodicTick(ownAnchor);
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("learning-tick-err",
                    "LearningService.PeriodicTick threw " + ex.GetType().Name + ": " + ex.Message);
            }

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

            // P11 T8 Item 64: ControlFrame-output recorder. No-op when FrameRecorder.Enabled
            // is false (default). When recording, snapshot the published ControlProfile (the
            // exact values the controller just wrote into TankControl) per tech per frame.
            // Hooked here AFTER OnControlFrame so the recording reflects what the actuators
            // actually saw — not intermediate publishes from the MPC worker.
            try
            {
                if (Tests.FrameRecorder.Enabled)
                {
                    var profile = state.Controller.ControlProfileBuffer.Read();
                    var step = profile.Steps != null && profile.Steps.Count > 0
                        ? profile.Steps[0]
                        : Control.ControlVector.Zero;
                    Tests.FrameRecorder.RecordControlFrame(
                        state.TechId.Value,
                        World.MonoClock.Now(),
                        throttle: step.Throttle,
                        steer: step.Steer,
                        brake: step.Brake,
                        fireAny: profile.FireAny,
                        aimPointWorld: profile.AimPointWorld,
                        aimRadius: profile.AimRadiusWorld);
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("frame-recorder-hook",
                    "Smart.ControlFrame[" + state.TechId + "] FrameRecorder hook: " + ex.Message);
            }
        }

        // -------- v2 isolation seams --------

        public void OnWorldReset()
        {
            // L-069: collapsed to publish WorldResetting → ResetAll → publish WorldResetCompleted.
            // Every reset hook (WorldModel.Clear, PathingService caches, backpressure state,
            // TerrainMap, IWorldResettable consumers) registered with WorldResetRegistry at
            // Init. Grep `[WORLD-RESET]` returns ≥7 lines per cycle: 1 starting + per-hook
            // + 1 completed/partial.
            if (!SmartRuntime.IsRunning) return;
            long tickMono = World.MonoClock.Now();
            try { World.WorldEventBus.Publish(new World.WorldResetting(tickMono)); }
            catch (System.Exception ex) { DebugTAC_AI.LogWarning("Smart.OnWorldReset.Publish(Resetting): " + ex.Message); }

            // Also register the legacy World + Pathing resets as inline lambdas so the
            // unified registry walk is the single ResetAll call. (These two were not
            // registered as IWorldResettable in Wave 1; doing so here would be a static
            // re-Init concern. Use a one-time gate.)
            EnsureLegacyResetsRegistered();

            int ok = Pathing.WorldResetRegistry.ResetAll();
            int total = Pathing.WorldResetRegistry.Count;

            try { World.WorldEventBus.Publish(new World.WorldResetCompleted(World.MonoClock.Now(), ok, total)); }
            catch (System.Exception ex) { DebugTAC_AI.LogWarning("Smart.OnWorldReset.Publish(Completed): " + ex.Message); }
        }

        // L-069: one-time registration of legacy reset paths into WorldResetRegistry so
        // OnWorldReset can collapse to a single ResetAll call.
        private static int _legacyResetsRegistered;
        private static void EnsureLegacyResetsRegistered()
        {
            if (System.Threading.Interlocked.Exchange(ref _legacyResetsRegistered, 1) == 1) return;
            Pathing.WorldResetRegistry.RegisterLambda("WorldModel.Clear",
                () => SmartRuntime.World?.Clear());
            Pathing.WorldResetRegistry.RegisterLambda("PathingService.OnWorldReset",
                () => { if (PathingService.IsRunning) PathingService.OnWorldReset(); });
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
            UnityEngine.GUI.Box(new UnityEngine.Rect(4, 138, W, 182), UnityEngine.GUIContent.none);  // L-038/L-065/L-073: +backpressure +lifetime +workers
            UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                "Smart: pool=" + (SmartRuntime.Pool != null ? SmartRuntime.Pool.WorkerCount : 0)
                + " workers, world="
                + (SmartRuntime.World != null ? "active" : "null"));
            y += 18;
            // L-073: live/expected workers line. Counts live registry entries; reads
            // expected from WorkerHealthMonitor (canonical count = 7 daemons typically).
            // Color-codes red when degraded; second line lists missing names truncated to box width.
            try
            {
                int liveCount = Threading.WorkerLifecycleRegistry.SnapshotLive().Count;
                int expected = Threading.WorkerHealthMonitor.ExpectedCount;
                bool degraded = expected > 0 && liveCount < expected;
                var prior = UnityEngine.GUI.contentColor;
                UnityEngine.GUI.contentColor = degraded ? UnityEngine.Color.red : new UnityEngine.Color(0.6f, 1f, 0.6f);
                UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                    "Workers: " + liveCount + "/" + (expected > 0 ? expected.ToString() : "?")
                    + (degraded ? " DEGRADED" : " alive"));
                UnityEngine.GUI.contentColor = prior;
                y += 18;
            }
            catch (System.Exception) { /* defensive — never block GUI */ }
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
            // L-065: TeamReaperDaemon lifetime counters — created/evicted help operator
            // verify "are old teams being reaped or accumulating forever?" without grep.
            UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                "  Lifetime: created=" + Coordination.TeamReaperDaemon.TeamsCreatedObserved
                + "  evicted=" + Coordination.TeamReaperDaemon.TeamsEvictedTotal);
            y += 18;
            if (PathingService.IsRunning)
            {
                UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                    "Pathing: running   terrain=" +
                    (PathingService.CurrentTerrain != null ? "alive" : "null"));
                y += 18;
                // L-038: backpressure block. Surfaces shed-state + queue + dropped/expired
                // + p50/p99 so the operator can see backpressure trend before shed activates.
                var bp = PathingService.Backpressure;
                if (bp != null)
                {
                    UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                        "  Queue: " + bp.QueueDepth + "/" + bp.QueueCapacity
                        + (bp.ShedActive ? "  SHED:ON" : "  SHED:off")
                        + "  Drop=" + bp.DroppedTotal + " Exp=" + bp.ExpiredTotal);
                    y += 18;
                    UnityEngine.GUI.Label(new UnityEngine.Rect(10, y, W - 12, 18),
                        "  Latency: p50=" + bp.SolveLatencyMsP50.ToString("0.0")
                        + "ms p99=" + bp.SolveLatencyMsP99.ToString("0.0") + "ms");
                    y += 18;
                }
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

                    // P6 Item 26: tank-wide HP via block-survival ratio. Pattern matches
                    // TankAIHelper.cs:3267's production multi-block fallback:
                    // (1 - blockC/maxBlockCount)*100 -> HpFraction = blockC/maxBlockCount.
                    // HealthSidecar tracks per-tech max internally (ratchets upward on growth).
                    if (tank.blockman != null)
                    {
                        int bc = tank.blockman.blockCount;
                        if (bc > 0) SmartRuntime.Health?.Record(techId, bc);
                    }

                    // P7 Item 18: trigger residual-recorder observation match. Per-tank
                    // call is cheap (ConcurrentDictionary TryGet fast-path when no pending
                    // fire-commit exists for this target). Same 33ms cadence as Observer.Submit.
                    var recorder = World.MonoClock.Now() == 0L ? null
                        : Learning.LeadResidualRecorder.Instance;
                    if (recorder != null)
                        recorder.OnObservation(techId, pos, World.MonoClock.Now());
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
                    bool teamResolved = false;
                    if (perTechCopy.TryGetValue(orphanId, out var entry) && entry.PriorBelief != null)
                    {
                        teamId = entry.PriorBelief.Team;
                        teamResolved = true;
                    }
                    // L-011: last-resort TeamId recovery — PriorBelief invariantly non-null
                    // today but a future regression would silently pass default(TeamId) to
                    // Deregister and miss per-team state cleanup. Walk teams as fallback.
                    if (!teamResolved)
                    {
                        if (SmartRuntime.FindTeamForTech(orphanId, out var foundTeam))
                        {
                            teamId = foundTeam;
                            teamResolved = true;
                        }
                        else
                        {
                            DebugTAC_AI.LogWarnFileOnly("tech-leak-no-team-" + orphanId.Value,
                                "[TECH-LEAK-NO-TEAM] orphanId=" + orphanId.Value
                                + " — PriorBelief null AND no TeamRuntime owns it; per-team cleanup skipped");
                        }
                    }
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
