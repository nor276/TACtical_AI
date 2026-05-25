using TAC_AI.AI.Enemy;
using TAC_AI.AI.Forms;

namespace TAC_AI.AI.Engine
{
    /// <summary>
    /// The thin per-tick router called by the two *OperationsController.Execute methods. It ensures the modules
    /// are loaded, then delegates the whole tick to the ACTIVE AI form (ModifiedForm by default; the player selects
    /// the global form in-game). The form owns the actual dispatch logic - so swapping forms swaps the entire AI
    /// behavior set. The old switch-based dispatch now lives in ModifiedForm.
    /// </summary>
    public static class ProfileRunner
    {
        public static void RunEnemy(AIContext ctx, EnemyMind mind)
        {
            AIModuleBootstrap.InitAIModules();
            AIFormRegistry.Active?.RunEnemy(ctx, mind);
        }

        public static void RunAllied(AIContext ctx)
        {
            AIModuleBootstrap.InitAIModules();
            AIFormRegistry.Active?.RunAllied(ctx);
        }
    }
}
