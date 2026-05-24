using UnityEngine;

namespace TAC_AI
{
    /// <summary>
    /// Seconds-based timer for discrete AI holds / cooldowns. Stores a <see cref="Time.time"/> deadline,
    /// so it reads correctly no matter which pass samples it (Pre / Operations 5Hz / Directors 2.5Hz /
    /// render-frame Maintainer / 4s strategic), regardless of framerate or single-player vs networked
    /// (AIClockPeriod). Pause and timeScale are inherited for free from Time.time.
    ///
    /// Sampling contract: a timer fires at most one poll-interval late and never accumulates missed
    /// ticks. A 0.1s timer polled by a 0.6s networked Operations pass simply fires on the next pass.
    ///
    /// Default (zero / unset) reads as already-expired, which is the safe default for "no hold active".
    /// This is the canonical replacement for the old int/short tick-counters (actionPause, *Clock,
    /// *Countdown) whose real-time meaning drifted with AIClockPeriod and framerate. For values that
    /// must integrate WITHIN a staggered pass (decay meters), use <see cref="AIAccumulator"/> instead.
    /// </summary>
    public struct AITimer
    {
        private float deadline;

        /// <summary>Arm to fire after <paramref name="seconds"/> from now.</summary>
        public void Set(float seconds) { deadline = Time.time + seconds; }

        /// <summary>True once the armed duration has elapsed (also true when unset / Cleared).</summary>
        public bool Due => Time.time >= deadline;

        /// <summary>True while still counting down.</summary>
        public bool Running => Time.time < deadline;

        /// <summary>Seconds left before due (0 once due).</summary>
        public float Remaining => deadline > Time.time ? deadline - Time.time : 0f;

        /// <summary>0..1 elapsed fraction of a hold of length <paramref name="duration"/> seconds.</summary>
        public float Fraction(float duration) => duration <= 0f ? 1f : Mathf.Clamp01(1f - Remaining / duration);

        /// <summary>Expire immediately.</summary>
        public void Clear() { deadline = 0f; }
    }

    /// <summary>
    /// Formalizes the already-correct "advance by AIClockPeriod * fixedDeltaTime per pass" idiom for
    /// values that must integrate WITHIN a staggered Operations/Directors pass (decay meters such as
    /// FrustrationMeter, grace timers). Advances at 1x wall-clock independent of AIClockPeriod.
    /// Use <see cref="AITimer"/> for discrete one-shot holds; use this only for continuous in-pass integration.
    /// </summary>
    public struct AIAccumulator
    {
        public float Value;

        /// <summary>Real seconds covered by one staggered pass of the given AIClockPeriod.</summary>
        public static float PassSeconds(int clockPeriod) => clockPeriod * Time.fixedDeltaTime;

        /// <summary>Advance by one pass-worth of real time.</summary>
        public void AddPass(int clockPeriod) { Value += clockPeriod * Time.fixedDeltaTime; }

        public void Reset() { Value = 0f; }
    }
}
