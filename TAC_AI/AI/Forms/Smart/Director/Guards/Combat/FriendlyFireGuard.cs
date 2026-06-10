using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Combat
{
    // Warn. Self damaged a same-team ally at least 3 × > 15 HP OR cumulative > 150 HP
    // within 60 s. Stale-aim heuristic excludes shots fired while the attacker drifted
    // > 10 m in the prior 1 s (no aim time stamp on SanitizedDamageInfo, so this is the
    // proxy). Beam-weapon target reacquisition is excepted via damage-type pattern.
    public sealed class FriendlyFireGuard : IBehaviorGuard
    {
        public const float MagnitudeThreshold = 15f;
        public const int LargeHitCountThreshold = 3;
        public const float CumulativeDamageThreshold = 150f;
        public const float StaleAimDriftThreshold = 10f;
        public const int RingCapacityPerTech = 16;

        private static readonly SmartIdentity[] _identities = new[]
        {
            SmartIdentity.Hunter, SmartIdentity.Sniper, SmartIdentity.AircraftHunter,
            SmartIdentity.Patrol, SmartIdentity.RepairSupport,
        };

        public string Name => "FriendlyFire";
        public GuardSeverity Severity => GuardSeverity.Warn;
        public int WindowSec => 60;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => null;

        private struct DamageEvent
        {
            public long Mono;
            public float Magnitude;
        }

        private sealed class Ring
        {
            public readonly DamageEvent[] Slots = new DamageEvent[RingCapacityPerTech];
            public int Head;
            public int Count;
        }

        // Per-attacker damage history. Keyed by attacker techValue.
        private static readonly Dictionary<int, Ring> _rings = new Dictionary<int, Ring>();
        private static readonly object _ringGate = new object();

        // Prior-tick attacker positions for the stale-aim heuristic. Keyed by attacker
        // techValue. Updated each guard tick on the attacker's own evaluation pass.
        private static readonly Dictionary<int, Vector3> _priorTickPos =
            new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, long> _priorTickStamp =
            new Dictionary<int, long>();
        private static readonly object _aimGate = new object();

        private static int _subscribed;

        public static void ForgetTech(TechId techId)
        {
            lock (_ringGate) _rings.Remove(techId.Value);
            lock (_aimGate)
            {
                _priorTickPos.Remove(techId.Value);
                _priorTickStamp.Remove(techId.Value);
            }
        }

        public bool ShouldEvaluate(GuardContext ctx)
        {
            EnsureSubscribed();
            if (ctx.Snapshot == null) return false;
            if (ctx.Helper == null) return false;
            return true;
        }

        public GuardVerdict Evaluate(GuardContext ctx)
        {
            var v = new GuardVerdict();
            var snap = ctx.Snapshot;

            // Stamp this tick's position so the next OnDamage callback for events from
            // this attacker can compute the stale-aim drift.
            lock (_aimGate)
            {
                _priorTickPos[ctx.TechId.Value] = snap.PositionWorld;
                _priorTickStamp[ctx.TechId.Value] = ctx.MonoTick;
            }

            // Tally damage events in window.
            int largeHits = 0;
            float cumulative = 0f;
            long winStart = ctx.MonoTick
                - System.Diagnostics.Stopwatch.Frequency * (long)WindowSec;

            Ring ring;
            lock (_ringGate)
            {
                if (!_rings.TryGetValue(ctx.TechId.Value, out ring) || ring == null)
                {
                    v.Fired = false;
                    return v;
                }
                int n = ring.Count;
                for (int i = 0; i < n; i++)
                {
                    int idx = (ring.Head - 1 - i + RingCapacityPerTech) % RingCapacityPerTech;
                    var e = ring.Slots[idx];
                    if (e.Mono < winStart) break;
                    if (e.Magnitude > MagnitudeThreshold) largeHits++;
                    cumulative += e.Magnitude;
                }
            }

            bool largeHitTrip = largeHits >= LargeHitCountThreshold;
            bool cumulativeTrip = cumulative > CumulativeDamageThreshold;
            if (!(largeHitTrip || cumulativeTrip))
            {
                v.Fired = false;
                return v;
            }

            // Sane exceptions.
            if (ctx.Helper.AIAlign == AIAlignment.Player)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 1;
                v.Fired = false;
                return v;
            }
            if (snap.AnchorState != 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 2;
                v.Fired = false;
                return v;
            }

            v.Fired = true;
            v.Magnitude = 1f;
            v.EvidenceFields = "largeHits=" + largeHits + " cum=" + cumulative.ToString("F0");
            return v;
        }

        private static void EnsureSubscribed()
        {
            if (Interlocked.CompareExchange(ref _subscribed, 1, 0) != 0) return;
            try { WorldEventBus.Subscribe<DamageObserved>(OnDamage); }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("friendly-fire-subscribe",
                    "[AIWARN] FriendlyFireGuard subscribe " + ex.GetType().Name + ": "
                    + ex.Message);
                Interlocked.Exchange(ref _subscribed, 0);
            }
        }

        // Subscriber. Runs on main thread inside WorldEventBus.Publish. Filters out
        // attackers that are not the victim's own techId or are not same-team. Stamps the
        // per-attacker ring with the damage magnitude.
        private static void OnDamage(DamageObserved ev)
        {
            try
            {
                var info = ev.Damage;
                if (!info.HasAttacker) return;
                TechId attacker = info.AttackerIfKnown;
                TechId victim = ev.Id;
                if (attacker == victim) return;

                TeamId attackerTeam, victimTeam;
                if (!SmartRuntime.FindTeamForTech(attacker, out attackerTeam)) return;
                if (!SmartRuntime.FindTeamForTech(victim, out victimTeam)) return;
                if (attackerTeam != victimTeam) return;

                // Stale-aim drift check via the belief snapshot's attacker position.
                Vector3 priorPos;
                long priorStamp;
                bool hadPrior = false;
                lock (_aimGate)
                {
                    if (_priorTickPos.TryGetValue(attacker.Value, out priorPos)
                        && _priorTickStamp.TryGetValue(attacker.Value, out priorStamp))
                    {
                        hadPrior = true;
                    }
                    else
                    {
                        priorPos = Vector3.zero;
                        priorStamp = 0L;
                    }
                }
                if (hadPrior)
                {
                    var beliefs = SmartRuntime.World?.FusedBuffer.Read();
                    if (beliefs != null && beliefs.ByTech != null)
                    {
                        BeliefState bs;
                        if (beliefs.ByTech.TryGetValue(attacker, out bs))
                        {
                            float drift = (bs.PositionMean - priorPos).magnitude;
                            float sinceSec = MonoClock.Seconds(priorStamp, MonoClock.Now());
                            if (sinceSec < 1.5f && drift > StaleAimDriftThreshold) return;
                        }
                    }
                }

                Ring ring;
                lock (_ringGate)
                {
                    if (!_rings.TryGetValue(attacker.Value, out ring) || ring == null)
                    {
                        ring = new Ring();
                        _rings[attacker.Value] = ring;
                    }
                    ring.Slots[ring.Head] = new DamageEvent
                    {
                        Mono = MonoClock.Now(),
                        Magnitude = info.Damage,
                    };
                    ring.Head = (ring.Head + 1) % RingCapacityPerTech;
                    if (ring.Count < RingCapacityPerTech) ring.Count++;
                }
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("friendly-fire-ondamage",
                    "[AIWARN] FriendlyFireGuard.OnDamage " + ex.GetType().Name + ": "
                    + ex.Message);
            }
        }
    }
}
