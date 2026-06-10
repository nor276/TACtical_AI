using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Movement
{
    // Warn. Slow tech with no kinetic progress + a destination it can't reach. Distinct
    // from BackwardsLock (which fingerprints sustained reverse) and from the unjam FSM
    // (which owns active back-out). 30 s window matches the worker's scratch ring.
    public sealed class WedgedNoProgressGuard : IBehaviorGuard
    {
        public const float MeanSpeedXZMax = 0.5f;
        public const float NetDisplacementMax = 4f;
        public const float DestDistanceMin = 12f;
        public const float DamageFreshSec = 30f;

        private static readonly SmartIdentity[] _identities = new[]
        {
            SmartIdentity.Hunter, SmartIdentity.Gatherer, SmartIdentity.Patrol,
            SmartIdentity.AircraftHunter, SmartIdentity.RepairSupport,
        };

        public string Name => "WedgedNoProgress";
        public GuardSeverity Severity => GuardSeverity.Warn;
        public int WindowSec => 30;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => null;

        public bool ShouldEvaluate(GuardContext ctx)
        {
            if (ctx.Snapshot == null) return false;
            if (ctx.Helper == null) return false;
            return true;
        }

        public GuardVerdict Evaluate(GuardContext ctx)
        {
            var v = new GuardVerdict();
            var helper = ctx.Helper;
            var snap = ctx.Snapshot;

            // Kinetic pathology. recentSpeedXZ is unsigned magnitude so abs not needed.
            bool slow = ctx.MeanRecentSpeedXZ < MeanSpeedXZMax;
            bool stuck = ctx.NetDisplacement.magnitude < NetDisplacementMax;
            if (!(slow && stuck))
            {
                v.Fired = false;
                return v;
            }

            // Not in combat / not just damaged. Provoked is the controller's combat latch.
            bool provokedNow = snap.ProvokedCountdown > 0;
            bool damageFresh = false;
            if (snap.HasLastAttacker)
            {
                float ageSec = MonoClock.Seconds(snap.LastAttackerSeenMono, ctx.MonoTick);
                if (ageSec < DamageFreshSec) damageFresh = true;
            }
            if (provokedNow || damageFresh)
            {
                v.Fired = false;
                return v;
            }

            // Has a destination it's not reaching. Vector3.zero is the unset sentinel.
            Vector3 dest = ctx.LastDestinationCore;
            if (dest == Vector3.zero)
            {
                v.Fired = false;
                return v;
            }
            float distToDest = (dest - snap.PositionWorld).magnitude;
            if (distToDest <= DestDistanceMin)
            {
                v.Fired = false;
                return v;
            }

            // Unjam FSM owns its own back-out.
            if (helper.PublishedFrustrationMeter != 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 1;
                v.Fired = false;
                return v;
            }

            // Sane exceptions.
            if (helper.AIAlign == AIAlignment.Player)
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

            // Corroborate via the net-progress ring — high count of "made progress" ticks
            // means we're not really wedged, just slow.
            int progressTicks = helper.WedgeNetProgressCount(WindowSec);
            if (progressTicks > WindowSec / 2)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 4;
                v.Fired = false;
                return v;
            }

            v.Fired = true;
            v.Magnitude = 1f;
            v.EvidenceFields = "meanXZ=" + ctx.MeanRecentSpeedXZ.ToString("F2")
                + " disp=" + ctx.NetDisplacement.magnitude.ToString("F1")
                + " distDest=" + distToDest.ToString("F1");
            return v;
        }
    }
}
