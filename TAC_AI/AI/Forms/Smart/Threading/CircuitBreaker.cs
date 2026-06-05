using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// Per-daemon failure-rate circuit breaker. Per the 9-agent threading review (T4):
    /// promotes <see cref="ThreadingDiagnostics"/> from telemetry-only to an auto-cancel
    /// guard. A long-running daemon that throws repeatedly on a recurring exception
    /// (e.g., NaN in a downstream snapshot it can't filter) would otherwise tightloop
    /// in catch-and-continue forever — spamming the log and consuming CPU on the
    /// failure path.
    ///
    /// Usage: daemon owns one instance, calls <see cref="Tripped"/> from its catch block.
    /// When Tripped returns true, the daemon should <c>return</c> from its RunLoop
    /// (clean exit). The breaker also fires <see cref="ThreadingDiagnostics.RaiseWorkerTerminated"/>
    /// with <see cref="TerminationReason.RetryBudgetExhausted"/> so external observers see
    /// the termination.
    ///
    /// Default policy: 10 failures within 30 s → trip. Tunable per-daemon via constructor.
    /// Outside the window, failures are forgotten — a transient burst of 5 failures
    /// followed by 30 s of success will not trip on the 6th failure 60 s later.
    /// </summary>
    public sealed class CircuitBreaker
    {
        public const int DefaultMaxFailures = 10;
        public const int DefaultWindowMs = 30000;

        private readonly string _daemonName;
        private readonly int _maxFailures;
        private readonly int _windowMs;
        private long _bucketStartMono;
        private int _failureCount;

        public CircuitBreaker(string daemonName, int maxFailures = DefaultMaxFailures, int windowMs = DefaultWindowMs)
        {
            _daemonName = daemonName;
            // P9 Item 31 (REV 7 C12): sentinel-overlay pattern. Caller passing the literal
            // const default opts into AetherTuning override; explicit non-default values
            // bypass tuning (per-daemon override). Trade-off: a caller explicitly passing
            // literal 10 (matching DefaultMaxFailures) triggers the override — acceptable
            // because 10 IS the documented default. Per-daemon explicit-overrides that need
            // to bypass tuning should pass a sentinel like int.MinValue + custom factory.
            _maxFailures = (maxFailures == DefaultMaxFailures)
                ? World.AetherTuning.CircuitBreakerMaxFailures
                : maxFailures;
            _windowMs = (windowMs == DefaultWindowMs)
                ? World.AetherTuning.CircuitBreakerWindowMs
                : windowMs;
        }

        /// <summary>
        /// Register a failure. Returns true if the breaker should trip (caller should exit
        /// its daemon loop). Returns false to continue.
        /// </summary>
        public bool Tripped()
        {
            long now = MonoClock.Now();
            if (_bucketStartMono == 0L
                || MonoClock.Seconds(_bucketStartMono, now) * 1000f > _windowMs)
            {
                // Outside (or first) window — reset.
                _bucketStartMono = now;
                _failureCount = 1;
                return false;
            }

            _failureCount++;
            if (_failureCount >= _maxFailures)
            {
                DebugTAC_AI.LogError("Smart.CircuitBreaker: daemon '" + _daemonName
                    + "' tripped after " + _failureCount + " exceptions within "
                    + _windowMs + "ms. Terminating worker (TerminationReason.RetryBudgetExhausted).");
                ThreadingDiagnostics.RaiseWorkerTerminated(_daemonName, TerminationReason.RetryBudgetExhausted);
                // P9 Item 31: register the trip in the session-wide calibration aggregator
                // so SmartRuntime.Shutdown can emit the CircuitBreaker.log session report.
                CircuitBreakerCalibration.RegisterTrip(_daemonName);
                return true;
            }
            return false;
        }
    }
}
