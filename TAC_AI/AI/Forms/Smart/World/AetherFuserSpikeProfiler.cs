namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>Single recorded spike snapshot.</summary>
    public readonly struct AetherSpikeReport
    {
        public readonly long TimestampMono;
        public readonly float DurationMs;
        public readonly int PerTechCount;
        public readonly int IntakeCount;

        public AetherSpikeReport(long timestampMono, float durationMs, int perTechCount, int intakeCount)
        {
            TimestampMono = timestampMono;
            DurationMs = durationMs;
            PerTechCount = perTechCount;
            IntakeCount = intakeCount;
        }

        public static readonly AetherSpikeReport Empty = new AetherSpikeReport(0L, 0f, 0, 0);
    }

    /// <summary>
    /// P9 Item 32: per-tick spike profiler for the AetherFuser worker. The Aether worker
    /// occasionally takes 500-600ms (vs ~0.05ms typical); a ring of size <see cref="RingSize"/>
    /// captures each spike snapshot with timestamp + duration + per-tech / intake counts.
    /// Pure observability — does not change Tick behavior.
    ///
    /// Owned per-AetherFuser instance. <see cref="Snapshot"/> returns the most-recent
    /// spike for the diagnostic log; ring contents are not exposed in v0.2 (P10 tooling
    /// surfaces could iterate the ring for a session-wide spike histogram).
    /// </summary>
    public sealed class AetherFuserSpikeProfiler
    {
        public const float SpikeThresholdMs = 100f;
        public const int RingSize = 256;

        private readonly AetherSpikeReport[] _ring = new AetherSpikeReport[RingSize];
        private int _head;        // next write index
        private int _count;       // 0..RingSize
        private long _tickStartStopwatch;
        private long _totalSpikes;

        public long TotalSpikes => System.Threading.Volatile.Read(ref _totalSpikes);

        /// <summary>Stamp the tick start. Call as the first line of AetherFuser.Tick.</summary>
        public void OnTickStart()
        {
            _tickStartStopwatch = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Compute elapsed; if &gt;= <see cref="SpikeThresholdMs"/>, record a snapshot in the
        /// ring and emit a file-only [AETHER-SPIKE] log line. Returns the snapshot if a
        /// spike was recorded; otherwise returns <see cref="AetherSpikeReport.Empty"/>.
        /// </summary>
        public AetherSpikeReport OnTickEnd(int perTechCount, int intakeCount)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float elapsedMs = (float)((now - _tickStartStopwatch) * 1000.0 * MonoClock.TickFreq);
            if (elapsedMs < SpikeThresholdMs) return AetherSpikeReport.Empty;

            var report = new AetherSpikeReport(now, elapsedMs, perTechCount, intakeCount);
            // Single-writer (AetherFuser worker) so lock-free ring is safe.
            _ring[_head] = report;
            _head = (_head + 1) % RingSize;
            if (_count < RingSize) _count++;
            System.Threading.Interlocked.Increment(ref _totalSpikes);

            DebugTAC_AI.LogWarnFileOnly(
                "aether-spike-" + (TotalSpikes & 0xFFFL),
                "[AETHER-SPIKE] dur=" + elapsedMs.ToString("F1") + "ms perTech="
                + perTechCount + " intake=" + intakeCount);
            return report;
        }

        /// <summary>Most-recent spike (or Empty if none recorded yet). Worker-safe read.</summary>
        public AetherSpikeReport Snapshot()
        {
            if (_count == 0) return AetherSpikeReport.Empty;
            int lastIdx = (_head - 1 + RingSize) % RingSize;
            return _ring[lastIdx];
        }
    }
}
