using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    // Avoidance SHELL remnant (v2). The dodge-vector + ally-avoidance brain (GetDir/GetOtherDir/GetDirAuto +
    // AvoidAssist/AvoidAssistPrecise/AvoidAssistPrediction/AvoidAssistAirSpacing + posWeights) is DETACHED to the
    // Modified form (ModifiedAvoidance). What stays here is shell: the tank.control SINKS (ProcessControl/SteerControl/
    // UpdateVanillaAvoidence), the control-state utility FixControlReversal, the DodgeStrength tuning property, and the
    // GetFrameHeight terrain-altitude sensing (+ its CurHeight cache, reset by UpdateTechControl).
    public partial class TankAIHelper
    {
        internal void UpdateVanillaAvoidence()
        {
            tank.control.m_Movement.m_USE_AVOIDANCE = AvoidStuff;
        }
        public bool FixControlReversal(float Drive)
        {
            var thisControl = tank.control;
            return (thisControl.ActiveScheme == null || !thisControl.ActiveScheme.ReverseSteering) &&
                Drive < -0.01f &&
                Vector3.Dot(SafeVelocity, thisControl.Tech.rootBlockTrans.forward) < 0f;
        }
        internal void ProcessControl(Vector3 DriveVal, Vector3 TurnVal, Vector3 Throttle, bool props, bool jets)
        {
            tank.control.CollectMovementInput(DriveVal, TurnVal, Throttle, props, jets);
        }
        internal void SteerControl(Vector3 direction, float throttle)
        {
            tank.control.m_Movement.FaceDirection(tank, direction, throttle);
        }
        internal float DodgeStrength
        {
            get
            {
                if (UsingAirControls)
                    return AIGlobals.AirborneDodgeStrengthMultiplier * lastOperatorRange;
                return AIGlobals.DefaultDodgeStrengthMultiplier * lastOperatorRange;
            }
        }
        // v2: GetFrameHeight (terrain-cache sensing) is DETACHED to ModifiedAvoidance (it reads the form-owned
        // AIEPathMapper cache, so it belongs with the form). CurHeight stays here as the per-frame altitude cache
        // (reset to -500 by UpdateTechControl/ControlFrame; read+filled by the detached GetFrameHeight).
        internal float CurHeight = 0;
    }
}
