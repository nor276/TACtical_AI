using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    // AircraftSupport identity goal source. Allies present, aircraft - orbit team centroid
    // at cruise altitude, prioritize hostiles threatening allies.
    //
    //  - Target priority: nearest hostile within EngagementRadius of any same-team belief.
    //    Falls back to nearest hostile to team centroid, then nearest to self.
    //  - No target: orbit team centroid at OrbitRadius / +CruiseAltitudeOffset above. Per-
    //    tech phase from TechId so multiple support aircraft circle at different angles.
    //  - Y clamped to MinCruiseAltitudeAGL above the ground (simple terrain-relative floor:
    //    own altitude + offset; no PathingService terrain query at v0.1).
    //
    // Stateless. Reads beliefs to find which allies are being engaged.
    public sealed class AircraftSupportGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.AircraftSupport;

        public const float EngagementRadius = 120f;
        public const float StandoffFraction = 0.75f;
        public const float OrbitRadius = 80f;
        public const float OrbitTimeScale = 0.05f;
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

            // Find a hostile threatening an ally. Walk all hostiles, score each by minimum
            // distance to any same-team belief. Best score = closest engagement to any ally.
            BeliefState bestTarget = null;
            float bestThreatToAllySq = float.MaxValue;
            if (beliefs != null && beliefs.ByTech.Count > 0)
            {
                foreach (var pair in beliefs.ByTech)
                {
                    var b = pair.Value;
                    if (b.Team.Equals(ownBelief.Team)) continue;

                    float minDsqToAlly = float.MaxValue;
                    foreach (var allyPair in beliefs.ByTech)
                    {
                        if (allyPair.Key == ctx.SelfTechId) continue;
                        if (!allyPair.Value.Team.Equals(ownBelief.Team)) continue;
                        float allyDsq = (b.PositionMean - allyPair.Value.PositionMean).sqrMagnitude;
                        if (allyDsq < minDsqToAlly) minDsqToAlly = allyDsq;
                    }

                    if (minDsqToAlly <= EngagementRadius * EngagementRadius
                        && minDsqToAlly < bestThreatToAllySq)
                    {
                        bestThreatToAllySq = minDsqToAlly;
                        bestTarget = b;
                    }
                }

                // Fallback: nearest hostile (any) when nothing is threatening allies.
                if (bestTarget == null)
                {
                    float nearestSq = float.MaxValue;
                    foreach (var pair in beliefs.ByTech)
                    {
                        if (pair.Value.Team.Equals(ownBelief.Team)) continue;
                        float dsq = (pair.Value.PositionMean - ownPos).sqrMagnitude;
                        if (dsq < nearestSq) { nearestSq = dsq; bestTarget = pair.Value; }
                    }
                }
            }

            if (bestTarget != null)
            {
                float standoff = meanRange * StandoffFraction;
                Vector3 hostilePos = bestTarget.PositionMean;
                Vector3 toMe = ownPos - hostilePos;
                toMe.y = 0f;
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

            // No target: orbit team centroid (or fall back to spawn anchor if centroid is
            // unavailable - HasAllies should be true for this identity by classification,
            // but defensive).
            Vector3 orbitCenter = ctx.HasAllies ? ctx.TeamCentroid : ctx.Stamp.SpawnAnchor;
            uint seed = unchecked((uint)ctx.SelfTechId.Value * 2654435761u);
            float phase = (seed & 0xFFFFu) * (2f * Mathf.PI / 65536f);
            float t = ctx.TickCounter * OrbitTimeScale + phase;
            Vector3 offset = new Vector3(OrbitRadius * Mathf.Cos(t), 0f, OrbitRadius * Mathf.Sin(t));
            Vector3 orbitPos = orbitCenter + offset;
            orbitPos.y = ownPos.y + CruiseAltitudeOffset * 0.5f;

            Vector3 oDir = orbitPos - ownPos;
            float oHeading = oDir.sqrMagnitude > 1e-3f
                ? Mathf.Atan2(oDir.x, oDir.z)
                : ownBelief.HeadingMean;
            Vector3 oVel = oDir / Mathf.Max(LookAheadSeconds, 0.1f);
            return new TacticalGoal(orbitPos, oHeading, oVel, LookAheadSeconds);
        }
    }
}
