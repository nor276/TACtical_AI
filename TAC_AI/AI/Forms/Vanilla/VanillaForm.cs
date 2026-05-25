using TAC_AI.AI.Engine;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Forms.Vanilla
{
    /// <summary>
    /// "Vanilla (mod AI off)" - the mod's AI stands down: it issues no drive intent and holds fire, so modded
    /// techs go passive. IMPORTANT / honest caveat: this is NOT a true handback to TerraTech's stock AI. The mod
    /// establishes its AI takeover at load, so genuine stock-AI behaviour requires DISABLING the mod (the "Enable
    /// Mod" option) and reloading - it cannot be done cleanly as a live per-tick form. This form is the closest
    /// in-session approximation ("mod not driving"); treat it as experimental. It still demonstrates the form
    /// system (a third discovered, selectable form).
    /// </summary>
    public sealed class VanillaForm : IAIForm
    {
        public string Id => "Vanilla";
        public string DisplayName => "Vanilla (mod AI off)";

        public void RunEnemy(AIContext ctx, EnemyMind mind) => StandDown(ctx);
        public void RunAllied(AIContext ctx) => StandDown(ctx);

        private static void StandDown(AIContext ctx)
        {
            ctx.LoadOperator();
            ctx.OpStop();
            ctx.WantsToFight = false;
            ctx.CommitOperator();
        }
    }
}
