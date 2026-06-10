using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI;

namespace TAC_AI.AI.Forms.Smart.Director.Guards.Movement
{
    // Observe-only. Land-classified tech sitting under water with no kinetic progress.
    // Spawner placement glitches + nav drift can park a Hunter / Gatherer in a lake;
    // training signal lets the model learn to avoid those biome bands.
    public sealed class WaterloggedTechGuard : IBehaviorGuard
    {
        public const float UnderWaterDepthFloor = 2.0f;
        public const float FractionUnderwaterThreshold = 0.75f;
        public const float MeanSpeedXZMax = 1.0f;
        public const float NetDisplacementRatePerSec = 0.5f;
        public const float WaterHeightSentinel = -9001f;

        private static readonly SmartIdentity[] _identities = new[]
        {
            SmartIdentity.Hunter, SmartIdentity.Gatherer, SmartIdentity.Patrol,
            SmartIdentity.RepairSupport,
        };

        public string Name => "WaterloggedTech";
        public GuardSeverity Severity => GuardSeverity.Observe;
        public int WindowSec => 40;
        public SmartIdentity[] AppliesToIdentities => _identities;
        public int[] AppliesToScenarios => null;

        public bool ShouldEvaluate(GuardContext ctx)
        {
            if (ctx.Snapshot == null) return false;
            if (ctx.Helper == null) return false;
            // Skip entirely when no WaterMod is present (KickStart.WaterHeight sentinel).
            if (ctx.Snapshot.WaterHeight == WaterHeightSentinel) return false;
            // Authored Sea techs are by-design under water.
            if (ctx.Helper.AuthoredTerrain == TerraTechETCUtil.BaseTerrain.Sea) return false;
            // AIType-aware terrain skip — Buccaneer / Aviator / Astrotech are designed
            // for water / air paths.
            var dedi = ctx.Helper.DediAI;
            if (dedi == AIType.Buccaneer || dedi == AIType.Aviator || dedi == AIType.Astrotech)
                return false;
            return true;
        }

        public GuardVerdict Evaluate(GuardContext ctx)
        {
            var v = new GuardVerdict();
            var snap = ctx.Snapshot;

            float depthBelow = (snap.WaterHeight - UnderWaterDepthFloor) - snap.PositionWorld.y;
            // Single-tick depth as a proxy for the window fraction; the Y ring backs the
            // no-kinetic-progress half.
            bool underwaterNow = depthBelow > 0f;
            if (!underwaterNow)
            {
                v.Fired = false;
                return v;
            }

            bool slow = ctx.MeanRecentSpeedXZ < MeanSpeedXZMax;
            float netDistMin = NetDisplacementRatePerSec * WindowSec;
            bool stuck = ctx.NetDisplacement.magnitude < netDistMin;
            if (!(slow && stuck))
            {
                v.Fired = false;
                return v;
            }

            // Sane exceptions.
            if (snap.ProvokedCountdown > 0 && snap.HasLastAttacker)
            {
                v.ExceptionMatched = true;
                v.ExceptionId = 1;
                v.Fired = false;
                return v;
            }
            // Player-controlled exemption — AIAlign==Player techs are not pathology cases.
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
            v.EvidenceFields = "depth=" + depthBelow.ToString("F1")
                + " meanXZ=" + ctx.MeanRecentSpeedXZ.ToString("F2");
            return v;
        }
    }
}
