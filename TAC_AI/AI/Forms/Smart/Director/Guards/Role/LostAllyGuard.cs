using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Role
{
    // Per-tech last-ally-loss mono accessor. Default impl returns 0 (= no loss seen)
    // so LostAllyGuard's grace gate bypasses on cold start.
    public interface IAllyProtectionPublisher
    {
        long LastAllyLossMono(TechId supportTechId);
    }

    internal sealed class NoOpAllyProtectionPublisher : IAllyProtectionPublisher
    {
        public long LastAllyLossMono(TechId supportTechId) => 0L;
    }

    // Warn. RepairSupport / AircraftSupport with no friendly Smart-driven tech within
    // proximity for ≥ 80% of a 90 s window, where the support tech HAD a friendly at the
    // window's start. Grace window: don't fire within 30 s of a known ally loss (the
    // tech's pair just died, that's not a navigation failure).
    public sealed class LostAllyGuard : IBehaviorGuard
    {
        public const float GroundProximityRadius = 50f;
        public const float AirProximityRadius = 150f;
        public const float NoAllyFractionThreshold = 0.80f;
        public const float AllyLossGraceSec = 30f;

        private static readonly SmartIdentity[] _identities = new[]
        {
            SmartIdentity.RepairSupport, SmartIdentity.AircraftSupport,
        };
        private static readonly int[] _scenarios = new[]
        {
            (int)ScenarioId.S3, (int)ScenarioId.S4, (int)ScenarioId.S5,
            (int)ScenarioId.S6prime,
        };

        public string Name => "LostAlly";
        public GuardSeverity Severity => GuardSeverity.Warn;
        public int WindowSec => 90;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => _scenarios;

        // Volatile reference so InstallPublisher writes are visible to bg-thread readers
        // without a memory barrier. Ref assignment is atomic on CLR.
        private static volatile IAllyProtectionPublisher _publisher
            = new NoOpAllyProtectionPublisher();
        // Tracks whether each support tech ever saw an ally at the start of its window.
        // 0 = never observed; >0 = mono stamp of first ally observation. Keyed by techId.
        private static readonly System.Collections.Generic.Dictionary<int, long> _pairSeenMono
            = new System.Collections.Generic.Dictionary<int, long>();
        private static readonly object _pairGate = new object();

        public static void InstallPublisher(IAllyProtectionPublisher publisher)
        {
            if (publisher != null) _publisher = publisher;
        }

        public static void ForgetTech(TechId techId)
        {
            lock (_pairGate) _pairSeenMono.Remove(techId.Value);
        }

        public bool ShouldEvaluate(GuardContext ctx)
        {
            if (ctx.Snapshot == null) return false;
            if (ctx.Helper == null) return false;
            return true;
        }

        public GuardVerdict Evaluate(GuardContext ctx)
        {
            var v = new GuardVerdict();
            var snap = ctx.Snapshot;
            bool isAir = ctx.Identity == SmartIdentity.AircraftSupport;
            float radius = isAir ? AirProximityRadius : GroundProximityRadius;
            float radiusSq = radius * radius;

            // Walk friendly Smart-driven techs (same team, different tech) and check
            // proximity. We read each via SelfStateProbe.TryRead so the work stays
            // bg-safe; the published position is a few frames stale at worst.
            TeamId myTeam = snap.SubjectTeam;
            bool allyFound = false;
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                if (team.TeamId != myTeam) continue;
                foreach (var techId in team.SnapshotTechIds())
                {
                    if (techId == ctx.TechId) continue;
                    var st = team.TryGetTechState(techId);
                    if (st == null) continue;
                    if (st.SelfStateProbe == null) continue;
                    var allySnap = st.SelfStateProbe.TryRead();
                    if (allySnap == null) continue;
                    float dSq = (allySnap.PositionWorld - snap.PositionWorld).sqrMagnitude;
                    if (dSq <= radiusSq) { allyFound = true; break; }
                }
                if (allyFound) break;
            }

            var window = GuardWindowPool.EnsureWindow(ctx.TechId, (int)GuardId.LostAlly,
                WindowSec);
            window.Push(allyFound ? 1f : 0f, allyFound);

            // Stamp "pair existed at window start" the first time we ever observe an ally.
            // Without this gate, a solo support that never had a pair would trip on its
            // first window — that's a roster issue, not pathology.
            long pairStartMono;
            bool pairKnown;
            lock (_pairGate)
            {
                pairKnown = _pairSeenMono.TryGetValue(ctx.TechId.Value, out pairStartMono);
                if (allyFound && !pairKnown)
                {
                    pairStartMono = ctx.MonoTick;
                    _pairSeenMono[ctx.TechId.Value] = pairStartMono;
                    pairKnown = true;
                }
            }
            if (!pairKnown)
            {
                v.Fired = false;
                return v;
            }

            // Window has to be deep enough to measure 80%.
            if (window.Count < (WindowSec * 3 / 4))
            {
                v.Fired = false;
                return v;
            }

            // Window-level pathology: ally absent for ≥ 80% of ticks.
            float fractionAllyPresent = window.FractionFlag();
            float fractionAllyAbsent = 1f - fractionAllyPresent;
            if (fractionAllyAbsent < NoAllyFractionThreshold)
            {
                v.Fired = false;
                return v;
            }

            // Grace gate: don't fire within 30 s of a known ally loss.
            long lastLossMono = _publisher.LastAllyLossMono(ctx.TechId);
            if (lastLossMono > 0L)
            {
                float sinceLoss = MonoClock.Seconds(lastLossMono, ctx.MonoTick);
                if (sinceLoss <= AllyLossGraceSec)
                {
                    v.ExceptionMatched = true;
                    v.ExceptionId = 1;
                    v.Fired = false;
                    return v;
                }
            }

            // Sane exceptions.
            if (ctx.Helper.AIAlign == AIAlignment.Player)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 2;
                v.Fired = false;
                return v;
            }
            if (snap.AnchorState != 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 3;
                v.Fired = false;
                return v;
            }

            v.Fired = true;
            v.Magnitude = 1f;
            v.EvidenceFields = "absent=" + fractionAllyAbsent.ToString("F2")
                + " radius=" + radius.ToString("F0");
            return v;
        }
    }
}
