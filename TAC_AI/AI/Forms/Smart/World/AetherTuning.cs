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
    /// - D2 velocity smoothing: 0.35 by default (REV 7 P4 Item 15 doc-fix — the live field is
    ///   initialized to 0.35, not 0; the previous "OFF by default" line was a doc-vs-code mismatch).
    /// - D3 BeliefUpdated cadence: unchanged.
    /// - D4 MaxAccelerationEstimate: set-once at RegisterTech, no refresh.
    /// - UseCoastAwareLead (P4 Item 12): OFF by default — preserves v0.1 weapon-fire lead math
    ///   (PositionMean + VelocityMean * projTime) bit-identically. When ON, WeaponFireController
    ///   routes through BeliefState.PositionAt/VelocityAt which apply DeadReckon extrapolation
    ///   for dt&gt;0 and zero-out Lost-state targets. Flip requires the P4.1 spawn-test gate.
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

        /// <summary>
        /// P9 Item 31 (REV 7 C12): mutable static for the CircuitBreaker max-failures
        /// threshold. Sentinel-overlay pattern in CircuitBreaker.cs reads this when caller
        /// passes the literal const default. Initialized to <c>CircuitBreaker.DefaultMaxFailures</c>
        /// so bind(default) at register-time is a no-op. CMA-ES / in-game tunable can adjust.
        /// </summary>
        public static int CircuitBreakerMaxFailures = TAC_AI.AI.Forms.Smart.Threading.CircuitBreaker.DefaultMaxFailures;

        /// <summary>
        /// L-064: TeamReaperDaemon Draining-grace seconds (Active → Draining transition).
        /// Default 30s matches the const baked into TeamReaperDaemon; live tunable lets the
        /// operator extend grace on known-flaky MP sessions where teams briefly empty during
        /// host migration. Read by TeamReaperDaemon at sweep time (each tick).
        /// </summary>
        public static float TeamReaperDrainingGraceSeconds = TAC_AI.AI.Forms.Smart.Coordination.TeamReaperDaemon.DrainingGraceSeconds;

        /// <summary>L-064: TeamReaperDaemon Disposed-grace seconds (Draining → Disposed transition).</summary>
        public static float TeamReaperDisposeGraceSeconds = TAC_AI.AI.Forms.Smart.Coordination.TeamReaperDaemon.DisposeGraceSeconds;

        /// <summary>
        /// P9 Item 31 (REV 7 C12): mutable static for the CircuitBreaker window in ms.
        /// Sentinel-overlay pattern in CircuitBreaker.cs reads this when caller passes the
        /// literal const default. Initialized to <c>CircuitBreaker.DefaultWindowMs</c>.
        /// </summary>
        public static int CircuitBreakerWindowMs = TAC_AI.AI.Forms.Smart.Threading.CircuitBreaker.DefaultWindowMs;

        /// <summary>
        /// P4 Item 12 (REV 2 + REV 3 S2): gate WeaponFireController lead-math between v0.1
        /// raw-mean path and the coast-aware BeliefState.PositionAt/VelocityAt path.
        ///
        /// false (default): WeaponFireController computes lead as
        ///   <c>target.PositionMean + target.VelocityMean * projTime</c>
        /// — byte-identical to v0.1.
        ///
        /// true: WeaponFireController uses
        ///   <c>target.PositionAt(nowMono) + target.VelocityAt(nowMono) * projTime</c>
        /// — Fresh targets get DeadReckon-extrapolated lead for dt&gt;0; Lost targets get
        /// LastSeenPosition + Vector3.zero * projTime (per BeliefState.cs:170,182 short-
        /// circuits). Flip requires P4.1 spawn-test playtest — the Lost-state behavior
        /// shift is the largest combat-feel delta covered by this knob.
        /// </summary>
        public static bool UseCoastAwareLead = true;   // P11 post-flip default (was false)
    }
}
