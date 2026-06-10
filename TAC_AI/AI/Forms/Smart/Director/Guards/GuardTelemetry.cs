using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Per-guard fire counters + chronic-suppression state. Anti-stifling: per-(tech, guard)
    // suppression engages after 5 consecutive re-fires; releases when the pathology test
    // holds FALSE for 5 minutes.
    public static class GuardTelemetry
    {
        public const int ChronicConsecutiveRefireThreshold = 5;
        public const float ChronicFalseHoldSeconds = 300f;

        private sealed class ChronicState
        {
            public int ConsecRefires;
            public long LastFireMono;
            public long FirstFalseMono;
            public bool Suppressed;
        }

        // Bulk counters per guard ordinal.
        private static readonly long[] _firedCount = new long[GuardRegistry.GuardCount];
        private static readonly long[] _suppressedByCooldownCount = new long[GuardRegistry.GuardCount];
        private static readonly long[] _exceptionMatchedCount = new long[GuardRegistry.GuardCount];

        // Anti-stifling cross-guard counters. Only the hard-correct path populates the
        // model-override counter; observe-only guards never enter arbitration.
        private static long _guardsReFiredWithinCooldown;
        private static long _learnedModelOverrodeGuardCount;
        private static long _softCorrectTelemetryFiresCount;
        private static long _softCorrectKinematicCorrelationCount;
        // Hard-correct mode-collapse + oscillation surfaces. hard_correct_on_active_target =
        // GroundedAircraft fired while strafing a ground target (digest dive-interrupt
        // warning). oscill = near-suppression-threshold anti-loop cycle count.
        private static long _hardCorrectOnActiveTargetCount;
        private static long _oscillCount;

        // Per-(tech, guard) chronic-suppression dict keyed by ((techValue << 8) | ordinal).
        private static readonly object _chronicGate = new object();
        private static readonly Dictionary<long, ChronicState> _chronic =
            new Dictionary<long, ChronicState>();

        private static long PackKey(TechId techId, int guardOrdinal)
        {
            return ((long)techId.Value << 8) | (long)(guardOrdinal & 0xFF);
        }

        public static void RecordFire(int guardOrdinal)
        {
            if (guardOrdinal < 0 || guardOrdinal >= GuardRegistry.GuardCount) return;
            Interlocked.Increment(ref _firedCount[guardOrdinal]);
        }

        public static void RecordSuppressedByCooldown(int guardOrdinal)
        {
            if (guardOrdinal < 0 || guardOrdinal >= GuardRegistry.GuardCount) return;
            Interlocked.Increment(ref _suppressedByCooldownCount[guardOrdinal]);
            Interlocked.Increment(ref _guardsReFiredWithinCooldown);
        }

        public static void RecordExceptionMatched(int guardOrdinal)
        {
            if (guardOrdinal < 0 || guardOrdinal >= GuardRegistry.GuardCount) return;
            Interlocked.Increment(ref _exceptionMatchedCount[guardOrdinal]);
        }

        // Chronic-suppression check. Called BEFORE publishing an outcome — returns
        // true when the guard is suppressed for this tech and the worker should
        // record the suppression-fire telemetry then skip publish. Falling-edge of the
        // pathology test (notifyTestFell=true) clears the consec count + records the
        // first-false mono; if 5 min pass with the test held FALSE, suppression lifts.
        public static bool IsSuppressed(TechId techId, int guardOrdinal, bool testRoseThisTick,
            bool testHeldFalse)
        {
            long key = PackKey(techId, guardOrdinal);
            long now = MonoClock.Now();
            bool emitChronic = false;
            lock (_chronicGate)
            {
                ChronicState st;
                if (!_chronic.TryGetValue(key, out st))
                {
                    st = new ChronicState();
                    _chronic[key] = st;
                }
                if (testRoseThisTick)
                {
                    st.ConsecRefires++;
                    st.LastFireMono = now;
                    st.FirstFalseMono = 0L;
                    if (!st.Suppressed && st.ConsecRefires >= ChronicConsecutiveRefireThreshold)
                    {
                        st.Suppressed = true;
                        emitChronic = true;
                    }
                }
                else if (testHeldFalse)
                {
                    if (st.FirstFalseMono == 0L) st.FirstFalseMono = now;
                    if (st.Suppressed)
                    {
                        float held = MonoClock.Seconds(st.FirstFalseMono, now);
                        if (held >= ChronicFalseHoldSeconds)
                        {
                            st.Suppressed = false;
                            st.ConsecRefires = 0;
                        }
                    }
                }
                bool suppressed = st.Suppressed;
                if (emitChronic)
                {
                    // Emit outside the lock proper would mean an extra branch — but
                    // LogWarnFileOnly is itself dedup'd by key and cheap on the steady-state
                    // path, so the in-lock emission is fine for the 1 Hz cadence.
                    string guardName = GuardRegistry.GetName(guardOrdinal);
                    DebugTAC_AI.LogWarnFileOnly(
                        "guard-chronic-" + guardName + "-" + techId.Value,
                        "[GUARD-CHRONIC]:" + guardName + ":tech=" + techId.Value);
                }
                return suppressed;
            }
        }

        public static void ForgetTech(TechId techId)
        {
            lock (_chronicGate)
            {
                var toDrop = new List<long>();
                foreach (var kv in _chronic)
                {
                    if ((int)(kv.Key >> 8) == techId.Value) toDrop.Add(kv.Key);
                }
                for (int i = 0; i < toDrop.Count; i++) _chronic.Remove(toDrop[i]);
            }
        }

        public static long GetFiredCount(int guardOrdinal)
        {
            if (guardOrdinal < 0 || guardOrdinal >= GuardRegistry.GuardCount) return 0;
            return Interlocked.Read(ref _firedCount[guardOrdinal]);
        }

        public static long GuardsReFiredWithinCooldown
            => Interlocked.Read(ref _guardsReFiredWithinCooldown);

        public static long LearnedModelOverrodeGuardCount
            => Interlocked.Read(ref _learnedModelOverrodeGuardCount);

        public static long SoftCorrectTelemetryFiresCount
            => Interlocked.Read(ref _softCorrectTelemetryFiresCount);

        public static long SoftCorrectKinematicCorrelationCount
            => Interlocked.Read(ref _softCorrectKinematicCorrelationCount);

        public static long HardCorrectOnActiveTargetCount
            => Interlocked.Read(ref _hardCorrectOnActiveTargetCount);

        public static long OscillCount
            => Interlocked.Read(ref _oscillCount);

        public static void RecordHardCorrectOnActiveTarget()
            => Interlocked.Increment(ref _hardCorrectOnActiveTargetCount);

        public static void RecordOscillation()
            => Interlocked.Increment(ref _oscillCount);

        public static void RecordLearnedModelOverride()
            => Interlocked.Increment(ref _learnedModelOverrodeGuardCount);

        public static void RecordSoftCorrectTelemetryFire()
            => Interlocked.Increment(ref _softCorrectTelemetryFiresCount);

        public static void RecordSoftCorrectKinematicCorrelation()
            => Interlocked.Increment(ref _softCorrectKinematicCorrelationCount);

        public static void Reset()
        {
            for (int i = 0; i < GuardRegistry.GuardCount; i++)
            {
                Interlocked.Exchange(ref _firedCount[i], 0L);
                Interlocked.Exchange(ref _suppressedByCooldownCount[i], 0L);
                Interlocked.Exchange(ref _exceptionMatchedCount[i], 0L);
            }
            Interlocked.Exchange(ref _guardsReFiredWithinCooldown, 0L);
            Interlocked.Exchange(ref _learnedModelOverrodeGuardCount, 0L);
            Interlocked.Exchange(ref _softCorrectTelemetryFiresCount, 0L);
            Interlocked.Exchange(ref _softCorrectKinematicCorrelationCount, 0L);
            Interlocked.Exchange(ref _hardCorrectOnActiveTargetCount, 0L);
            Interlocked.Exchange(ref _oscillCount, 0L);
            lock (_chronicGate) _chronic.Clear();
        }
    }
}
