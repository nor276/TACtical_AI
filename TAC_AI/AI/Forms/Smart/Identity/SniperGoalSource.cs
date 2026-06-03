using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    // Sniper identity goal source. Long-range standoff: sit near max weapon range, face
    // target, micro-shuffle laterally to break aim solutions, hold position when no target.
    //
    //  - With a hostile in beliefs: standoff at ~0.9 * meanRange (vs Hunter's 0.75) along
    //    the hostile-to-self axis. Heading faces hostile.
    //  - No hostile: hold at current position. Snipers don't roam.
    //  - No flee for v0.1 (matches Hunter; HP-aware flee is a v0.2 Q3 decision).
    //
    // Stateless. Per-tech phase for the lateral shuffle derives from TechId so two snipers
    // don't shuffle identically.
    public sealed class SniperGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.Sniper;

        public const float StandoffFraction = 0.9f;
        public const float LateralShuffleAmplitude = 5f;
        public const float ShuffleTimeScale = 0.015f;

        public TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                                    BeliefSnapshot beliefs, IdentityContext ctx)
        {
            if (ownBelief == null) return TacticalGoal.AtCurrent(Vector3.zero, 0f);

            Vector3 ownPos = ownBelief.PositionMean;
            const float LookAheadSeconds = 1.0f;

            // Mean weapon range. Same logic as Hunter; pulled out as a local for clarity.
            float meanRange = 50f;
            if (vehicle.Weapons != null && vehicle.Weapons.Count > 0)
            {
                float sum = 0f;
                int n = vehicle.Weapons.Count;
                for (int i = 0; i < n; i++) sum += vehicle.Weapons[i].Range;
                float candidate = sum / n;
                if (candidate > 1f) meanRange = candidate;
            }

            // Nearest hostile.
            BeliefState target = null;
            float bestDistSq = float.MaxValue;
            if (beliefs != null && beliefs.ByTech.Count > 0)
            {
                foreach (var pair in beliefs.ByTech)
                {
                    if (pair.Value.Team.Equals(ownBelief.Team)) continue;
                    float dsq = (pair.Value.PositionMean - ownPos).sqrMagnitude;
                    if (dsq < bestDistSq) { bestDistSq = dsq; target = pair.Value; }
                }
            }

            if (target == null)
            {
                // No target: hold at current. Heading retains last bearing.
                return TacticalGoal.AtCurrent(ownPos, ownBelief.HeadingMean);
            }

            // Standoff at 0.9 * meanRange from target along the hostile->self axis.
            float standoff = meanRange * StandoffFraction;
            Vector3 hostilePos = target.PositionMean;
            Vector3 toMe = ownPos - hostilePos;
            float dist = toMe.magnitude;
            Vector3 dirFromHostile = dist > 1e-3f ? toMe / dist : Vector3.forward;

            // Lateral shuffle perpendicular to the target bearing. Per-tech phase from TechId.
            uint seed = unchecked((uint)ctx.SelfTechId.Value * 2654435761u);
            float phase = (seed & 0xFFFFu) * (2f * Mathf.PI / 65536f);
            float shuffleT = ctx.TickCounter * ShuffleTimeScale;
            float lateralOffset = LateralShuffleAmplitude * Mathf.Sin(shuffleT + phase);

            // Perpendicular axis in the horizontal plane: rotate dirFromHostile 90deg around Y.
            Vector3 perp = new Vector3(dirFromHostile.z, 0f, -dirFromHostile.x);

            Vector3 goalPos = hostilePos + dirFromHostile * standoff + perp * lateralOffset;
            goalPos.y = ownPos.y;

            Vector3 toHostile = -dirFromHostile;
            float heading = Mathf.Atan2(toHostile.x, toHostile.z);

            Vector3 displacement = goalPos - ownPos;
            Vector3 velocity = displacement / Mathf.Max(LookAheadSeconds, 0.1f);
            return new TacticalGoal(goalPos, heading, velocity, LookAheadSeconds);
        }
    }
}
