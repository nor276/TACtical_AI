using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Identity;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Combat
{
    // Observe-only. Hunter that wandered far from any friendly + within 60 m of a
    // hostile + lost > 25% HP across the window. Catches solo over-extension where the
    // Q-net's value estimate is too tolerant of getting flanked.
    public sealed class OverextendedHunterGuard : IBehaviorGuard
    {
        public const float DefaultLeashM = 250f;
        public const float NearestHostileDistMax = 60f;
        public const float HpDropFractionMin = 0.25f;

        private static readonly SmartIdentity[] _identities = new[] { SmartIdentity.Hunter };
        private static readonly int[] _scenarios = new[]
        {
            (int)ScenarioId.S3, (int)ScenarioId.S4, (int)ScenarioId.S5, (int)ScenarioId.S6prime,
        };

        public string Name => "OverextendedHunter";
        public GuardSeverity Severity => GuardSeverity.Observe;
        public int WindowSec => 75;
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

            // Distance to the nearest hostile via last-attacker proxy.
            if (!snap.HasLastAttacker)
            {
                v.Fired = false;
                return v;
            }
            float distHostile = (snap.LastAttackerPosWorld - snap.PositionWorld).magnitude;
            if (distHostile >= NearestHostileDistMax)
            {
                v.Fired = false;
                return v;
            }

            // HP drop test — rolling window kept in the per-(tech, guard) pool.
            var window = GuardWindowPool.EnsureWindow(ctx.TechId, (int)GuardId.OverextendedHunter,
                WindowSec);
            window.Push(snap.HpFraction, false);
            float baseline = window.Mean();
            float hpDelta = baseline - snap.HpFraction;
            // Only fire when the average over the window is meaningfully above the
            // present HP — guards on cold-start with HP==1.0 throughout never fire.
            bool hpDropped = hpDelta > HpDropFractionMin;
            if (!hpDropped)
            {
                v.Fired = false;
                return v;
            }

            // Leash test — no friendly within 100 m. Without a fast bg-thread roster
            // distance scan, approximate via team density: live SmartTechCountFor on
            // the support identities; if zero, treat as far from any friendly.
            float leashM = ctx.OverextendedHunterLeashM > 0f
                ? ctx.OverextendedHunterLeashM : DefaultLeashM;
            int liveSupports = SmartRuntime.LiveSmartTechCountFor(SmartIdentity.RepairSupport)
                + SmartRuntime.LiveSmartTechCountFor(SmartIdentity.AircraftSupport);
            bool unsupported = liveSupports <= 0;
            // Leash check is informational - without a per-tick roster scan we bias to
            // false positives only when supports are absent.
            if (!unsupported)
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
            v.EvidenceFields = "hostile=" + distHostile.ToString("F1")
                + " hpDelta=" + hpDelta.ToString("F2")
                + " leashM=" + leashM.ToString("F0");
            return v;
        }
    }
}
