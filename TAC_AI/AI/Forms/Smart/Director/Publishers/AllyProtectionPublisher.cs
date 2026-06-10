using System;
using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Director.Guards.Role;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Publishers
{
    // Graded-magnitude AllyProtected publisher. Per-1 Hz scanner driven by the Director
    // tick (NOT a separate daemon). Per Smart-driven AircraftSupport / RepairSupport /
    // Patrol tech maintains a 60 s window; at window close emits
    // IdentityOutcome(AllyProtected, magnitude = max(0, 1 - friendlyDamageInWindow/budget)).
    // Implements LostAllyGuard's IAllyProtectionPublisher (LastAllyLossMono).
    public sealed class AllyProtectionPublisher : IAllyProtectionPublisher
    {
        public const float WindowSec = 60f;
        public const float DegradedThreshold = 0.1f;

        private sealed class Window
        {
            public long WindowStartMono;
            public float SumTimeInRange;
            public float FriendlyDamageInWindow;
            public int PairedAllyTechId;
            public long LastTouchMono;
        }

        private static AllyProtectionPublisher _instance;
        private static volatile bool _installed;

        private readonly Dictionary<int, Window> _windows = new Dictionary<int, Window>();
        private readonly object _gate = new object();
        // Paired-ally death timestamps for the LostAlly grace gate.
        private readonly Dictionary<int, long> _allyLossMono = new Dictionary<int, long>();

        private static long _evictionsPerMinute;
        private static long _evictionWindowStart;
        private static long _evictionsThisWindow;

        private static readonly SmartIdentity[] _supportIdentities =
        {
            SmartIdentity.AircraftSupport, SmartIdentity.RepairSupport, SmartIdentity.Patrol,
        };

        public static void Install()
        {
            if (_installed) return;
            _instance = new AllyProtectionPublisher();
            LostAllyGuard.InstallPublisher(_instance);
            // Track paired-ally death via TechDespawned.
            WorldEventBus.Subscribe<TechDespawned>(_instance.OnTechDespawned);
            WorldEventBus.Subscribe<DamageObserved>(_instance.OnDamageObserved);
            _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            if (_instance != null)
            {
                WorldEventBus.Unsubscribe<TechDespawned>(_instance.OnTechDespawned);
                WorldEventBus.Unsubscribe<DamageObserved>(_instance.OnDamageObserved);
            }
            LostAllyGuard.InstallPublisher(new NoOpAllyProtectionPublisher());
            _instance = null;
            _installed = false;
        }

        public static AllyProtectionPublisher Instance => _instance;

        public static int EvictionsPerMinute => (int)Interlocked.Read(ref _evictionsPerMinute);

        // 1 Hz scanner — called from TrainingDirector.AggregationTick on the daemon thread.
        // Position reads via BeliefSnapshot.ByTech acquired ONCE per tick.
        public static void Tick()
        {
            if (!_installed || _instance == null) return;
            try { _instance.TickInternal(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("ally-protect-tick",
                    "[AIWARN] AllyProtectionPublisher.Tick " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void TickInternal()
        {
            long now = MonoClock.Now();
            float budget = DirectorTunables.AllyProtectDamageBudget;
            int capacity = DirectorTunables.AllyProtectLruCapacity;

            // Walk support techs.
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                foreach (var techId in team.SnapshotTechIds())
                {
                    var state = team.TryGetTechState(techId);
                    if (state == null) continue;
                    if (!IsSupport(state.IdentityStamp.Identity)) continue;
                    if (state.SelfStateProbe == null) continue;
                    var snap = state.SelfStateProbe.TryRead();
                    if (snap == null) continue;

                    Window w;
                    lock (_gate)
                    {
                        if (!_windows.TryGetValue(techId.Value, out w))
                        {
                            w = new Window { WindowStartMono = now, PairedAllyTechId = -1 };
                            _windows[techId.Value] = w;
                        }
                        w.LastTouchMono = now;
                    }

                    // Pair-in-range accounting: nearest same-team Smart tech.
                    int pairedId;
                    bool hasPair = NearestAlly(team, techId, snap.PositionWorld, out pairedId);
                    lock (_gate)
                    {
                        if (hasPair) { w.SumTimeInRange += 1f; w.PairedAllyTechId = pairedId; }
                    }

                    // Close the window at 60 s.
                    float age = MonoClock.Seconds(w.WindowStartMono, now);
                    if (age >= WindowSec) CloseWindow(techId, state.IdentityStamp.Identity, w, budget, false);
                }
            }

            // LRU eviction: cap the dict; finalize pairs with windowAge >= 15 s scaled by time.
            EvictIfOver(capacity, budget, now);
            RollEvictionWindow(now);
        }

        private void CloseWindow(TechId techId, SmartIdentity identity, Window w, float budget, bool partial)
        {
            float mag = budget > 0f ? Mathf.Max(0f, 1f - w.FriendlyDamageInWindow / budget) : 1f;
            if (partial)
            {
                float scale = w.SumTimeInRange / WindowSec;
                if (scale > 1f) scale = 1f;
                mag *= scale;
            }
            // Reset the window in place.
            lock (_gate)
            {
                w.WindowStartMono = MonoClock.Now();
                w.SumTimeInRange = 0f;
                w.FriendlyDamageInWindow = 0f;
            }
            if (mag <= 0f) return;

            var outcome = new IdentityOutcome(techId, identity, OutcomeKind.AllyProtected, mag);
            try { WorldEventBus.PublishFromWorker(outcome); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("ally-protect-publish",
                    "[AIWARN] AllyProtectionPublisher.Close " + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            if (mag < DegradedThreshold)
            {
                DebugTAC_AI.LogWarnFileOnly("ally-protect-degraded-" + techId.Value,
                    "[ALLY-PROTECT-DEGRADED] tech=" + techId.Value + " mag=" + mag.ToString("F2"));
            }
        }

        private void EvictIfOver(int capacity, float budget, long now)
        {
            lock (_gate)
            {
                if (_windows.Count <= capacity) return;
                // Find the oldest-touched entries to evict down to capacity.
                while (_windows.Count > capacity)
                {
                    int oldestKey = -1;
                    long oldest = long.MaxValue;
                    foreach (var kv in _windows)
                    {
                        if (kv.Value.LastTouchMono < oldest) { oldest = kv.Value.LastTouchMono; oldestKey = kv.Key; }
                    }
                    if (oldestKey < 0) break;
                    Window w = _windows[oldestKey];
                    _windows.Remove(oldestKey);
                    Interlocked.Increment(ref _evictionsThisWindow);
                    float windowAge = MonoClock.Seconds(w.WindowStartMono, now);
                    if (windowAge >= 15f)
                    {
                        // Finalize with partial magnitude scaled by time-in-range.
                        float mag = budget > 0f ? Mathf.Max(0f, 1f - w.FriendlyDamageInWindow / budget) : 1f;
                        float scale = w.SumTimeInRange / WindowSec;
                        if (scale > 1f) scale = 1f;
                        mag *= scale;
                        if (mag > 0f)
                        {
                            var outcome = new IdentityOutcome(new TechId(oldestKey),
                                SmartIdentity.AircraftSupport, OutcomeKind.AllyProtected, mag);
                            try { WorldEventBus.PublishFromWorker(outcome); } catch { }
                        }
                    }
                }
            }
        }

        private void RollEvictionWindow(long now)
        {
            long start = Interlocked.Read(ref _evictionWindowStart);
            if (start == 0L) { Interlocked.Exchange(ref _evictionWindowStart, now); return; }
            if (MonoClock.Seconds(start, now) >= 60f)
            {
                long count = Interlocked.Exchange(ref _evictionsThisWindow, 0L);
                Interlocked.Exchange(ref _evictionsPerMinute, count);
                Interlocked.Exchange(ref _evictionWindowStart, now);
            }
        }

        private static bool NearestAlly(TeamRuntime team, TechId self, Vector3 selfPos, out int allyTechId)
        {
            allyTechId = -1;
            float bestSq = 150f * 150f;
            bool found = false;
            foreach (var tid in team.SnapshotTechIds())
            {
                if (tid == self) continue;
                var st = team.TryGetTechState(tid);
                if (st == null || st.SelfStateProbe == null) continue;
                var snap = st.SelfStateProbe.TryRead();
                if (snap == null) continue;
                float dSq = (snap.PositionWorld - selfPos).sqrMagnitude;
                if (dSq < bestSq) { bestSq = dSq; allyTechId = tid.Value; found = true; }
            }
            return found;
        }

        private static bool IsSupport(SmartIdentity id)
        {
            for (int i = 0; i < _supportIdentities.Length; i++)
                if (_supportIdentities[i] == id) return true;
            return false;
        }

        private void OnDamageObserved(DamageObserved evt)
        {
            // Friendly damage accounting: if the victim is a paired ally of a tracked
            // support tech, add the damage to that support tech's window.
            lock (_gate)
            {
                foreach (var kv in _windows)
                {
                    if (kv.Value.PairedAllyTechId == evt.Id.Value)
                        kv.Value.FriendlyDamageInWindow += evt.Damage.Damage;
                }
            }
        }

        private void OnTechDespawned(TechDespawned evt)
        {
            long now = MonoClock.Now();
            lock (_gate)
            {
                // If the despawned tech was paired to any support tech, stamp ally-loss.
                foreach (var kv in _windows)
                {
                    if (kv.Value.PairedAllyTechId == evt.Id.Value)
                        _allyLossMono[kv.Key] = now;
                }
                _windows.Remove(evt.Id.Value);
            }
        }

        // ---- accessors for LostAllyGuard ----
        public long LastAllyLossMono(TechId supportTechId)
        {
            lock (_gate)
            {
                long v;
                return _allyLossMono.TryGetValue(supportTechId.Value, out v) ? v : 0L;
            }
        }

        public bool TryGetPairedAlly(int supportTechId, out int allyTechId)
        {
            allyTechId = -1;
            lock (_gate)
            {
                Window w;
                if (_windows.TryGetValue(supportTechId, out w) && w.PairedAllyTechId >= 0)
                {
                    allyTechId = w.PairedAllyTechId;
                    return true;
                }
            }
            return false;
        }

        public static void ForgetTech(TechId techId)
        {
            if (_instance == null) return;
            lock (_instance._gate)
            {
                _instance._windows.Remove(techId.Value);
                _instance._allyLossMono.Remove(techId.Value);
            }
        }
    }
}
