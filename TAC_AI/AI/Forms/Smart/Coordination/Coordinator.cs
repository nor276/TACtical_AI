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
        private long _publishTickCounter;

        public Coordinator(
            DoubleBuffer<PlanLibrary.StrategicPlan> planBuffer,
            Func<TeamSnapshot> readTeam,
            Action<Dictionary<TechId, TacticalGoal>> publishGoals,
            Action<Dictionary<TechId, TargetAssignment>> publishTargets = null)
        {
            _planBuffer = planBuffer;
            _readTeam = readTeam;
            _publishGoals = publishGoals;
            _publishTargets = publishTargets;
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
            try
            {
                var team = _readTeam?.Invoke();
                var plan = _planBuffer.Read();
                if (team != null) TickOnce(plan, team);
            }
            catch (OperationCanceledException) { throw; }
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

        public TeamSnapshot(
            IReadOnlyList<TechId> ourTechs,
            IReadOnlyList<BeliefState> hostileBeliefs,
            BeliefSnapshot hostileSnapshot,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehiclesByTech,
            IReadOnlyDictionary<TechId, List<TechId>> losCoverage)
        {
            OurTechs = ourTechs;
            HostileBeliefs = hostileBeliefs;
            HostileSnapshot = hostileSnapshot;
            VehiclesByTech = vehiclesByTech;
            LosCoverage = losCoverage;
        }
    }
}
