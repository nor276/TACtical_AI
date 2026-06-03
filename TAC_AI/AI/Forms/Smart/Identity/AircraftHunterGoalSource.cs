using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    // AircraftHunter identity goal source. Solo aircraft - Hunter pattern in 3D.
    //
    //  - With a hostile in beliefs: pursue at ~0.75 * meanRange standoff. Same shape as
    //    ground Hunter but with a small altitude offset so the aircraft doesn't try to
    //    match the target's ground Y.
    //  - No hostile: 3D Lissajous wander around SpawnAnchor with small Y oscillation,
    //    radius ~200m (wider than ground Hunter so aircraft cover more territory).
    //  - No flee for v0.1.
    //
    // Stateless. Per-tech phase from TechId.
    public sealed class AircraftHunterGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.AircraftHunter;

        public const float StandoffFraction = 0.75f;
        public const float WanderRadius = 200f;
        public const float WanderAltitudeAmplitude = 30f;
        public const float WanderTimeScale = 0.018f;
        public const float CruiseAltitudeOffset = 30f;

        public TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                                    BeliefSnapshot beliefs, IdentityContext ctx)
        {
            if (ownBelief == null) return TacticalGoal.AtCurrent(Vector3.zero, 0f);

            Vector3 ownPos = ownBelief.PositionMean;
            const float LookAheadSeconds = 1.0f;

            float meanRange = 50f;
            if (vehicle.Weapons != null && vehicle.Weapons.Count > 0)
            {
                float sum = 0f;
                int n = vehicle.Weapons.Count;
                for (int i = 0; i < n; i++) sum += vehicle.Weapons[i].Range;
                float c = sum / n;
                if (c > 1f) meanRange = c;
            }

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

            if (target != null)
            {
                float standoff = meanRange * StandoffFraction;
                Vector3 hostilePos = target.PositionMean;
                Vector3 toMe = ownPos - hostilePos;
                float dist = toMe.magnitude;
                Vector3 dirFromHostile = dist > 1e-3f ? toMe / dist : Vector3.forward;

                Vector3 goalPos = hostilePos + dirFromHostile * standoff;
                goalPos.y = ownPos.y + CruiseAltitudeOffset * 0.5f;

                Vector3 toHostile = -dirFromHostile;
                float heading = Mathf.Atan2(toHostile.x, toHostile.z);
                Vector3 dir = goalPos - ownPos;
                Vector3 vel = dir / Mathf.Max(LookAheadSeconds, 0.1f);
                return new TacticalGoal(goalPos, heading, vel, LookAheadSeconds);
            }

            // No hostile: 3D wander. Spawn anchor is the orbital center for solo aircraft.
            uint seed = unchecked((uint)ctx.SelfTechId.Value * 2654435761u);
            float phaseA = (seed & 0xFFFFu) * (2f * Mathf.PI / 65536f);
            float phaseB = ((seed >> 16) & 0xFFFFu) * (2f * Mathf.PI / 65536f);
            float t = ctx.TickCounter * WanderTimeScale;

            Vector3 offset = new Vector3(
                WanderRadius * Mathf.Sin(3f * t + phaseA),
                WanderAltitudeAmplitude * Mathf.Sin(t * 0.5f + phaseB),
                WanderRadius * Mathf.Cos(2f * t + phaseB));
            Vector3 wPos = ctx.Stamp.SpawnAnchor + offset;
            wPos.y = ownPos.y + offset.y;

            Vector3 wDir = wPos - ownPos;
            float wHeading = wDir.sqrMagnitude > 1e-3f
                ? Mathf.Atan2(wDir.x, wDir.z)
                : ownBelief.HeadingMean;
            Vector3 wVel = wDir / Mathf.Max(LookAheadSeconds, 0.1f);
            return new TacticalGoal(wPos, wHeading, wVel, LookAheadSeconds);
        }
    }
}
