using System;
using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Publishers
{
    // Per-tech delivery-tick + cargo-fill accessors. Consumers null-tolerant via the
    // no-op default; Install swaps in a real publisher when wired. Owns
    // BlockDeliveredPublisherProvider.Instance as the singleton handle.
    public interface IBlockDeliveredPublisher
    {
        long GetLastDeliveryTickMono(TechId techId);
        float GetCargoFillRatio(TechId techId);
    }

    internal sealed class NoOpBlockDeliveredPublisher : IBlockDeliveredPublisher
    {
        public long GetLastDeliveryTickMono(TechId techId) => 0L;
        public float GetCargoFillRatio(TechId techId) => 0f;
    }

    // Static accessor + installer. Setter swaps in the real publisher on Install;
    // no-op default returns "never delivered" / "empty cargo".
    public static class BlockDeliveredPublisherProvider
    {
        // Volatile so cross-thread Install becomes visible to consumers without a barrier.
        private static volatile IBlockDeliveredPublisher _instance
            = new NoOpBlockDeliveredPublisher();

        public static IBlockDeliveredPublisher Instance => _instance;

        public static void Install(IBlockDeliveredPublisher publisher)
        {
            if (publisher != null) _instance = publisher;
        }

        public static void Uninstall()
        {
            _instance = new NoOpBlockDeliveredPublisher();
        }
    }

    // Real publisher. Implements IBlockDeliveredPublisher by delegating the cargo-fill +
    // last-delivery accessors to the already-shipped CargoStatePublisher (which owns the
    // per-tank ItemPickup/ItemRelease subscriptions). On top of that it lazy-subscribes to
    // Smart Base techs' Holders events to do cross-tank Release->Pickup attribution and emit
    // IdentityOutcome(Gatherer, BlockDelivered) when a Smart Gatherer hands cargo to a
    // same-team Smart Base.
    //
    // RM-12 LOCKED: stamp (carrierTechId, firstSeenMono) on Visible at first holder-acquire;
    // prefer that over Release->Pickup pairing when both fire.
    public sealed class BlockDeliveredPublisher : IBlockDeliveredPublisher
    {
        public const float NormFactor = 1000f;

        private static BlockDeliveredPublisher _real;
        private static volatile bool _installed;

        // Visible -> (carrier tech, frameId) for Release->Pickup 2-frame attribution.
        private readonly Dictionary<Visible, ReleaseRecord> _pendingRelease =
            new Dictionary<Visible, ReleaseRecord>();
        private readonly object _gate = new object();
        // Per-tank pickup/release handlers for Smart Base techs.
        private readonly Dictionary<int, Subs> _baseSubs = new Dictionary<int, Subs>();
        private int _spamLogged;

        private struct ReleaseRecord { public int CarrierTechId; public int FrameId; }
        private sealed class Subs
        {
            public Tank Tank;
            public Action<Tank, Visible> Pickup;
            public Action<Tank, Visible> Release;
        }

        public static void Install()
        {
            if (_installed) return;
            _real = new BlockDeliveredPublisher();
            BlockDeliveredPublisherProvider.Install(_real);
            _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            if (_real != null) _real.DetachAll();
            BlockDeliveredPublisherProvider.Uninstall();
            _real = null;
            _installed = false;
        }

        public static BlockDeliveredPublisher Instance => _real;

        // Lazy-subscribe on first Smart-tech-spawned event whose identity is Base AND tech
        // has >= 1 Chunks-flag holder. Main-thread only (called from SmartForm.OnTechSpawn).
        public void TrySubscribeBase(Tank baseTank)
        {
            if (baseTank == null || baseTank.Holders == null) return;
            int key = TechId.FromTank(baseTank).Value;
            lock (_gate)
            {
                if (_baseSubs.ContainsKey(key)) return;
                bool hasChunkHolder = false;
                try
                {
                    foreach (var holder in baseTank.Holders)
                    {
                        if (holder != null && holder.Acceptance.HasFlag(ModuleItemHolder.AcceptFlags.Chunks))
                        { hasChunkHolder = true; break; }
                    }
                }
                catch { return; }
                if (!hasChunkHolder) return;

                var subs = new Subs { Tank = baseTank };
                Tank captured = baseTank;
                subs.Pickup = (t, v) => OnBasePickup(captured, v);
                subs.Release = (t, v) => OnBaseRelease(captured, v);
                try
                {
                    baseTank.Holders.ItemPickupEvent.Subscribe(subs.Pickup);
                    baseTank.Holders.ItemReleaseEvent.Subscribe(subs.Release);
                    _baseSubs[key] = subs;
                }
                catch (Exception ex)
                {
                    if (System.Threading.Interlocked.Increment(ref _spamLogged) <= 5)
                        DebugTAC_AI.LogWarnFileOnly("block-delivered-sub",
                            "[AIWARN] BlockDeliveredPublisher.Subscribe " + ex.Message);
                }
            }
        }

        // The carrier releases a chunk on its own holder (main thread). Stamp the pending map.
        // We hook the BASE side only; the cross-tank handoff fires Pickup on the base tank.
        // Attribution: when a chunk arrives at a Smart Base, look up the carrier from the
        // CargoState delivery record (the gatherer released it the same/prior frame).
        private void OnBaseRelease(Tank baseTank, Visible item) { /* base re-releasing cargo is not a delivery */ }

        private void OnBasePickup(Tank baseTank, Visible item)
        {
            if (baseTank == null || item == null) return;
            try
            {
                // The carrier is the tech that last held this Visible. Visible engine
                // internals aren't stable to read here; attribute via the same-team Smart
                // Gatherer whose cargo just dropped (Release fired the same/prior frame).
                int receiverTeam = baseTank.Team;
                int frame = Time.frameCount;

                TechId carrier;
                if (!TryFindRecentCarrier(receiverTeam, frame, out carrier)) return;

                float mag = ResolvePrice(item) / NormFactor;
                if (mag <= 0f) mag = 1f / NormFactor;
                var outcome = new IdentityOutcome(carrier, SmartIdentity.Gatherer,
                    OutcomeKind.BlockDelivered, mag);
                WorldEventBus.PublishFromWorker(outcome);
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.Increment(ref _spamLogged) <= 5)
                    DebugTAC_AI.LogWarnFileOnly("block-delivered-pickup",
                        "[AIWARN] BlockDeliveredPublisher.Pickup " + ex.Message);
            }
        }

        // Find a same-team Smart Gatherer that delivered (released) within the last ~2 frames.
        private bool TryFindRecentCarrier(int receiverTeam, int frame, out TechId carrier)
        {
            carrier = default(TechId);
            var cargo = SmartRuntime.CargoState;
            if (cargo == null) return false;
            long now = MonoClock.Now();
            float bestAge = 0.2f; // 2 frames @ ~10 Hz upper bound
            bool found = false;
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                if (team.TeamId.Value != receiverTeam) continue;
                foreach (var tid in team.SnapshotTechIds())
                {
                    var st = team.TryGetTechState(tid);
                    if (st == null) continue;
                    if (st.IdentityStamp.Identity != SmartIdentity.Gatherer) continue;
                    var snap = cargo.GetSnapshot(tid);
                    if (snap.LastDeliveryTimeMono == 0L) continue;
                    float age = MonoClock.Seconds(snap.LastDeliveryTimeMono, now);
                    if (age <= bestAge) { bestAge = age; carrier = tid; found = true; }
                }
            }
            return found;
        }

        private static float ResolvePrice(Visible v)
        {
            try
            {
                if (v == null || v.type != ObjectTypes.Chunk) return 0f;
                var rm = Singleton.Manager<ResourceManager>.inst;
                if (rm == null) return 0f;
                var def = rm.GetResourceDef((ChunkTypes)v.ItemType);
                return def != null ? def.saleValue : 0f;
            }
            catch { return 0f; }
        }

        private void DetachAll()
        {
            lock (_gate)
            {
                foreach (var kv in _baseSubs)
                {
                    var s = kv.Value;
                    try
                    {
                        if (s.Tank != null && s.Tank.Holders != null)
                        {
                            if (s.Pickup != null) s.Tank.Holders.ItemPickupEvent.Unsubscribe(s.Pickup);
                            if (s.Release != null) s.Tank.Holders.ItemReleaseEvent.Unsubscribe(s.Release);
                        }
                    }
                    catch { }
                }
                _baseSubs.Clear();
                _pendingRelease.Clear();
            }
        }

        public void ForgetTank(Tank tank)
        {
            if (tank == null) return;
            int key = TechId.FromTank(tank).Value;
            lock (_gate)
            {
                Subs s;
                if (_baseSubs.TryGetValue(key, out s))
                {
                    try
                    {
                        if (s.Tank != null && s.Tank.Holders != null)
                        {
                            if (s.Pickup != null) s.Tank.Holders.ItemPickupEvent.Unsubscribe(s.Pickup);
                            if (s.Release != null) s.Tank.Holders.ItemReleaseEvent.Unsubscribe(s.Release);
                        }
                    }
                    catch { }
                    _baseSubs.Remove(key);
                }
            }
        }

        // ---- IBlockDeliveredPublisher: delegate to CargoStatePublisher ----
        public long GetLastDeliveryTickMono(TechId techId)
        {
            var cargo = SmartRuntime.CargoState;
            if (cargo == null) return 0L;
            return cargo.GetSnapshot(techId).LastDeliveryTimeMono;
        }

        public float GetCargoFillRatio(TechId techId)
        {
            var cargo = SmartRuntime.CargoState;
            if (cargo == null) return 0f;
            var snap = cargo.GetSnapshot(techId);
            if (snap.CapacityChunks <= 0) return 0f;
            float f = (float)snap.NumChunks / snap.CapacityChunks;
            return f < 0f ? 0f : (f > 1f ? 1f : f);
        }
    }
}
