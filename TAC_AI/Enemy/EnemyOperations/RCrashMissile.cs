using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TerraTechETCUtil;
using TAC_AI.AI;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI.Enemy.EnemyOperations
{
    internal static class RCrashMissile
    {
        public static void AttackCrash(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            BGeneral.ResetValues(helper, ref direct);
            helper.AvoidStuff = false;
            helper.Attempt3DNavi = true;

            // B7: null-target case centralized in EnemyOperationsController.Execute.
            RGeneral.Engadge(helper, tank, mind);

            helper.WantsToFight = true;
            helper.AISetSettings.FullMelee = true;
            helper.FullBoost = true;
            helper.ThrottleState = AIThrottleState.ForceSpeed;
            helper.DriveVar = 1;

            direct.DriveDest = EDriveDest.ToLastDestination;
            direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
        }
    }
}
