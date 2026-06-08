using System;
using System.Collections.Concurrent;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Result of <see cref="RecentDamageDealtAccumulator.SumWithin"/>. Renamed from
    /// DamageSummary to avoid the same-namespace collision with
    /// <see cref="DamageHintBuffer.DamageSummary"/> (victim-side mirror). Same three
    /// fields — TotalMagnitude / DominantTypeCode / AttackCount — different lifecycle
    /// (attacker-side accumulator, not victim-side ring). Zero-default empty.
    /// </summary>
    public readonly struct DealtDamageSummary
    {
        public readonly float TotalMagnitude;
        public readonly byte DominantTypeCode;
        public readonly int AttackCount;

        public DealtDamageSummary(float totalMagnitude, byte dominantTypeCode, int attackCount)
        {
            TotalMagnitude = totalMagnitude;
            DominantTypeCode = dominantTypeCode;
            AttackCount = attackCount;
        }
    }

    /// <summary>
    /// Plan §8.4: attacker-side mirror of <see cref="DamageHintBuffer"/>. Subscribes to
    /// <c>WorldEventBus&lt;DamageObserved&gt;</c> as a parallel consumer and indexes each
    /// event by <c>Damage.AttackerIfKnown</c> instead of the victim id. Feeds
    /// AV[32..33] / Threat[32..33] / Intent[20] (slot citations in §3.3/§3.5/§3.2).
    ///
    /// Per-attacker ring depth 16 (round-2 — matches the HealthSidecar HP history ring
    /// added in §8.2). Drop-oldest on overflow. Single-writer (WorldEventBus.Publish
    /// dispatches on the main thread from SmartEventBridge.OnTankDamage); readers may
    /// be background daemons (StrategicStateExtractor §8.9) — Snapshot under the per-ring
    /// lock matches the DamageHintBuffer pattern at DamageHintBuffer.cs:67-93.
    ///
    /// Lifecycle: owned by SmartRuntime. Static field holds the captured subscription
    /// delegate so GC does not collect it between Wire and Shutdown — same survival
    /// pattern as DamageHintBuffer.cs:100-101.
    /// </summary>
    public sealed class RecentDamageDealtAccumulator : ITechSidecar
    {
        public string Name => "RecentDamageDealtAccumulator";
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotKeys()
            => (System.Collections.Generic.IReadOnlyCollection<TechId>)_byAttacker.Keys;

        /// <summary>Per-attacker ring depth. Matches HealthSidecar's new HP-history ring (§8.2).</summary>
        public const int CapacityPerTech = 16;

        // One entry per dealt-damage event. Magnitude is the engine ManDamage.Damage
        // float; DamageType byte is plumbed via SanitizedDamageInfo.DamageType per
        // §8.5 (until that delta lands callers may pass 0 — sum still works, dominant
        // type degrades to "all zero" which the consumer treats as Standard).
        private struct Entry
        {
            public float Magnitude;
            public byte DamageType;
            public long TickMono;
        }

        // Per-attacker ring. Compact array + head index. Lock-protected to keep
        // Snapshot consistent with concurrent Push, matching DamageHintBuffer's ring
        // semantics (DamageHintBuffer.cs:73-92).
        private sealed class Ring
        {
            internal readonly Entry[] _slots = new Entry[CapacityPerTech];
            internal int _head;   // next write index
            internal int _count;  // 0..CapacityPerTech

            internal void Push(Entry e)
            {
                lock (_slots)
                {
                    _slots[_head] = e;
                    _head = (_head + 1) % CapacityPerTech;
                    if (_count < CapacityPerTech) _count++;
                }
            }

            // Walk under the same lock that Push holds. Folds total + per-type
            // histogram + count in one pass to avoid two locked traversals from
            // SumWithin's hot path.
            internal void SumWithinLocked(long fromMonoTick, long toMonoTick,
                out float total, out byte dominantType, out int attackCount)
            {
                total = 0f;
                attackCount = 0;
                // Tiny histogram on the stack — DamageType byte is the engine's
                // ManDamage.DamageType enum (Impact/Standard/Cutting/Plasma/Bullet/
                // Explosive/Ion/Fire/Energy — 9 values today). Cap at 16 so a future
                // enum extension fits without bounds drama.
                var hist = new int[16];
                byte best = 0;
                int bestCount = -1;

                lock (_slots)
                {
                    int n = _count;
                    int start = (_head - n + CapacityPerTech) % CapacityPerTech;
                    for (int i = 0; i < n; i++)
                    {
                        var e = _slots[(start + i) % CapacityPerTech];
                        if (e.TickMono < fromMonoTick) continue;
                        if (e.TickMono > toMonoTick) continue;
                        total += e.Magnitude;
                        attackCount++;
                        int slot = e.DamageType & 0x0F;
                        int h = ++hist[slot];
                        if (h > bestCount) { bestCount = h; best = (byte)slot; }
                    }
                }

                dominantType = best;
            }
        }

        private readonly ConcurrentDictionary<TechId, Ring> _byAttacker =
            new ConcurrentDictionary<TechId, Ring>();

        // Static subscription state — same GC-survival pattern as DamageHintBuffer.cs:100.
        private static Action<DamageObserved> _subscription;
        private static RecentDamageDealtAccumulator _wiredInstance;

        /// <summary>
        /// Subscribe this instance to <c>WorldEventBus&lt;DamageObserved&gt;</c>. Called
        /// from <see cref="SmartRuntime.Init"/>. Re-Wire on a new instance unsubscribes
        /// the previous delegate first.
        /// </summary>
        public static void Wire(RecentDamageDealtAccumulator accumulator)
        {
            if (_subscription != null)
                WorldEventBus.Unsubscribe(_subscription);
            _wiredInstance = accumulator;
            _subscription = OnDamage;
            WorldEventBus.Subscribe(_subscription);
        }

        /// <summary>Unsubscribe (called from <see cref="SmartRuntime.Shutdown"/>).</summary>
        public static void Unwire()
        {
            if (_subscription != null) WorldEventBus.Unsubscribe(_subscription);
            _subscription = null;
            _wiredInstance = null;
        }

        // Static dispatch trampoline — capture identity must match for Unsubscribe.
        private static void OnDamage(DamageObserved ev)
        {
            var inst = _wiredInstance;
            if (inst == null) return;
            // Attacker-side filter: ignore self-damage / unattributable hits. The
            // victim-side mirror (DamageHintBuffer) accepts these because the victim
            // is always known; we cannot bucket without an attacker id.
            if (!ev.Damage.HasAttacker) return;
            // DamageType byte sourced from SanitizedDamageInfo.DamageType (Phase-4 wiring
            // plumbed it through ManDamage.DamageInfo.DamageType at SmartEventBridge).
            // Feeds the dominant-type histogram in SumWithin.
            inst.Record(ev.Damage.AttackerIfKnown, ev.Damage.Damage, ev.Damage.DamageType, MonoClock.Now());
        }

        /// <summary>
        /// Append one dealt-damage event for <paramref name="attackerId"/>. Drop-oldest
        /// on overflow. Public so DamageType-byte producers (§8.5 delta) and tests can
        /// push directly without going through the WorldEventBus trampoline.
        /// </summary>
        public void Record(TechId attackerId, float magnitude, byte damageType, long monoTick)
        {
            var ring = _byAttacker.GetOrAdd(attackerId, _ => new Ring());
            ring.Push(new Entry { Magnitude = magnitude, DamageType = damageType, TickMono = monoTick });
        }

        /// <summary>
        /// Sum dealt-damage events for <paramref name="attackerId"/> whose tickMono
        /// falls in [<paramref name="fromMonoTick"/>, <paramref name="toMonoTick"/>].
        /// Returns total magnitude, dominant DamageType byte (mode over entries in
        /// the window — ties broken by first-seen), and attack count. Caller computes
        /// the window from a <c>nowMono</c> + <c>windowSec</c> via
        /// <c>fromMonoTick = nowMono - (long)(windowSec / MonoClock.TickFreq)</c>.
        /// Empty window or unknown attacker returns the {0,0,0} default.
        /// </summary>
        public DealtDamageSummary SumWithin(TechId attackerId, long fromMonoTick, long toMonoTick)
        {
            Ring r;
            if (!_byAttacker.TryGetValue(attackerId, out r))
                return new DealtDamageSummary(0f, 0, 0);
            float total;
            byte dominant;
            int count;
            r.SumWithinLocked(fromMonoTick, toMonoTick, out total, out dominant, out count);
            return new DealtDamageSummary(total, dominant, count);
        }

        /// <summary>L-009 lifecycle hook — symmetric to DamageHintBuffer.Forget at DamageHintBuffer.cs:163.</summary>
        public void Forget(TechId attackerId) { Ring _; _byAttacker.TryRemove(attackerId, out _); }

        public void Clear() => _byAttacker.Clear();

        public int AttackerCount => _byAttacker.Count;
    }
}
