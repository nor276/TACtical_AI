using TAC_AI.AI.Engine;

namespace TAC_AI.AI.AlliedOperations
{
    // REVISED (Step 5): the per-tick DediAI x DriverType switch is replaced by ProfileRunner.RunAllied. The B*
    // handlers are preserved - now invoked through the Allied.* / Economy.* / Support.* behavior modules the
    // runner dispatches - and the DriverType==Stationary branch + the Escort inner DriverType switch are
    // reproduced in the runner's assignment (Stationary) and the AlliedEscort module (Escort) respectively.
    internal class AlliedOperationsController
    {
        private TankAIHelper helper;

        public AlliedOperationsController(TankAIHelper helper)
        {
            this.helper = helper;
        }

        public void Execute()
        {
            ProfileRunner.RunAllied(new AIContext(helper));
        }
    }
}
