using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// L-029: adapter that exposes <see cref="Pathing.PathingService"/>'s per-tech
    /// last-paths dictionary as an <see cref="ITechSidecar"/>. The PathingService class
    /// itself is static and can't implement an interface; a single instance of this
    /// adapter registers with TechLifecycleRegistry at Init.
    /// </summary>
    public sealed class PathingLastPathsSidecar : ITechSidecar
    {
        public string Name => "PathingService.LastPaths";
        public void Forget(TechId id) => Pathing.PathingService.ForgetTech(id);
        public IReadOnlyCollection<TechId> SnapshotKeys() => Pathing.PathingService.SnapshotLastPathKeys();
    }

    /// <summary>
    /// L-029: adapter that fan-outs Forget across every team's Coordinator
    /// (_previousAssignments lives per-team, not per-service). One adapter instance
    /// registered at Init; iterates SmartRuntime.EnumerateTeams() on each Forget so
    /// reaped teams are skipped automatically.
    /// </summary>
    public sealed class CoordinatorAssignmentsSidecar : ITechSidecar
    {
        public string Name => "Coordinator.PreviousAssignments";

        public void Forget(TechId id)
        {
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                try { team.Coordinator?.ForgetTech(id); }
                catch (System.Exception ex)
                {
                    DebugTAC_AI.LogWarning("CoordinatorAssignmentsSidecar.Forget: team="
                        + team.TeamId.Value + " " + ex.Message);
                }
            }
        }

        public IReadOnlyCollection<TechId> SnapshotKeys()
        {
            // Union across teams. TechLeakWatchdog runs at 60s cadence so per-team allocation
            // is acceptable here. HashSet bypasses duplicate keys across teams (a tech briefly
            // double-registered during a MigrateTech race would otherwise inflate the snapshot).
            var union = new HashSet<TechId>();
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                var keys = team.Coordinator?.SnapshotAssignmentKeys();
                if (keys == null) continue;
                foreach (var k in keys) union.Add(k);
            }
            return union;
        }
    }
}
