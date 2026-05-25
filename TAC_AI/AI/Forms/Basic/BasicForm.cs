using System.Collections.Generic;
using TAC_AI.AI.Behaviors;
using TAC_AI.AI.Engine;
using TAC_AI.AI.Engine.Registry;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Forms.Basic
{
    /// <summary>
    /// "Basic TAC AI" - a deliberately simpler form, closer to the original pre-enhancement feel. It is its own
    /// self-contained AI (a separate folder, discovered by simply existing - the proof that a new form drops in
    /// and shows up in-game). Enemy combat here is a straight charge-and-fire to a basic stand-off: NONE of the
    /// Modified form's circle/face duty-cycle, bounds-aware stand-off dead-band, or FACE-ENEMY reverse. Target
    /// acquisition + idle + retreat reuse the shared behavior modules (so miners still mine, etc.); allied control
    /// reuses the Modified allied path for now (the headline Basic difference is enemy combat feel). This is the
    /// FIRST module written against the IAIContext surface rather than wrapping a legacy handler - a template for
    /// authoring new forms.
    /// </summary>
    public sealed class BasicForm : IAIForm
    {
        public string Id => "Basic";
        public string DisplayName => "Basic TAC AI";

        // allied control + the shared idle/retreat acquisition reuse the Modified implementation.
        private static readonly ModifiedForm modified = new ModifiedForm();

        private static readonly Dictionary<string, IBehavior> cache = new Dictionary<string, IBehavior>();
        private static IBehavior Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (cache.TryGetValue(id, out var b)) return b;
            b = AIModuleRegistry.TryGet(id, out var e) ? e.Create() : null;
            if (b != null) cache[id] = b;
            return b;
        }
        private static bool NoTarget(TankAIHelper helper)
        {
            var e = helper.lastEnemyGet;
            return e == null || e.tank == null;
        }

        public void RunEnemy(AIContext ctx, EnemyMind mind)
        {
            var helper = ctx.Helper;
            ctx.LoadOperator();

            // no-target: reuse the shared acquisition/idle (attitude routing -> mine/scavenge/guard).
            if (NoTarget(helper))
            {
                Get("Enemy.Idle.NoTarget")?.Tick(ctx);
                if (NoTarget(helper))
                {
                    if (helper.Retreat) Get("Enemy.Retreat")?.Tick(ctx);
                    ctx.CommitOperator();
                    return;
                }
            }

            // simple "true to original" combat: lock the target, hold a basic stand-off, charge straight in
            // facing it, and let the (form-independent) weapon system fire. No circling / duty-cycle / dead-band.
            var enemy = helper.lastEnemyGet;
            ctx.SetPursuit(enemy);
            ctx.WantsToFight = true;
            ctx.AutoSpacing = ctx.MinCombatRange;
            ctx.OpSetLastDest(enemy.tank.boundsCentreWorldNoCheck);
            ctx.OpDriveToFacingTowards();

            if (helper.Retreat) Get("Enemy.Retreat")?.Tick(ctx);
            ctx.CommitOperator();
        }

        public void RunAllied(AIContext ctx) => modified.RunAllied(ctx);
    }
}
