using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.Planning;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>Per-tech role per COORDINATION-CONTRACT §5.1.</summary>
    public enum Role
    {
        Pursuer,
        Flanker,
        Holder,
        Retreater,
        Scout,
        Support,
    }

    /// <summary>Rule-based role assignment. Per COORDINATION-CONTRACT §5.</summary>
    public static class RoleAssignment
    {
        public static Dictionary<TechId, Role> Assign(
            IReadOnlyList<TechId> ourTechs,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehiclesByTech,
            IReadOnlyDictionary<TechId, TargetAssignment> targets,
            PlanLibrary.StrategicPlan plan,
            IReadOnlyDictionary<TechId, List<TechId>> losCoverage)
        {
            var result = new Dictionary<TechId, Role>(ourTechs.Count);
            if (ourTechs.Count == 0) return result;

            // Compute mean mobility for the comparison threshold.
            float meanMobility = 0f;
            int counted = 0;
            for (int i = 0; i < ourTechs.Count; i++)
            {
                if (vehiclesByTech.TryGetValue(ourTechs[i], out var v))
                {
                    meanMobility += v.Mobility.TopSpeedForward;
                    counted++;
                }
            }
            if (counted > 0) meanMobility /= counted;

            // Retreat-class plans override everything.
            bool retreatPlan = plan.Type == PlanLibrary.PlanType.FightingRetreat
                            || plan.Type == PlanLibrary.PlanType.Disengage;

            for (int i = 0; i < ourTechs.Count; i++)
            {
                var techId = ourTechs[i];
                Role role;

                if (retreatPlan) role = Role.Retreater;
                else if (!vehiclesByTech.TryGetValue(techId, out var vehicle)) role = Role.Holder;
                else if (targets.TryGetValue(techId, out var target) && target.IsValid)
                {
                    if (vehicle.Mobility.TopSpeedForward > meanMobility * 1.2f) role = Role.Flanker;
                    else if (vehicle.Mobility.TippingSusceptibility > 0.5f) role = Role.Holder;
                    else role = Role.Pursuer;
                }
                else
                {
                    // No target: scout if we have observers with empty LOS lists, else support.
                    role = NeedsScout(losCoverage) ? Role.Scout : Role.Support;
                }

                result[techId] = role;
            }
            return result;
        }

        private static bool NeedsScout(IReadOnlyDictionary<TechId, List<TechId>> losCoverage)
        {
            // Crude heuristic: if any observer sees zero enemies, scouting may help.
            foreach (var kv in losCoverage) if (kv.Value.Count == 0) return true;
            return false;
        }
    }
}
