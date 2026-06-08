using System;
using System.Collections.Concurrent;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// P4 Item 11: per-damage-event hint. DirectionWorld REMOVED per REV 2 — engine
    /// <c>ManDamage.DamageInfo</c> exposes no <c>HitPosition</c> or <c>HitForceDir</c>;
    /// SanitizedDamageInfo hardwires both to <c>Vector3.zero</c> at SmartEventBridge.cs:314-318.
    /// Shipping a zeroed direction would propagate phantom data into consumers.
    /// </summary>
    public readonly struct DamageHint
    {
        public readonly float Magnitude;
        public readonly long TickMono;
        public readonly TechId AttackerIfKnown;
        public readonly bool HasAttacker;
        // P11 T6 Item 68 (post-decompile, was v0.3): the engine ManDamage.DamageInfo
        // struct DOES carry HitPosition + DamageDirection (verified at
        // F:\Tac-AI\Decompiled\AssemblyCSharp\ManDamage.cs:54-88). SmartEventBridge now
        // pipes those through; consumers (RepairSupportGoalSource facing-threat orbit)
        // read the world-space impact pose to compute "place me opposite the attacker
        // bearing" math without needing a v0.3 surface.
        public readonly UnityEngine.Vector3 ImpactPositionWorld;
        public readonly UnityEngine.Vector3 ImpactDirectionWorld;
        // Smart §8.5: tech-wide DamageType enum cast to byte. Source is
        // ManDamage.DamageInfo.DamageType (ManDamage.cs:58) — NOT per-block
        // Damageable.DamageableType. Plumbed at SmartEventBridge's SanitizedDamageInfo
        // producer; SumWithin consumers vote dominantType over the visible window.
        public readonly byte DamageType;

        public DamageHint(float magnitude, long tickMono, TechId attackerIfKnown, bool hasAttacker,
            UnityEngine.Vector3 impactPositionWorld, UnityEngine.Vector3 impactDirectionWorld,
            byte damageType)
        {
            Magnitude = magnitude;
            TickMono = tickMono;
            AttackerIfKnown = attackerIfKnown;
            HasAttacker = hasAttacker;
            ImpactPositionWorld = impactPositionWorld;
            ImpactDirectionWorld = impactDirectionWorld;
            DamageType = damageType;
        }
    }

    /// <summary>
    /// Smart §8.5 / F-05: small immutable summary returned by
    /// <see cref="DamageHintBuffer.SumWithin(TechId,long,long)"/>. Fields:
    ///   <c>TotalMagnitude</c> — sum of <c>DamageHint.Magnitude</c> in window
    ///   <c>DominantTypeCode</c> — mode over <c>DamageHint.DamageType</c> in window (0 when empty)
    ///   <c>AttackerCount</c> — distinct <c>AttackerIfKnown</c> values in window (HasAttacker only)
    /// </summary>
    public readonly struct DamageSummary
    {
        public readonly float TotalMagnitude;
        public readonly byte DominantTypeCode;
        public readonly int AttackerCount;

        public DamageSummary(float totalMagnitude, byte dominantTypeCode, int attackerCount)
        {
            TotalMagnitude = totalMagnitude;
            DominantTypeCode = dominantTypeCode;
            AttackerCount = attackerCount;
        }

        public static readonly DamageSummary Empty = new DamageSummary(0f, 0, 0);
    }

    /// <summary>
    /// P4 Item 11: per-victim drop-oldest ring buffer of recent damage events.
    /// Subscribes to <c>WorldEventBus&lt;DamageObserved&gt;</c> as a parallel consumer
    /// alongside <c>LearningService.OnDamageObserved</c> (multi-subscriber-safe — EventBus
    /// Publish iterates a copy-on-write snapshot per EventBus.cs:148-152, 198-210).
    ///
    /// Per-victim ring: <see cref="CapacityPerTech"/> entries, drop-oldest on overflow.
    /// Storage <see cref="ConcurrentDictionary{TKey, TValue}"/> because Publish runs on
    /// the main thread (SmartEventBridge.cs:320) but TryGetRecent reads from any consumer
    /// (P6 RepairSupport reads on a Coordinator worker via the v0.3 facing-threat path —
    /// not the v0.2 plain-orbit path, which uses HpFraction only).
    ///
    /// Lifecycle: owned by <see cref="SmartRuntime"/>. Subscription delegate captured into
    /// a static field at <see cref="Wire"/> so it survives GC between Wire and Shutdown.
    /// </summary>
    public sealed class DamageHintBuffer : ITechSidecar
    {
        // L-028 ITechSidecar wiring.
        public string Name => "DamageHintBuffer";
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotKeys()
            => (System.Collections.Generic.IReadOnlyCollection<TechId>)_byTech.Keys;

        /// <summary>Max DamageHints retained per victim. Older entries are dropped on overflow.</summary>
        public const int CapacityPerTech = 8;

        // Per-victim ring buffer. Compact array + head index. Single-writer (main thread
        // via WorldEventBus.Publish dispatch); readers see a snapshot via Count + index
        // walk. Lock-free; race on overlap is bounded by the ring size which is tiny.
        private sealed class Ring
        {
            internal readonly DamageHint[] _slots = new DamageHint[CapacityPerTech];
            internal int _head;   // next write index
            internal int _count;  // 0..CapacityPerTech

            internal void Push(DamageHint h)
            {
                lock (_slots)
                {
                    _slots[_head] = h;
                    _head = (_head + 1) % CapacityPerTech;
                    if (_count < CapacityPerTech) _count++;
                }
            }

            internal int Snapshot(DamageHint[] dst)
            {
                lock (_slots)
                {
                    int n = _count;
                    int start = (_head - n + CapacityPerTech) % CapacityPerTech;
                    for (int i = 0; i < n; i++) dst[i] = _slots[(start + i) % CapacityPerTech];
                    return n;
                }
            }
        }

        private readonly ConcurrentDictionary<TechId, Ring> _byTech =
            new ConcurrentDictionary<TechId, Ring>();

        // Static field holds the subscription delegate so GC doesn't collect it between
        // Wire and Shutdown. P4 Item 11 producer-wiring requirement.
        private static Action<DamageObserved> _subscription;
        private static DamageHintBuffer _wiredInstance;

        /// <summary>
        /// Subscribe this instance to <c>WorldEventBus&lt;DamageObserved&gt;</c>. Called once
        /// at <see cref="SmartRuntime.Init"/> after WorldModel construction. Re-Wire on a
        /// new instance unsubscribes the previous instance's delegate first.
        /// </summary>
        public static void Wire(DamageHintBuffer buffer)
        {
            if (_subscription != null)
                WorldEventBus.Unsubscribe(_subscription);
            _wiredInstance = buffer;
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

        // Static dispatch trampoline (matches the captured-delegate identity for Unsubscribe).
        private static void OnDamage(DamageObserved ev)
        {
            var inst = _wiredInstance;
            if (inst == null) return;
            // Smart §8.5: damageType byte sourced from SanitizedDamageInfo.DamageType,
            // populated at SmartEventBridge.OnTankDamage from ManDamage.DamageInfo.DamageType
            // (ManDamage.cs:58). Feeds SumWithin's DominantTypeCode mode aggregation.
            inst.Push(ev.Id, new DamageHint(
                magnitude: ev.Damage.Damage,
                tickMono: MonoClock.Now(),
                attackerIfKnown: ev.Damage.AttackerIfKnown,
                hasAttacker: ev.Damage.HasAttacker,
                impactPositionWorld: ev.Damage.ImpactPositionWorld,
                impactDirectionWorld: ev.Damage.ImpactDirectionWorld,
                damageType: ev.Damage.DamageType));
        }

        private void Push(TechId victim, DamageHint h)
        {
            var ring = _byTech.GetOrAdd(victim, _ => new Ring());
            ring.Push(h);
        }

        /// <summary>
        /// Copy up to <paramref name="dst"/>.Length most-recent hints for <paramref name="victim"/>
        /// into <paramref name="dst"/> in chronological order. Returns number of valid entries.
        /// Caller allocates the buffer (avoids per-query GC).
        /// </summary>
        public int TryGetRecent(TechId victim, DamageHint[] dst)
        {
            if (dst == null || dst.Length == 0) return 0;
            Ring r;
            if (!_byTech.TryGetValue(victim, out r)) return 0;
            // Snapshot into a local sized buffer then copy out — bounded by min of ring + dst.
            var tmp = new DamageHint[CapacityPerTech];
            int n = r.Snapshot(tmp);
            int copyN = n < dst.Length ? n : dst.Length;
            for (int i = 0; i < copyN; i++) dst[i] = tmp[n - copyN + i];  // most recent at the end
            return copyN;
        }

        /// <summary>
        /// Smart §8.5 / F-05: sum + dominant-type + attacker-count over the per-victim ring
        /// for hints whose <c>TickMono</c> falls in <c>[fromMonoTick, toMonoTick]</c> inclusive.
        /// Returns <see cref="DamageSummary.Empty"/> when the victim has no ring or no entries
        /// land in the window. Bounds reversed (from &gt; to) also returns Empty.
        ///
        /// Background-thread safe — snapshots under the same per-instance lock the writer uses
        /// (`Ring._slots`), then aggregates lock-free. Distinct-attacker count uses an O(n²)
        /// scan over the visible window, bounded by <see cref="CapacityPerTech"/>=8.
        /// </summary>
        public DamageSummary SumWithin(TechId victimId, long fromMonoTick, long toMonoTick)
        {
            if (toMonoTick < fromMonoTick) return DamageSummary.Empty;
            Ring r;
            if (!_byTech.TryGetValue(victimId, out r)) return DamageSummary.Empty;

            var tmp = new DamageHint[CapacityPerTech];
            int n = r.Snapshot(tmp);
            if (n <= 0) return DamageSummary.Empty;

            float total = 0f;
            // Type-mode histogram on the fly. 8 buckets cover the ManDamage.DamageType enum's
            // realistic range; anything wider just falls into the last bucket without losing
            // the dominant winner (collisions are accepted — same engine semantics across
            // enum widening).
            int[] typeCounts = new int[256];
            byte dominantType = 0;
            int dominantCount = 0;
            // Distinct-attacker collector. Stack-bounded by CapacityPerTech.
            TechId[] seenAttackers = new TechId[CapacityPerTech];
            int seenAttackerCount = 0;

            for (int i = 0; i < n; i++)
            {
                long tick = tmp[i].TickMono;
                if (tick < fromMonoTick || tick > toMonoTick) continue;

                total += tmp[i].Magnitude;

                byte t = tmp[i].DamageType;
                int c = ++typeCounts[t];
                if (c > dominantCount)
                {
                    dominantCount = c;
                    dominantType = t;
                }

                if (tmp[i].HasAttacker)
                {
                    TechId a = tmp[i].AttackerIfKnown;
                    bool dup = false;
                    for (int j = 0; j < seenAttackerCount; j++)
                    {
                        if (seenAttackers[j].Equals(a)) { dup = true; break; }
                    }
                    if (!dup && seenAttackerCount < seenAttackers.Length)
                        seenAttackers[seenAttackerCount++] = a;
                }
            }

            if (dominantCount == 0) return DamageSummary.Empty;
            return new DamageSummary(total, dominantType, seenAttackerCount);
        }

        public void Forget(TechId victim) { Ring _; _byTech.TryRemove(victim, out _); }

        public void Clear() => _byTech.Clear();

        public int VictimCount => _byTech.Count;
    }
}
