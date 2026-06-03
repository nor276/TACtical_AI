using UnityEngine;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Control
{
    /// <summary>
    /// Tactical goal state at the planning horizon. Per CONTROL-CONTRACT §6.1.
    /// Defined here (rather than in TacticalOptimizer) because CostFunction is the
    /// primary consumer.
    /// </summary>
    public readonly struct TacticalGoal
    {
        public readonly Vector3 Position;
        public readonly float Heading;
        public readonly Vector3 Velocity;
        public readonly float LookAheadSeconds;

        public TacticalGoal(Vector3 pos, float heading, Vector3 vel, float lookAheadSec)
        {
            Position = pos;
            Heading = heading;
            Velocity = vel;
            LookAheadSeconds = lookAheadSec;
        }

        public static TacticalGoal AtCurrent(Vector3 pos, float heading) =>
            new TacticalGoal(pos, heading, Vector3.zero, 1.0f);
    }

    /// <summary>
    /// Multi-objective cost function per CONTROL-CONTRACT §5. Weighted sum of
    /// reach + effort + threat (no-op until Pathing) + weapon alignment + learned (no-op).
    ///
    /// Cost weights are tunable hyperparameters; provisional values per §5.6.
    /// </summary>
    public static class CostFunction
    {
        // Provisional weights per CONTROL-CONTRACT §5.6. Mutable so CMA-ES (Training §5)
        // can adapt them; default values preserve the previous const semantics for any
        // callers that don't write through the harness.
        public static float WReach = 1.0f;
        public static float WEffort = 0.05f;
        public static float WThreat = 2.0f;
        public static float WWeapon = 0.5f;
        public static float WLearned = 0.0f;
        // Movement bias: small reward for trajectories that integrate forward motion when
        // the goal is meaningfully far from spawn. Tilts the MPPI weighted average toward
        // action whenever reach cost is flat across samples (e.g. perpendicular goal that
        // throttle alone can't approach, or threat-field plateau). Zero when goal is at-
        // current, so Base identities still hold position.
        // Magnitude rationale: WMovement * (-totalDistance ~5m) ~= -1.5 at 5 m/s for 1 s,
        // versus WReach * posErr ~ 1200 for a 35 m residual. Reach still dominates by 3
        // orders of magnitude. The bias matters only on flat reach surfaces.
        public static float WMovement = 0.3f;

        // Sub-weights per CONTROL-CONTRACT §5.6.
        public static float AlphaHeading = 5.0f;
        public static float BetaVelocity = 0.5f;
        public static float GammaThrottle = 0.1f;
        public static float GammaSteer = 0.1f;
        public static float GammaSmoothness = 1.0f;

        /// <summary>
        /// Total cost of a trajectory + control sequence. Lower is better.
        /// </summary>
        public static float Compute(
            RolloutState[] trajectory,
            ControlVector[] controls,
            TacticalGoal goal,
            VehicleModelSnapshot vehicle,
            BeliefSnapshot beliefs,
            IThreatField threatField,
            VehicleCapability capability)
        {
            return
                WReach * StateReachCost(trajectory[trajectory.Length - 1], goal)
              + WEffort * ControlEffortCost(controls)
              + WThreat * ThreatFieldCost(trajectory, threatField, capability)
              + WWeapon * WeaponAlignmentCost(trajectory, vehicle, beliefs)
              + WLearned * LearnedCost(trajectory, controls)
              + WMovement * MovementBiasCost(trajectory, goal);
        }

        /// <summary>State error at horizon end.</summary>
        public static float StateReachCost(RolloutState endState, TacticalGoal goal)
        {
            float posErr = (goal.Position - endState.Position).sqrMagnitude;
            float headingDiff = goal.Heading - endState.Heading;
            // Phase 6 (FIX-PLAN.md): closed-form wrap to (-π, π], NaN-safe.
            if (float.IsNaN(headingDiff) || float.IsInfinity(headingDiff)) headingDiff = 0f;
            else headingDiff = Mathf.Repeat(headingDiff + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
            float headingErr = headingDiff * headingDiff;
            float velErr = (goal.Velocity - endState.Velocity).sqrMagnitude;
            return posErr + AlphaHeading * headingErr + BetaVelocity * velErr;
        }

        /// <summary>Control effort + smoothness penalty.</summary>
        public static float ControlEffortCost(ControlVector[] controls)
        {
            float total = 0f;
            for (int k = 0; k < controls.Length; k++)
            {
                var c = controls[k];
                total += GammaThrottle * c.Throttle * c.Throttle;
                total += GammaSteer * c.Steer * c.Steer;
                if (k > 0)
                {
                    var prev = controls[k - 1];
                    float dThrottle = c.Throttle - prev.Throttle;
                    float dSteer = c.Steer - prev.Steer;
                    float dBrake = c.Brake - prev.Brake;
                    total += GammaSmoothness * (dThrottle * dThrottle + dSteer * dSteer + dBrake * dBrake);
                }
            }
            return total;
        }

        /// <summary>
        /// Integral of threat field along the trajectory per CONTROL-CONTRACT §5.4.
        /// Returns zero if the threat field is null (PathingService not initialized,
        /// client path). Sum form — multiplying by dt would just rescale the W_Threat
        /// coefficient, which is itself a tunable.
        ///
        /// Phase 7 (FIX-PLAN.md) — AUDIT R1 2.3: the previous implementation averaged
        /// (divided by trajectory.Length), which made the term horizon-length-invariant
        /// and defeated the W_Threat=2.0 vs W_Reach=1.0 dominance. The contract calls
        /// for a sum.
        /// </summary>
        public static float ThreatFieldCost(RolloutState[] trajectory, IThreatField threatField, VehicleCapability capability)
        {
            if (threatField == null || trajectory == null || trajectory.Length == 0) return 0f;
            float total = 0f;
            for (int k = 0; k < trajectory.Length; k++)
            {
                total += threatField.Evaluate(trajectory[k].Position, capability);
            }
            return total;
        }

        /// <summary>
        /// Penalize trajectories that point our weapons away from relevant targets.
        /// v0.1.0: simplified — penalize at horizon end only, against the nearest hostile.
        /// </summary>
        public static float WeaponAlignmentCost(RolloutState[] trajectory, VehicleModelSnapshot vehicle, BeliefSnapshot beliefs)
        {
            if (vehicle.Weapons.Count == 0 || beliefs == null || beliefs.ByTech.Count == 0) return 0f;

            var endState = trajectory[trajectory.Length - 1];

            // Find nearest hostile-like target (any tech for v0.1.0).
            BeliefState nearest = null;
            float bestDistSq = float.MaxValue;
            foreach (var pair in beliefs.ByTech)
            {
                float distSq = (pair.Value.PositionMean - endState.Position).sqrMagnitude;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = pair.Value;
                }
            }
            if (nearest == null) return 0f;

            float totalPenalty = 0f;
            Vector3 toTarget = (nearest.PositionMean - endState.Position).normalized;
            Vector3 ownFwd = new Vector3(Mathf.Sin(endState.Heading), 0f, Mathf.Cos(endState.Heading));

            for (int i = 0; i < vehicle.Weapons.Count; i++)
            {
                var w = vehicle.Weapons[i];
                // Approximate: dot product between weapon forward (in own-frame, rotated to world by heading) and toTarget.
                Vector3 worldForward = new Vector3(
                    Mathf.Cos(endState.Heading) * w.ForwardDirectionLocal.x + Mathf.Sin(endState.Heading) * w.ForwardDirectionLocal.z,
                    w.ForwardDirectionLocal.y,
                    -Mathf.Sin(endState.Heading) * w.ForwardDirectionLocal.x + Mathf.Cos(endState.Heading) * w.ForwardDirectionLocal.z);
                float dot = Vector3.Dot(worldForward, toTarget);
                float angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f));
                if (angle > w.YawArcRadians)
                    totalPenalty += angle - w.YawArcRadians;
            }
            return totalPenalty;
        }

        /// <summary>
        /// Learned cost contribution. v0.1.0: NO-OP. Activates when LEARNING-CONTRACT's
        /// LearnedCost model trains and Learning publishes a callback.
        /// </summary>
        public static float LearnedCost(RolloutState[] trajectory, ControlVector[] controls) => 0f;

        /// <summary>
        /// Movement bias: NEGATIVE cost proportional to integrated horizontal motion across
        /// the trajectory, scaled by goal distance. Zero when goal is essentially at-current
        /// (Base identity holds position via its at-current goal - reach cost is minimized
        /// at zero velocity and we don't want the bias to override that).
        /// </summary>
        public static float MovementBiasCost(RolloutState[] trajectory, TacticalGoal goal)
        {
            if (trajectory == null || trajectory.Length < 2) return 0f;
            // Goal-distance gate. If goal is within 5 m of starting position, this is a
            // hold-position goal (Base identity, Sniper-no-target, etc.) - no motion bias.
            Vector3 toGoal = goal.Position - trajectory[0].Position;
            toGoal.y = 0f;
            float goalDist = toGoal.magnitude;
            if (!(goalDist > 5f) || float.IsNaN(goalDist) || float.IsInfinity(goalDist)) return 0f;

            // Sum horizontal distance traveled across the trajectory.
            float totalDistance = 0f;
            for (int k = 1; k < trajectory.Length; k++)
            {
                Vector3 step = trajectory[k].Position - trajectory[k - 1].Position;
                step.y = 0f;
                float seg = step.magnitude;
                if (float.IsNaN(seg) || float.IsInfinity(seg)) continue;
                totalDistance += seg;
            }

            // Linear ramp up to a 30 m goal so distant goals get full bias and near goals
            // (5-30 m) get a partial bias. Negative return = lower cost = MPPI rewards this
            // trajectory.
            float distanceFactor = Mathf.Clamp01(goalDist / 30f);
            return -totalDistance * distanceFactor;
        }
    }
}
