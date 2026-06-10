using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Role
{
    // Soft-correct, telemetry-only. Gatherer with ≥ 75 % cargo for the entire 90 s
    // window who has not delivered a block, sits > 50 m from any friendly base,
    // can reach one (path-traversable), and made < 15 m of net progress toward it.
    // On fire: emits IdentityOutcome(GuardViolation_Role) + schedules a 30 s
    // kinematic-correlation IdentityOutcome via BaseReturnTelemetryObserver.
    //
    // NEVER writes the mailbox. NEVER calls GuardCorrectiveActuator. NEVER emits
    // GuardCorrectionInjected. The training signal is the entire surface.
    public sealed class OverdueDeliveryGuard : IBehaviorGuard
    {
        public const float CargoFillFloor = 0.75f;
        public const float DistToBaseMin = 50f;
        public const float NetProgressTowardBaseMargin = 15f;
        public const float RefireCooldownSec = 30f;

        private static readonly SmartIdentity[] _identities = new[] { SmartIdentity.Gatherer };
        private static readonly int[] _scenarios = new[]
        {
            (int)ScenarioId.S2, (int)ScenarioId.S5,
        };

        public string Name => "OverdueDelivery";
        public GuardSeverity Severity => GuardSeverity.SoftCorrect;
        public int WindowSec => 90;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => _scenarios;

        // Per-tech window-start cache. Stamps the window-start mono + distance-to-base at
        // that mono. Reset on falling edge of cargo / on delivery.
        private struct WindowState
        {
            public long WindowStartMono;
            public float DistToBaseAtStart;
            public Vector3 BasePosAtStart;
            public TechId BaseTechId;
            public long LastFireMono;
        }
        private static readonly Dictionary<int, WindowState> _windowState
            = new Dictionary<int, WindowState>();
        private static readonly object _stateGate = new object();

        public static void ForgetTech(TechId techId)
        {
            lock (_stateGate) _windowState.Remove(techId.Value);
            BaseReturnTelemetryObserver.ForgetTech(techId);
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
            var helper = ctx.Helper;

            // Cargo fill ≥ 75 % via the bg-safe SelfProbeSnapshot. CargoNumContents +
            // CargoCapacity are stamped each tick by SelfStateProbe from the main thread.
            float fill = snap.CargoCapacity > 0
                ? (float)snap.CargoNumContents / snap.CargoCapacity : 0f;
            if (fill < CargoFillFloor)
            {
                ResetWindow(ctx.TechId);
                v.Fired = false;
                return v;
            }

            // Recent delivery cancels the window.
            long lastDelivered = Interlocked.Read(ref helper.LastBlockDeliveredMono);
            if (lastDelivered != 0L)
            {
                float ageSec = MonoClock.Seconds(lastDelivered, ctx.MonoTick);
                if (ageSec < WindowSec)
                {
                    ResetWindow(ctx.TechId);
                    v.Fired = false;
                    return v;
                }
            }

            // Find the nearest friendly Smart-classified Base on the same team.
            TeamId myTeam = snap.SubjectTeam;
            TechId nearestBase = default(TechId);
            Vector3 nearestBasePos = Vector3.zero;
            float nearestDist = float.MaxValue;
            bool foundBase = false;
            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                if (team.TeamId != myTeam) continue;
                foreach (var bid in team.SnapshotTechIds())
                {
                    var st = team.TryGetTechState(bid);
                    if (st == null) continue;
                    if (st.IdentityStamp.Identity != SmartIdentity.Base) continue;
                    if (st.SelfStateProbe == null) continue;
                    var baseSnap = st.SelfStateProbe.TryRead();
                    if (baseSnap == null) continue;
                    float dist = (baseSnap.PositionWorld - snap.PositionWorld).magnitude;
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestBase = bid;
                        nearestBasePos = baseSnap.PositionWorld;
                        foundBase = true;
                    }
                }
            }
            if (!foundBase)
            {
                ResetWindow(ctx.TechId);
                v.Fired = false;
                return v;
            }
            if (nearestDist <= DistToBaseMin)
            {
                ResetWindow(ctx.TechId);
                v.Fired = false;
                return v;
            }

            // Reachability gate. Cold-start returns assume-reachable.
            if (!PathingService.IsReachable(snap.PositionWorld, nearestBasePos,
                VehicleCapability.Default))
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 1;
                v.Fired = false;
                return v;
            }
            if (PathingService.LastQueryFailedRecently(ctx.TechId, nearestBasePos, 30f))
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 2;
                v.Fired = false;
                return v;
            }

            // Cache or update the window state.
            WindowState ws;
            bool windowStarted;
            lock (_stateGate)
            {
                if (!_windowState.TryGetValue(ctx.TechId.Value, out ws))
                {
                    ws = new WindowState
                    {
                        WindowStartMono = ctx.MonoTick,
                        DistToBaseAtStart = nearestDist,
                        BasePosAtStart = nearestBasePos,
                        BaseTechId = nearestBase,
                        LastFireMono = 0L,
                    };
                    _windowState[ctx.TechId.Value] = ws;
                    windowStarted = false;
                }
                else
                {
                    windowStarted = true;
                }
            }

            if (!windowStarted)
            {
                v.Fired = false;
                return v;
            }

            float windowAge = MonoClock.Seconds(ws.WindowStartMono, ctx.MonoTick);
            if (windowAge < WindowSec)
            {
                v.Fired = false;
                return v;
            }

            // Net-progress gate. dist_end >= dist_start − 15 m → no net progress.
            if (nearestDist < ws.DistToBaseAtStart - NetProgressTowardBaseMargin)
            {
                // Made progress; reset and don't fire.
                ResetWindow(ctx.TechId);
                v.Fired = false;
                return v;
            }

            // Sane exceptions.
            if (helper.AIAlign == AIAlignment.Player)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 3;
                v.Fired = false;
                return v;
            }
            if (snap.AnchorState != 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 4;
                v.Fired = false;
                return v;
            }
            if (helper.PublishedFrustrationMeter > 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 5;
                v.Fired = false;
                return v;
            }

            // Per-tech re-fire cooldown. Prevents IdentityOutcome storms within a
            // single overdue episode.
            bool gatedByCooldown;
            lock (_stateGate)
            {
                _windowState.TryGetValue(ctx.TechId.Value, out ws);
                gatedByCooldown = false;
                if (ws.LastFireMono != 0L)
                {
                    float sinceFire = MonoClock.Seconds(ws.LastFireMono, ctx.MonoTick);
                    if (sinceFire < RefireCooldownSec) gatedByCooldown = true;
                }
            }

            // Always poll the kinematic correlation tracker — even when we won't fire
            // again, a prior fire's 30 s tracker may be ready.
            BaseReturnTelemetryObserver.PollKinematicCorrelation(ctx.TechId,
                snap.PositionWorld, ctx.MonoTick);

            if (gatedByCooldown)
            {
                v.Fired = false;
                return v;
            }

            // Fire path: worker emits the initial IdentityOutcome; observer schedules the
            // 30 s kinematic-correlation tracker for the second emission.
            BaseReturnTelemetryObserver.Schedule(ctx.TechId, ws.BaseTechId,
                snap.PositionWorld, ws.BasePosAtStart);
            lock (_stateGate)
            {
                if (_windowState.TryGetValue(ctx.TechId.Value, out ws))
                {
                    ws.LastFireMono = ctx.MonoTick;
                    _windowState[ctx.TechId.Value] = ws;
                }
            }

            v.Fired = true;
            v.Magnitude = windowAge / 90f;
            if (v.Magnitude > 1f) v.Magnitude = 1f;
            v.EvidenceFields = "fill=" + fill.ToString("F2")
                + " distBase=" + nearestDist.ToString("F1")
                + " ageSec=" + windowAge.ToString("F0");
            return v;
        }

        private static void ResetWindow(TechId techId)
        {
            lock (_stateGate) _windowState.Remove(techId.Value);
        }
    }
}
