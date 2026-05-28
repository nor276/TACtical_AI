using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    // Unjam bus-state RESET (v2). The unjam FSM brain (AutoHandleObstruction / TryHandleObstruction /
    // RemoveObstruction / GetObstruction) is DETACHED to the Modified form (ModifiedUnjam). SettleDown stays on the
    // shell: it is a bus-state reset (clears Urgency / FrustrationMeter / Obst / IsTryingToUnjam / ForceSetBeam /
    // FIRE_ALL / BeamTimeoutClock + the Obsticle weapon state) and OPTIONALLY issues the SetCoreControlStop sink.
    // REVISED: stopCore now defaults to FALSE to match the original SettleDown (which NEVER stopped the drive core).
    // The refactor had defaulted it true and only RWheeled was patched to SettleDown(false); every other op (~110 bare
    // callers across all B*/R*/BGeneral/ModifiedOperations) was silently stopping the drive core every settle tick,
    // intermittently stalling combat movement (the "non-committal / understeer everywhere" regression). A genuine hard
    // stop now passes SettleDown(true) explicitly (e.g. AIContext arrival).
    public partial class TankAIHelper
    {
        public void SettleDown(bool stopCore = false)
        {
            UrgencyOverload = 0;
            Urgency = 0;
            FrustrationMeter = 0;
            Obst = null;
            IsTryingToUnjam = false;
            if (stopCore)
                SetCoreControlStop();
            ForceSetBeam = false;
            if (WeaponState == AIWeaponState.Obsticle)
                WeaponState = AIWeaponState.Normal;
            BeamTimeoutClock = 0;
            FIRE_ALL = false;
        }
    }
}
