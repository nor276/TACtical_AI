using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Role
{
    // Observe-only. Hunter / AircraftHunter / Patrol / Sniper sitting near the friendly
    // base while a hostile is alive within 300 m of that base and the tech is not
    // engaging. The anchored test reads from the IdentityStamp.SpawnAnchor — for
    // base-defenders that's the base footprint; for free-roamers it's the spawn point.
    // S5 (Mining + Defend) is intentionally dropped from the scenario gate — sitting at
    // the base IS the role there.
    public sealed class HomesicknessGuard : IBehaviorGuard
    {
        public const float MeanDistanceToBaseMax = 25f;
        public const float NoTargetFractionThreshold = 0.80f;
        public const float MeanSpeedXZMax = 1.5f;
        public const float HostileNearBaseRadius = 300f;

        private static readonly SmartIdentity[] _identities = new[]
        {
            SmartIdentity.Hunter, SmartIdentity.AircraftHunter, SmartIdentity.Patrol,
            SmartIdentity.Sniper,
        };
        private static readonly int[] _scenarios = new[]
        {
            (int)ScenarioId.S1, (int)ScenarioId.S2, (int)ScenarioId.S4,
        };

        public string Name => "Homesickness";
        public GuardSeverity Severity => GuardSeverity.Observe;
        public int WindowSec => 120;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => _scenarios;

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

            // Distance to the friendly base — approximated via the IdentityStamp's
            // SpawnAnchor. For base-defenders this is the base footprint; for hunters
            // who spawn at the base it doubles as a "near home" proxy.
            Vector3 anchor = ctx.IdentityStamp.SpawnAnchor;
            float distToAnchor = (snap.PositionWorld - anchor).magnitude;
            bool nearBase = distToAnchor < MeanDistanceToBaseMax;
            if (!nearBase)
            {
                v.Fired = false;
                return v;
            }

            // Not chasing / not provoked / slow.
            bool noTarget = !snap.HasLastAttacker;
            bool notProvoked = snap.ProvokedCountdown <= 0;
            bool slow = ctx.MeanRecentSpeedXZ < MeanSpeedXZMax;
            if (!(noTarget && notProvoked && slow))
            {
                v.Fired = false;
                return v;
            }

            // Hostile-near-base gate stops quiet moments at the base from tripping the
            // guard. Use the snapshot's last-attacker history as a proxy - if some attacker
            // was seen near the anchor in the last 90 s, the pathology is real.
            bool hostileNearBaseRecently = false;
            if (snap.HasLastAttacker)
            {
                float ageSec = MonoClock.Seconds(snap.LastAttackerSeenMono, ctx.MonoTick);
                if (ageSec < 90f)
                {
                    float toAnchor = (snap.LastAttackerPosWorld - anchor).magnitude;
                    if (toAnchor < HostileNearBaseRadius) hostileNearBaseRecently = true;
                }
            }
            if (!hostileNearBaseRecently)
            {
                // No hostile to chase → not pathology, anti-stifling exemption.
                v.ExceptionMatched = true;
                v.ExceptionId = 1;
                v.Fired = false;
                return v;
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
            v.EvidenceFields = "distAnchor=" + distToAnchor.ToString("F1")
                + " meanXZ=" + ctx.MeanRecentSpeedXZ.ToString("F2");
            return v;
        }
    }
}
