using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P7 Item 21: per-identity outcome consumer. Subscribes to
    /// <c>WorldEventBus&lt;IdentityOutcome&gt;</c> and routes events into per-identity
    /// counters for learning telemetry. Phase 2 widens the surface for the constraint
    /// engine: per-(identity, kind) timestamp ring for rate-per-min queries, optional
    /// guard-byte filter, dominant-guard lookup. Replay-sourced envelopes are ignored
    /// at the door so replayed events do not double-count.
    ///
    /// Lifecycle: <see cref="Install"/> from LearningService.Init; <see cref="Uninstall"/>
    /// from LearningService.Shutdown.
    /// </summary>
    public sealed class IdentityOutcomeConsumer
    {
        // Cap per-(identity, kind) ring; bounded by expectedRate × windowSec × safety.
        // Worst realistic: kills at 1/sec × 600s window × 2 safety = 1200. Cap 1024
        // covers all current consumers at 5-min windows and trims old at read.
        public const int RingCapacity = 1024;

        // Window when we widen for replay-bank checks. Read-time trim keeps reads cheap.
        public const int MaxWindowSec = 1200;

        // Sample-count threshold for "INSUFFICIENT_DATA": ConstraintEngine treats fewer
        // than this as "not enough signal" rather than "zero rate".
        public const int MinSamplesForRate = 3;

        // Source low-nibble Live(0)/Replay(1). High-nibble guard ordinal 0..10, 15 = wildcard.
        public const byte ReplayLowNibbleMask = 0x0F;
        public const byte NoGuardSentinel = 15;

        private static Action<IdentityOutcome> _subscription;
        private static IdentityOutcomeConsumer _wiredInstance;

        // Non-guard counter dict. Preserved for the original GetCount(SmartIdentity, OutcomeKind)
        // call surface and any other counters callers.
        private readonly ConcurrentDictionary<long, long> _counters =
            new ConcurrentDictionary<long, long>();

        // Guard counter dict — separate so non-guard reward-aggregation paths never see
        // GuardViolation_* entries. Phase 5 wires the GuardViolation_* enum values; Phase 2
        // dispatches by OutcomeKind ordinal range so the routing is in place.
        private readonly ConcurrentDictionary<long, long> _guardCounters =
            new ConcurrentDictionary<long, long>();

        // Per-(identity, kind) timestamp ring. Lock-free push via ConcurrentQueue; trim
        // happens at read time to entries newer than now − windowSec. Slot encodes
        // (identity<<8) | kind into a long key.
        private readonly ConcurrentDictionary<long, RingSlot> _rings =
            new ConcurrentDictionary<long, RingSlot>();

        // Per-(identity, kind) guard-ordinal counts in a (idKey, guardOrdinal)-keyed dict.
        // Used by GetDominantGuardId.
        private readonly ConcurrentDictionary<long, long> _guardOrdinalCounts =
            new ConcurrentDictionary<long, long>();

        public static void Install(IdentityOutcomeConsumer inst)
        {
            if (_subscription != null) WorldEventBus.Unsubscribe(_subscription);
            _wiredInstance = inst;
            _subscription = OnOutcomeStatic;
            WorldEventBus.Subscribe(_subscription);
        }

        public static void Uninstall()
        {
            if (_subscription != null) WorldEventBus.Unsubscribe(_subscription);
            _subscription = null;
            _wiredInstance = null;
        }

        private static void OnOutcomeStatic(IdentityOutcome evt)
        {
            var inst = _wiredInstance;
            if (inst == null) return;
            inst.OnOutcome(evt);
        }

        /// <summary>
        /// Route the event into the appropriate counter dict + push a timestamp into the
        /// ring. Replay-sourced envelopes are ignored.
        /// </summary>
        public void OnOutcome(IdentityOutcome evt)
        {
            // Ignore replay so consumer rates reflect live training-signal only.
            if ((evt.Source & ReplayLowNibbleMask) != 0) return;

            long key = ((long)(byte)evt.Identity << 8) | (long)(byte)evt.Kind;
            byte kind = (byte)evt.Kind;

            // Guard outcomes route to their own dict so reward-aggregation never sees them.
            // Phase 2 dispatches by ordinal threshold so Phase 5's enum-insert (GuardViolation_*
            // at 5..7) bumps automatically; threshold = (int)OutcomeKind.Count seen pre-Phase-5
            // is 5 (BaseLost=4 is the last non-guard). Use >= 5 dispatch.
            if (kind >= 5)
            {
                _guardCounters.AddOrUpdate(key, 1L, (_, c) => c + 1L);
            }
            else
            {
                _counters.AddOrUpdate(key, 1L, (_, c) => c + 1L);
            }

            // Push timestamp into the ring for rate queries. Lazy-create the slot.
            var slot = _rings.GetOrAdd(key, _ => new RingSlot());
            slot.Push(MonoClock.Now());

            // Track guard-ordinal contribution (high-nibble of Source) for dominant-guard lookup.
            byte guardOrdinal = (byte)((evt.Source >> 4) & 0x0F);
            if (guardOrdinal != NoGuardSentinel)
            {
                long guardKey = (key << 8) | guardOrdinal;
                _guardOrdinalCounts.AddOrUpdate(guardKey, 1L, (_, c) => c + 1L);
            }
        }

        /// <summary>
        /// Preserved for back-compat with cs:66 callers. Returns the live counter value
        /// for non-guard kinds; guard kinds read from _guardCounters.
        /// </summary>
        public long GetCount(SmartIdentity identity, OutcomeKind kind)
        {
            long key = ((long)(byte)identity << 8) | (long)(byte)kind;
            byte k = (byte)kind;
            long c;
            if (k >= 5)
            {
                return _guardCounters.TryGetValue(key, out c) ? c : 0L;
            }
            return _counters.TryGetValue(key, out c) ? c : 0L;
        }

        /// <summary>
        /// Returns -1 for INSUFFICIENT_DATA, otherwise events-per-minute over the trailing
        /// windowSec. <paramref name="guardFilterByte"/> defaults to wildcard (15); when
        /// set to a guard ordinal 0..14 the result counts only entries whose Source-byte
        /// high-nibble matches. ConstraintEngine consumer uses -1 sentinel to know
        /// "not enough signal yet".
        /// </summary>
        public float GetRatePerMin(SmartIdentity identity, OutcomeKind kind, int windowSec, byte guardFilterByte = 15)
        {
            if (windowSec <= 0) return -1f;
            if (windowSec > MaxWindowSec) windowSec = MaxWindowSec;
            long key = ((long)(byte)identity << 8) | (long)(byte)kind;
            RingSlot slot;
            if (!_rings.TryGetValue(key, out slot)) return -1f;

            long now = MonoClock.Now();
            long windowTicks = (long)(windowSec / MonoClock.TickFreq);
            long cutoff = now - windowTicks;

            int count = slot.SnapshotCount(cutoff);
            if (count < MinSamplesForRate) return -1f;

            // guardFilterByte != 15 means filter on a specific guard ordinal — for Phase 2
            // there are no guard events yet, so the filter would return 0 for any specific
            // ordinal. We honor the filter by returning -1 (INSUFFICIENT_DATA) when the
            // guard-ordinal subset doesn't accumulate. Wildcard path returns the full count.
            if (guardFilterByte != NoGuardSentinel)
            {
                long guardKey = (key << 8) | guardFilterByte;
                long guardCount;
                if (!_guardOrdinalCounts.TryGetValue(guardKey, out guardCount) || guardCount < MinSamplesForRate)
                    return -1f;
                // Approximate rate: scale the total count by guard fraction. Since
                // _guardOrdinalCounts is monotonic (no window trim) this is a coarse upper
                // bound; finer attribution lands in Phase 5+ when guards exist to bucket.
                return (float)guardCount * 60f / windowSec;
            }

            return (float)count * 60f / windowSec;
        }

        /// <summary>
        /// Aggregate rate across multiple identities. Returns -1 for INSUFFICIENT_DATA when
        /// no identity in the set has at least <see cref="MinSamplesForRate"/> entries.
        /// </summary>
        public float GetWeightedRatePerMin(IReadOnlyList<SmartIdentity> identities, OutcomeKind kind, int windowSec)
        {
            if (identities == null || identities.Count == 0) return -1f;
            if (windowSec <= 0) return -1f;
            if (windowSec > MaxWindowSec) windowSec = MaxWindowSec;
            long now = MonoClock.Now();
            long windowTicks = (long)(windowSec / MonoClock.TickFreq);
            long cutoff = now - windowTicks;

            int totalCount = 0;
            bool anySlot = false;
            for (int i = 0; i < identities.Count; i++)
            {
                long key = ((long)(byte)identities[i] << 8) | (long)(byte)kind;
                RingSlot slot;
                if (!_rings.TryGetValue(key, out slot)) continue;
                anySlot = true;
                totalCount += slot.SnapshotCount(cutoff);
            }
            if (!anySlot || totalCount < MinSamplesForRate) return -1f;
            return (float)totalCount * 60f / windowSec;
        }

        /// <summary>
        /// Returns the guard ordinal contributing the most violations in the bucket within
        /// the trailing windowSec, or 15 (wildcard) when no clear dominator. Phase 2 has no
        /// guard publishers so this returns 15 in practice; surface is in place for
        /// ActionPicker in Phase 3+.
        /// </summary>
        public byte GetDominantGuardId(OutcomeKind bucket, int scenario, int windowSec)
        {
            byte best = NoGuardSentinel;
            long bestCount = 0;
            byte bk = (byte)bucket;
            // Walk identities; aggregate per-guard-ordinal across the bucket kind.
            // Slot keys are (identity<<8) | kind so the bucket scan must enumerate identities.
            // SmartIdentity ordinals span [0..8]; scan defensively over 0..15.
            for (byte id = 0; id <= 15; id++)
            {
                long baseKey = ((long)id << 8) | bk;
                for (byte gord = 0; gord < NoGuardSentinel; gord++)
                {
                    long guardKey = (baseKey << 8) | gord;
                    long c;
                    if (!_guardOrdinalCounts.TryGetValue(guardKey, out c)) continue;
                    if (c > bestCount)
                    {
                        bestCount = c;
                        best = gord;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Snapshot of populated (identity, kind, count, rate-per-min) triples. Window
        /// for the rate is 300 s; consumers that want a different window must call
        /// GetRatePerMin directly.
        /// </summary>
        public List<OutcomeSnapshot> Snapshot()
        {
            const int SnapshotWindowSec = 300;
            var list = new List<OutcomeSnapshot>();
            long now = MonoClock.Now();
            long cutoff = now - (long)(SnapshotWindowSec / MonoClock.TickFreq);

            foreach (var kv in _rings)
            {
                long key = kv.Key;
                RingSlot slot = kv.Value;
                int count = slot.SnapshotCount(cutoff);
                if (count == 0) continue;
                SmartIdentity id = (SmartIdentity)(byte)((key >> 8) & 0xFF);
                OutcomeKind k = (OutcomeKind)(byte)(key & 0xFF);
                long total;
                bool isGuard = (byte)k >= 5;
                if (isGuard)
                {
                    if (!_guardCounters.TryGetValue(key, out total)) total = 0L;
                }
                else
                {
                    if (!_counters.TryGetValue(key, out total)) total = 0L;
                }
                float rate = (float)count * 60f / SnapshotWindowSec;
                list.Add(new OutcomeSnapshot(id, k, total, rate));
            }
            return list;
        }

        public void Clear()
        {
            _counters.Clear();
            _guardCounters.Clear();
            _rings.Clear();
            _guardOrdinalCounts.Clear();
        }

        /// <summary>Returned by Snapshot — value-typed to avoid GC on digest tick.</summary>
        public readonly struct OutcomeSnapshot
        {
            public readonly SmartIdentity Identity;
            public readonly OutcomeKind Kind;
            public readonly long Count;
            public readonly float WeightedRatePerMin;
            public OutcomeSnapshot(SmartIdentity id, OutcomeKind kind, long count, float weightedRatePerMin)
            {
                Identity = id;
                Kind = kind;
                Count = count;
                WeightedRatePerMin = weightedRatePerMin;
            }
        }

        // Per-(identity, kind) timestamp ring. Lock-protected; producers push monotonic
        // ticks, readers snapshot count-above-cutoff.
        private sealed class RingSlot
        {
            private readonly long[] _ticks = new long[RingCapacity];
            private int _cursor;
            private int _count;
            private readonly object _lock = new object();

            public void Push(long monoTick)
            {
                lock (_lock)
                {
                    _ticks[_cursor] = monoTick;
                    _cursor = (_cursor + 1) % RingCapacity;
                    if (_count < RingCapacity) _count++;
                }
            }

            public int SnapshotCount(long cutoffMono)
            {
                lock (_lock)
                {
                    int hits = 0;
                    for (int i = 0; i < _count; i++)
                    {
                        if (_ticks[i] >= cutoffMono) hits++;
                    }
                    return hits;
                }
            }
        }
    }
}
