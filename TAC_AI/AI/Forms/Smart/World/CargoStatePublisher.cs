using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// §8.6 — event-driven per-tech cargo cache. Subscribes per-tank to
    /// <c>TechHolders.ItemPickupEvent</c> + <c>ItemReleaseEvent</c>
    /// (decompile TechHolders.cs:163,165). Both events only fire on a CROSS-tank
    /// transition (Visible.cs:910,928 gate <c>tank != tank2</c>) so re-stack within
    /// the same tank is silent — capacity totals are still accurate because the
    /// in-tank Holder still owns those contents.
    ///
    /// Capacity recompute: lightweight refresh on first event after spawn, plus an
    /// explicit <see cref="OnTankAttach"/> hook that the SmartForm lifecycle can
    /// call after Tank.AttachEvent / DetachEvent settle (block adds/removes change
    /// the holder list). The refresh walks <c>Tank.Holders</c> once and sums
    /// <c>ModuleItemHolder.GetTotalCapacityForLimiter()</c> per holder. NOT called
    /// per event — only on attach/detach edges (rare) or first-touch.
    ///
    /// Per-chunk counters: int[] indexed by (int)ChunkTypes. Smaller than a Dict
    /// and cheaper than re-hashing on every event. Sized once at class init from
    /// the highest known ChunkTypes value (68 today; plan allows slack).
    ///
    /// Threading: events fire main-thread (engine MonoBehaviour callback site at
    /// Visible.cs:912,930). <see cref="GetSnapshot"/> is callable from any thread
    /// (returns struct by value). State held in <see cref="ConcurrentDictionary{TKey, TValue}"/>;
    /// per-entry mutation happens under the entry's own lock.
    ///
    /// Wire/Unwire pattern mirrors <see cref="DamageHintBuffer"/> + per-tank
    /// AttachPerTank/DetachPerTank mirrors <see cref="WeaponFireBuffer"/>:
    ///   1. Static <see cref="Wire"/> installs the singleton instance.
    ///   2. Per-tank <see cref="AttachPerTank"/> subscribes both events on the
    ///      target Tank's TechHolders; called from SmartForm.OnTechSpawn.
    ///   3. <see cref="DetachPerTank"/> on OnTechRecycle; <see cref="Forget"/>
    ///      drops the per-tech state (idempotent).
    /// </summary>
    public sealed class CargoStatePublisher : ITechSidecar
    {
        // ChunkTypes is open-ended; size the counter array from the highest enum
        // ordinal at static init so future engine additions don't OOB.
        private static readonly int s_chunkTypeArraySize = ComputeChunkTypeArraySize();

        private static int ComputeChunkTypeArraySize()
        {
            int max = 0;
            try
            {
                Array values = Enum.GetValues(typeof(ChunkTypes));
                for (int i = 0; i < values.Length; i++)
                {
                    int v = (int)values.GetValue(i);
                    if (v > max) max = v;
                }
            }
            catch { max = 70; }
            return max + 1;
        }

        // Per-tech mutable state. One instance per registered Tank. Single-writer
        // (main-thread event callback); GetSnapshot copies under the entry lock.
        private sealed class State
        {
            internal readonly object _gate = new object();
            internal int _numChunks;
            internal int _capacityChunks;
            internal float _cargoSaleValue;
            internal long _lastPickupTimeMono;
            internal long _lastDeliveryTimeMono;
            internal readonly int[] _perChunkType = new int[s_chunkTypeArraySize];
            internal bool _capacityKnown;  // false until first refresh; lazy on first event
        }

        private readonly ConcurrentDictionary<TechId, State> _byTech =
            new ConcurrentDictionary<TechId, State>();

        // Subscription bookkeeping per Tank — keep the exact delegate refs so
        // Unsubscribe gets identity-match (Event.cs DelegateIterator compares by
        // reference, same constraint as WeaponFireBuffer).
        private sealed class TankSubs
        {
            internal Action<Tank, Visible> Pickup;
            internal Action<Tank, Visible> Release;
        }

        private readonly ConcurrentDictionary<TechId, TankSubs> _subs =
            new ConcurrentDictionary<TechId, TankSubs>();

        // Tank shadow map for the TechId-keyed Detach path (mirrors
        // SmartEventBridge._damageHandlersByTechId; the engine may purge the
        // Tank ref before our DetachPerTank runs).
        private readonly ConcurrentDictionary<TechId, Tank> _tankByTechId =
            new ConcurrentDictionary<TechId, Tank>();

        // Static field holds the wired singleton so the Subscribe delegate
        // closures resolve through it (matches DamageHintBuffer Wire pattern;
        // GC won't collect the delegates while _wiredInstance lives).
        private static CargoStatePublisher _wiredInstance;

        public string Name => "CargoStatePublisher";
        public IReadOnlyCollection<TechId> SnapshotKeys()
            => (IReadOnlyCollection<TechId>)_byTech.Keys;

        /// <summary>
        /// Install the singleton instance. Per-tank subscriptions still go through
        /// <see cref="AttachPerTank"/>. Re-wiring detaches every prior subscription
        /// so we don't double-fire after a Shutdown/Init cycle.
        /// </summary>
        public static void Wire(CargoStatePublisher publisher)
        {
            if (_wiredInstance != null && _wiredInstance != publisher)
                _wiredInstance.DetachAll();
            _wiredInstance = publisher;
        }

        /// <summary>Drop the singleton ref and detach every per-tank subscription.</summary>
        public static void Unwire()
        {
            if (_wiredInstance == null) return;
            _wiredInstance.DetachAll();
            _wiredInstance = null;
        }

        /// <summary>
        /// Subscribe both holder events for <paramref name="tank"/>. MAIN-THREAD
        /// ONLY (touches the Tank component). Idempotent — re-attach replaces.
        /// </summary>
        public void AttachPerTank(Tank tank)
        {
            if (tank == null || tank.Holders == null) return;
            TechId id = TechId.FromTank(tank);
            DetachPerTankInternal(id);  // drop any prior wiring

            var captured = tank;
            var subs = new TankSubs
            {
                Pickup = (t, v) => OnItemPickup(captured, v),
                Release = (t, v) => OnItemRelease(captured, v),
            };
            try
            {
                tank.Holders.ItemPickupEvent.Subscribe(subs.Pickup);
                tank.Holders.ItemReleaseEvent.Subscribe(subs.Release);
                _subs[id] = subs;
                _tankByTechId[id] = tank;
                // Eager capacity stamp — the daemon may read before any item
                // moves, and an unknown-capacity tech reads as fill=0/cap=0.
                RefreshCapacity(id, tank);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("CargoStatePublisher.AttachPerTank: " + ex.Message);
                _subs.TryRemove(id, out _);
                _tankByTechId.TryRemove(id, out _);
            }
        }

        /// <summary>Unsubscribe both events for <paramref name="tank"/>. MAIN-THREAD ONLY.</summary>
        public void DetachPerTank(Tank tank)
        {
            if (tank == null) return;
            DetachPerTankInternal(TechId.FromTank(tank));
        }

        /// <summary>
        /// TechId-keyed detach for the orphan-sweep path (engine purged the Tank
        /// before lifecycle teardown finished). Mirrors
        /// <c>SmartEventBridge.DetachPerTank(TechId)</c>.
        /// </summary>
        public void DetachPerTank(TechId id) => DetachPerTankInternal(id);

        private void DetachPerTankInternal(TechId id)
        {
            TankSubs prior;
            if (!_subs.TryRemove(id, out prior)) return;
            _tankByTechId.TryRemove(id, out var tank);
            if (tank == null || tank.Holders == null) return;
            try
            {
                if (prior.Pickup != null) tank.Holders.ItemPickupEvent.Unsubscribe(prior.Pickup);
                if (prior.Release != null) tank.Holders.ItemReleaseEvent.Unsubscribe(prior.Release);
            }
            catch { /* tank may have been partially recycled */ }
        }

        private void DetachAll()
        {
            // Snapshot keys first; DetachPerTankInternal mutates _subs.
            var ids = new List<TechId>(_subs.Keys);
            for (int i = 0; i < ids.Count; i++) DetachPerTankInternal(ids[i]);
        }

        /// <summary>
        /// Hook for SmartForm to call after Tank.AttachEvent / DetachEvent settle.
        /// Recomputes capacity by walking holders. MAIN-THREAD ONLY.
        /// </summary>
        public void OnTankAttach(Tank tank)
        {
            if (tank == null) return;
            RefreshCapacity(TechId.FromTank(tank), tank);
        }

        private void RefreshCapacity(TechId id, Tank tank)
        {
            if (tank == null || tank.Holders == null) return;
            int cap = 0;
            try
            {
                foreach (var holder in tank.Holders)
                {
                    if (holder == null) continue;
                    cap += holder.GetTotalCapacityForLimiter();
                }
            }
            catch { /* holder iteration race during teardown; keep prior cap */ return; }
            var st = _byTech.GetOrAdd(id, _ => new State());
            lock (st._gate)
            {
                st._capacityChunks = cap;
                st._capacityKnown = true;
            }
        }

        // -------- event callbacks (main-thread) --------

        private void OnItemPickup(Tank tank, Visible item)
        {
            if (tank == null || item == null) return;
            TechId id = TechId.FromTank(tank);
            var st = _byTech.GetOrAdd(id, _ => new State());
            long now = MonoClock.Now();
            int chunkIdx;
            float sale;
            ResolveChunk(item, out chunkIdx, out sale);
            lock (st._gate)
            {
                if (!st._capacityKnown) RefreshCapacityNoLock(st, tank);
                st._numChunks++;
                st._cargoSaleValue += sale;
                st._lastPickupTimeMono = now;
                if (chunkIdx >= 0 && chunkIdx < st._perChunkType.Length)
                    st._perChunkType[chunkIdx]++;
            }
        }

        private void OnItemRelease(Tank tank, Visible item)
        {
            if (tank == null || item == null) return;
            TechId id = TechId.FromTank(tank);
            var st = _byTech.GetOrAdd(id, _ => new State());
            long now = MonoClock.Now();
            int chunkIdx;
            float sale;
            ResolveChunk(item, out chunkIdx, out sale);
            lock (st._gate)
            {
                if (!st._capacityKnown) RefreshCapacityNoLock(st, tank);
                if (st._numChunks > 0) st._numChunks--;
                st._cargoSaleValue -= sale;
                if (st._cargoSaleValue < 0f) st._cargoSaleValue = 0f;
                st._lastDeliveryTimeMono = now;
                if (chunkIdx >= 0 && chunkIdx < st._perChunkType.Length && st._perChunkType[chunkIdx] > 0)
                    st._perChunkType[chunkIdx]--;
            }
        }

        // Inside-lock capacity refresh used on first event. Tolerates holder
        // iteration mid-spawn (Tank.Holders becoming valid late).
        private void RefreshCapacityNoLock(State st, Tank tank)
        {
            if (tank == null || tank.Holders == null) return;
            int cap = 0;
            try
            {
                foreach (var holder in tank.Holders)
                {
                    if (holder == null) continue;
                    cap += holder.GetTotalCapacityForLimiter();
                }
            }
            catch { return; }
            st._capacityChunks = cap;
            st._capacityKnown = true;
        }

        private static void ResolveChunk(Visible v, out int chunkIdx, out float saleValue)
        {
            chunkIdx = -1;
            saleValue = 0f;
            if (v == null) return;
            try
            {
                // Visible.ItemType is the int ItemTypeInfo payload; for Chunk it's
                // the ChunkTypes ordinal. Reject non-chunk visibles (blocks held by
                // an Anchor module aren't real cargo, even though the event still
                // fires for them — the Tank gate at Visible.cs:910 lets them through).
                if (v.type != ObjectTypes.Chunk) return;
                int raw = v.ItemType;
                chunkIdx = raw;
                var rm = Singleton.Manager<ResourceManager>.inst;
                if (rm != null)
                {
                    var def = rm.GetResourceDef((ChunkTypes)raw);
                    if (def != null) saleValue = def.saleValue;
                }
            }
            catch { /* manager not ready / item already gone */ }
        }

        // -------- read side (any thread) --------

        /// <summary>
        /// Non-allocating snapshot of <paramref name="id"/>'s cargo state. Returns
        /// <see cref="CargoSnapshot.Empty"/> when the tech isn't registered. The
        /// returned struct is a value copy — safe to consume off-thread.
        /// </summary>
        public CargoSnapshot GetSnapshot(TechId id)
        {
            State st;
            if (!_byTech.TryGetValue(id, out st)) return CargoSnapshot.Empty;
            lock (st._gate)
            {
                return new CargoSnapshot(
                    numChunks: st._numChunks,
                    capacityChunks: st._capacityChunks,
                    cargoSaleValue: st._cargoSaleValue,
                    lastPickupTimeMono: st._lastPickupTimeMono,
                    lastDeliveryTimeMono: st._lastDeliveryTimeMono,
                    capacityKnown: st._capacityKnown);
            }
        }

        /// <summary>Copy the per-chunk-type histogram. Caller-allocated dst.</summary>
        public int CopyPerChunkType(TechId id, int[] dst)
        {
            if (dst == null) return 0;
            State st;
            if (!_byTech.TryGetValue(id, out st)) return 0;
            lock (st._gate)
            {
                int n = dst.Length < st._perChunkType.Length ? dst.Length : st._perChunkType.Length;
                for (int i = 0; i < n; i++) dst[i] = st._perChunkType[i];
                return n;
            }
        }

        public void Forget(TechId id)
        {
            DetachPerTankInternal(id);
            State _; _byTech.TryRemove(id, out _);
        }

        public void Clear()
        {
            DetachAll();
            _byTech.Clear();
            _tankByTechId.Clear();
        }

        public int TechCount => _byTech.Count;
    }

    /// <summary>
    /// Non-allocating read view returned by <see cref="CargoStatePublisher.GetSnapshot"/>.
    /// Fields match the per-tech state shape from FEATURE-EXPANSION-PLAN.md §8.6.
    /// </summary>
    public readonly struct CargoSnapshot
    {
        public readonly int NumChunks;
        public readonly int CapacityChunks;
        public readonly float CargoSaleValue;
        public readonly long LastPickupTimeMono;
        public readonly long LastDeliveryTimeMono;
        public readonly bool CapacityKnown;

        public CargoSnapshot(int numChunks, int capacityChunks, float cargoSaleValue,
            long lastPickupTimeMono, long lastDeliveryTimeMono, bool capacityKnown)
        {
            NumChunks = numChunks;
            CapacityChunks = capacityChunks;
            CargoSaleValue = cargoSaleValue;
            LastPickupTimeMono = lastPickupTimeMono;
            LastDeliveryTimeMono = lastDeliveryTimeMono;
            CapacityKnown = capacityKnown;
        }

        /// <summary>Fill fraction in [0,1]; 0 when capacity unknown.</summary>
        public float Fill => CapacityChunks > 0 ? (float)NumChunks / CapacityChunks : 0f;

        public static readonly CargoSnapshot Empty = new CargoSnapshot(0, 0, 0f, 0L, 0L, false);
    }
}
