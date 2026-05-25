using TAC_AI.AI.Engine;

namespace TAC_AI.AI.Enemy.EnemyOperations
{
    // REVISED (Step 5): the per-tick EvilCommander switch is replaced by ProfileRunner.RunEnemy. The R* attack
    // handlers are preserved - now invoked through the Enemy.Attack.* behavior modules the runner dispatches - and
    // the no-target pre-pass (DispatchNoTargetIdle) + retreat post-pass (GetRetreatLocation) structure plus the
    // unknown-handling self-heal are reproduced inside the runner.
    internal class EnemyOperationsController
    {
        private EnemyMind Mind;

        public EnemyOperationsController(EnemyMind Mind)
        {
            this.Mind = Mind;
        }

        public void Execute()
        {
            TankAIHelper helper = Mind.AIControl;
            ProfileRunner.RunEnemy(new AIContext(helper, Mind), Mind);
        }
    }
}
