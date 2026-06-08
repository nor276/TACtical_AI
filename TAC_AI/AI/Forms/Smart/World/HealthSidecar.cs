using System.Collections.Concurrent;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// P6 Item 26 (REV 7) — per-tech tank-wide HpFraction sidecar via block-survival ratio.
    ///
    /// Matches the production multi-block HP semantic from <c>TankAIHelper.cs:3267</c>
    /// (<c>DamageThreshold = (1 - blockC/maxBlockCount) * 100</c>). Tracks
    /// <c>(current, max)</c> per TechId; max ratchets upward to handle in-game block
    /// additions. NEVER reduces max — damage is measured against the tech's high-water mark.
    ///
    /// Populated by main-thread <c>SmartForm.ObserveWorldTechsIfDue</c> (33ms cadence).
    /// Read by <see cref="TAC_AI.AI.Forms.Smart.Identity.SmartIdentity.RepairSupport"/>
    /// goal source. Lifecycle mirrors P4 IntentSidecar/DamageHints pattern (cleared in
    /// Shutdown; Forget hooked from <c>SmartRuntime.Deregister</c> + <c>WorldModel.DeregisterTech</c>).
    ///
    /// Single-block tanks: <c>blockCount = 1 = maxBlockCount</c> always → <c>HpFraction = 1.0</c>
    /// until the block is destroyed (which despawns the tech). Single-block HP loss is below
    /// the granularity this sidecar tracks; acceptable for v0.2 RepairSupport heuristics
    /// (which target visible block loss on multi-block techs).
    /// </summary>
    public sealed class HealthSidecar : ITechSidecar
    {
        private struct HpState { public int Current; public int Max; }

        private readonly ConcurrentDictionary<TechId, HpState> _state =
            new ConcurrentDictionary<TechId, HpState>();

        // L-028 ITechSidecar wiring.
        public string Name => "HealthSidecar";
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotKeys()
            => (System.Collections.Generic.IReadOnlyCollection<TechId>)_state.Keys;

        /// <summary>
        /// Record current block count for <paramref name="id"/>. First call snapshots max;
        /// subsequent calls ratchet max upward if blockCount grows. Zero/negative block
        /// counts are ignored.
        /// </summary>
        public void Record(TechId id, int blockCount)
        {
            if (blockCount <= 0) return;
            var updated = _state.AddOrUpdate(id,
                addValueFactory: _ => new HpState { Current = blockCount, Max = blockCount },
                updateValueFactory: (_, s) => new HpState
                {
                    Current = blockCount,
                    Max = blockCount > s.Max ? blockCount : s.Max
                });
            // §8.2 R2-21: shadow the block-count ratchet into the lookback ring so the
            // §7.2 ActionValue reward formula can compute hp(now) − hp(now − 0.5s).
            float frac = updated.Max > 0 ? (float)updated.Current / updated.Max : 1f;
            if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
            RecordHpFraction(id, frac, MonoClock.Now());
        }

        /// <summary>Tank-wide HpFraction in [0, 1]. Returns 1f for unknown techs (safe default).</summary>
        public float Get(TechId id)
        {
            HpState s;
            if (!_state.TryGetValue(id, out s) || s.Max <= 0) return 1f;
            float frac = (float)s.Current / s.Max;
            return frac < 0f ? 0f : (frac > 1f ? 1f : frac);
        }

        public void Forget(TechId id)
        {
            HpState _; _state.TryRemove(id, out _);
            HpHistoryRing __; _hpRings.TryRemove(id, out __);
        }
        public void Clear() { _state.Clear(); _hpRings.Clear(); }
        public int Count => _state.Count;

        // §8.2 R2-21 DELTA: per-tech HpFraction lookback ring (16-deep ≈ 0.5s at 30Hz).
        // Ring layout + lock discipline mirror DamageHintBuffer.Ring (DamageHintBuffer.cs:67-93)
        // verbatim — drop-oldest, single-writer (main-thread producer), any-thread reader.
        public const int HpRingCapacity = 16;

        private sealed class HpHistoryRing
        {
            internal readonly long[] _ticks = new long[HpRingCapacity];
            internal readonly float[] _fracs = new float[HpRingCapacity];
            internal int _head;
            internal int _count;

            internal void Push(long tickMono, float hpFraction)
            {
                lock (_ticks)
                {
                    _ticks[_head] = tickMono;
                    _fracs[_head] = hpFraction;
                    _head = (_head + 1) % HpRingCapacity;
                    if (_count < HpRingCapacity) _count++;
                }
            }

            // Newest-sample-strictly-before cutoff. Walks ring under the same lock as Push;
            // binary-search-style spirit but a 16-element linear walk is faster than the
            // index math + monotonicity branches. Samples land chronological by Push order.
            internal bool TryGetAt(long monoCutoff, out float hpFraction)
            {
                lock (_ticks)
                {
                    if (_count <= 0) { hpFraction = 0f; return false; }
                    int start = (_head - _count + HpRingCapacity) % HpRingCapacity;
                    long bestTick = long.MinValue;
                    float bestFrac = 0f;
                    bool found = false;
                    for (int i = 0; i < _count; i++)
                    {
                        int idx = (start + i) % HpRingCapacity;
                        long t = _ticks[idx];
                        if (t <= monoCutoff && t >= bestTick)
                        {
                            bestTick = t;
                            bestFrac = _fracs[idx];
                            found = true;
                        }
                    }
                    hpFraction = bestFrac;
                    return found;
                }
            }
        }

        private readonly ConcurrentDictionary<TechId, HpHistoryRing> _hpRings =
            new ConcurrentDictionary<TechId, HpHistoryRing>();

        /// <summary>
        /// Push <paramref name="hpFraction"/> stamped with <paramref name="monoTick"/> into the
        /// per-tech ring. Called immediately downstream of <see cref="Record"/>; also exposed
        /// for callers that already hold a fraction (no need to re-derive from block counts).
        /// </summary>
        public void RecordHpFraction(TechId id, float hpFraction, long monoTick)
        {
            var ring = _hpRings.GetOrAdd(id, _ => new HpHistoryRing());
            ring.Push(monoTick, hpFraction);
        }

        /// <summary>
        /// Read the nearest sample at or before <paramref name="monoCutoff"/> for the §7.2
        /// reward formula. Returns false on unknown tech / empty ring / no sample old enough.
        /// Any-thread.
        /// </summary>
        public bool GetAt(TechId id, long monoCutoff, out float hpFraction)
        {
            HpHistoryRing ring;
            if (!_hpRings.TryGetValue(id, out ring)) { hpFraction = 0f; return false; }
            return ring.TryGetAt(monoCutoff, out hpFraction);
        }

        // Lifecycle alias for symmetry with SmartRuntime/WorldModel deregister wiring.
        public void OnTechRecycle(TechId id) => Forget(id);
    }
}
