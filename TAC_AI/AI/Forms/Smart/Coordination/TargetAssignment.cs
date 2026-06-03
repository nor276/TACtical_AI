using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;
using TAC_AI.AI.Forms.Smart.Planning;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>Per-tech target assignment record.</summary>
    public readonly struct TargetAssignment
    {
        public readonly TechId TargetId;
        public readonly float AssignmentCost;
        public readonly bool StickyToPreviousTick;

        public TargetAssignment(TechId targetId, float cost, bool sticky)
        {
            TargetId = targetId; AssignmentCost = cost; StickyToPreviousTick = sticky;
        }

        public static readonly TargetAssignment None = new TargetAssignment(new TechId(-1), float.MaxValue, false);
        public bool IsValid => TargetId.Value >= 0;
    }

    /// <summary>
    /// Hungarian-algorithm target assignment per COORDINATION-CONTRACT §4.
    /// Cost matrix terms (provisional weights per §4.2):
    ///   c_range:    Gaussian penalty around preferred weapon range
    ///   c_weapon:   binary — can our weapons hit this target?
    ///   c_threat:   higher target threat = higher cost for low-HP attacker
    ///   c_sticky:   bonus for keeping last tick's target
    ///   c_plan:     plan-specific bias (EngageFocused steers toward primary target)
    ///   c_overlap:  discourage two techs piling on same target unless plan says so
    /// </summary>
    public static class TargetAssignment_Hungarian
    {
        public const float CRange = 1.0f;
        public const float CWeapon = 5.0f;
        public const float CThreat = 1.0f;
        public const float CSticky = 0.3f;
        public const float CPlan = 2.0f;
        public const float COverlap = 1.5f;

        /// <summary>
        /// Build and solve the assignment problem.
        /// </summary>
        public static Dictionary<TechId, TargetAssignment> Assign(
            IReadOnlyList<TechId> ourTechs,
            IReadOnlyList<BeliefState> targets,
            IReadOnlyDictionary<TechId, VehicleModelSnapshot> vehiclesByTech,
            IReadOnlyDictionary<TechId, TargetAssignment> previousAssignments,
            PlanLibrary.StrategicPlan plan)
        {
            var result = new Dictionary<TechId, TargetAssignment>();
            if (ourTechs.Count == 0 || targets.Count == 0) return result;

            int n = ourTechs.Count;
            int m = targets.Count;
            int dim = Mathf.Max(n, m);
            float[,] cost = new float[dim, dim];
            float noTargetCost = 0.5f * CWeapon;

            for (int i = 0; i < dim; i++)
                for (int j = 0; j < dim; j++)
                    cost[i, j] = noTargetCost;

            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6: COverlap was declared as a weight but
            // never appeared in the cost matrix. Hungarian solves 1-to-1 (or virtual-row
            // padded), so target overlap only matters when previousAssignments showed
            // multiple of our techs landing on the same target — an "everyone keeps
            // piling on one tank" pattern from prior ticks. Build a per-target tally of
            // how many of our techs were previously assigned, and add a per-tech penalty
            // ∝ max(0, tally − 1) to nudge against re-piling next tick.
            var prevTargetTally = new Dictionary<int, int>(targets.Count);
            foreach (var kvp in previousAssignments)
            {
                if (!kvp.Value.IsValid) continue;
                int key = kvp.Value.TargetId.Value;
                prevTargetTally[key] = prevTargetTally.TryGetValue(key, out int existing) ? existing + 1 : 1;
            }

            for (int i = 0; i < n; i++)
            {
                if (!vehiclesByTech.TryGetValue(ourTechs[i], out var vehicle))
                    continue;

                previousAssignments.TryGetValue(ourTechs[i], out var prev);

                for (int j = 0; j < m; j++)
                {
                    var t = targets[j];
                    float c = 0f;

                    // Range fitness.
                    float dist = (t.PositionMean - vehicle.Kinematics.PositionWorld).magnitude;
                    float preferredRange = ComputePreferredRange(vehicle);
                    float delta = dist - preferredRange;
                    float rangePenalty = (delta * delta) / Mathf.Max(preferredRange * preferredRange, 1f);
                    c += CRange * rangePenalty;

                    // Weapon coverage feasibility.
                    bool feasible = dist < ComputeMaxWeaponRange(vehicle);
                    if (!feasible) c += CWeapon;

                    // Threat.
                    c += CThreat * t.MaxAccelerationEstimate * 0.1f;

                    // Sticky bonus.
                    if (prev.IsValid && prev.TargetId == t.Id) c -= CSticky;

                    // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6: plan-aware bias broadens
                    // beyond EngageFocused. Flank and Bait also have a designated
                    // PrimaryTarget that the assigner should favor. EngageDistributed
                    // adds an explicit anti-pile-on bias via COverlap.
                    bool planFavorsThisTarget =
                        (plan.Type == PlanLibrary.PlanType.EngageFocused ||
                         plan.Type == PlanLibrary.PlanType.Flank ||
                         plan.Type == PlanLibrary.PlanType.Bait)
                        && plan.PrimaryTarget == t.Id;
                    if (planFavorsThisTarget) c -= CPlan;

                    // Anti-overlap penalty using previous-tick tally.
                    int prevTally = prevTargetTally.TryGetValue(t.Id.Value, out int tally) ? tally : 0;
                    int overlap = prevTally - 1;
                    if (overlap > 0) c += COverlap * overlap;

                    // Phase 6 (FIX-PLAN.md) — AUDIT-R2 §2.R2.F: replace NaN/Infinity with
                    // a large finite cost so the Hungarian solver's potential-based
                    // augmenting-path invariants hold. NaN comparisons are always false
                    // and would otherwise produce j1=-1 early-exit + corrupt assignment.
                    if (float.IsNaN(c) || float.IsInfinity(c)) c = 1e9f;
                    cost[i, j] = c;
                }
            }

            // Solve.
            var assignment = HungarianSolve(cost, dim);

            // Translate back to TargetAssignment entries.
            for (int i = 0; i < n; i++)
            {
                int j = assignment[i];
                if (j < m)
                {
                    bool sticky = previousAssignments.TryGetValue(ourTechs[i], out var prev) && prev.TargetId == targets[j].Id;
                    result[ourTechs[i]] = new TargetAssignment(targets[j].Id, cost[i, j], sticky);
                }
                // else: virtual no-target slot.
            }
            return result;
        }

        private static float ComputePreferredRange(VehicleModelSnapshot v)
        {
            if (v.Weapons.Count == 0) return 50f;
            float sum = 0f;
            for (int i = 0; i < v.Weapons.Count; i++) sum += v.Weapons[i].Range;
            return (sum / v.Weapons.Count) * 0.75f;
        }

        private static float ComputeMaxWeaponRange(VehicleModelSnapshot v)
        {
            float max = 0f;
            for (int i = 0; i < v.Weapons.Count; i++)
                if (v.Weapons[i].Range > max) max = v.Weapons[i].Range;
            return max > 0f ? max * 1.2f : 60f;
        }

        /// <summary>
        /// Simple Hungarian-algorithm implementation for n × n cost matrices.
        /// Returns assignment[row] = column. Performance acceptable up to n ~30.
        /// Based on the classic O(n³) potential-based approach.
        /// </summary>
        private static int[] HungarianSolve(float[,] cost, int n)
        {
            // Implementation uses the "potentials + shortest augmenting path" pattern.
            var u = new float[n + 1];
            var v = new float[n + 1];
            var p = new int[n + 1];
            var way = new int[n + 1];

            for (int i = 1; i <= n; i++)
            {
                p[0] = i;
                int j0 = 0;
                var minv = new float[n + 1];
                var used = new bool[n + 1];
                for (int j = 0; j <= n; j++) minv[j] = float.MaxValue;

                do
                {
                    used[j0] = true;
                    int i0 = p[j0], j1 = -1;
                    float delta = float.MaxValue;
                    for (int j = 1; j <= n; j++)
                    {
                        if (used[j]) continue;
                        float cur = cost[i0 - 1, j - 1] - u[i0] - v[j];
                        if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                        if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                    }
                    if (j1 < 0) break;
                    for (int j = 0; j <= n; j++)
                    {
                        if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                        else { minv[j] -= delta; }
                    }
                    j0 = j1;
                } while (p[j0] != 0);

                do { int j1 = way[j0]; p[j0] = p[j1]; j0 = j1; } while (j0 != 0);
            }

            var result = new int[n];
            for (int j = 1; j <= n; j++) if (p[j] > 0) result[p[j] - 1] = j - 1;
            return result;
        }
    }
}
