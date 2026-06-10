using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Publishers
{
    /// <summary>
    /// §9.3 OrbitNoFireGuard precondition. Per-tech rolling fire-edge counter sourced
    /// from TankControl.manualAimFireEvent + targetedAimFireEvent. Mirrors the
    /// WeaponFireBuffer shape (per-TechId Ring of long monoTicks + CountWithin).
    /// OrbitNoFireGuard consumes CountWithin.
    ///
    /// Lifecycle: Wire on per-tank attach, Unwire on detach. Singleton instance lives
    /// at SmartRuntime.Projectiles when SmartEventBridge installs it; until then the
    /// publisher is constructible standalone for tests.
    /// </summary>
    public sealed class ProjectileFiredPublisher : ITechSidecar
    {
        public const int CapacityPerTech = 32;

        public string Name => "ProjectileFiredPublisher";
        public IReadOnlyCollection<TechId> SnapshotKeys()
            => (IReadOnlyCollection<TechId>)_byTech.Keys;

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

        // Per-(TechId) bound delegate so subscribe/unsubscribe target the same closure.
        // manualAimFireEvent is Event<int, bool>; targetedAimFireEvent is Event<Vector3, float>.
        private readonly ConcurrentDictionary<TechId, Action<int, bool>> _manualSubs =
            new ConcurrentDictionary<TechId, Action<int, bool>>();
        private readonly ConcurrentDictionary<TechId, Action<Vector3, float>> _targetedSubs =
            new ConcurrentDictionary<TechId, Action<Vector3, float>>();
        private readonly ConcurrentDictionary<TechId, TankControl> _wiredControls =
            new ConcurrentDictionary<TechId, TankControl>();

        /// <summary>
        /// Inject a fire-edge for tests / non-engine callers.
        /// </summary>
        public void RecordFireEvent(TechId attackerId, long monoTick)
        {
            var ring = _byTech.GetOrAdd(attackerId, _ => new Ring());
            ring.Push(monoTick);
        }

        public int CountWithin(TechId attackerId, long fromMonoTick, long toMonoTick)
        {
            Ring r;
            if (!_byTech.TryGetValue(attackerId, out r)) return 0;
            return r.CountWithin(fromMonoTick, toMonoTick);
        }

        /// <summary>
        /// Subscribe per-tank to TankControl.manualAimFireEvent + targetedAimFireEvent.
        /// Idempotent: re-wiring replaces the prior subscription. MAIN-THREAD ONLY
        /// (touches Unity component).
        /// </summary>
        public void Wire(TechId attackerId, TankControl control)
        {
            if (control == null) return;
            UnwireInternal(attackerId, control);
            TechId capturedId = attackerId;
            Action<int, bool> manualHandler = (mode, fire) =>
            {
                if (!fire) return;
                RecordFireEvent(capturedId, MonoClock.Now());
            };
            Action<Vector3, float> targetedHandler = (pos, radius) =>
            {
                RecordFireEvent(capturedId, MonoClock.Now());
            };
            _manualSubs[attackerId] = manualHandler;
            _targetedSubs[attackerId] = targetedHandler;
            _wiredControls[attackerId] = control;
            try
            {
                control.manualAimFireEvent.Subscribe(manualHandler);
                control.targetedAimFireEvent.Subscribe(targetedHandler);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-projectile-wire",
                    "[AIWARN] ProjectileFiredPublisher.Wire " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Unsubscribe per-tank handlers. MAIN-THREAD ONLY.</summary>
        public void Unwire(TechId attackerId, TankControl control)
        {
            UnwireInternal(attackerId, control);
        }

        private void UnwireInternal(TechId attackerId, TankControl control)
        {
            Action<int, bool> mh;
            if (_manualSubs.TryRemove(attackerId, out mh) && control != null)
            {
                try { control.manualAimFireEvent.Unsubscribe(mh); }
                catch { /* engine may have purged */ }
            }
            Action<Vector3, float> th;
            if (_targetedSubs.TryRemove(attackerId, out th) && control != null)
            {
                try { control.targetedAimFireEvent.Unsubscribe(th); }
                catch { }
            }
            _wiredControls.TryRemove(attackerId, out _);
        }

        public void Forget(TechId attackerId)
        {
            TankControl ctl;
            if (_wiredControls.TryRemove(attackerId, out ctl) && ctl != null)
            {
                Action<int, bool> mh;
                if (_manualSubs.TryRemove(attackerId, out mh))
                {
                    try { ctl.manualAimFireEvent.Unsubscribe(mh); }
                    catch { }
                }
                Action<Vector3, float> th;
                if (_targetedSubs.TryRemove(attackerId, out th))
                {
                    try { ctl.targetedAimFireEvent.Unsubscribe(th); }
                    catch { }
                }
            }
            else
            {
                Action<int, bool> discardM;
                _manualSubs.TryRemove(attackerId, out discardM);
                Action<Vector3, float> discardT;
                _targetedSubs.TryRemove(attackerId, out discardT);
            }
            Ring discardedRing;
            _byTech.TryRemove(attackerId, out discardedRing);
        }

        /// <summary>
        /// Matched-pair teardown — iterate every live (TechId, TankControl) wiring and
        /// call Unsubscribe with the IDENTICAL Action so the engine's multicast list
        /// releases the closure. MAIN-THREAD ONLY.
        /// </summary>
        public void DetachAll()
        {
            var ids = new List<TechId>(_wiredControls.Keys);
            for (int i = 0; i < ids.Count; i++)
            {
                TankControl ctl;
                if (!_wiredControls.TryRemove(ids[i], out ctl)) continue;
                Action<int, bool> mh;
                if (_manualSubs.TryRemove(ids[i], out mh) && ctl != null)
                {
                    try { ctl.manualAimFireEvent.Unsubscribe(mh); }
                    catch { }
                }
                Action<Vector3, float> th;
                if (_targetedSubs.TryRemove(ids[i], out th) && ctl != null)
                {
                    try { ctl.targetedAimFireEvent.Unsubscribe(th); }
                    catch { }
                }
            }
        }

        public void Clear()
        {
            DetachAll();
            _byTech.Clear();
            _manualSubs.Clear();
            _targetedSubs.Clear();
            _wiredControls.Clear();
        }

        public int AttackerCount => _byTech.Count;
    }
}
