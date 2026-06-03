using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Coordination;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Planning;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart
{
    /// <summary>
    /// Smart's per-tech state object. Stored opaquely in TankAIHelper.FormState by SmartForm.
    /// One instance per Smart-driven tech. Per CONTROL-CONTRACT §2.1.
    ///
    /// Holds the Coordination-published external tactical goal buffer (with freshness TTL).
    /// ContinuousController prefers a fresh external goal over running its own tactical
    /// optimizer; absent or stale, it falls back per CONTROL §6.
    /// </summary>
    internal sealed class SmartPerTechState
    {
        public Tank Tank { get; }
        public TankAIHelper Helper { get; }
        public TechId TechId { get; }
        /// <summary>
        /// Per FIX-PLAN.md Phase 3.3 + AUDIT-R2 §2.R2.B: TeamId is mutable so a tech
        /// captured / converted at runtime can be moved between team registries. The
        /// setter is internal — only <see cref="UpdateTeam"/> may write it, called from
        /// <see cref="SmartEventBridge"/> when the engine raises a team-changed event.
        /// </summary>
        public TeamId TeamId { get; private set; }
        internal void UpdateTeam(TeamId newTeam) { TeamId = newTeam; }

        public KinematicTracker KinematicTracker { get; }
        public DoubleBuffer<KinematicState> KinematicBuffer { get; }
        public DoubleBuffer<VehicleModelSnapshot> VehicleBuffer { get; }
        public DoubleBuffer<TacticalGoal> ExternalGoalBuffer { get; }
        public DoubleBuffer<TargetAssignment> ExternalTargetBuffer { get; }
        public ContinuousController Controller { get; }

        public long LastObservationTick { get; private set; }

        // Coordination publishes per-tech goals + per-tech target assignments on its
        // own cadence (~300ms). Both are accepted as "fresh" within ExternalGoalMaxAgeMs.
        private const int ExternalGoalMaxAgeMs = 1000;
        private int _externalGoalWriteMs = int.MinValue;
        private int _externalTargetWriteMs = int.MinValue;
        // Reference to the owning team's runtime — used to read friendly-positions for
        // friendly-fire avoidance in the weapon-fire controller. Set after construction
        // by TeamRuntime.RegisterTech.
        internal TeamRuntime OwnerTeam;

        // Per-tech identity stamped once by SmartIdentityClassifier at OnTechSpawn. Drives
        // identity-specific goal sources via the ContinuousController fallback path. Generic
        // is the safe default - the controller bypasses the goal-source dispatch entirely for
        // Generic and calls TacticalOptimizer.Step inline. See Docs/SMART-IDENTITY-DESIGN.md.
        public SmartIdentityStamp IdentityStamp { get; private set; } = SmartIdentityStamp.DefaultGeneric;
        public ISmartGoalSource GoalSource { get; private set; }

        // Stamped once by SmartForm.OnTechSpawn. Identity is immutable per design L2.
        // Second call asserts in debug to catch accidental reclassification.
        internal void StampIdentity(SmartIdentityStamp stamp, ISmartGoalSource src)
        {
            IdentityStamp = stamp;
            GoalSource = src;
        }

        public SmartPerTechState(
            Tank tank,
            TankAIHelper helper,
            TeamId teamId,
            WorkerPool pool,
            Func<BeliefSnapshot> readBeliefs)
        {
            Tank = tank;
            Helper = helper;
            TechId = TechId.FromTank(tank);
            TeamId = teamId;

            KinematicTracker = new KinematicTracker();
            KinematicBuffer = new DoubleBuffer<KinematicState>(KinematicState.Zero);
            VehicleBuffer = new DoubleBuffer<VehicleModelSnapshot>(VehicleModelSnapshot.Empty(TechId.Value));
            ExternalGoalBuffer = new DoubleBuffer<TacticalGoal>(TacticalGoal.AtCurrent(Vector3.zero, 0f));
            ExternalTargetBuffer = new DoubleBuffer<TargetAssignment>(TargetAssignment.None);

            Controller = new ContinuousController(
                pool, teamId, VehicleBuffer, KinematicBuffer, readBeliefs,
                readExternalGoal: TryReadFreshExternalGoal,
                readCoordinationTarget: TryReadFreshExternalTargetId,
                readFriendlyPositions: ReadFriendlyPositionsExcludingSelf,
                readGoalSource: ReadGoalSource,
                readIdentityCtx: BuildIdentityContext,
                // Per-tech seed for SamplingMPC sample independence (Phase 7 / AUDIT R1 2.4).
                // Phase 11 (verify-build): 2654435761 (Knuth's multiplicative-hash constant)
                // exceeds int.MaxValue so the literal is parsed as long, making the
                // expression long-typed and rejecting the implicit narrowing to int. Use
                // explicit uint arithmetic with an unchecked cast to int — wraps cleanly,
                // preserves the hash mixing semantics, and stays inside int.
                mpcSeed: unchecked((int)((uint)TechId.Value * 2654435761u + (uint)teamId.Value)));
        }

        // Delegate exposed to ContinuousController. Returns the per-tech goal source (set by
        // StampIdentity) or null if identity is Generic / not yet stamped. Phase 1: always null
        // because the registry is empty and every tech classifies Generic - controller falls
        // through to TacticalOptimizer.Step unchanged from today's behavior.
        private ISmartGoalSource ReadGoalSource() => GoalSource;

        // Builds the per-tick IdentityContext consumed by goal sources. TeamCentroid is only
        // valid when HasAllies is true (TeamRuntime.TryGetCentroid is the truth source).
        // Main thread only.
        private IdentityContext BuildIdentityContext()
        {
            bool hasAllies = false;
            Vector3 centroid = Vector3.zero;
            var team = OwnerTeam;
            if (team != null) hasAllies = team.TryGetCentroid(TechId, out centroid);
            // Tick counter from controller's internal counter is not directly accessible; use
            // LastObservationTick as a per-tech deterministic seed for meander phase. Good enough
            // for Phase 1 (no identity reads it yet).
            return new IdentityContext(TechId, IdentityStamp, centroid, hasAllies,
                nearestOwnBaseId: null, tickCounter: LastObservationTick);
        }

        /// <summary>
        /// Called by TeamRuntime.PublishGoals on the Coordinator worker thread. Marks the
        /// goal fresh as of <see cref="Environment.TickCount"/>.
        /// </summary>
        public void WriteExternalGoal(TacticalGoal goal)
        {
            ExternalGoalBuffer.Write(goal);
            Interlocked.Exchange(ref _externalGoalWriteMs, Environment.TickCount);
        }

        /// <summary>
        /// Returns the latest external goal if it was written within
        /// <see cref="ExternalGoalMaxAgeMs"/>, else null. Called from
        /// ContinuousController on the main (Operations) thread each tick.
        /// </summary>
        public TacticalGoal? TryReadFreshExternalGoal()
        {
            int stamp = Volatile.Read(ref _externalGoalWriteMs);
            if (stamp == int.MinValue) return null;
            // Environment.TickCount wraps every ~25 days; the difference is correct
            // for short intervals (we cap at 1 s).
            int ageMs = unchecked(Environment.TickCount - stamp);
            if (ageMs < 0 || ageMs > ExternalGoalMaxAgeMs) return null;
            return ExternalGoalBuffer.Read();
        }

        public void WriteExternalTarget(TargetAssignment target)
        {
            ExternalTargetBuffer.Write(target);
            Interlocked.Exchange(ref _externalTargetWriteMs, Environment.TickCount);
        }

        public TechId? TryReadFreshExternalTargetId()
        {
            int stamp = Volatile.Read(ref _externalTargetWriteMs);
            if (stamp == int.MinValue) return null;
            int ageMs = unchecked(Environment.TickCount - stamp);
            if (ageMs < 0 || ageMs > ExternalGoalMaxAgeMs) return null;
            var assignment = ExternalTargetBuffer.Read();
            return assignment.IsValid ? assignment.TargetId : (TechId?)null;
        }

        /// <summary>
        /// Per CONTROL §10.7 — returns positions of friendly Smart-driven techs on the
        /// same team for friendly-fire raycast. Excludes self. v0.1.0 uses kinematic-buffer
        /// position (most recent main-thread observation); slightly stale but main-thread
        /// reads are cheap and the ray test forgives small offsets via FriendlyFireRadius.
        /// </summary>
        public System.Collections.Generic.List<Vector3> ReadFriendlyPositionsExcludingSelf()
        {
            var team = OwnerTeam;
            if (team == null) return null;
            return team.SnapshotFriendlyPositionsExcluding(TechId);
        }

        /// <summary>Main-thread tick: refresh kinematic, vehicle snapshot. Called from SmartForm.Operations.</summary>
        public void TickMainThread(float dt)
        {
            KinematicTracker.Observe(Tank, dt);
            KinematicBuffer.Write(KinematicTracker.Latest);

            // Vehicle rebuild on demand. v0.1.0: every Nth tick; later: debounced on block events.
            LastObservationTick++;
            if (LastObservationTick % 30 == 0)
            {
                RebuildVehicleSnapshotInternal();
            }
        }

        /// <summary>
        /// Force a synchronous vehicle-snapshot rebuild bypassing the % 30 tick gate. Called
        /// from SmartForm.OnTechSpawn so SmartIdentityClassifier sees real composition data
        /// (not VehicleModelSnapshot.Empty) and downstream consumers like World.RegisterTech's
        /// maxAccelEstimate read get a real value from frame 0 instead of the 5f fallback.
        /// </summary>
        public void RebuildVehicleSnapshotNow()
        {
            // Observe kinematics first so the snapshot carries fresh kinematic state.
            KinematicTracker.Observe(Tank, 0f);
            KinematicBuffer.Write(KinematicTracker.Latest);
            RebuildVehicleSnapshotInternal();
        }

        // Shared rebuild body. Called from TickMainThread on the % 30 boundary and from
        // RebuildVehicleSnapshotNow at spawn. Main thread only.
        private void RebuildVehicleSnapshotInternal()
        {
            // [TEMP DIAGNOSTIC] Time the first VehicleRebuildTimingCap full vehicle-rebuild
            // passes (the heavy synchronous block-introspection path: BlockCapture +
            // MassDistribution + ThrustMap + WeaponProfileBuilder + ArmorMap + MobilityProfile).
            // Strong candidate for the spawn freeze if first rebuild on a large tech is heavy.
            // Tagged with "[TIMING]" for grep; remove with the rest of the diagnostic block.
            bool doTime = System.Threading.Interlocked.Increment(ref VehicleRebuildTimingCounter) <= VehicleRebuildTimingCap;
            int rebuildIdx = doTime ? VehicleRebuildTimingCounter - 1 : -1;
            System.Diagnostics.Stopwatch swRebuildTotal = doTime ? System.Diagnostics.Stopwatch.StartNew() : null;
            System.Diagnostics.Stopwatch swRebuild = doTime ? System.Diagnostics.Stopwatch.StartNew() : null;

            // Chassis (Step 8a/8b): production pipeline only. ChassisCapture walks the tank
            // once, populating the typed BlockCatalog lazily; mass/thrust/weapons/armor all
            // consume the BlockInstancePose[] directly. No BlockObservation, no role-override.
            var poses = Vehicle.ChassisCapture.Capture(Tank, SmartRuntime.BlockCatalog);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] tech '" + (Tank != null ? Tank.name : "<null>") + "' ChassisCapture (" + (poses != null ? poses.Length : 0) + " poses): " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var mass = MassDistribution.Compute(poses);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] MassDistribution: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var thrust = Vehicle.ThrustField.Compute(poses, SmartRuntime.BlockCatalog, mass.TotalMass);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] ThrustField: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var weapons = WeaponProfileBuilder.Build(poses, SmartRuntime.BlockCatalog);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] WeaponProfileBuilder: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var armor = ArmorMap.Compute(poses, new Vector3Int(8, 8, 8));
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] ArmorMap: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var mobility = MobilityProfile.Derive(mass, thrust, armor);
            if (doTime) { DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] MobilityProfile: " + swRebuild.ElapsedMilliseconds + "ms"); swRebuild.Restart(); }
            var kindCounts = Vehicle.BlockKindCounts.From(poses, SmartRuntime.BlockCatalog);
            var snapshot = new VehicleModelSnapshot(
                TechId.Value,
                LastObservationTick,
                mass, thrust, weapons, armor,
                KinematicTracker.Latest, mobility,
                kindCounts);
            VehicleBuffer.Write(snapshot);
            if (doTime) DebugTAC_AI.Log("Smart.VehicleRebuild[TIMING #" + rebuildIdx + "] TOTAL: " + swRebuildTotal.ElapsedMilliseconds + "ms");
        }

        // [TEMP DIAGNOSTIC] Static counter so a session-wide cap of timing logs avoids spam
        // when many techs hit their 30-tick rebuild boundaries together. Lives at the type
        // level rather than instance so all techs share the cap.
        internal static int VehicleRebuildTimingCounter = 0;
        internal const int VehicleRebuildTimingCap = 10;
    }

    /// <summary>
    /// Per-team workers and tech registry. One instance per active team observed
    /// by Smart. Owns this team's StrategicPlanner + Coordinator + per-tech registry,
    /// and supplies the cross-thread snapshots both workers consume.
    ///
    /// Construction starts the long-running planner/coordinator workers via the
    /// shared WorkerPool. Shutdown happens via WorkerLifecycleRegistry cancellation
    /// (no per-team teardown needed beyond clearing the runtime map).
    ///
    /// Per ARCHITECTURE §3 host-authority + COORDINATION-CONTRACT §8 lifecycle.
    /// </summary>
    internal sealed class TeamRuntime
    {
        public TeamId TeamId { get; }
        public StrategicPlanner Planner { get; }
        public Coordinator Coordinator { get; }

        // Snapshot of techs currently driven by Smart on this team.
        private readonly ConcurrentDictionary<TechId, SmartPerTechState> _techs =
            new ConcurrentDictionary<TechId, SmartPerTechState>();

        // Plan-tick counter used by StrategicState (resets when plan type changes).
        private int _planTickCount;
        private PlanLibrary.PlanType _lastPlanType = PlanLibrary.PlanType.Hold;

        public TeamRuntime(TeamId teamId, WorkerPool pool)
        {
            TeamId = teamId;
            Planner = new StrategicPlanner(BuildStrategicState);
            Coordinator = new Coordinator(Planner.PlanBuffer, BuildTeamSnapshot, PublishGoals, PublishTargets);
            // Aether/T2: planner + coordinator are driven by GlobalPlannerDaemon + GlobalCoordinatorDaemon
            // (started once in SmartRuntime.Init). No per-team RunLoop threads — under heavy load the
            // prior 2-threads-per-team pattern proliferated to ~70 long-running threads, contending
            // with AetherFuser for CPU and producing the 500-600 ms preempt spikes observed in the
            // post-Aether diagnostic. Per the 9-agent threading review's T2 patch.
        }

        public void RegisterTech(SmartPerTechState state)
        {
            state.OwnerTeam = this;
            _techs[state.TechId] = state;
        }
        public bool DeregisterTech(TechId id)
        {
            if (_techs.TryRemove(id, out var state)) { state.OwnerTeam = null; return true; }
            return false;
        }
        public int TechCount => _techs.Count;

        /// <summary>
        /// Per-team centroid of Smart-driven tech positions, EXCLUDING <paramref name="excludeSelf"/>.
        /// Returns true + centroid when at least one other tech is registered on this team;
        /// returns false (and centroid undefined) when the team is solo. Caller MUST gate on
        /// the bool, NOT on a Vector3.zero sentinel (world origin is a valid position).
        /// Used by Smart identity goal sources via IdentityContext.HasAllies / TeamCentroid.
        /// Main thread only.
        /// </summary>
        public bool TryGetCentroid(TechId excludeSelf, out Vector3 centroid)
        {
            centroid = Vector3.zero;
            int count = 0;
            foreach (var kv in _techs)
            {
                if (kv.Key == excludeSelf) continue;
                centroid += kv.Value.KinematicBuffer.Read().PositionWorld;
                count++;
            }
            if (count == 0) return false;
            centroid /= count;
            return true;
        }

        /// <summary>
        /// Snapshot of every Smart-driven friendly tech's last-observed position, EXCEPT
        /// <paramref name="excludeSelf"/>. Called per-tech-tick by the weapon-fire controller.
        /// </summary>
        public System.Collections.Generic.List<Vector3> SnapshotFriendlyPositionsExcluding(TechId excludeSelf)
        {
            var list = new System.Collections.Generic.List<Vector3>(_techs.Count);
            foreach (var kv in _techs)
            {
                if (kv.Key == excludeSelf) continue;
                var k = kv.Value.KinematicBuffer.Read();
                list.Add(k.PositionWorld);
            }
            return list;
        }

        /// <summary>
        /// Returns null at v0.1.0: Smart does not maintain VehicleModelSnapshots for enemies,
        /// only its own team's techs. ThreatFieldBuilder degrades gracefully when no
        /// enemy vehicle data is available (uses baseline rating/radius per §2.1 fallback).
        /// TODO v0.2: per WORLD-CONTRACT, the perception worker SHOULD synthesize a coarse
        /// VehicleModelSnapshot for observed enemies (weapon count + estimated mobility);
        /// pass that here when it lands.
        /// </summary>
        internal System.Collections.Generic.IReadOnlyDictionary<TechId, VehicleModelSnapshot> EnemyVehicleSnapshots() => null;

        // ---- StrategicState assembly (called from StrategicPlanner worker) ----
        private StrategicState BuildStrategicState()
        {
            var beliefSnap = SmartRuntime.World?.FusedBuffer.Read();
            if (beliefSnap == null) return null;

            var friendly = new List<StrategicTechSummary>(_techs.Count);
            foreach (var kv in _techs)
            {
                if (!beliefSnap.ByTech.TryGetValue(kv.Key, out var b)) continue;
                friendly.Add(SummarizeBelief(kv.Key, b, kv.Value.VehicleBuffer.Read()));
            }

            // Hostiles: every belief whose tech is not on our team and not in our registry.
            var hostile = new List<StrategicTechSummary>();
            foreach (var pair in beliefSnap.ByTech)
            {
                if (_techs.ContainsKey(pair.Key)) continue;
                if (pair.Value.Team.Value == TeamId.Value) continue;
                hostile.Add(SummarizeBelief(pair.Key, pair.Value, VehicleModelSnapshot.Empty(pair.Key.Value)));
            }

            var currentPlan = Planner.PlanBuffer.Read();
            if (currentPlan.Type != _lastPlanType) { _lastPlanType = currentPlan.Type; _planTickCount = 0; }
            _planTickCount++;

            return new StrategicState(friendly, hostile, currentPlan, _planTickCount);
        }

        private static StrategicTechSummary SummarizeBelief(TechId id, BeliefState b, VehicleModelSnapshot v)
        {
            // v0.1.0 proxies. Vehicle health and learned threat refine these in later steps.
            const float HealthFallback = 1.0f;
            float threat = v.Weapons != null ? Mathf.Min(v.Weapons.Count, 8) / 8f : 0f;
            float mobility = v.Mobility.TopSpeedForward;
            var pv = b.PositionVariance;
            float uncertainty = pv.x + pv.z;
            return new StrategicTechSummary(
                id, b.PositionMean, b.VelocityMean, b.HeadingMean,
                HealthFallback, threat, mobility, uncertainty);
        }

        // ---- TeamSnapshot assembly (called from Coordinator worker) ----
        private TeamSnapshot BuildTeamSnapshot()
        {
            var beliefSnap = SmartRuntime.World?.FusedBuffer.Read();
            if (beliefSnap == null) return null;

            var ourTechs = new List<TechId>(_techs.Count);
            var vehiclesByTech = new Dictionary<TechId, VehicleModelSnapshot>(_techs.Count);
            var losCoverage = new Dictionary<TechId, List<TechId>>(_techs.Count);

            foreach (var kv in _techs)
            {
                ourTechs.Add(kv.Key);
                vehiclesByTech[kv.Key] = kv.Value.VehicleBuffer.Read();
                // TODO v0.2: populate from main-thread LineOfSight raycasts (COORDINATION §3.1).
                // v0.1.0 leaves coverage empty; RoleAssignment.NeedsScout will treat the team
                // as needing scouts whenever any tech lacks targets.
                losCoverage[kv.Key] = new List<TechId>();
            }

            var hostileBeliefs = new List<BeliefState>();
            foreach (var pair in beliefSnap.ByTech)
            {
                if (_techs.ContainsKey(pair.Key)) continue;
                if (pair.Value.Team.Value == TeamId.Value) continue;
                hostileBeliefs.Add(pair.Value);
            }

            return new TeamSnapshot(ourTechs, hostileBeliefs, beliefSnap, vehiclesByTech, losCoverage);
        }

        // ---- Per-tech goal publish (called from Coordinator worker) ----
        private void PublishGoals(Dictionary<TechId, TacticalGoal> goals)
        {
            if (goals == null) return;
            foreach (var kv in goals)
            {
                if (_techs.TryGetValue(kv.Key, out var state))
                    state.WriteExternalGoal(kv.Value);
            }
        }

        // ---- Per-tech target publish (called from Coordinator worker) ----
        private void PublishTargets(Dictionary<TechId, TargetAssignment> targets)
        {
            if (targets == null) return;
            foreach (var kv in targets)
            {
                if (_techs.TryGetValue(kv.Key, out var state))
                    state.WriteExternalTarget(kv.Value);
            }
        }
    }

    /// <summary>
    /// Smart's process-wide globals: WorkerPool, WorldModel, per-team runtimes,
    /// lifecycle hooks. Initialized by SmartForm.InitGlobal; shut down by
    /// SmartForm.DeInitGlobal.
    ///
    /// Static for easy access from any subsystem; the lifecycle is single-instance
    /// per form-active session per ARCHITECTURE §3.2 host-authority.
    /// </summary>
    public static class SmartRuntime
    {
        public static WorkerPool Pool { get; private set; }
        public static WorldModel World { get; private set; }
        public static AetherFuser Perception { get; private set; }

        /// <summary>
        /// Chassis (REV 3.1): process-wide typed per-block-type catalog. Init creates;
        /// Shutdown AFTER WorkerLifecycleRegistry.CancelAllAndJoin calls Reset (workers
        /// quiesced before archetype references drop). Worker-safe read via TryGet; writes
        /// (GetOrProbe) are main-thread-only and triggered exclusively from ChassisCapture.
        /// </summary>
        public static Vehicle.TypedBlockCatalog BlockCatalog { get; private set; }

        // Per-team runtimes (Planning + Coordination workers scoped per team).
        // Lazy creation on first OnTechSpawn for each team — happens on the main thread,
        // so the GetOrAdd factory race is not a concern in practice.
        private static readonly ConcurrentDictionary<TeamId, TeamRuntime> _teams =
            new ConcurrentDictionary<TeamId, TeamRuntime>();

        public static bool IsRunning => Pool != null && Pool.IsRunning && World != null;

        /// <summary>
        /// Phase 5 (FIX-PLAN.md) host-authority flag. Defaults true so single-player and
        /// pre-host-aware test runs work unchanged; updated from <see cref="SmartForm.Operations"/>
        /// and <see cref="SmartForm.Directors"/> on each tick. Read by every long-running
        /// worker at the top of each iteration — when false, workers idle without dispatching
        /// substantive work per ARCHITECTURE §3.2. Declared `volatile` so the cross-thread
        /// read from worker loops observes main-thread writes promptly.
        /// </summary>
        internal static volatile bool IsHost = true;

        public static void Init()
        {
            if (Pool != null) return; // idempotent

            // Register identity goal sources. Generic is intentionally NOT registered - the
            // ContinuousController dispatch checks src.Identity != Generic and falls through to
            // TacticalOptimizer.Step inline for Generic, so a Generic registry entry would be
            // unreachable. See Docs/SMART-IDENTITY-DESIGN.md sec 7.3.
            SmartIdentityRegistry.Register(new HunterGoalSource());
            SmartIdentityRegistry.Register(new BaseGoalSource());
            SmartIdentityRegistry.Register(new SniperGoalSource());
            SmartIdentityRegistry.Register(new GathererGoalSource());
            SmartIdentityRegistry.Register(new AircraftSupportGoalSource());
            SmartIdentityRegistry.Register(new AircraftHunterGoalSource());

            // [TEMP DIAGNOSTIC] Per-sub-call timing to bisect the observed ~3s init freeze.
            // Tagged with "[TIMING]" so the lines are greppable; remove this block once the
            // freeze cause is identified and addressed.
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            Pool = new WorkerPool("Smart", WorkerPool.DefaultWorkerCount);
            DebugTAC_AI.Log("Smart.Init[TIMING] WorkerPool ctor (" + Pool.WorkerCount + " threads): " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            World = new WorldModel();
            DebugTAC_AI.Log("Smart.Init[TIMING] WorldModel ctor: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Chassis Step 2: catalog wired here, populated lazily by ChassisCapture's
            // GetOrProbe calls. Empty at Init; no Prewarm in v0.1 (open question -- defer
            // synchronous vs first-idle Prewarm to in-game profiling).
            BlockCatalog = new Vehicle.TypedBlockCatalog();
            DebugTAC_AI.Log("Smart.Init[TIMING] BlockCatalog ctor: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Phase 3.1 (FIX-PLAN.md): PerceptionWorker was authored but never instantiated
            // or enqueued anywhere (AUDIT R1 1.3). Without it, World.FusedBuffer stayed at
            // BeliefSnapshot.Empty forever and every downstream consumer (Planner,
            // Coordinator, ThreatField, Controller) ran against a dead world. This single
            // wiring fix unblocks the entire downstream chain — multiple planners called it
            // "the highest-leverage edit in the plan" (Plan 2 #6, Plan 6 #2).
            Perception = new AetherFuser(World, World.FusedBuffer);
            Pool.EnqueueLongRunning(Perception.RunLoop, "AetherFuser");
            DebugTAC_AI.Log("Smart.Init[TIMING] AetherFuser ctor + enqueue: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Aether/T2 (9-agent threading review): one global daemon drives every team's
            // Planner and Coordinator. Replaces the prior pattern of 2 threads per team.
            // Under heavy load the prior pattern proliferated to ~70 long-running threads
            // and contended with AetherFuser; collapsing to 2 daemons removes the contention
            // without changing per-team contracts (PlanBuffer / StateBuffer publish surfaces
            // unchanged).
            Pool.EnqueueLongRunning(Planning.GlobalPlannerDaemon.RunLoop, "GlobalPlanner");
            Pool.EnqueueLongRunning(Coordination.GlobalCoordinatorDaemon.RunLoop, "GlobalCoordinator");
            DebugTAC_AI.Log("Smart.Init[TIMING] GlobalPlanner+GlobalCoordinator enqueue: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            PathingService.Init(Pool);
            DebugTAC_AI.Log("Smart.Init[TIMING] PathingService.Init: " + sw.ElapsedMilliseconds + "ms");
            sw.Restart();

            // Phase 5 (FIX-PLAN.md) — AUDIT R1 2.7: the previous path was
            // <cwd>/Mods/SmartAI; LearningService.Init then appended ANOTHER "SmartAI"
            // segment, producing <cwd>/Mods/SmartAI/SmartAI/Profiles. Pass the parent
            // <cwd>/Mods so LearningService's single append produces the spec'd
            // <mod-root>/SmartAI/Profiles. TODO v0.2: replace with KickStart's verified
            // mod-root accessor when the API surface is confirmed.
            string modDir = System.IO.Path.Combine(System.Environment.CurrentDirectory, "Mods");
            LearningService.Init(Pool, modDir);
            DebugTAC_AI.Log("Smart.Init[TIMING] LearningService.Init: " + sw.ElapsedMilliseconds + "ms");

            DebugTAC_AI.Log("Smart.Runtime: initialized with " + Pool.WorkerCount + " workers; perception worker live. [TIMING] TOTAL " + swTotal.ElapsedMilliseconds + "ms");
        }

        public static void Shutdown()
        {
            if (Pool == null) return;

            DebugTAC_AI.Log("Smart.Runtime: shutting down.");

            // Phase 4 (FIX-PLAN.md): INVERT the order. Previously LearningService /
            // PathingService Shutdown ran BEFORE CancelAllAndJoin — trainer workers
            // could still be mutating _params while the main thread saved them
            // (R1 1.4); PathSolveLoop could still dereference _terrain after the
            // PathingService Shutdown nulled it (R1 2.7). Now: workers are cancelled
            // and joined FIRST so subsystem cleanup operates on quiesced state.
            // Plan 5 insight #3: this one ordering change closes four critical races
            // (R1 1.4, R1 2.7, R2 1.R2-N, R2 2.R2.E) simultaneously.
            WorkerLifecycleRegistry.CancelAllAndJoin(TimeSpan.FromSeconds(2));

            // With workers quiesced, subsystem cleanup is race-free.
            try { LearningService.Shutdown(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime: LearningService.Shutdown threw: " + ex.Message); }
            try { PathingService.Shutdown(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime: PathingService.Shutdown threw: " + ex.Message); }

            try { Pool.Dispose(); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.Runtime: pool dispose threw: " + ex.Message); }
            Pool = null;

            _teams.Clear();
            WorldEventBus.ClearAll();
            World?.Clear();
            World = null;
            Perception = null;

            // Chassis: Reset() AFTER CancelAllAndJoin per CHASSIS-DESIGN.md threading
            // ordering. Workers are already quiesced (above); any archetype references
            // they were holding via snapshots are GC-eligible by now. Clearing the
            // catalog before workers join would race a TryGet that's mid-flight.
            BlockCatalog?.Reset();
            BlockCatalog = null;
        }

        /// <summary>
        /// Get the TeamRuntime for this team, creating (and starting its workers) if absent.
        /// Called from SmartForm.OnTechSpawn on the main thread.
        /// </summary>
        internal static TeamRuntime GetOrCreateTeam(TeamId teamId)
        {
            if (Pool == null) return null;
            return _teams.GetOrAdd(teamId, id => new TeamRuntime(id, Pool));
        }

        /// <summary>Look up an existing TeamRuntime, or null if none. Used by OnTechRecycle.</summary>
        internal static TeamRuntime GetTeam(TeamId teamId)
        {
            return _teams.TryGetValue(teamId, out var team) ? team : null;
        }

        /// <summary>
        /// Aether/T3 (9-agent threading review): single tech-cleanup fan-out used by every
        /// site that retires a tech. Replaces the prior pattern of 3 duplicated cleanups
        /// (OnTechRecycle / OnTankDestroyed / orphan-sweep) that drifted apart subtly.
        ///
        /// Preconditions vary by callsite:
        /// - OnTechRecycle (form lifecycle): helper alive, tank alive, both passed.
        /// - OnTankDestroyed (combat death): helper alive, tank in death sequence; both passed.
        /// - Orphan-sweep (engine purged the GameObject): helper unknown, tank gone; both null.
        ///
        /// Safe to call with any combination of null helper/tank. Idempotent.
        /// </summary>
        public static void Deregister(TechId techId, TeamId teamId, TankAIHelper helperOrNull, Tank tankOrNull)
        {
            GetTeam(teamId)?.DeregisterTech(techId);
            World?.DeregisterTech(techId);
            if (tankOrNull != null)
            {
                try { Integration.SmartEventBridge.DetachPerTank(tankOrNull); }
                catch (System.Exception ex)
                {
                    DebugTAC_AI.LogWarning("SmartRuntime.Deregister.DetachPerTank: " + ex.Message);
                }
            }
            if (helperOrNull != null && helperOrNull.FormState != null)
                helperOrNull.FormState = null;
        }

        /// <summary>
        /// Enumerate active TeamRuntimes. Used by PathingService.ThreatFieldRebuildLoop
        /// to rebuild every active team's threat field per tick.
        /// </summary>
        internal static System.Collections.Generic.IEnumerable<TeamRuntime> EnumerateTeams() => _teams.Values;
    }
}
