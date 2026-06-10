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

        // BUG-052: shadow Tank map so Clear() / form-swap can iterate per-tech and call
        // the matching Unsubscribe with the live TechWeapon. Without it we drop closures
        // but leave them on each live TechWeapon.WeaponsFiredEvent multicast list.
        private readonly ConcurrentDictionary<TechId, TechWeapon> _wiredWeapons =
            new ConcurrentDictionary<TechId, TechWeapon>();

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
            _wiredWeapons[attackerId] = weapons;
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
            _wiredWeapons.TryRemove(attackerId, out _);
        }

        public void Forget(TechId attackerId)
        {
            // BUG-052: route through the shadow map when present so the engine's delegate
            // list is properly cleaned. Falls back silently when the weapon ref is gone.
            TechWeapon weapons;
            if (_wiredWeapons.TryRemove(attackerId, out weapons) && weapons != null)
            {
                Action prior;
                if (_subs.TryRemove(attackerId, out prior))
                {
                    try { weapons.WeaponsFiredEvent.Unsubscribe(prior); }
                    catch { /* engine may have purged the multicast list */ }
                }
            }
            else
            {
                Action discardedAction;
                _subs.TryRemove(attackerId, out discardedAction);
            }
            Ring discardedRing;
            _byTech.TryRemove(attackerId, out discardedRing);
        }

        /// <summary>
        /// BUG-052: matched-pair teardown — iterate every live (TechId, TechWeapon)
        /// wiring and call Unsubscribe with the IDENTICAL Action so the engine's
        /// multicast list releases the closure. Without this, form-swap Smart→Vanilla
        /// leaves every per-tech subscription pinned to a dead WeaponFireBuffer.
        /// MAIN-THREAD ONLY — touches Unity components.
        /// </summary>
        public void DetachAll()
        {
            var ids = new List<TechId>(_wiredWeapons.Keys);
            for (int i = 0; i < ids.Count; i++)
            {
                TechWeapon w;
                if (!_wiredWeapons.TryRemove(ids[i], out w)) continue;
                Action prior;
                if (_subs.TryRemove(ids[i], out prior) && w != null)
                {
                    try { w.WeaponsFiredEvent.Unsubscribe(prior); }
                    catch { /* engine may have already purged */ }
                }
            }
        }

        public void Clear()
        {
            // BUG-052: detach live subscriptions before dropping the dicts so per-tank
            // TechWeapon.WeaponsFiredEvent doesn't walk a dead closure on the next shot.
            DetachAll();
            _byTech.Clear();
            _subs.Clear();
            _wiredWeapons.Clear();
        }

        public int AttackerCount => _byTech.Count;
    }
}
