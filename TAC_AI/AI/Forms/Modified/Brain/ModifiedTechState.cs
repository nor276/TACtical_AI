namespace TAC_AI.AI
{
    /// <summary>
    /// Per-tech BRAIN state owned by the Modified form (v2). Stored in TankAIHelper.FormState (the shell's
    /// form-agnostic slot); created in ModifiedForm.OnTechSpawn, cleared in OnTechRecycle. Holds the brain-local
    /// state that used to be private fields ON TankAIHelper - now detached from the shell so the helper stays a
    /// lean bus+sinks+lifecycle. Brain extension methods reach it via helper.MState().
    /// </summary>
    public class ModifiedTechState
    {
        // DutyCycle: per-tech random phase offset so neighbours desync their circle/face windows.
        public float combatCyclePhase01 = -1f;

        // Targeting (cleanly brain-local; the LOS streak/grace fields stay on the bus - shell-touched/tech-wide):
        public float LastWeapCheck = 0f;        // next CheckEnemyAndAiming validation time
    }

    public static class ModifiedTechStateExt
    {
        /// <summary>The Modified form's per-tech brain state for this helper, lazily created in the shell's FormState
        /// slot on first access (and recreated if some other form left a different state there). Cleared on recycle
        /// via OnTechRecycle so pooled tech reuse starts fresh.</summary>
        public static ModifiedTechState MState(this TankAIHelper helper)
        {
            if (!(helper.FormState is ModifiedTechState ms))
            {
                ms = new ModifiedTechState();
                helper.FormState = ms;
            }
            return ms;
        }
    }
}
