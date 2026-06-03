using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Planning;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// Decomposes a team-level StrategicPlan into per-tech TacticalGoals.
    /// Per COORDINATION-CONTRACT §6. Each plan has a static Decompose method.
    /// Pure functions of inputs; no side effects.
    /// </summary>
    public static class PlanDecomposition
    {
        public static Dictionary<TechId, TacticalGoal> Decompose(
            PlanLibrary.StrategicPlan plan,
            IReadOnlyList<TechId> ourTechs,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehiclesByTech,
            IReadOnlyDictionary<TechId, TargetAssignment> targets,
            IReadOnlyDictionary<TechId, Role> roles,
            BeliefSnapshot beliefs)
        {
            switch (plan.Type)
            {
                case PlanLibrary.PlanType.EngageFocused:    return DecomposeEngageFocused(plan, ourTechs, vehiclesByTech, targets, roles, beliefs);
                case PlanLibrary.PlanType.EngageDistributed:return DecomposeEngageDistributed(ourTechs, vehiclesByTech, targets, beliefs);
                case PlanLibrary.PlanType.Skirmish:         return DecomposeSkirmish(ourTechs, vehiclesByTech, targets, beliefs);
                case PlanLibrary.PlanType.Flank:            return DecomposeFlank(plan, ourTechs, vehiclesByTech, roles, beliefs);
                case PlanLibrary.PlanType.DefensivePerimeter:return DecomposeDefensivePerimeter(plan, ourTechs, vehiclesByTech);
                case PlanLibrary.PlanType.MobileScreen:     return DecomposeMobileScreen(plan, ourTechs, vehiclesByTech);
                case PlanLibrary.PlanType.FightingRetreat:  return DecomposeFightingRetreat(plan, ourTechs, vehiclesByTech);
                case PlanLibrary.PlanType.Disengage:        return DecomposeDisengage(plan, ourTechs, vehiclesByTech);
                case PlanLibrary.PlanType.Bait:             return DecomposeBait(plan, ourTechs, vehiclesByTech, beliefs);
                case PlanLibrary.PlanType.Hold:             return DecomposeHold(ourTechs, vehiclesByTech);
                default:                                    return DecomposeHold(ourTechs, vehiclesByTech);
            }
        }

        // ----- per-plan decomposition implementations (lean v0.1.0 versions) -----

        private static Dictionary<TechId, TacticalGoal> DecomposeEngageFocused(
            PlanLibrary.StrategicPlan plan, IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles,
            IReadOnlyDictionary<TechId, TargetAssignment> targets,
            IReadOnlyDictionary<TechId, Role> roles,
            BeliefSnapshot beliefs)
        {
            var result = new Dictionary<TechId, TacticalGoal>();
            if (!beliefs.ByTech.TryGetValue(plan.PrimaryTarget, out var target)) return DecomposeHold(ours, vehicles);

            for (int i = 0; i < ours.Count; i++)
            {
                var id = ours[i];
                if (!vehicles.TryGetValue(id, out var v)) continue;

                Vector3 toTarget = target.PositionMean - v.Kinematics.PositionWorld;
                float preferredRange = MeanWeaponRange(v) * 0.75f;
                Vector3 toTargetDir = toTarget.sqrMagnitude > 1e-3f ? toTarget.normalized : Vector3.forward;
                Vector3 goalPos;
                Role role = roles.TryGetValue(id, out var r) ? r : Role.Pursuer;

                if (role == Role.Flanker)
                {
                    Vector3 perp = new Vector3(-toTargetDir.z, 0f, toTargetDir.x);
                    goalPos = target.PositionMean - toTargetDir * preferredRange + perp * preferredRange;
                }
                else if (role == Role.Holder)
                {
                    goalPos = v.Kinematics.PositionWorld; // hold
                }
                else
                {
                    goalPos = target.PositionMean - toTargetDir * preferredRange;
                }
                float heading = Mathf.Atan2(toTargetDir.x, toTargetDir.z);
                result[id] = new TacticalGoal(goalPos, heading, Vector3.zero, 1.0f);
            }
            return result;
        }

        private static Dictionary<TechId, TacticalGoal> DecomposeEngageDistributed(
            IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles,
            IReadOnlyDictionary<TechId, TargetAssignment> targets,
            BeliefSnapshot beliefs)
        {
            var result = new Dictionary<TechId, TacticalGoal>();
            for (int i = 0; i < ours.Count; i++)
            {
                var id = ours[i];
                if (!vehicles.TryGetValue(id, out var v)) continue;
                if (!targets.TryGetValue(id, out var target) || !target.IsValid ||
                    !beliefs.ByTech.TryGetValue(target.TargetId, out var t))
                {
                    result[id] = TacticalGoal.AtCurrent(v.Kinematics.PositionWorld, 0f);
                    continue;
                }
                Vector3 toT = t.PositionMean - v.Kinematics.PositionWorld;
                Vector3 dir = toT.sqrMagnitude > 1e-3f ? toT.normalized : Vector3.forward;
                Vector3 goalPos = t.PositionMean - dir * MeanWeaponRange(v) * 0.75f;
                float heading = Mathf.Atan2(dir.x, dir.z);
                result[id] = new TacticalGoal(goalPos, heading, Vector3.zero, 1.0f);
            }
            return result;
        }

        private static Dictionary<TechId, TacticalGoal> DecomposeSkirmish(
            IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles,
            IReadOnlyDictionary<TechId, TargetAssignment> targets,
            BeliefSnapshot beliefs) =>
            DecomposeEngageDistributed(ours, vehicles, targets, beliefs);

        private static Dictionary<TechId, TacticalGoal> DecomposeFlank(
            PlanLibrary.StrategicPlan plan, IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles,
            IReadOnlyDictionary<TechId, Role> roles,
            BeliefSnapshot beliefs)
        {
            var result = new Dictionary<TechId, TacticalGoal>();
            if (!beliefs.ByTech.TryGetValue(plan.PrimaryTarget, out var target)) return DecomposeHold(ours, vehicles);

            for (int i = 0; i < ours.Count; i++)
            {
                var id = ours[i];
                if (!vehicles.TryGetValue(id, out var v)) continue;
                Vector3 toTarget = target.PositionMean - v.Kinematics.PositionWorld;
                Vector3 dir = toTarget.sqrMagnitude > 1e-3f ? toTarget.normalized : Vector3.forward;
                Vector3 perp = new Vector3(-dir.z, 0f, dir.x) * plan.FlankSide;
                float weaponRange = MeanWeaponRange(v);
                float heading = Mathf.Atan2(dir.x, dir.z);
                // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: role-aware Flank. Previously every
                // tech went to the same flank standoff position, which made Holder and
                // Pursuer roles ignore their semantics during a Flank plan. Honour roles:
                //   Flanker → the deep-flank standoff (the original position)
                //   Pursuer → close-range frontal pressure (target front, weapon-range arc)
                //   Holder  → stay put — provides anchor for the maneuver
                Role role = roles != null && roles.TryGetValue(id, out var r) ? r : Role.Flanker;
                Vector3 goalPos;
                switch (role)
                {
                    case Role.Holder:
                        goalPos = v.Kinematics.PositionWorld;
                        break;
                    case Role.Pursuer:
                        // Frontal pressure — keeps target oriented while flanker comes around.
                        goalPos = target.PositionMean - dir * weaponRange;
                        break;
                    default: // Flanker (and any unknown role)
                        goalPos = target.PositionMean - dir * weaponRange + perp * weaponRange * 1.5f;
                        break;
                }
                result[id] = new TacticalGoal(goalPos, heading, Vector3.zero, 1.5f);
            }
            return result;
        }

        private static Dictionary<TechId, TacticalGoal> DecomposeDefensivePerimeter(
            PlanLibrary.StrategicPlan plan, IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles)
        {
            var result = new Dictionary<TechId, TacticalGoal>();
            int n = ours.Count;
            if (n == 0) return result;
            for (int i = 0; i < n; i++)
            {
                float angle = (i / (float)n) * Mathf.PI * 2f;
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * plan.Scalar;
                Vector3 pos = plan.Vector + offset;
                // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: heading was equal to `angle`, which
                // is the parametric perimeter angle (0..2π) — meaningless as a yaw value
                // and causes every defender to face arbitrary cardinal directions instead
                // of outward. Convert to chassis yaw: outward direction in chassis-frame
                // yaw convention atan2(x, z) = atan2(cos(angle), sin(angle)) = π/2 − angle.
                float heading = Mathf.PI * 0.5f - angle;
                result[ours[i]] = new TacticalGoal(pos, heading, Vector3.zero, 1.0f);
            }
            return result;
        }

        private static Dictionary<TechId, TacticalGoal> DecomposeMobileScreen(
            PlanLibrary.StrategicPlan plan, IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles)
        {
            var result = new Dictionary<TechId, TacticalGoal>();
            int n = ours.Count;
            if (n == 0) return result;
            Vector3 dir = plan.Vector.sqrMagnitude > 1e-3f ? plan.Vector.normalized : Vector3.forward;
            Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
            float spacing = 15f;
            float halfWidth = (n - 1) * spacing * 0.5f;
            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: plan.Scalar comes from PlanLibrary as
            // meanSpeed*0.5 (m/s) — using it as a position offset confused units (50
            // meters of advance for a 100 m/s tech, 5 meters for a 10 m/s tech, and
            // an unmodelled "advance proportional to top speed" coupling). Separate the
            // two roles: position uses a fixed advance distance, velocity uses plan.Scalar
            // as the screen speed (its actual intended meaning).
            const float ScreenAdvanceMeters = 30f;
            float screenSpeed = plan.Scalar;
            for (int i = 0; i < n; i++)
            {
                if (!vehicles.TryGetValue(ours[i], out var v)) continue;
                float lateral = -halfWidth + i * spacing;
                Vector3 pos = v.Kinematics.PositionWorld + perp * lateral + dir * ScreenAdvanceMeters;
                float heading = Mathf.Atan2(dir.x, dir.z);
                result[ours[i]] = new TacticalGoal(pos, heading, dir * screenSpeed, 1.0f);
            }
            return result;
        }

        private static Dictionary<TechId, TacticalGoal> DecomposeFightingRetreat(
            PlanLibrary.StrategicPlan plan, IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles)
        {
            var result = new Dictionary<TechId, TacticalGoal>();
            Vector3 dir = plan.Vector.sqrMagnitude > 1e-3f ? plan.Vector.normalized : Vector3.back;

            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6: previously each tech's retreat speed
            // came from its OWN top-speed — so a 30 m/s scout left the 8 m/s missile boat
            // behind every tick, breaking the "fighting" half of fighting retreat (no
            // mutual support). Now sample the slowest top-speed across the group and pace
            // everyone to it. A retreat without mutual cover is a rout, not a fighting
            // retreat.
            float sharedSpeed = float.MaxValue;
            for (int i = 0; i < ours.Count; i++)
            {
                if (!vehicles.TryGetValue(ours[i], out var vp)) continue;
                float ts = vp.Mobility.TopSpeedForward;
                if (ts > 0f && ts < sharedSpeed) sharedSpeed = ts;
            }
            if (sharedSpeed == float.MaxValue || float.IsNaN(sharedSpeed) || float.IsInfinity(sharedSpeed))
                sharedSpeed = 8f; // fallback when the group has no usable mobility info

            for (int i = 0; i < ours.Count; i++)
            {
                if (!vehicles.TryGetValue(ours[i], out var v)) continue;
                Vector3 goal = v.Kinematics.PositionWorld + dir * 50f;
                float heading = Mathf.Atan2(-dir.x, -dir.z); // face the engagement
                result[ours[i]] = new TacticalGoal(goal, heading, dir * sharedSpeed, 1.0f);
            }
            return result;
        }

        private static Dictionary<TechId, TacticalGoal> DecomposeDisengage(
            PlanLibrary.StrategicPlan plan, IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles)
        {
            var result = new Dictionary<TechId, TacticalGoal>();
            for (int i = 0; i < ours.Count; i++)
            {
                if (!vehicles.TryGetValue(ours[i], out var v)) continue;
                // Phase 6 (FIX-PLAN.md) — AUDIT-R2 §2.R2.F: guard against zero-length
                // direction (tech already at the rendezvous point). Falling back to
                // Vector3.back keeps the goal sensible and the heading well-defined.
                Vector3 diff = plan.Vector - v.Kinematics.PositionWorld;
                Vector3 dir = diff.sqrMagnitude > 1e-3f ? diff.normalized : Vector3.back;
                float heading = Mathf.Atan2(dir.x, dir.z);
                result[ours[i]] = new TacticalGoal(plan.Vector, heading, dir * v.Mobility.TopSpeedForward, 2.0f);
            }
            return result;
        }

        private static Dictionary<TechId, TacticalGoal> DecomposeBait(
            PlanLibrary.StrategicPlan plan, IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles,
            BeliefSnapshot beliefs)
        {
            var result = new Dictionary<TechId, TacticalGoal>();
            if (!beliefs.ByTech.TryGetValue(plan.PrimaryTarget, out var target)) return DecomposeHold(ours, vehicles);
            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: previously every flanker (i ≥ 1)
            // landed on the SAME world point 60m perpendicular from the target — three
            // flankers stacked on one cell, defeating the point of Bait. Distribute
            // flankers across both sides and along an arc that interleaves them around
            // the target. Bait (i==0) keeps the slow-advance behavior.
            int flankerCount = Mathf.Max(0, ours.Count - 1);
            for (int i = 0; i < ours.Count; i++)
            {
                if (!vehicles.TryGetValue(ours[i], out var v)) continue;
                // Phase 6 (FIX-PLAN.md): safe direction; falls back to forward when
                // observer is on top of the target.
                Vector3 diff = target.PositionMean - v.Kinematics.PositionWorld;
                Vector3 toTarget = diff.sqrMagnitude > 1e-3f ? diff.normalized : Vector3.forward;
                Vector3 perp = new Vector3(-toTarget.z, 0f, toTarget.x);
                Vector3 goal;
                if (i == 0)
                {
                    goal = v.Kinematics.PositionWorld + toTarget * 20f; // bait advances slowly
                }
                else
                {
                    int flankerIdx = i - 1;
                    // Alternate sides: even → +perp, odd → -perp. Pair index drives
                    // arc spread so 3 flankers go (+60, -60, +75), 4 go (+60, -60, +75, -75), etc.
                    float side = (flankerIdx % 2 == 0) ? 1f : -1f;
                    float radius = 60f + (flankerIdx / 2) * 15f;
                    goal = target.PositionMean + perp * (side * radius)
                                                 - toTarget * 20f; // also stand off behind
                }
                float heading = Mathf.Atan2(toTarget.x, toTarget.z);
                result[ours[i]] = new TacticalGoal(goal, heading, Vector3.zero, 1.5f);
            }
            return result;
        }

        private static Dictionary<TechId, TacticalGoal> DecomposeHold(
            IReadOnlyList<TechId> ours,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehicles)
        {
            // Hold is the "no active strategic plan" case (no engagements, no retreats).
            // Per SMART-IDENTITY-DESIGN.md sec 7.3, identity goal sources are the v0.1
            // mechanism for motivating motion when Coordinator has nothing to say. If Hold
            // published AtCurrent goals for every tech, the freshness window (1s) would
            // override every identity source on every Operations tick, leaving every tech
            // static even when its identity (Hunter, Gatherer, Aircraft variants) is
            // designed to meander. Publishing an empty dict lets _readExternalGoal return
            // null in the controller, falling through to the identity dispatch.
            //
            // Generic-identity techs lose Coordinator's static-hold behavior here too, but
            // they fall through to TacticalOptimizer.Step which matches the pre-identity
            // baseline (flat utility when no enemies = no motion). No regression.
            return new Dictionary<TechId, TacticalGoal>();
        }

        private static float MeanWeaponRange(VehicleModelSnapshot v)
        {
            if (v.Weapons.Count == 0) return 50f;
            float sum = 0f;
            for (int i = 0; i < v.Weapons.Count; i++) sum += v.Weapons[i].Range;
            return sum / v.Weapons.Count;
        }
    }
}
