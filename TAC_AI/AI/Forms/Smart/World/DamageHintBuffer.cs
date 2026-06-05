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

        public DamageHint(float magnitude, long tickMono, TechId attackerIfKnown, bool hasAttacker,
            UnityEngine.Vector3 impactPositionWorld, UnityEngine.Vector3 impactDirectionWorld)
        {
            Magnitude = magnitude;
            TickMono = tickMono;
            AttackerIfKnown = attackerIfKnown;
            HasAttacker = hasAttacker;
            ImpactPositionWorld = impactPositionWorld;
            ImpactDirectionWorld = impactDirectionWorld;
        }
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
            inst.Push(ev.Id, new DamageHint(
                magnitude: ev.Damage.Damage,
                tickMono: MonoClock.Now(),
                attackerIfKnown: ev.Damage.AttackerIfKnown,
                hasAttacker: ev.Damage.HasAttacker,
                impactPositionWorld: ev.Damage.ImpactPositionWorld,
                impactDirectionWorld: ev.Damage.ImpactDirectionWorld));
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

        public void Forget(TechId victim) { Ring _; _byTech.TryRemove(victim, out _); }

        public void Clear() => _byTech.Clear();

        public int VictimCount => _byTech.Count;
    }
}
