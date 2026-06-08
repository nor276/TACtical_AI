using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// §8.7 — per-tech ring of recent fire-tick timestamps. Subscribes per-tank to
    /// <c>TechWeapon.WeaponsFiredEvent</c> (decompile TechWeapon.cs:145), which is
    /// <c>EventNoParams</c> (EventNoParams.cs:5) — NO shot-count payload, so each
    /// event counts as exactly one fire-tick regardless of how many barrels fired
    /// in that frame. Per plan §3.5/§8.7 round-1 F-12.
    ///
    /// Stores only <c>tickMono</c> (long). 16-deep drop-oldest ring per attacker.
    /// Ring layout + lock discipline mirror DamageHintBuffer.Ring at
    /// DamageHintBuffer.cs:67-93 verbatim — single-writer (main-thread event
    /// callback), any-thread reader.
    ///
    /// Lifecycle: <see cref="Wire(TechId, TechWeapon)"/> at per-tech construct,
    /// <see cref="Unwire(TechId, TechWeapon)"/> on detach. <see cref="ITechSidecar.Forget(TechId)"/>
    /// drops the per-tech ring (also unsubscribes if still wired).
    /// </summary>
    public sealed class WeaponFireBuffer : ITechSidecar
    {
        public const int CapacityPerTech = 16;

        private sealed class Ring
        {
            internal readonly long[] _slots = new long[CapacityPerTech];
            internal int _head;
            internal int _count;

            internal void Push(long tickMono)
            {
                lock (_slots)
                {
                    _slots[_head] = tickMono;
                    _head = (_head + 1) % CapacityPerTech;
                    if (_count < CapacityPerTech) _count++;
                }
            }

            internal int CountWithin(long fromMono, long toMono)
            {
                int hit = 0;
                lock (_slots)
                {
                    int n = _count;
                    for (int i = 0; i < n; i++)
                    {
                        long t = _slots[i];
                        if (t >= fromMono && t <= toMono) hit++;
                    }
                }
                return hit;
            }
        }

        private readonly ConcurrentDictionary<TechId, Ring> _byTech =
            new ConcurrentDictionary<TechId, Ring>();

        // Subscription bookkeeping: one delegate per (TechId, TechWeapon) wiring so
        // Unwire can hand the IDENTICAL Action reference back to Unsubscribe (the
        // engine's DelegateIterator compares by delegate identity).
        private readonly ConcurrentDictionary<TechId, Action> _subs =
            new ConcurrentDictionary<TechId, Action>();

        // L-028 ITechSidecar wiring.
        public string Name => "WeaponFireBuffer";
        public IReadOnlyCollection<TechId> SnapshotKeys()
            => (IReadOnlyCollection<TechId>)_byTech.Keys;

        /// <summary>
        /// Record one fire-tick for <paramref name="attackerId"/>. Invoked from the
        /// main-thread <c>WeaponsFiredEvent</c> trampoline; also callable directly for
        /// test injection.
        /// </summary>
        public void RecordFireEvent(TechId attackerId, long monoTick)
        {
            var ring = _byTech.GetOrAdd(attackerId, _ => new Ring());
            ring.Push(monoTick);
        }

        /// <summary>
        /// Count fire-ticks for <paramref name="attackerId"/> whose timestamp lies in
        /// [<paramref name="fromMonoTick"/>, <paramref name="toMonoTick"/>]. Inclusive
        /// on both ends. Returns 0 for unknown attackers.
        /// </summary>
        public int CountWithin(TechId attackerId, long fromMonoTick, long toMonoTick)
        {
            Ring r;
            if (!_byTech.TryGetValue(attackerId, out r)) return 0;
            return r.CountWithin(fromMonoTick, toMonoTick);
        }

        /// <summary>
        /// Subscribe this buffer to <paramref name="weapons"/>'s WeaponsFiredEvent for
        /// <paramref name="attackerId"/>. Idempotent — re-wiring the same id replaces
        /// the prior subscription (prevents double-count on Attach/Detach churn).
        /// MAIN-THREAD ONLY (touches Unity component).
        /// </summary>
        public void Wire(TechId attackerId, TechWeapon weapons)
        {
            if (weapons == null) return;
            UnwireInternal(attackerId, weapons);
            TechId capturedId = attackerId;
            Action handler = () => RecordFireEvent(capturedId, MonoClock.Now());
            _subs[attackerId] = handler;
            weapons.WeaponsFiredEvent.Subscribe(handler);
        }

        /// <summary>
        /// Unsubscribe the per-tech handler from <paramref name="weapons"/>. MAIN-THREAD
        /// ONLY. The per-tech ring is left in place so windowed reads stay valid until
        /// <see cref="Forget"/> drops it (matches DamageHintBuffer lifecycle).
        /// </summary>
        public void Unwire(TechId attackerId, TechWeapon weapons)
        {
            UnwireInternal(attackerId, weapons);
        }

        private void UnwireInternal(TechId attackerId, TechWeapon weapons)
        {
            Action prior;
            if (_subs.TryRemove(attackerId, out prior) && weapons != null)
                weapons.WeaponsFiredEvent.Unsubscribe(prior);
        }

        public void Forget(TechId attackerId)
        {
            Action prior;
            // Subscription drop without the TechWeapon ref — the engine clears
            // delegate lists on tech destroy; leaking the closure here is bounded by
            // the Forget call from TechLifecycleRegistry.
            _subs.TryRemove(attackerId, out prior);
            Ring _; _byTech.TryRemove(attackerId, out _);
        }

        public void Clear() { _byTech.Clear(); _subs.Clear(); }

        public int AttackerCount => _byTech.Count;
    }
}
