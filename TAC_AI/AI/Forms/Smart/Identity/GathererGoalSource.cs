using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    // Gatherer identity goal source.
    //
    //  - If provoked (any armed hostile within ProvocationRadius): flee toward a safe anchor
    //    (team centroid if HasAllies, else SpawnAnchor per Q2). Sticky to prevent oscillation
    //    against a target at the border of the trigger ring.
    //  - Otherwise: spiral search from spawn anchor. Real resource-node detection is a v0.2
    //    item (Q1) - until then, Gatherers wander deterministically rather than committing
    //    to specific resource targets. The spiral covers a region around the spawn anchor
    //    so the tech roams without drifting off the map.
    //  - NEVER engages. EngagementUtility-style geometry is identity-bypassed - the goal
    //    source never produces a pursuit goal even when a hostile is in beliefs (the flee
    //    branch overrides). WeaponFireController still fires opportunistically (it's a per-
    //    tick overlay run by ContinuousController after this source).
    //
    // Stateless. Per-tech wander phase from TechId; no per-tick scratch state lives here.
    public sealed class GathererGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.Gatherer;

        public const float ProvocationRadius = 80f;
        public const float FleeStandoffDistance = 60f;
        public const float SpiralOuterRadius = 80f;
        public const float SpiralTimeScale = 0.015f;

        public TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                                    BeliefSnapshot beliefs, IdentityContext ctx)
        {
            if (ownBelief == null) return TacticalGoal.AtCurrent(Vector3.zero, 0f);

            Vector3 ownPos = ownBelief.PositionMean;
            const float LookAheadSeconds = 1.2f;

            // Check for provocation: any armed hostile inside the trigger ring.
            BeliefState threat = null;
            float provoSq = ProvocationRadius * ProvocationRadius;
            float bestDistSq = float.MaxValue;
            if (beliefs != null && beliefs.ByTech.Count > 0)
            {
                foreach (var pair in beliefs.ByTech)
                {
                    if (pair.Value.Team.Equals(ownBelief.Team)) continue;
                    float dsq = (pair.Value.PositionMean - ownPos).sqrMagnitude;
                    if (dsq > provoSq) continue;
                    if (dsq < bestDistSq) { bestDistSq = dsq; threat = pair.Value; }
                }
            }

            if (threat != null)
            {
                // Flee toward a safe anchor. Direction = anchor minus self, projected away
                // from threat to avoid running TOWARD the attacker even when the anchor
                // happens to be roughly behind them.
                Vector3 safeAnchor = ctx.HasAllies ? ctx.TeamCentroid : ctx.Stamp.SpawnAnchor;
                Vector3 awayFromThreat = ownPos - threat.PositionMean;
                if (awayFromThreat.sqrMagnitude < 1e-3f) awayFromThreat = Vector3.forward;
                awayFromThreat.y = 0f;
                awayFromThreat.Normalize();

                Vector3 goalPos = safeAnchor + awayFromThreat * FleeStandoffDistance;
                goalPos.y = ownPos.y;

                Vector3 dir = goalPos - ownPos;
                float heading = dir.sqrMagnitude > 1e-3f
                    ? Mathf.Atan2(dir.x, dir.z)
                    : ownBelief.HeadingMean;
                Vector3 vel = dir / Mathf.Max(LookAheadSeconds, 0.1f);
                return new TacticalGoal(goalPos, heading, vel, LookAheadSeconds);
            }

            // No provocation: spiral search around spawn anchor. Deterministic per-tech phase
            // keeps two Gatherers from tracing identical patterns. r(t) = R * 0.5 * (1 + sin)
            // gives a breathing radius; theta winds with time for the angular sweep.
            uint seed = unchecked((uint)ctx.SelfTechId.Value * 2654435761u);
            float phase = (seed & 0xFFFFu) * (2f * Mathf.PI / 65536f);
            float t = ctx.TickCounter * SpiralTimeScale;
            float r = SpiralOuterRadius * 0.5f * (1f + Mathf.Sin(t * 0.3f + phase));
            float theta = t * 0.7f + phase;
            Vector3 anchor = ctx.Stamp.SpawnAnchor;
            Vector3 offset = new Vector3(r * Mathf.Cos(theta), 0f, r * Mathf.Sin(theta));
            Vector3 spiralPos = anchor + offset;
            spiralPos.y = anchor.y;

            Vector3 spiralDir = spiralPos - ownPos;
            float spiralHeading = spiralDir.sqrMagnitude > 1e-3f
                ? Mathf.Atan2(spiralDir.x, spiralDir.z)
                : ownBelief.HeadingMean;
            Vector3 spiralVel = spiralDir / Mathf.Max(LookAheadSeconds, 0.1f);
            return new TacticalGoal(spiralPos, spiralHeading, spiralVel, LookAheadSeconds);
        }
    }
}
