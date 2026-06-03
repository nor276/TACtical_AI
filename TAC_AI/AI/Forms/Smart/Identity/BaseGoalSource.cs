using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    // Base identity goal source. Anchored or authored-stationary defender.
    //
    //  - ALWAYS holds position. Goal = current position (TacticalGoal.AtCurrent). The MPC
    //    receives a zero-displacement goal and produces drive=0; the weapon-fire controller
    //    still aims and fires.
    //  - Heading rotates to face the nearest hostile within 1.5 * maxWeaponRange so the
    //    chassis-yaw drives turret/weapon arc. If no hostile, hold last heading.
    //  - Never flees. Bases die in place.
    //
    // Supersedes the MPC's KinematicFeasibility-driven drift entirely - the at-current goal
    // collapses all motion terms to zero. Stateless.
    public sealed class BaseGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.Base;

        // Engagement window: face hostiles within this fraction-of-max-range from the base.
        // Lets the base swing its body to bring turrets onto a target before they enter
        // weapon range, so the first salvo lands as soon as the hostile crosses the line.
        public const float TrackDistanceMul = 1.5f;

        public TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                                    BeliefSnapshot beliefs, IdentityContext ctx)
        {
            if (ownBelief == null) return TacticalGoal.AtCurrent(Vector3.zero, 0f);

            Vector3 ownPos = ownBelief.PositionMean;
            float currentHeading = ownBelief.HeadingMean;

            float maxRange = 0f;
            if (vehicle.Weapons != null)
            {
                for (int i = 0; i < vehicle.Weapons.Count; i++)
                    if (vehicle.Weapons[i].Range > maxRange) maxRange = vehicle.Weapons[i].Range;
            }
            if (maxRange < 1f) maxRange = 50f;
            float trackRangeSq = (maxRange * TrackDistanceMul) * (maxRange * TrackDistanceMul);

            // Find the nearest hostile inside the tracking window.
            BeliefState target = null;
            float bestDistSq = float.MaxValue;
            if (beliefs != null && beliefs.ByTech.Count > 0)
            {
                foreach (var pair in beliefs.ByTech)
                {
                    if (pair.Value.Team.Equals(ownBelief.Team)) continue;
                    float dsq = (pair.Value.PositionMean - ownPos).sqrMagnitude;
                    if (dsq > trackRangeSq) continue;
                    if (dsq < bestDistSq) { bestDistSq = dsq; target = pair.Value; }
                }
            }

            float heading = currentHeading;
            if (target != null)
            {
                Vector3 toTarget = target.PositionMean - ownPos;
                if (toTarget.sqrMagnitude > 1e-3f)
                    heading = Mathf.Atan2(toTarget.x, toTarget.z);
            }

            // Position = current (hold). Velocity = zero. LookAhead irrelevant for static goal.
            return TacticalGoal.AtCurrent(ownPos, heading);
        }
    }
}
