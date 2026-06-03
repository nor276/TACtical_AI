using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Per-tech SPSC observation intake slot. One main-thread writer (SmartForm.ObserveWorldTechsIfDue
    /// → Observer.Submit), one worker-thread reader (AetherFuser). Pre-allocated; zero
    /// allocation per observation. Per AETHER-DESIGN.md "In-sight model".
    ///
    /// Concurrency primitive: a seqlock on `_version`. Writer publishes odd (in-flight),
    /// even (stable). Reader retries on torn read up to 4 times; if all 4 attempts see a
    /// writer mid-update, reader returns false and the worker treats this tick as a coast
    /// tick for that tech (no observation consumed).
    ///
    /// Sequence-counter pattern is standard SPSC scalar publish for value-typed snapshots
    /// where the reader can tolerate occasional misses. On 64-bit CLR / 32-bit Mono with
    /// .NET 4.6.1, Volatile.Read/Write provide the required acquire/release semantics.
    /// </summary>
    public sealed class ObservationIntakeSlot
    {
        private int _version;       // even = stable, odd = writing
        private Vector3 _position;
        private Vector3 _velocity;
        private Vector3 _forward;
        private long _tickMono;

        /// <summary>Called from the main thread by Observer.Submit. Single writer.</summary>
        public void Write(Vector3 position, Vector3 velocity, Vector3 forward, long tickMono)
        {
            int v = Volatile.Read(ref _version);
            Volatile.Write(ref _version, v + 1);        // mark writing (odd)
            _position = position;
            _velocity = velocity;
            _forward  = forward;
            _tickMono = tickMono;
            Volatile.Write(ref _version, v + 2);        // mark stable (even)
        }

        /// <summary>Called from the worker thread by AetherFuser. Single reader.</summary>
        public bool TryRead(out Vector3 position, out Vector3 velocity, out Vector3 forward, out long tickMono)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int v1 = Volatile.Read(ref _version);
                if ((v1 & 1) != 0) continue;            // writer in progress; retry
                position = _position;
                velocity = _velocity;
                forward  = _forward;
                tickMono = _tickMono;
                int v2 = Volatile.Read(ref _version);
                if (v1 == v2) return true;              // stable read
            }
            position = Vector3.zero;
            velocity = Vector3.zero;
            forward  = Vector3.forward;
            tickMono = 0L;
            return false;
        }

        /// <summary>Lock-free peek at just the timestamp — used by AetherFuser to detect "fresh this tick".</summary>
        public long PeekTickMono() => Volatile.Read(ref _tickMono);
    }

    /// <summary>
    /// Per-tech intake registry. Maintains one ObservationIntakeSlot per tracked TechId.
    /// Slot allocated once at RegisterTech (via GetOrCreate); released at DeregisterTech.
    ///
    /// Backed by ConcurrentDictionary so lifecycle hooks from any thread (Register/Deregister
    /// today happens on main, but the contract is thread-safe).
    /// </summary>
    public sealed class ObservationIntake
    {
        private readonly ConcurrentDictionary<TechId, ObservationIntakeSlot> _slots
            = new ConcurrentDictionary<TechId, ObservationIntakeSlot>();

        public ObservationIntakeSlot GetOrCreate(TechId id)
        {
            return _slots.GetOrAdd(id, _ => new ObservationIntakeSlot());
        }

        public bool TryGet(TechId id, out ObservationIntakeSlot slot)
        {
            return _slots.TryGetValue(id, out slot);
        }

        public void Remove(TechId id)
        {
            ObservationIntakeSlot _;
            _slots.TryRemove(id, out _);
        }

        public void Clear()
        {
            _slots.Clear();
        }

        public int Count => _slots.Count;
    }
}
