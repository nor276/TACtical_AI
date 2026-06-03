using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    // Hunter identity goal source.
    //
    //  - With a hostile in beliefs: pursue. Goal = hostile position offset toward self by
    //    ~0.75 * mean weapon range so MPC closes to the preferred standoff.
    //  - No hostile: deterministic Lissajous meander around (team centroid if HasAllies,
    //    else spawn anchor). Per-tech phase derived from TechId so two Hunters spawned in
    //    the same frame don't lock-step. ~60m amplitude, slow drift.
    //
    // Stateless singleton. All inputs immutable (BeliefSnapshot, VehicleModelSnapshot,
    // IdentityContext are value types or read-only snapshots). Safe to share across techs
    // - per-tech "memory" comes from TechId-derived deterministic seeding, NOT instance state.
    public sealed class HunterGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.Hunter;

        // Standoff bias: sit at this fraction of mean weapon range from the target. Matches
        // EngagementUtility's preferred-range default at TacticalOptimizer.cs:156 - keeps
        // Hunter and the (Generic) optimizer producing similar geometry against the same
        // target, just from a different cost-function shape.
        public const float StandoffFraction = 0.75f;

        // Meander geometry. Lissajous (a=3, b=2) gives a non-self-intersecting figure-8-ish
        // pattern that visits a 2D region rather than spiraling. Phase-offset per tech via
        // TechId hash so two Hunters don't trace the same path. Drift rate kept slow (~10s
        // for a full cycle at 30Hz ops tick) so techs commit to direction before turning.
        public const float MeanderRadius = 60f;
        public const float MeanderTimeScale = 0.02f;

        // Cheap weapon-range mean. WeaponProfile.Range is currently a placeholder (100f) so
        // the actual returned value is uniform across techs at v0.1; structurally correct so
        // when WeaponProfileBuilder reads real ranges the Hunter geometry adapts automatically.
        private static float ComputeMeanWeaponRange(VehicleModelSnapshot vehicle)
        {
            if (vehicle.Weapons == null || vehicle.Weapons.Count == 0) return 50f;
            float sum = 0f;
            int n = vehicle.Weapons.Count;
            for (int i = 0; i < n; i++) sum += vehicle.Weapons[i].Range;
            if (n <= 0) return 50f;
            float mean = sum / n;
            return mean > 1f ? mean : 50f;
        }

        // Nearest hostile in beliefs filtered by team. Returns null if none.
        // Mirrors the filter pattern at TacticalOptimizer.cs:139-144.
        private static BeliefState FindNearestHostile(BeliefSnapshot beliefs, TeamId ownTeam, Vector3 ownPos)
        {
            if (beliefs == null || beliefs.ByTech.Count == 0) return null;
            BeliefState nearest = null;
            float bestDistSq = float.MaxValue;
            foreach (var pair in beliefs.ByTech)
            {
                if (pair.Value.Team.Equals(ownTeam)) continue;
                float dsq = (pair.Value.PositionMean - ownPos).sqrMagnitude;
                if (dsq < bestDistSq) { bestDistSq = dsq; nearest = pair.Value; }
            }
            return nearest;
        }

        public TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                                    BeliefSnapshot beliefs, IdentityContext ctx)
        {
            if (ownBelief == null) return TacticalGoal.AtCurrent(Vector3.zero, 0f);

            Vector3 ownPos = ownBelief.PositionMean;
            var hostile = FindNearestHostile(beliefs, ownBelief.Team, ownPos);
            const float LookAheadSeconds = 1.0f;

            if (hostile != null)
            {
                // Pursuit: sit at StandoffFraction * meanRange from the hostile along the
                // self-to-hostile axis. If the tech is already inside that ring, the goal
                // pulls it back out; if outside, pulls it in. Heading faces hostile so the
                // chassis-yaw drives weapon aim per WeaponFireController.IsAimedWithinArc.
                float meanRange = ComputeMeanWeaponRange(vehicle);
                float standoff = meanRange * StandoffFraction;
                Vector3 hostilePos = hostile.PositionMean;
                Vector3 toMe = ownPos - hostilePos;
                float dist = toMe.magnitude;
                Vector3 dirFromHostile = dist > 1e-3f ? toMe / dist : Vector3.forward;
                Vector3 goalPos = hostilePos + dirFromHostile * standoff;
                Vector3 toHostile = -dirFromHostile;
                float heading = Mathf.Atan2(toHostile.x, toHostile.z);
                Vector3 displacement = goalPos - ownPos;
                Vector3 velocity = displacement / Mathf.Max(LookAheadSeconds, 0.1f);
                return new TacticalGoal(goalPos, heading, velocity, LookAheadSeconds);
            }

            // No hostile: Lissajous meander around an anchor. Anchor preference:
            //   - HasAllies: team centroid (so Hunters stick loosely with the squad)
            //   - else: spawn anchor (so a solo Hunter wanders near where it started rather
            //     than drifting unboundedly across the map)
            Vector3 anchor = ctx.HasAllies ? ctx.TeamCentroid : ctx.Stamp.SpawnAnchor;

            // Per-tech phase derived from TechId via Knuth multiplicative hash. Two Hunters
            // spawned in the same frame get different phases -> they don't lock-step.
            uint seed = unchecked((uint)ctx.SelfTechId.Value * 2654435761u);
            float phaseA = (seed & 0xFFFFu) * (2f * Mathf.PI / 65536f);
            float phaseB = ((seed >> 16) & 0xFFFFu) * (2f * Mathf.PI / 65536f);

            // Slow time parameter. ctx.TickCounter is per-tech LastObservationTick increment
            // (~30Hz on Operations cadence). MeanderTimeScale=0.02 -> ~10s for the slower
            // axis's full cycle, so the tech commits to a direction long enough to develop
            // velocity before turning.
            float t = ctx.TickCounter * MeanderTimeScale;

            // Lissajous (a=3, b=2) traces a non-degenerate 2D region rather than a circle.
            // Keeps the goal Y at the anchor's Y (ground meander; lift is the airframe's
            // problem and Hunter doesn't classify aircraft).
            Vector3 offset = new Vector3(
                MeanderRadius * Mathf.Sin(3f * t + phaseA),
                0f,
                MeanderRadius * Mathf.Cos(2f * t + phaseB));
            Vector3 mPos = anchor + offset;
            mPos.y = anchor.y;

            Vector3 mDisp = mPos - ownPos;
            // Heading toward direction of motion (the goal). If degenerate, hold current.
            float mHeading = mDisp.sqrMagnitude > 1e-3f
                ? Mathf.Atan2(mDisp.x, mDisp.z)
                : Mathf.Atan2(ownBelief.VelocityMean.x, ownBelief.VelocityMean.z);
            Vector3 mVel = mDisp / Mathf.Max(LookAheadSeconds, 0.1f);
            return new TacticalGoal(mPos, mHeading, mVel, LookAheadSeconds);
        }
    }
}
