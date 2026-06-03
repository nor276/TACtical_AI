using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>Per-tech instantaneous kinematic estimate. Updated every main-thread perception tick.</summary>
    public readonly struct KinematicState
    {
        public readonly Vector3 PositionWorld;
        public readonly Vector3 VelocityWorld;
        public readonly Vector3 AccelerationWorld;
        public readonly Vector3 AngularVelocityWorld;
        public readonly Vector3 JerkWorld;
        public readonly Vector3 HeadingWorld;
        public readonly long TickStamp;

        public KinematicState(Vector3 pos, Vector3 vel, Vector3 acc, Vector3 angVel, Vector3 jerk, Vector3 heading, long tick)
        {
            PositionWorld = pos;
            VelocityWorld = vel;
            AccelerationWorld = acc;
            AngularVelocityWorld = angVel;
            JerkWorld = jerk;
            HeadingWorld = heading;
            TickStamp = tick;
        }

        public static readonly KinematicState Zero = new KinematicState(
            Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, Vector3.forward, 0);
    }

    /// <summary>
    /// Per-tech estimator that observes Tank kinematic state on the main thread and
    /// produces smoothed KinematicState. Per VEHICLE-CONTRACT §7.
    ///
    /// History buffer of 4 samples; EWMA smoothing on derived acceleration / jerk
    /// suppresses single-frame numerical noise.
    /// </summary>
    public sealed class KinematicTracker
    {
        // EWMA smoothing factor for acceleration/jerk. Higher = more smoothing.
        private const float SmoothingAlpha = 0.3f;

        private bool _hasPrior;
        private Vector3 _priorPosition;
        private Vector3 _priorVelocity;
        private Vector3 _priorAcceleration;
        private float _priorDeltaTime;
        private long _tickStamp;

        public KinematicState Latest { get; private set; }

        public KinematicTracker()
        {
            Latest = KinematicState.Zero;
        }

        /// <summary>
        /// Observe the Tank's current pose. Must be called on the main thread (reads engine objects).
        /// </summary>
        public void Observe(Tank tank, float dt)
        {
            if (tank == null) return;
            _tickStamp++;

            Vector3 pos = tank.boundsCentreWorldNoCheck;
            Vector3 vel = tank.rbody != null ? tank.rbody.velocity : Vector3.zero;
            Vector3 angVel = tank.rbody != null ? tank.rbody.angularVelocity : Vector3.zero;
            Vector3 heading = tank.transform != null ? tank.transform.forward : Vector3.forward;

            Vector3 acc, jerk;
            if (!_hasPrior || dt <= 1e-6f)
            {
                acc = Vector3.zero;
                jerk = Vector3.zero;
                _hasPrior = true;
            }
            else
            {
                Vector3 instantAcc = (vel - _priorVelocity) / dt;
                acc = Vector3.Lerp(_priorAcceleration, instantAcc, 1f - SmoothingAlpha);

                Vector3 instantJerk = (acc - _priorAcceleration) / dt;
                jerk = Vector3.Lerp(Latest.JerkWorld, instantJerk, 1f - SmoothingAlpha);
            }

            Latest = new KinematicState(pos, vel, acc, angVel, jerk, heading, _tickStamp);

            _priorPosition = pos;
            _priorVelocity = vel;
            _priorAcceleration = acc;
            _priorDeltaTime = dt;
        }

        public void Reset()
        {
            _hasPrior = false;
            Latest = KinematicState.Zero;
            _tickStamp = 0;
        }
    }
}
