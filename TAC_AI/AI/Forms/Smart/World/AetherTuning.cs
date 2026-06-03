namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Aether belief-subsystem runtime tunables. Per AETHER-DESIGN.md decisions D1-D4.
    ///
    /// v0.1: static fields. v0.2 follow-up: wire into the Smart Modified tunable framework
    /// (the ~49-tunable in-game UI) so these are settable without recompile.
    ///
    /// Defaults match the AETHER-DESIGN.md "Decisions Requiring User Approval" recommendation:
    /// - D1 ThreatField Lost-gate: DEFERRED (no Lost gate; ThreatField unchanged).
    /// - D2 velocity smoothing: OFF by default (raw observation = LastSeenVelocity).
    /// - D3 BeliefUpdated cadence: unchanged.
    /// - D4 MaxAccelerationEstimate: set-once at RegisterTech, no refresh.
    /// </summary>
    public static class AetherTuning
    {
        /// <summary>
        /// D2 velocity-smoothing strength. 0 = no smoothing (raw observation → LastSeenVelocity).
        /// 1 = full smoothing (ignore observation; keep prior velocity).
        ///
        /// Default: 0.35 — empirically close to today's Kalman gain&lt;1 absorption of single-frame
        /// physics-impulse spikes (collisions, explosion impulses in `rbody.velocity`). Spawn-test
        /// showed combat-near-explosions wiggle when set to 0; 0.35 dampens that without making
        /// the AI feel sluggish on real maneuvers.
        ///
        /// When non-zero, AetherFuser applies `Lerp(rawVel, priorVel, strength)` when constructing
        /// a fresh-observed BeliefState.
        /// </summary>
        public static float VelocitySmoothingStrength = 0.35f;
    }
}
