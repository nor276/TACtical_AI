using System;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// P9 Item 29 (REV 7 C3): WorkerPool sizing policy. Authoritative source for the
    /// default worker count — <see cref="WorkerPool.DefaultWorkerCount"/> delegates here.
    ///
    /// **No hard upper cap** per FORM-SPEC §5.4 + WorkerPool.cs:43. REV 1 proposed a
    /// `MaxWorkers = 22` clamp citing one incident-log comment; REV 2/REV 7 corrected to
    /// drop the cap entirely (would regress Threadripper / EPYC / M-series Ultra hosts
    /// 25–65% vs the uncapped ProcessorCount-2 default).
    ///
    /// <see cref="Override"/> &gt; 0 lets the user / test harness pin a specific count
    /// (still clamped to <see cref="MinWorkers"/> minimum). Negative or zero = use the
    /// policy default.
    /// </summary>
    public static class WorkerPoolSizing
    {
        public const int MinWorkers = 2;

        /// <summary>Pin worker count via test harness / runtime tunable. -1 = use policy default.</summary>
        public static int Override = -1;

        /// <summary>
        /// Resolved worker count. With Override=-1 returns <c>Math.Max(2, ProcessorCount-2)</c>
        /// — bit-identical to the pre-P9 <see cref="WorkerPool.DefaultWorkerCount"/> on every host.
        /// </summary>
        public static int Resolve()
        {
            if (Override > 0) return Math.Max(MinWorkers, Override);
            return Math.Max(MinWorkers, Environment.ProcessorCount - 2);
        }
    }
}
