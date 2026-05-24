using UnityEngine;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;

namespace TAC_AI.AI.AlliedOperations
{
    internal static class BBase
    {
        // P13 TD-3: HoldSupport was byte-for-byte identical to HoldProtect. Collapsed to a thin
        // forwarder so the copy-paste twin can't silently drift. Its only caller (the Escort +
        // Stationary arm in AlliedOperationsController) was itself unreachable and has been removed;
        // the name is retained as an alias so any external reference still resolves.
        public static void HoldSupport(TankAIHelper helper, Tank tank, ref EControlOperatorSet direct)
        {
            HoldProtect(helper, tank, ref direct);
        }
        public static void HoldProtect(TankAIHelper helper, Tank tank, ref EControlOperatorSet direct)
        {
            //The Handler that tells the Tank (Base) what to do movement-wise
            helper.IsMultiTech = false;
            helper.Attempt3DNavi = true;
            helper.ChaseThreat = false;    //Prevent the auto-driveaaaa

            BGeneral.ResetValues(helper, ref direct);

            helper.SetDistanceFromTaskUnneeded();

            helper.ThrottleState = AIThrottleState.PivotOnly;
            helper.SettleDown();
            // P13 BUG-2: guard .tank, not just the Visible. `if (helper.lastEnemyGet)` only checks
            // that the Visible is alive (Visible has no IAIFollowable-aware bool), so a live Visible
            // whose Tank was torn down would NRE on the deref below. A null .tank falls to idle.
            if (helper.lastEnemyGet && helper.lastEnemyGet.tank)
            {
                direct.DriveDest = EDriveDest.ToLastDestination;
                direct.DriveDir = EDriveFacing.Forwards;
                direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
            }
            else
            {
                if (helper.ActionPause <= 0)
                {
                    helper.actionPause = UnityEngine.Random.Range(50, 300);
                    direct.DriveDest = EDriveDest.None;
                    direct.DriveDir = EDriveFacing.Neutral;
                }
                else if (helper.ActionPause < 160)
                {
                    direct.DriveDest = EDriveDest.None;
                    direct.DriveDir = EDriveFacing.Neutral;
                }
                else
                {
                    direct.DriveDest = EDriveDest.ToLastDestination;
                    direct.DriveDir = EDriveFacing.Forwards;
                }
            }
        }
    }
}
