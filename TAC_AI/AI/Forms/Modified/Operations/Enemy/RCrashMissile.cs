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
        // REVISED (overview): rewritten from a Safety-style retreating bucket FSM into a pure straight-line ram.
        // Dropped the distance buckets, the Homing-mend branch, and the null-target/LollyGag returns (no-target now
        // handled upstream by the controller). Now: AvoidStuff off, 3D nav on (was off), FullMelee + FullBoost +
        // ForceSpeed driving straight ToLastDestination at the enemy instead of retreating FromLastDestination.
        public static void AttackCrash(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            BGeneral.ResetValues(helper, ref direct);
            helper.AvoidStuff = false;
            helper.Attempt3DNavi = true;

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
