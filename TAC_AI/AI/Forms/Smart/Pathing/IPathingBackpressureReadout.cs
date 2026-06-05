namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>
    /// L-018: read-only view of the pathing backpressure state. Surfaced via
    /// <see cref="PathingService.Backpressure"/> for the diagnostic GUI (L-038),
    /// <c>smart.runtime.status</c> console block (L-039), and the [PATHING-DEPTH] 60s
    /// histogram (L-040). All properties allocation-free.
    ///
    /// Properties marked "(L-NNN)" land in the named Wave 1 item that populates them.
    /// Until that item lands, the implementation returns 0 / 0L (the contract is the
    /// surface, not the values).
    /// </summary>
    public interface IPathingBackpressureReadout
    {
        /// <summary>True iff shed-mode is active (queue sustained &gt; high-water for SustainMs).</summary>
        bool ShedActive { get; }

        /// <summary>Last observed queue depth.</summary>
        int QueueDepth { get; }

        /// <summary>Configured queue capacity.</summary>
        int QueueCapacity { get; }

        /// <summary>Total requests dropped since pool construction (low-priority shed).</summary>
        long DroppedTotal { get; }

        /// <summary>Total requests expired at dequeue (L-085).</summary>
        long ExpiredTotal { get; }

        /// <summary>Median solve latency in ms over the rolling reservoir (L-041).</summary>
        float SolveLatencyMsP50 { get; }

        /// <summary>99th-percentile solve latency in ms over the rolling reservoir (L-041).</summary>
        float SolveLatencyMsP99 { get; }

        /// <summary>MonoClock tick when shed-mode last transitioned OFF→ON (L-037).</summary>
        long LastShedTransitionMono { get; }

        /// <summary>MonoClock tick when shed-mode last transitioned ON→OFF (L-037).</summary>
        long LastClearTransitionMono { get; }
    }
}
