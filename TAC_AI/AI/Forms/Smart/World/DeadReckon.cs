using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Closed-form dead-reckoning math. Per AETHER-DESIGN.md "Out-of-sight model".
    ///
    /// Replaces today's <c>BeliefDecay.DampVelocity</c> + Kalman propagation. Pure functions,
    /// allocation-free.
    ///
    /// Velocity decays exponentially with <see cref="VelocityTimeConstantSec"/>; position
    /// drifts via the closed-form integral of the decaying velocity, capped by a hard
    /// kinematic bound (<c>min(v0 · τ, 250 m)</c>). Uncertainty grows as the kinematic
    /// upper-bound on plausible displacement: <c>U = base + speed·dt + 0.5·maxAccel·dt²</c>.
    ///
    /// Headings are not extrapolated — angular velocity is not in the observation channel
    /// and integrating it would be fiction.
    /// </summary>
    public static class DeadReckon
    {
        public const float VelocityTimeConstantSec = 10f;   // τ in exp(-dt/τ); matches today's BeliefDecay.DampVelocity
        public const float MaxCoastSec             = 12f;   // saturation horizon (no coast beyond this)
        public const float StaleAfterSec           = 1.0f;
        public const float LostAfterSec            = 8.0f;
        public const float BaseObservationUncertainty = 0.5f;
        public const float HardKinematicCapMeters  = 250f;  // absolute drift cap for high-speed techs

        /// <summary>
        /// Compute a fresh BeliefState by ageing `prev` forward to `nowMono`. Pure; no allocation
        /// beyond the single returned BeliefState.
        ///
        /// IMPORTANT: PositionMean and VelocityMean are NOT extrapolated in the BeliefState fields.
        /// They stay equal to LastSeenPosition / LastSeenVelocity until the next fresh observation.
        /// Goal sources (Hunter, Gatherer, etc.) read `ownBelief.PositionMean` to compute their
        /// goals; if the field drifted via coast extrapolation, the goal source would see "tech
        /// already at goal" while the real tech is still en route — producing freeze-then-move
        /// behavior. The frozen-field semantics keeps goal-source decisions stable between
        /// observations.
        ///
        /// Coast extrapolation is still available to opt-in consumers via
        /// <see cref="BeliefState.PositionAt"/> / <see cref="BeliefState.VelocityAt"/>, which
        /// compute on demand from the LastSeen anchors.
        ///
        /// What DOES update on each coast tick: <see cref="BeliefState.AgeSeconds"/>,
        /// <see cref="BeliefState.UncertaintyMeters"/>, <see cref="BeliefState.Sight"/>,
        /// <see cref="BeliefState.PublishedTickMono"/>.
        /// </summary>
        public static BeliefState Coast(BeliefState prev, long nowMono)
        {
            float dt = MonoClock.Seconds(prev.LastObservedTickMono, nowMono);
            if (dt <= 0f) return prev;                                  // clock-skew / NaN guard

            float uncertainty = BaseObservationUncertainty
                              + prev.SpeedAtObserve * dt
                              + 0.5f * prev.MaxAccelerationEstimate * dt * dt;

            SightState sight =
                dt >= LostAfterSec  ? SightState.Lost    :
                dt >= StaleAfterSec ? SightState.Stale   :
                                      SightState.Coasting;

            // PositionMean / VelocityMean frozen at LastSeen anchors — see method-summary.
            return BeliefStateFactory.FromCoast(
                prev, prev.LastSeenPosition, prev.LastSeenVelocity,
                dt, uncertainty, sight, nowMono);
        }

        /// <summary>Exponentially-decayed velocity. <c>v0 · exp(-dt/τ)</c>.</summary>
        public static Vector3 ExtrapolateVelocity(Vector3 lastVelocity, float dt)
        {
            if (dt <= 0f) return lastVelocity;
            float decay = Mathf.Exp(-dt / VelocityTimeConstantSec);
            return lastVelocity * decay;
        }

        /// <summary>
        /// Closed-form integral of decaying velocity, kinematic-capped.
        /// Drift = <c>v0 · τ · (1 - exp(-dt/τ))</c>, clamped at <c>min(speed·τ, 250 m)</c>.
        /// </summary>
        public static Vector3 ExtrapolatePositionDrift(Vector3 lastVelocity, float speedAtObserve, float dt)
        {
            if (dt <= 0f) return Vector3.zero;
            float decay = Mathf.Exp(-dt / VelocityTimeConstantSec);
            Vector3 drift = lastVelocity * VelocityTimeConstantSec * (1f - decay);

            float kinematicCap = Mathf.Min(speedAtObserve * VelocityTimeConstantSec, HardKinematicCapMeters);
            float driftSqr = drift.sqrMagnitude;
            if (driftSqr > kinematicCap * kinematicCap)
                drift = drift.normalized * kinematicCap;

            return drift;
        }

        /// <summary>Extrapolated position from a last-seen anchor.</summary>
        public static Vector3 ExtrapolatePosition(Vector3 lastPosition, Vector3 lastVelocity, float speedAtObserve, float dt)
        {
            return lastPosition + ExtrapolatePositionDrift(lastVelocity, speedAtObserve, dt);
        }

        /// <summary>
        /// Categorical confidence in [0, 1]. 1.0 if Fresh (dt = 0); linear 1→0 between
        /// StaleAfterSec and LostAfterSec; 0 once Lost. Matches the SightState bands.
        /// </summary>
        public static float ConfidenceAt(float dt)
        {
            if (dt <= 0f) return 1f;
            if (dt <= StaleAfterSec) return 1f;
            if (dt >= LostAfterSec) return 0f;
            return 1f - (dt - StaleAfterSec) / (LostAfterSec - StaleAfterSec);
        }

        /// <summary>SightState derivation from a dt (seconds since last observation).</summary>
        public static SightState SightFor(float dt)
        {
            if (dt <= 0f) return SightState.Fresh;
            if (dt < StaleAfterSec) return SightState.Coasting;
            if (dt < LostAfterSec) return SightState.Stale;
            return SightState.Lost;
        }
    }
}
