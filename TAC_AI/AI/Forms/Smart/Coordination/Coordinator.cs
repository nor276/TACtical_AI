using System;
using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Planning;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// Phase 7 (FIX-PLAN.md) — AUDIT R1 §3.7: unified coordination state per
    /// COORDINATION-CONTRACT §3.1. Single snapshot containing every per-team
    /// decision made by Coordinator that downstream subsystems (Control,
    /// Diagnostics) need. Published via <see cref="DoubleBuffer{T}"/> so reads
    /// from any thread see an atomic snapshot. Immutable; constructed once per
    /// strategic tick.
    /// </summary>
    public sealed class CoordinationState
    {
        public PlanLibrary.StrategicPlan ActivePlan { get; }
        public IReadOnlyDictionary<TechId, TargetAssignment> TargetMap { get; }
        public IReadOnlyDictionary<TechId, Role> RoleMap { get; }
        public IReadOnlyDictionary<TechId, TacticalGoal> GoalMap { get; }
        public IReadOnlyDictionary<TechId, List<TechId>> LOSCoverage { get; }
        public long PublishTick { get; }

        public CoordinationState(
            PlanLibrary.StrategicPlan activePlan,
            IReadOnlyDictionary<TechId, TargetAssignment> targets,
            IReadOnlyDictionary<TechId, Role> roles,
            IReadOnlyDictionary<TechId, TacticalGoal> goals,
            IReadOnlyDictionary<TechId, List<TechId>> losCoverage,
            long publishTick)
        {
            ActivePlan = activePlan;
            TargetMap = targets;
            RoleMap = roles;
            GoalMap = goals;
            LOSCoverage = losCoverage;
            PublishTick = publishTick;
        }

        public static readonly CoordinationState Empty = new CoordinationState(
            PlanLibrary.StrategicPlan.Hold,
            new Dictionary<TechId, TargetAssignment>(),
            new Dictionary<TechId, Role>(),
            new Dictionary<TechId, TacticalGoal>(),
            new Dictionary<TechId, List<TechId>>(),
            0L);
    }

    /// <summary>
    /// Per-team coordinator. Per COORDINATION-CONTRACT §8.
    /// Runs in a long-living worker; ticks each strategic-tick interval.
    /// Consumes the latest StrategicPlan from <see cref="StrategicPlanner.PlanBuffer"/>
    /// and decomposes it into per-tech TacticalGoals that ContinuousController reads.
    /// </summary>
    public sealed class Coordinator
    {
        public int MillisecondsPerTick { get; set; } = 300;

        private readonly DoubleBuffer<PlanLibrary.StrategicPlan> _planBuffer;
        private readonly Func<TeamSnapshot> _readTeam;
        private readonly Action<Dictionary<TechId, TacticalGoal>> _publishGoals;
        private readonly Action<Dictionary<TechId, TargetAssignment>> _publishTargets;

        // Phase 7 (FIX-PLAN.md): unified CoordinationState double-buffer.
        public DoubleBuffer<CoordinationState> StateBuffer { get; } = new DoubleBuffer<CoordinationState>(CoordinationState.Empty);

        private Dictionary<TechId, TargetAssignment> _previousAssignments = new Dictionary<TechId, TargetAssignment>();
        // L-029: lock for _previousAssignments — TechLifecycleRegistry.ForgetAll fires
        // from the sweep thread; the assignments dict is read+rewritten on the coordinator
        // tick thread. ReaderWriterLockSlim isn't worth the kernel cost here (writes are
        // every tick, reads are rare); plain Object lock.
        private readonly object _assignmentsLock = new object();

        /// <summary>L-029: drop per-tech assignment state. Called via sidecar adapter.</summary>
        public void ForgetTech(TechId id)
        {
            lock (_assignmentsLock) { _previousAssignments.Remove(id); }
        }

        /// <summary>L-029: snapshot of TechIds with active assignments. Called via sidecar adapter at leak-watch cadence.</summary>
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotAssignmentKeys()
        {
            lock (_assignmentsLock)
            {
                var arr = new TechId[_previousAssignments.Count];
                int i = 0;
                foreach (var k in _previousAssignments.Keys) arr[i++] = k;
                return arr;
            }
        }
        private long _publishTickCounter;
        // P7 Item 17 (REV 2 C10): plan-transition edge tracker — lives on Coordinator
        // (Coordinator-worker side) rather than TeamRuntime's _lastPlanType (Planner-worker
        // side). Avoids cross-worker races and keeps the publish-step concern local.
        private TAC_AI.AI.Forms.Smart.Planning.PlanLibrary.PlanType _lastObservedPlanType
            = TAC_AI.AI.Forms.Smart.Planning.PlanLibrary.PlanType.Hold;

        // P11 T2 Item 51: snapshot of friendly/hostile counts at the start of the current plan,
        // used to compute the reward fed to ActionValueEstimator when the plan transitions.
        // Reward = ΔfriendlyAlive · 1.0 - ΔhostileAlive · 0.5 (positive = good for us). The
        // count proxy is coarser than per-tech HP delta but doesn't require an extra HP-sum pass
        // through the team snapshot every tick; the HealthSidecar HP fix (also Item 51) refines
        // the Friendly[].Health signal that downstream StrategicValueFunction consumes.
        private int _planStartFriendlyCount;
        private int _planStartHostileCount;

        // L-046: predicate returning true while the owning team is in Active lifecycle
        // state. Optional — null means "always run" (test harness pattern). TeamRuntime
        // passes a lambda capturing its own State so StepOnce can hard-stop if the team
        // transitioned to Draining/Disposed between the daemon's iteration snapshot
        // (L-045 gate) and StepOnce dispatch.
        private readonly Func<bool> _isActive;

        public Coordinator(
            DoubleBuffer<PlanLibrary.StrategicPlan> planBuffer,
            Func<TeamSnapshot> readTeam,
            Action<Dictionary<TechId, TacticalGoal>> publishGoals,
            Action<Dictionary<TechId, TargetAssignment>> publishTargets = null,
            Func<bool> isActive = null)
        {
            _planBuffer = planBuffer;
            _readTeam = readTeam;
            _publishGoals = publishGoals;
            _publishTargets = publishTargets;
            _isActive = isActive;
        }

        /// <summary>
        /// One coordination step. Reads team snapshot, runs target+role+goal pipeline, publishes.
        /// Exception-isolated: a failure logs and returns; the caller keeps driving subsequent ticks.
        ///
        /// Aether/T2: extracted so a global daemon (<see cref="GlobalCoordinatorDaemon"/>) can
        /// drive every team's coordinator from one thread instead of 1 thread per team.
        /// </summary>
        public void StepOnce()
        {
            // L-046: hard-stop if the owning team is no longer Active. Catches the race
            // where L-045's iteration snapshot saw Active but the reaper transitioned the
            // team to Draining/Disposed between iteration and dispatch. Cheap predicate
            // call; null guard preserves test-harness behaviour.
            if (_isActive != null && !_isActive()) return;
            try
            {
                var team = _readTeam?.Invoke();
                var plan = _planBuffer.Read();
                if (team != null) TickOnce(plan, team);
            }
            catch (OperationCanceledException) { throw; }
            // L-024: explicit TAE catch ahead of generic Exception. Per-team Coordinator
            // shares AbortGuard storm window with the global coordinator daemon.
            catch (System.Threading.ThreadAbortException)
            {
                if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("GlobalCoordinator")
                    == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) throw;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Coordinator: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Long-running loop. Retained for single-team / test usage; production wiring now
        /// uses <see cref="GlobalCoordinatorDaemon"/> which calls <see cref="StepOnce"/> per team.
        /// </summary>
        public void RunLoop(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(MillisecondsPerTick)) return;
                    continue;
                }
                try { StepOnce(); }
                catch (OperationCanceledException) { return; }
                if (cancellation.WaitHandle.WaitOne(MillisecondsPerTick)) return;
            }
        }

        private void TickOnce(PlanLibrary.StrategicPlan plan, TeamSnapshot team)
        {
            // Stage 1: target assignment (Hungarian).
            var targets = TargetAssignment_Hungarian.Assign(
                team.OurTechs, team.HostileBeliefs, team.VehiclesByTech, _previousAssignments, plan);
            _previousAssignments = targets;

            // Stage 2: role assignment.
            var roles = RoleAssignment.Assign(
                team.OurTechs, team.VehiclesByTech, targets, plan, team.LosCoverage);

            // Stage 3: plan decomposition into per-tech TacticalGoals.
            var goals = PlanDecomposition.Decompose(
                plan, team.OurTechs, team.VehiclesByTech, targets, roles, team.HostileSnapshot);

            // Stage 4: publish.
            // Phase 7 (FIX-PLAN.md) — AUDIT R1 §3.7: publish the unified CoordinationState
            // BEFORE the per-tech write-outs so any downstream consumer reading the
            // unified snapshot through StateBuffer sees a consistent set of (plan,
            // targets, roles, goals). The per-tech publishGoals/publishTargets paths
            // still fire — they feed ContinuousController's per-tech buffers which can't
            // currently consume a team-level CoordinationState from the worker thread
            // boundary (each per-tech state is its own consumer of its own buffer).
            _publishTickCounter++;
            var unified = new CoordinationState(
                activePlan: plan,
                targets: targets,
                roles: roles,
                goals: goals,
                losCoverage: team.LosCoverage,
                publishTick: _publishTickCounter);
            StateBuffer.Write(unified);

            // P7 Item 17 (REV 2 C10): plan-transition edge fire. After unified state
            // is published — consumer (ActionValueEstimator via WorldEventBus) sees a
            // transition tied to a globally-consistent state snapshot.
            //
            // P11 T2 Item 51: real reward = friendly-count delta + (hostile-count delta inverted),
            // measured between the start of the prior plan and the moment of transition. Coarse
            // count proxy (HP-delta is recorded inside StrategicState.Friendly[].Health via the
            // HealthSidecar fix at SmartRuntime.SummarizeBelief). On the very first transition,
            // both deltas are zero — the initial plan's reward is "no information yet".
            if (plan.Type != _lastObservedPlanType)
            {
                var prior = _lastObservedPlanType;
                _lastObservedPlanType = plan.Type;

                int currentFriendly = team.OurTechs != null ? team.OurTechs.Count : 0;
                int currentHostile = team.HostileBeliefs != null ? team.HostileBeliefs.Count : 0;
                int dFriendly = currentFriendly - _planStartFriendlyCount;
                int dHostile = currentHostile - _planStartHostileCount;
                float reward = (float)dFriendly * 1.0f + (float)(-dHostile) * 0.5f;
                _planStartFriendlyCount = currentFriendly;
                _planStartHostileCount = currentHostile;

                TAC_AI.AI.Forms.Smart.Learning.PlanTransitionPublisher.OnPlanPublished(
                    team.TeamId,
                    prior,
                    plan.Type,
                    reward: reward,
                    tickMono: MonoClock.Now());
            }

            _publishGoals?.Invoke(goals);
            _publishTargets?.Invoke(targets);
        }
    }

    /// <summary>Cross-thread team snapshot — produced on main thread, consumed by coordinator worker.</summary>
    public sealed class TeamSnapshot
    {
        public IReadOnlyList<TechId> OurTechs { get; }
        public IReadOnlyList<BeliefState> HostileBeliefs { get; }
        public BeliefSnapshot HostileSnapshot { get; }
        public IReadOnlyDictionary<TechId, VehicleModelSnapshot> VehiclesByTech { get; }
        public IReadOnlyDictionary<TechId, List<TechId>> LosCoverage { get; }
        // P7 Item 17 (REV 2 C10): TeamId stamp added so Coordinator.TickOnce edge tracker
        // can pass the team to PlanTransitionPublisher without needing a TeamRuntime ref.
        // Default-constructed (zero) when caller passes no explicit teamId.
        public TeamId TeamId { get; }

        public TeamSnapshot(
            IReadOnlyList<TechId> ourTechs,
            IReadOnlyList<BeliefState> hostileBeliefs,
            BeliefSnapshot hostileSnapshot,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehiclesByTech,
            IReadOnlyDictionary<TechId, List<TechId>> losCoverage,
            TeamId teamId = default(TeamId))
        {
            OurTechs = ourTechs;
            HostileBeliefs = hostileBeliefs;
            HostileSnapshot = hostileSnapshot;
            VehiclesByTech = vehiclesByTech;
            LosCoverage = losCoverage;
            TeamId = teamId;
        }
    }
}
