using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    internal static class MultiTechUtils
    {
        internal static Vector3 HandleMultiTech(TankAIHelper help, Tank tank, ref EControlCoreSet core)
        {
            Vector3 targPos;
            if (help.lastEnemyGet)
                help.UpdateEnemyDistance(help.lastEnemyGet.tank.boundsCentreWorldNoCheck);
            else
                help.IgnoreEnemyDistance();
            core.DriveDest = EDriveDest.None;
            if (help.DediAI == AIType.MTTurret && help.lastEnemyGet)
            {
                core.DriveToFacingTowards();
                help.ThrottleState = AIThrottleState.PivotOnly;
                targPos = help.lastEnemyGet.tank.boundsCentreWorldNoCheck;
                help.AutoSpacing = 0;
            }
            else if (help.DediAI == AIType.MTMimic && help.MTMimicHostAvail)
            {
                if (help.theResource != null && help.theResource.tank)
                {
                    targPos = AIEPathing.GetDriveApproxAirDirector(help.theResource.tank, help, out bool IsMoving);
                    if (IsMoving)
                    {
                        help.AutoSpacing = 0;
                        core.TurningStrictness = ESteeringStrength.MaxSteering;
                        if (Vector3.Dot(tank.rootBlockTrans.forward, (targPos - tank.boundsCentreWorldNoCheck).normalized) >= 0)
                        {
                            core.DriveToFacingTowards();
                            help.ThrottleState = AIThrottleState.FullSpeed;
                            if (AIGlobals.ShowDebugFeedBack)
                                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 5, targPos - tank.boundsCentreWorldNoCheck, new Color(1, 1, 0, 1));
                        }
                        else
                        {
                            core.DriveToFacingBackwards();
                            help.ThrottleState = AIThrottleState.FullSpeed;
                            if (AIGlobals.ShowDebugFeedBack)
                                DebugExtUtilities.DrawDirIndicator(tank.gameObject, 5, targPos - tank.boundsCentreWorldNoCheck, new Color(1, 0, 1, 1));
                        }
                    }
                    else
                    {
                        help.AutoSpacing = 0f;
                        targPos = tank.boundsCentreWorldNoCheck;
                        core.Stop();
                        help.ThrottleState = AIThrottleState.PivotOnly;
                    }
                }
                else
                {
                    help.AutoSpacing = 0f;
                    targPos = tank.boundsCentreWorldNoCheck;
                    core.Stop();
                    help.ThrottleState = AIThrottleState.PivotOnly;
                }
            }
            else
            {
                core.DriveDir = EDriveFacing.Neutral;
                targPos = tank.boundsCentreWorldNoCheck;
                help.AutoSpacing = 0;
            }
            return targPos;
        }

    }
}
