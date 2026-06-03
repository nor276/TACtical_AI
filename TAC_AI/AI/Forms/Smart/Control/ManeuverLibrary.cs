using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Control
{
    /// <summary>
    /// Named trajectory primitives that warm-start MPC sampling or seed tactical goals.
    /// Per CONTROL-CONTRACT §7. Initial six primitives (v0.1.0).
    /// </summary>
    public static class ManeuverLibrary
    {
        /// <summary>Drive straight toward target at constant throttle.</summary>
        public static ControlVector[] StraightLineApproach(int horizon, float throttle = 0.8f)
        {
            var seq = new ControlVector[horizon];
            for (int i = 0; i < horizon; i++)
                seq[i] = new ControlVector(throttle, 0f, 0f);
            return seq;
        }

        /// <summary>Constant-radius bank turn. <paramref name="direction"/>: +1 right, -1 left.</summary>
        public static ControlVector[] BankTurn(int horizon, float direction, float throttle = 0.6f)
        {
            var seq = new ControlVector[horizon];
            float steer = Mathf.Clamp(direction, -1f, 1f) * 0.7f;
            for (int i = 0; i < horizon; i++)
                seq[i] = new ControlVector(throttle, steer, 0f);
            return seq;
        }

        /// <summary>Sideways move: throttle 0, alternating steer.</summary>
        public static ControlVector[] Strafe(int horizon, float lateralDirection)
        {
            var seq = new ControlVector[horizon];
            float side = Mathf.Clamp(lateralDirection, -1f, 1f);
            for (int i = 0; i < horizon; i++)
            {
                float steer = side * (i % 4 < 2 ? 0.8f : -0.8f); // weave to drift sideways
                seq[i] = new ControlVector(0.3f, steer, 0f);
            }
            return seq;
        }

        /// <summary>Stop and hold position.</summary>
        public static ControlVector[] HoldPosition(int horizon)
        {
            var seq = new ControlVector[horizon];
            for (int i = 0; i < horizon; i++)
                seq[i] = new ControlVector(0f, 0f, 1f);
            return seq;
        }

        /// <summary>Reverse along a vector. <paramref name="bias"/>: relative urgency, [0..1].</summary>
        public static ControlVector[] Retreat(int horizon, float bias = 0.8f)
        {
            var seq = new ControlVector[horizon];
            float throttle = Mathf.Lerp(-0.4f, -1.0f, Mathf.Clamp01(bias));
            for (int i = 0; i < horizon; i++)
                seq[i] = new ControlVector(throttle, 0f, 0f);
            return seq;
        }

        /// <summary>Serpentine approach: alternating left/right turns while advancing.</summary>
        public static ControlVector[] SCurveApproach(int horizon, float amplitude = 0.5f, float throttle = 0.7f)
        {
            var seq = new ControlVector[horizon];
            float a = Mathf.Clamp01(amplitude);
            for (int i = 0; i < horizon; i++)
            {
                float steer = Mathf.Sin(i * 0.5f) * a;
                seq[i] = new ControlVector(throttle, steer, 0f);
            }
            return seq;
        }
    }
}
