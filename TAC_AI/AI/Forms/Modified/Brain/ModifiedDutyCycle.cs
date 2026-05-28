using UnityEngine;

namespace TAC_AI.AI
{
    /// <summary>
    /// Turret-fraction combat circle/face duty cycle - DETACHED from TankAIHelper into the Modified form (v2) as an
    /// extension method (so callers keep the same helper.CombatWantsCircleNow() syntax; no call-site changes). Returns
    /// true (circle) for ~TurretFraction of each KickStart.CombatFacingCyclePeriod, else false (face). Per-tech phase
    /// offset lives in ModifiedTechState (FormState). Read by RWheeled Circle + allied LandAICore.
    /// </summary>
    public static class ModifiedDutyCycle
    {
        public static bool CombatWantsCircleNow(this TankAIHelper helper)
        {
            float frac = Mathf.Clamp01(helper.TurretFraction);
            if (frac <= 0f)
                return false;
            if (frac >= 1f)
                return true;
            float period = KickStart.CombatFacingCyclePeriod;
            if (period <= 0.05f)
                return frac >= 0.5f;
            var st = helper.MState();
            if (st.combatCyclePhase01 < 0f)
                st.combatCyclePhase01 = UnityEngine.Random.value;
            float phase = (Time.time / period + st.combatCyclePhase01) % 1f;
            return phase < frac;
        }
    }
}
