using System.Threading;
using TAC_AI.AI.Forms.Smart.Pathing;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// P9 Item 30: PathSolveLoop backpressure. Tracks queue-depth fraction over time;
    /// activates "shed low-priority requests" mode when the queue stays above
    /// <see cref="HighWaterFraction"/> (75%) for at least <see cref="SustainMs"/> (3000ms).
    /// Pure observability + a derived boolean — the consumer (PathSolveLoop) decides what
    /// "shed low-priority" means.
    ///
    /// L-037: hysteresis. Shed ENTERS at HighWaterFraction; CLEARS only after the queue
    /// stays below LowWaterFraction for SustainMs. Without hysteresis a queue oscillating
    /// around 75% flapped shed-mode on and off every tick. Both transitions emit edge
    /// logs (one per transition, not one per tick).
    ///
    /// L-041: p50/p99 solve-latency reservoir. Bounded ring of recent solve latencies
    /// observed via <see cref="RecordSolveLatency"/>; SolveLatencyMsP50/P99 compute on
    /// read (60s cadence — sort cost is negligible).
    /// </summary>
    public sealed class PathRequestBackpressure : IPathingBackpressureReadout, IWorldResettable
    {
        // L-034: IWorldResettable wiring. Clears hysteresis stamps + latency reservoir on
        // world reset so the new mission's first 3s aren't influenced by the prior mission's
        // queue history. Counters (Dropped/Expired) NOT reset — those are diagnostic
        // session-totals.
        public string ResetName => "PathRequestBackpressure";
        public void ResetForWorldChange()
        {
            _highWaterStartMono = 0L;
            _lowWaterStartMono = 0L;
            _shouldShed = false;
            lock (_latencyLock)
            {
                _latencyCount = 0;
                _latencyCursor = 0;
            }
            DebugTAC_AI.LogWarnFileOnly("pathing-backpressure-worldreset",
                "[WORLD-RESET] hook='PathRequestBackpressure' outcome=ok (hysteresis + latency reservoir cleared)");
        }

        public const float HighWaterFraction = 0.75f;
        public const float LowWaterFraction = 0.25f;
        public const int SustainMs = 3000;
        public const int LatencyReservoirSize = 128;   // L-041

        // L-037 hysteresis state.
        private long _highWaterStartMono;   // time queue first crossed HW (0 = below)
        private long _lowWaterStartMono;    // time queue first dropped below LW while shed-active (0 = above)
        private bool _shouldShed;
        private long _lastShedTransitionMono;
        private long _lastClearTransitionMono;

        private long _dropped;
        private int _lastQueueDepth;
        private int _capacity;
        private long _expired;

        // L-041 reservoir — single-writer (PathSolveLoop), reads bounded by sort cost.
        private readonly float[] _latencyReservoir = new float[LatencyReservoirSize];
        private int _latencyCursor;
        private int _latencyCount;
        private readonly object _latencyLock = new object();

        // L-018 read API.
        public bool ShedActive => _shouldShed;
        public int QueueDepth => Volatile.Read(ref _lastQueueDepth);
        public int QueueCapacity => Volatile.Read(ref _capacity);
        public long DroppedTotal => Interlocked.Read(ref _dropped);
        public long ExpiredTotal => Interlocked.Read(ref _expired);
        public long LastShedTransitionMono => Interlocked.Read(ref _lastShedTransitionMono);
        public long LastClearTransitionMono => Interlocked.Read(ref _lastClearTransitionMono);

        public float SolveLatencyMsP50 => Percentile(0.50f);
        public float SolveLatencyMsP99 => Percentile(0.99f);

        // L-018 obsolete aliases preserved for any straggler caller.
        [System.Obsolete("L-018: use ShedActive.")]
        public bool ShouldShedLowPriority => _shouldShed;
        [System.Obsolete("L-018: use DroppedTotal.")]
        public long DroppedSinceLastReport => Interlocked.Read(ref _dropped);

        /// <summary>
        /// Update internal state from the latest queue snapshot. Call once per PathSolveLoop
        /// iteration just before TryDequeue. <paramref name="nowMono"/> is MonoClock.Now().
        /// </summary>
        public void Observe(int queueDepth, int capacity, long nowMono)
        {
            Volatile.Write(ref _lastQueueDepth, queueDepth);
            Volatile.Write(ref _capacity, capacity);
            if (capacity <= 0) { ResetWaterStamps(); _shouldShed = false; return; }
            float frac = (float)queueDepth / capacity;

            if (!_shouldShed)
            {
                // SHED-INACTIVE: track sustained HighWater.
                if (frac >= HighWaterFraction)
                {
                    if (_highWaterStartMono == 0L) _highWaterStartMono = nowMono;
                    long elapsedMs = (long)(World.MonoClock.Seconds(_highWaterStartMono, nowMono) * 1000f);
                    if (elapsedMs >= SustainMs)
                    {
                        _shouldShed = true;
                        Interlocked.Exchange(ref _lastShedTransitionMono, nowMono);
                        _lowWaterStartMono = 0L;
                        DebugTAC_AI.LogWarnFileOnly("pathing-shed-enter",
                            "[PATHING-SHED] enter queue=" + queueDepth + "/" + capacity
                            + " frac=" + frac.ToString("0.00") + " sustainedMs=" + elapsedMs);
                    }
                }
                else
                {
                    _highWaterStartMono = 0L;
                }
            }
            else
            {
                // SHED-ACTIVE: track sustained LowWater for clear.
                if (frac <= LowWaterFraction)
                {
                    if (_lowWaterStartMono == 0L) _lowWaterStartMono = nowMono;
                    long elapsedMs = (long)(World.MonoClock.Seconds(_lowWaterStartMono, nowMono) * 1000f);
                    if (elapsedMs >= SustainMs)
                    {
                        _shouldShed = false;
                        Interlocked.Exchange(ref _lastClearTransitionMono, nowMono);
                        _highWaterStartMono = 0L;
                        DebugTAC_AI.LogWarnFileOnly("pathing-shed-clear",
                            "[PATHING-SHED-CLEAR] queue=" + queueDepth + "/" + capacity
                            + " frac=" + frac.ToString("0.00") + " sustainedMs=" + elapsedMs);
                    }
                }
                else
                {
                    _lowWaterStartMono = 0L;
                }
            }
        }

        private void ResetWaterStamps()
        {
            _highWaterStartMono = 0L;
            _lowWaterStartMono = 0L;
        }

        public void RecordDropped() => Interlocked.Increment(ref _dropped);
        public void RecordExpired() => Interlocked.Increment(ref _expired);
        public void ResetDroppedCounter() => Interlocked.Exchange(ref _dropped, 0L);

        /// <summary>L-041: record a solve latency sample. Called by PathSolveLoop after each TrySolve.</summary>
        public void RecordSolveLatency(float ms)
        {
            if (ms < 0f || float.IsNaN(ms) || float.IsInfinity(ms)) return;
            lock (_latencyLock)
            {
                _latencyReservoir[_latencyCursor] = ms;
                _latencyCursor = (_latencyCursor + 1) % LatencyReservoirSize;
                if (_latencyCount < LatencyReservoirSize) _latencyCount++;
            }
        }

        private float Percentile(float fraction)
        {
            float[] sample;
            int n;
            lock (_latencyLock)
            {
                n = _latencyCount;
                if (n == 0) return 0f;
                sample = new float[n];
                System.Array.Copy(_latencyReservoir, sample, n);
            }
            System.Array.Sort(sample);
            int idx = (int)(fraction * (n - 1));
            if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
            return sample[idx];
        }
    }
}
