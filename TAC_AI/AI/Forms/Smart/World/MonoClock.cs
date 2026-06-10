using System.Diagnostics;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Process-wide monotonic clock for Smart's belief subsystem. Wraps Stopwatch.GetTimestamp().
    /// Per AETHER-DESIGN.md "Time source".
    ///
    /// API:
    ///   MonoClock.Now()      — long, current Stopwatch tick. Thread-safe (Stopwatch's own guarantee).
    ///   MonoClock.TickFreq   — double, 1.0 / Stopwatch.Frequency. Computed once at static init.
    ///   MonoClock.Seconds()  — float, dt in seconds between two tick values (clamps negative dt to 0).
    ///
    /// Usage: per-trace LastObservedTickMono/PublishedTickMono store raw long ticks. Consumers
    /// compute dt = (now - then) * MonoClock.TickFreq. AetherFuser samples Now() once per
    /// perception tick into a local for consistent in-tick stamping; other workers call
    /// Now() whenever they need current time (no shared mutable field; no contention).
    ///
    /// Stopwatch.GetTimestamp() is monotonic and not rebased — it counts from process start
    /// and never wraps within realistic session lengths (~292 years at 1 GHz tick rate).
    /// </summary>
    public static class MonoClock
    {
        /// <summary>
        /// Seconds per Stopwatch tick. Used by every consumer that converts a tick delta to dt.
        /// </summary>
        public static readonly double TickFreq = 1.0 / Stopwatch.Frequency;

        /// <summary>Current monotonic timestamp.</summary>
        public static long Now()
        {
            return Stopwatch.GetTimestamp();
        }

        /// <summary>Seconds → tick delta. Now() returns Stopwatch ticks, NOT ms — a bare
        /// "+ 5000" expires in microseconds. Use this for every deadline math.</summary>
        public static long FromSeconds(double sec)
        {
            return (long)(sec * Stopwatch.Frequency);
        }

        /// <summary>Dt in seconds between two monotonic ticks. Negative dt clamps to 0.</summary>
        public static float Seconds(long fromMono, long toMono)
        {
            long delta = toMono - fromMono;
            if (delta <= 0L) return 0f;
            return (float)(delta * TickFreq);
        }

        /// <summary>Double-precision variant for high-precision callers (e.g. DeadReckon).</summary>
        public static double SecondsDouble(long fromMono, long toMono)
        {
            long delta = toMono - fromMono;
            if (delta <= 0L) return 0.0;
            return delta * TickFreq;
        }
    }
}
