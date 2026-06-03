using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Planning
{
    /// <summary>
    /// Simplified team-level rollout for MCTS simulation. Per PLANNING-CONTRACT §4.
    /// 2-3 sec horizon at ~20 Hz; simplified per-tech dynamics (no full physics).
    ///
    /// Inputs are immutable; rollout produces a new StrategicState representing the
    /// projected end-of-rollout state. The state is then evaluated by the heuristic
    /// value function.
    /// </summary>
    public static class StrategicRollout
    {
        // Mutable so CMA-ES (Training §5) can adapt them; defaults match prior const semantics.
        public static float RolloutHorizonSeconds = 2.5f;
        public static float StepDt = 0.05f;
        public static float DecisiveHealthThreshold = 0.0001f;

        public static StrategicState Simulate(StrategicState start, PlanLibrary.StrategicPlan plannedAction)
        {
            var friendly = CopyList(start.Friendly);
            var hostile = CopyList(start.Hostile);

            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: StepDt and RolloutHorizonSeconds are
            // CMA-ES-tuned at runtime. A bad write (StepDt=0, RolloutHorizonSeconds=NaN,
            // negative values) would either produce 0 steps (decisions made from a
            // single-state evaluation, wrong) or an unbounded loop hanging the PUCT
            // search worker. Clamp here. The MCTS spec only requires steps ≥ 1; cap at
            // 200 so a misconfigured StepDt=0.001 doesn't murder the search budget.
            float safeStepDt = (StepDt > 1e-3f && !float.IsNaN(StepDt) && !float.IsInfinity(StepDt)) ? StepDt : 0.05f;
            float safeHorizon = (RolloutHorizonSeconds > 0f && !float.IsNaN(RolloutHorizonSeconds) && !float.IsInfinity(RolloutHorizonSeconds))
                ? RolloutHorizonSeconds : 2.5f;
            int steps = Mathf.Clamp((int)(safeHorizon / safeStepDt), 1, 200);
            for (int step = 0; step < steps; step++)
            {
                // Friendly advances per plan intent (very simplified for v0.1.0).
                AdvanceTeamPerPlan(friendly, hostile, plannedAction);
                // Hostile drifts toward the friendly centroid at half-speed (heuristic enemy assumption).
                AdvanceHostileToward(hostile, friendly);
                // Apply damage proportional to weapon coverage × proximity (very crude).
                ApplyMutualDamage(friendly, hostile);

                if (TeamWiped(friendly) || TeamWiped(hostile)) break;
            }

            return new StrategicState(friendly, hostile, plannedAction, start.PlanTickCount + 1);
        }

        private static List<StrategicTechSummary> CopyList(IReadOnlyList<StrategicTechSummary> src)
        {
            var list = new List<StrategicTechSummary>(src.Count);
            for (int i = 0; i < src.Count; i++) list.Add(src[i]);
            return list;
        }

        private static void AdvanceTeamPerPlan(List<StrategicTechSummary> friendly, List<StrategicTechSummary> hostile, PlanLibrary.StrategicPlan plan)
        {
            for (int i = 0; i < friendly.Count; i++)
            {
                var f = friendly[i];
                Vector3 target = ComputeIntentTarget(f, plan, friendly, hostile);
                Vector3 toTarget = target - f.PositionMean;
                float dist = toTarget.magnitude;
                if (dist > 0.01f)
                {
                    Vector3 dir = toTarget / dist;
                    float speed = Mathf.Min(f.MobilityRating * 10f, dist / StepDt);
                    Vector3 newPos = f.PositionMean + dir * speed * StepDt;
                    Vector3 newVel = dir * speed;
                    float newHeading = Mathf.Atan2(dir.x, dir.z);
                    friendly[i] = new StrategicTechSummary(
                        f.Id, newPos, newVel, newHeading,
                        f.HealthFraction, f.ThreatRating, f.MobilityRating, f.PositionUncertainty);
                }
            }
        }

        private static Vector3 ComputeIntentTarget(StrategicTechSummary self, PlanLibrary.StrategicPlan plan, List<StrategicTechSummary> friendly, List<StrategicTechSummary> hostile)
        {
            switch (plan.Type)
            {
                case PlanLibrary.PlanType.Hold:
                    return self.PositionMean;
                case PlanLibrary.PlanType.FightingRetreat:
                case PlanLibrary.PlanType.Disengage:
                    return self.PositionMean + plan.Vector.normalized * 50f;
                case PlanLibrary.PlanType.MobileScreen:
                    return self.PositionMean + plan.Vector.normalized * 20f;
                case PlanLibrary.PlanType.DefensivePerimeter:
                    {
                        Vector3 toCenter = self.PositionMean - plan.Vector;
                        if (toCenter.sqrMagnitude < 1e-3f) toCenter = Vector3.forward;
                        return plan.Vector + toCenter.normalized * plan.Scalar;
                    }
                case PlanLibrary.PlanType.EngageFocused:
                case PlanLibrary.PlanType.Flank:
                case PlanLibrary.PlanType.Bait:
                    {
                        // Approach the target.
                        for (int i = 0; i < hostile.Count; i++)
                            if (hostile[i].Id == plan.PrimaryTarget)
                                return hostile[i].PositionMean;
                        return self.PositionMean;
                    }
                default:
                    {
                        if (hostile.Count == 0) return self.PositionMean;
                        // Nearest hostile.
                        float bestDist = float.MaxValue;
                        Vector3 bestPos = self.PositionMean;
                        for (int i = 0; i < hostile.Count; i++)
                        {
                            float d = (hostile[i].PositionMean - self.PositionMean).sqrMagnitude;
                            if (d < bestDist) { bestDist = d; bestPos = hostile[i].PositionMean; }
                        }
                        return bestPos;
                    }
            }
        }

        private static void AdvanceHostileToward(List<StrategicTechSummary> hostile, List<StrategicTechSummary> friendly)
        {
            if (friendly.Count == 0) return;
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < friendly.Count; i++) centroid += friendly[i].PositionMean;
            centroid /= friendly.Count;

            for (int i = 0; i < hostile.Count; i++)
            {
                var h = hostile[i];
                Vector3 toCenter = centroid - h.PositionMean;
                if (toCenter.sqrMagnitude > 1e-3f)
                {
                    Vector3 dir = toCenter.normalized;
                    float speed = h.MobilityRating * 5f;
                    hostile[i] = new StrategicTechSummary(
                        h.Id, h.PositionMean + dir * speed * StepDt, dir * speed,
                        Mathf.Atan2(dir.x, dir.z),
                        h.HealthFraction, h.ThreatRating, h.MobilityRating, h.PositionUncertainty);
                }
            }
        }

        private static void ApplyMutualDamage(List<StrategicTechSummary> friendly, List<StrategicTechSummary> hostile)
        {
            // Simplified: each tech takes damage proportional to enemy threat within engagement range.
            const float EngagementRange = 100f;
            const float DamagePerStep = 0.002f;

            for (int i = 0; i < friendly.Count; i++)
            {
                var f = friendly[i];
                float incoming = 0f;
                for (int j = 0; j < hostile.Count; j++)
                {
                    float dist = (hostile[j].PositionMean - f.PositionMean).magnitude;
                    if (dist < EngagementRange) incoming += hostile[j].ThreatRating * (1f - dist / EngagementRange);
                }
                friendly[i] = new StrategicTechSummary(
                    f.Id, f.PositionMean, f.VelocityMean, f.Heading,
                    Mathf.Max(0f, f.HealthFraction - incoming * DamagePerStep),
                    f.ThreatRating, f.MobilityRating, f.PositionUncertainty);
            }
            for (int i = 0; i < hostile.Count; i++)
            {
                var h = hostile[i];
                float incoming = 0f;
                for (int j = 0; j < friendly.Count; j++)
                {
                    float dist = (friendly[j].PositionMean - h.PositionMean).magnitude;
                    if (dist < EngagementRange) incoming += friendly[j].ThreatRating * (1f - dist / EngagementRange);
                }
                hostile[i] = new StrategicTechSummary(
                    h.Id, h.PositionMean, h.VelocityMean, h.Heading,
                    Mathf.Max(0f, h.HealthFraction - incoming * DamagePerStep),
                    h.ThreatRating, h.MobilityRating, h.PositionUncertainty);
            }
        }

        private static bool TeamWiped(List<StrategicTechSummary> team)
        {
            for (int i = 0; i < team.Count; i++) if (team[i].HealthFraction > DecisiveHealthThreshold) return false;
            return true;
        }
    }
}
