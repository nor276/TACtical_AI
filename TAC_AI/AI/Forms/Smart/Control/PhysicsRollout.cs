using UnityEngine;
using TAC_AI.AI.Forms.Smart.Vehicle;

namespace TAC_AI.AI.Forms.Smart.Control
{
    /// <summary>One step of state along a rolled-out trajectory.</summary>
    public readonly struct RolloutState
    {
        public readonly Vector3 Position;
        public readonly Vector3 Velocity;
        public readonly float Heading;
        public readonly Vector3 AngularVelocity;

        public RolloutState(Vector3 pos, Vector3 vel, float heading, Vector3 angVel)
        {
            Position = pos;
            Velocity = vel;
            Heading = heading;
            AngularVelocity = angVel;
        }
    }

    /// <summary>One step of control. Per CONTROL-CONTRACT §9.1.</summary>
    public readonly struct ControlVector
    {
        public readonly float Throttle;   // [-1, +1]
        public readonly float Steer;      // [-1, +1]
        public readonly float Brake;      // [0, 1]

        public ControlVector(float throttle, float steer, float brake)
        {
            Throttle = throttle;
            Steer = steer;
            Brake = brake;
        }

        public static readonly ControlVector Zero = new ControlVector(0f, 0f, 0f);
        public static readonly ControlVector Neutral = new ControlVector(0f, 0f, 1f); // brake on
    }

    /// <summary>
    /// Newton-Euler rollout for MPC sample evaluation. Per CONTROL-CONTRACT §4.
    /// Simplified per §4.3: no collision, no damage; quadratic drag.
    /// </summary>
    public static class PhysicsRollout
    {
        private const float Gravity = 9.81f;
        private const float DragK = 0.05f; // provisional quadratic drag coefficient

        /// <summary>
        /// Simulate forward from <paramref name="x0"/> over <paramref name="controls"/>.Length steps.
        /// Returns a state per step (length = controls.Length + 1).
        /// </summary>
        public static RolloutState[] Simulate(
            VehicleModelSnapshot vehicle,
            RolloutState x0,
            ControlVector[] controls,
            float dt)
        {
            int H = controls?.Length ?? 0;
            var traj = new RolloutState[H + 1];
            SimulateInto(vehicle, x0, controls, dt, traj);
            return traj;
        }

