namespace TAC_AI.AI
{
    // StatusText service (partial-class split of TankAIHelper, Step 2). The tech's debug/HUD status string.
    // Reads the shared ControlCore bus field (declared in the core file). No tank.control writes.
    public partial class TankAIHelper
    {
        public string GetCoreControlString()
        {
            return ControlCore.ToString();
        }
    }
}
