using TAC_AI.AI.Forms.Smart.Identity;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Movement
{
    // Observe-only. Tech has been driving backwards for 45 s with no firing-arc target.
    // Modified-form combat-reverse is the exception, not the rule — sustained reverse
    // outside of standoff or combat-circle context is the fingerprint of a stuck plan.
    public sealed class BackwardsLockGuard : IBehaviorGuard
    {
        public const float MeanSignedThreshold = -1.5f;
        public const float FractionBelowZeroThreshold = 0.85f;
        public const float NetDisplacementMax = 8f;
        public const float TargetDistanceMin = 60f;

        private static readonly SmartIdentity[] _identities = new[]
        {
            SmartIdentity.Hunter, SmartIdentity.Gatherer, SmartIdentity.Patrol,
        };

        public string Name => "BackwardsLock";
        public GuardSeverity Severity => GuardSeverity.Observe;
        public int WindowSec => 45;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => null;

        public bool ShouldEvaluate(GuardContext ctx)
        {
            // No work if the rings have not filled enough samples (worker fills 30 s
            // scratch; we only need a clear backward fingerprint).
            if (ctx.Snapshot == null) return false;
            if (ctx.Helper == null) return false;
            return true;
        }

        public GuardVerdict Evaluate(GuardContext ctx)
        {
            var v = new GuardVerdict();
            var helper = ctx.Helper;
            var snap = ctx.Snapshot;

            // Pathology test.
            bool meanReverse = ctx.MeanRecentSpeedSigned < MeanSignedThreshold;
            bool mostlyReverse = ctx.FractionSpeedSignedBelowZero >= FractionBelowZeroThreshold;
            bool littleProgress = ctx.NetDisplacement.magnitude < NetDisplacementMax;

            // Target distance check via belief / probe. Snapshot exposes last-attacker
            // pos; if no attacker, treat as "no target near".
            float targetDist = 9999f;
            if (snap.HasLastAttacker)
            {
                targetDist = (snap.LastAttackerPosWorld - snap.PositionWorld).magnitude;
            }
            bool targetFarOrNone = targetDist > TargetDistanceMin;

            if (!(meanReverse && mostlyReverse && littleProgress && targetFarOrNone))
            {
                v.Fired = false;
                return v;
            }

            // Sane exceptions.
            // Sniper standoff is its own identity (not in gate); skipped already.
            if (helper.TurretFraction >= 0.5f && snap.HasLastAttacker)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 1;
                v.Fired = false;
                return v;
            }
            // Genuine kiting: net displacement large enough vs expected travel from the
            // mean signed speed × window time.
            float expectedTravel = -ctx.MeanRecentSpeedSigned * WindowSec * 0.3f;
            if (ctx.NetDisplacement.magnitude >= expectedTravel && expectedTravel > 0f)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 2;
                v.Fired = false;
                return v;
            }
            // Unjam FSM owns the back-out: skip when FrustrationMeter is actively elevated.
            if (helper.PublishedFrustrationMeter >= 25)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 3;
                v.Fired = false;
                return v;
            }
            // Anchored / parked.
            if (snap.AnchorState != 0)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 4;
                v.Fired = false;
                return v;
            }

            v.Fired = true;
            v.Magnitude = 1f;
            v.EvidenceFields = "meanSigned=" + ctx.MeanRecentSpeedSigned.ToString("F2")
                + " frac=" + ctx.FractionSpeedSignedBelowZero.ToString("F2")
                + " disp=" + ctx.NetDisplacement.magnitude.ToString("F1");
            return v;
        }
    }
}