        /// <summary>
        /// Phase 9 (FIX-PLAN.md): allocation-free variant. Writes results into the
        /// supplied <paramref name="trajBuffer"/>; caller must size it to at least
        /// (controls.Length + 1). Used by SamplingMPC to recycle one trajectory buffer
        /// across all N samples in a Solve call.
        /// </summary>
        public static void SimulateInto(
            VehicleModelSnapshot vehicle,
            RolloutState x0,
            ControlVector[] controls,
            float dt,
            RolloutState[] trajBuffer)
        {
            int H = controls?.Length ?? 0;
            if (trajBuffer == null || trajBuffer.Length < H + 1)
                throw new System.ArgumentException("trajBuffer must have length >= controls.Length + 1");
            var traj = trajBuffer;
            traj[0] = x0;
            if (H == 0) return;

            for (int k = 0; k < H; k++)
            {
                var state = traj[k];
                var control = controls[k];

                // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.7: rollout's thrust + brake math
                // must mirror the actuator-side mapping in ContinuousController.OnControlFrame
                // so the MPC's prediction matches what TankControl will actually receive.
                // Drive command: forward throttle multiplied by (1-brake); reverse throttle
                // applied directly. Brake's velocity damping is preserved as a single term
                // (the previous "throttle * (1 - brake)" + "-vel * brake" combo applied the
                // brake twice — once attenuating thrust, once damping velocity).
                float fwd_t = Mathf.Max(0f, control.Throttle);
                float rev_t = Mathf.Min(0f, control.Throttle);
                float brake = Mathf.Clamp01(control.Brake);
                float driveCmd = fwd_t * (1f - brake) + rev_t;
                float fwdAccelCapacity = driveCmd >= 0
                    ? vehicle.Thrust.MaxLinearAccelPositive.z
                    : vehicle.Thrust.MaxLinearAccelNegative.z;
                float fwdAccel = fwdAccelCapacity * driveCmd;

                // Forward direction from heading (yaw rotation around world Y).
                Vector3 fwd = new Vector3(Mathf.Sin(state.Heading), 0f, Mathf.Cos(state.Heading));

                Vector3 thrustForce = fwd * fwdAccel * vehicle.Mass.TotalMass;
                Vector3 gravityForce = Vector3.down * Gravity * vehicle.Mass.TotalMass;
                Vector3 dragForce = -DragK * state.Velocity.magnitude * state.Velocity * vehicle.Mass.TotalMass;
                Vector3 netForce = thrustForce + gravityForce + dragForce;

                // Brake as velocity damping — ground-friction proxy. Coefficient 2.0 is
                // provisional but applied just once.
                Vector3 brakeForce = -state.Velocity * brake * vehicle.Mass.TotalMass * 2f;
                netForce += brakeForce;

                Vector3 linearAccel = netForce / Mathf.Max(vehicle.Mass.TotalMass, 1e-3f);

                // Angular: yaw rate from steer × max angular accel.
                float yawAccel = control.Steer * vehicle.Thrust.MaxAngularAccel.y;

                // Integrate.
                Vector3 newVel = state.Velocity + linearAccel * dt;
                Vector3 newPos = state.Position + newVel * dt;
                Vector3 newAngVel = new Vector3(state.AngularVelocity.x, state.AngularVelocity.y + yawAccel * dt, state.AngularVelocity.z);
                float newHeading = state.Heading + newAngVel.y * dt;

                // Phase 6 (FIX-PLAN.md): closed-form wrap, NaN-safe. Prevents infinite-
                // loop hang if newHeading accumulates past float range or comes in as NaN
                // from a degenerate prior sample's angular velocity.
                if (float.IsNaN(newHeading) || float.IsInfinity(newHeading)) newHeading = 0f;
                else newHeading = Mathf.Repeat(newHeading + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;

                // P11 T6 Item 58: replace the v0.1 hand-wave (speedAlongFwd * 0.1f * dt) with
                // a basic wing-area dynamic-pressure model. Lift = ρ * 0.5 * v² * area * Cl,
                // capped at 2× gravity*mass so degenerate "infinite speed" candidates can't
                // produce runaway lift that breaks the trajectory optimizer. The cross-section
                // is approximated from the mass's principal extents — wider tech, more lift.
                if (vehicle.Mobility.VerticalAuthority > 0.5f)
                {
                    float speedAlongFwd = Vector3.Dot(newVel, fwd);
                    if (speedAlongFwd > 1.0f)
                    {
                        const float airDensity = 1.225f;   // kg/m³ at sea level
                        const float liftCoefficient = 0.6f; // provisional; tunable in a future pass
                        const float gravity = 9.81f;
                        float mass = Mathf.Max(vehicle.Mass.TotalMass, 1f);
                        // Cross-section proxy: TerraTech blocks are ~1 m³ × 0.5 kg each on
                        // average, so a tech's wing-area planform scales roughly as the
                        // square root of mass. 0.5 √mass m² is a reasonable midpoint between
                        // a 5-block planar wing (2 m²) and a 200-block heavy lifter (~7 m²).
                        // Clamped to a 4 m² floor so tiny aircraft still get a baseline lift.
                        float area = Mathf.Max(0.5f * Mathf.Sqrt(mass), 4f);
                        float dynamicPressure = 0.5f * airDensity * speedAlongFwd * speedAlongFwd;
                        float lift = dynamicPressure * area * liftCoefficient;
                        float liftCap = 2f * mass * gravity;
                        if (lift > liftCap) lift = liftCap;
                        newVel.y += (lift / mass) * dt;
                    }
                }

                traj[k + 1] = new RolloutState(newPos, newVel, newHeading, newAngVel);
            }
        }
    }
}
