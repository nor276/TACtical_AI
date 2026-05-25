using UnityEngine;

namespace TAC_AI.AI
{
    // DutyCycle service (partial-class split of TankAIHelper, Step 2). The turret-fraction combat circle/face
    // duty cycle. Owns its per-tech phase offset; reads TurretFraction (shared bus) + KickStart period.
    // No tank.control writes.
    public partial class TankAIHelper
    {
        private float combatCyclePhase01 = -1f;  // per-tech random phase offset so neighbours desync their circle/face windows

        // REVISED: new turret-fraction duty cycle. Returns true (circle) for ~TurretFraction of each KickStart.CombatFacingCyclePeriod, else false (face).
        // Replaces the old ActionPause>120 stop-and-shoot gate in the combat Circle bucket; read by RWheeled Circle + allied LandAICore.
        public bool CombatWantsCircleNow()
        {
            float frac = Mathf.Clamp01(TurretFraction);
            if (frac <= 0f)
                return false;
            if (frac >= 1f)
                return true;
            float period = KickStart.CombatFacingCyclePeriod;
            if (period <= 0.05f)
                return frac >= 0.5f;
            if (combatCyclePhase01 < 0f)
                combatCyclePhase01 = UnityEngine.Random.value;
            float phase = (Time.time / period + combatCyclePhase01) % 1f;
            return phase < frac;
        }
    }
}
