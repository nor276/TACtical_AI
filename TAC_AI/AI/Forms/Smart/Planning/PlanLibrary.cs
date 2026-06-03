using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Planning
{
    /// <summary>
    /// The 10 strategic plans Smart's PUCT explores. Per PLANNING-CONTRACT §6.
    /// Plans are NOT pluggable (per FORM-SPEC §3.3.6); adding one is a contract revision.
    /// </summary>
    public static class PlanLibrary
    {
        public enum PlanType
        {
            EngageFocused,
            EngageDistributed,
            Skirmish,
            Flank,
            DefensivePerimeter,
            MobileScreen,
            FightingRetreat,
            Disengage,
            Bait,
            Hold,
        }

        /// <summary>Per-plan parameters. Many plans have no parameters; the unused fields default.</summary>
        public readonly struct StrategicPlan
        {
            public readonly PlanType Type;
            public readonly TechId PrimaryTarget;   // EngageFocused, Flank, Bait
            public readonly int FlankSide;          // Flank: ±1
            public readonly Vector3 Vector;         // MobileScreen direction, Disengage rendezvous, FightingRetreat escape, DefensivePerimeter center
            public readonly float Scalar;           // MobileScreen advance rate, DefensivePerimeter radius

            public StrategicPlan(PlanType type, TechId target = default, int flankSide = 0, Vector3 vec = default, float scalar = 0f)
            {
                Type = type; PrimaryTarget = target; FlankSide = flankSide; Vector = vec; Scalar = scalar;
            }

            public static StrategicPlan Hold => new StrategicPlan(PlanType.Hold);
            public static StrategicPlan Skirmish => new StrategicPlan(PlanType.Skirmish);
        }

        /// <summary>
        /// Enumerate legal plans from a state per PLANNING §6.3.
        /// Most plans are always legal; the named exceptions are gated on the state.
        /// </summary>
        public static List<StrategicPlan> LegalActions(StrategicState s)
        {
            var actions = new List<StrategicPlan>(16);
            int nFriendly = s.Friendly.Count;
            int nHostile = s.Hostile.Count;

            // Always-legal no-parameter plans.
            actions.Add(new StrategicPlan(PlanType.Hold));
            actions.Add(new StrategicPlan(PlanType.Skirmish));
            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: previously gated on nHostile > 0,
            // making EngageDistributed redundant with EngageFocused when only one hostile
            // exists (both reduce to "everyone fight the only enemy"). Require ≥ 2
            // hostiles so the search distinguishes "concentrate all friendlies on one
            // target" (EngageFocused) from "split friendlies across multiple targets"
            // (EngageDistributed). Without this, PUCT wastes rollouts on a duplicate
            // action and the visit-count tiebreak in Search() can give us
            // EngageDistributed when EngageFocused was the same evaluated plan.
            if (nHostile >= 2) actions.Add(new StrategicPlan(PlanType.EngageDistributed));

            // Target-parameterized plans require at least one hostile.
            if (nHostile > 0)
            {
                for (int i = 0; i < nHostile; i++)
                {
                    var t = s.Hostile[i].Id;
                    actions.Add(new StrategicPlan(PlanType.EngageFocused, t));
                    actions.Add(new StrategicPlan(PlanType.Flank, t, flankSide: 1));
                    actions.Add(new StrategicPlan(PlanType.Flank, t, flankSide: -1));
                    if (nFriendly >= 2) actions.Add(new StrategicPlan(PlanType.Bait, t));
                }
            }

            // DefensivePerimeter / MobileScreen: derived parameters.
            if (nFriendly > 0)
            {
                Vector3 friendlyCentroid = Vector3.zero;
                float speedSum = 0f;
                for (int i = 0; i < nFriendly; i++) { friendlyCentroid += s.Friendly[i].PositionMean; speedSum += s.Friendly[i].MobilityRating; }
                friendlyCentroid /= nFriendly;
                float meanSpeed = speedSum / nFriendly;

                // Spread to derive radius.
                float spread = 1f;
                for (int i = 0; i < nFriendly; i++) spread = Mathf.Max(spread, (s.Friendly[i].PositionMean - friendlyCentroid).magnitude);
                actions.Add(new StrategicPlan(PlanType.DefensivePerimeter, vec: friendlyCentroid, scalar: Mathf.Max(spread, 20f)));

                // Advance direction = average current heading.
                Vector3 headingDir = Vector3.zero;
                for (int i = 0; i < nFriendly; i++)
                    headingDir += new Vector3(Mathf.Sin(s.Friendly[i].Heading), 0f, Mathf.Cos(s.Friendly[i].Heading));
                if (headingDir.sqrMagnitude > 1e-3f) headingDir.Normalize();
                else headingDir = Vector3.forward;
                actions.Add(new StrategicPlan(PlanType.MobileScreen, vec: headingDir, scalar: meanSpeed * 0.5f));
            }

            // Retreat / disengage: require at least one friendly above health threshold.
            bool anyAboveThreshold = false;
            for (int i = 0; i < nFriendly; i++) if (s.Friendly[i].HealthFraction > 0.2f) { anyAboveThreshold = true; break; }
            if (anyAboveThreshold)
            {
                Vector3 escape = Vector3.zero;
                if (nHostile > 0)
                {
                    Vector3 hostileCentroid = Vector3.zero;
                    Vector3 friendlyCentroid2 = Vector3.zero;
                    for (int i = 0; i < nHostile; i++) hostileCentroid += s.Hostile[i].PositionMean;
                    hostileCentroid /= nHostile;
                    for (int i = 0; i < nFriendly; i++) friendlyCentroid2 += s.Friendly[i].PositionMean;
                    friendlyCentroid2 /= nFriendly;
                    Vector3 diff = friendlyCentroid2 - hostileCentroid;
                    escape = diff.sqrMagnitude > 1e-3f ? diff.normalized : Vector3.back;
                }
                else escape = Vector3.back;
                actions.Add(new StrategicPlan(PlanType.FightingRetreat, vec: escape));
                actions.Add(new StrategicPlan(PlanType.Disengage, vec: escape * 200f));
            }

            return actions;
        }

        /// <summary>Equality for tree-node action keys. Plan parameters are compared up to a small position bucket.</summary>
        public static bool ActionEquals(StrategicPlan a, StrategicPlan b)
        {
            if (a.Type != b.Type) return false;
            if (a.PrimaryTarget != b.PrimaryTarget) return false;
            if (a.FlankSide != b.FlankSide) return false;
            return true;
        }

        public static int ActionHash(StrategicPlan a)
        {
            unchecked
            {
                int h = (int)a.Type;
                h = h * 31 + a.PrimaryTarget.Value;
                h = h * 31 + a.FlankSide;
                return h;
            }
        }
    }
}
