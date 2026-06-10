using System;
using System.Collections.Concurrent;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Publishers
{
    // BaseHeld + BaseLost paired publisher. Subscribes to WorldEventBus<DamageObserved>.
    // Per Base-identity tech maintains {hpAtWindowStart, hpNow, windowStart, totalDamage}.
    // HP is read bg-safe from HealthSidecar (block-survival ratio, populated main-thread by
    // SmartForm.ObserveWorldTechsIfDue) — avoids an off-thread Damageable walk. 60 s window;
    // at close, if the tech survived AND totalDamage >= threshold, publish BaseHeld with
    // magnitude = hpNow/hpAtWindowStart. TechDespawned of a Base emits BaseLost (mag 1.0).
    public sealed class BaseHeldPublisher
    {
        private sealed class BaseWindow
        {
            public float HpAtWindowStart;
            public float HpNow;
            public long WindowStartMono;
            public float TotalDamageInWindow;
        }

        private static BaseHeldPublisher _instance;
        private static volatile bool _installed;

        private readonly ConcurrentDictionary<int, BaseWindow> _windows =
            new ConcurrentDictionary<int, BaseWindow>();

        public static void Install()
        {
            if (_installed) return;
            _instance = new BaseHeldPublisher();
            WorldEventBus.Subscribe<DamageObserved>(_instance.OnDamageObserved);
            WorldEventBus.Subscribe<TechDespawned>(_instance.OnTechDespawned);
            _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            if (_instance != null)
            {
                WorldEventBus.Unsubscribe<DamageObserved>(_instance.OnDamageObserved);
                WorldEventBus.Unsubscribe<TechDespawned>(_instance.OnTechDespawned);
            }
            _instance = null;
            _installed = false;
        }

        public static BaseHeldPublisher Instance => _instance;

        // 0.2 Hz HP poll + window-close driven from the Director tick (every 5th aggregation).
        public static void Tick()
        {
            if (!_installed || _instance == null) return;
            try { _instance.TickInternal(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("base-held-tick",
                    "[AIWARN] BaseHeldPublisher.Tick " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void TickInternal()
        {
            long now = MonoClock.Now();
            float threshold = DirectorTunables.BaseHeldDamageThreshold;
            float windowSec = DirectorTunables.BaseHeldWindowSec;
            var hp = SmartRuntime.Health;

            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                foreach (var techId in team.SnapshotTechIds())
                {
                    var state = team.TryGetTechState(techId);
                    if (state == null) continue;
                    if (state.IdentityStamp.Identity != SmartIdentity.Base) continue;

                    float curHp = hp != null ? hp.Get(techId) : 1f;
                    var w = _windows.GetOrAdd(techId.Value, _ => new BaseWindow
                    {
                        HpAtWindowStart = curHp, HpNow = curHp, WindowStartMono = now, TotalDamageInWindow = 0f,
                    });
                    w.HpNow = curHp;

                    float age = MonoClock.Seconds(w.WindowStartMono, now);
                    if (age < windowSec) continue;

                    // Window close: survived + meaningful damage absorbed -> BaseHeld.
                    if (w.TotalDamageInWindow >= threshold)
                    {
                        float mag = w.HpAtWindowStart > 0f ? w.HpNow / w.HpAtWindowStart : 1f;
                        if (mag < 0f) mag = 0f; else if (mag > 1f) mag = 1f;
                        var outcome = new IdentityOutcome(techId, SmartIdentity.Base, OutcomeKind.BaseHeld, mag);
                        try { WorldEventBus.PublishFromWorker(outcome); }
                        catch (Exception ex)
                        {
                            DebugTAC_AI.LogWarnFileOnly("base-held-publish",
                                "[AIWARN] BaseHeldPublisher " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                    // Roll the window.
                    w.HpAtWindowStart = curHp;
                    w.WindowStartMono = now;
                    w.TotalDamageInWindow = 0f;
                }
            }
        }

        private void OnDamageObserved(DamageObserved evt)
        {
            BaseWindow w;
            if (_windows.TryGetValue(evt.Id.Value, out w))
                w.TotalDamageInWindow += evt.Damage.Damage;
        }

        private void OnTechDespawned(TechDespawned evt)
        {
            BaseWindow w;
            if (_windows.TryRemove(evt.Id.Value, out w))
            {
                // A tracked Base died — emit BaseLost (OutcomeKind=4).
                var outcome = new IdentityOutcome(evt.Id, SmartIdentity.Base, OutcomeKind.BaseLost, 1f);
                try { WorldEventBus.PublishFromWorker(outcome); } catch { }
            }
        }

        // Accessors reused by OverextendedHunterGuard's HP-aggregation check.
        public float GetTechCurrentHP(TechId techId)
        {
            BaseWindow w;
            return _windows.TryGetValue(techId.Value, out w) ? w.HpNow : 1f;
        }

        public float GetTechMaxHP(TechId techId)
        {
            BaseWindow w;
            return _windows.TryGetValue(techId.Value, out w) ? w.HpAtWindowStart : 1f;
        }

        public static void ForgetTech(TechId techId)
        {
            if (_instance == null) return;
            BaseWindow _;
            _instance._windows.TryRemove(techId.Value, out _);
        }
    }
}
