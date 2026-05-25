using UnityEngine;
using TAC_AI.World;

namespace TAC_AI.AI
{
    /// REVISED (overview): ResetValues now preserves a live player-RTS hold-fire command instead of always clearing FIRE_ALL;
    /// the AimDefend helper was removed; GetMineableScenery now branches on the success case (foundGoal) and records theResourceNode.
    internal static class BGeneral
    {
        public static void ResetValues(TankAIHelper helper, ref EControlOperatorSet direct)
        {
            helper.ThrottleState = AIThrottleState.FullSpeed;
            // REVISED: skip clearing FIRE_ALL when a local non-networked player RTS fire command is holding it on
            if (!(helper.AIAlign == AIAlignment.Player && !ManNetwork.IsNetworked &&
                  ManWorldRTS.inst != null && AIGlobals.PlayerClientFireCommand() &&
                  ManWorldRTS.inst.LocalPlayerTechsControlled.Contains(helper)))
                helper.FIRE_ALL = false;
            helper.FullBoost = false;
            helper.FirePROPS = false;
            helper.ForceSetBeam = false;
            helper.LightBoost = false;
            helper.DriveVar = 0;

            direct.FaceDest();
        }

        /// <summary>
        /// Defend like default
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        public static bool AidDefend(TankAIHelper helper, Tank tank)
        {
            // Determines the weapons actions and aiming of the AI
            if (helper.lastEnemyGet != null)
            {
                helper.TryRefreshEnemyAllied();
                //Fire even when retreating - the AI's life depends on this!
                helper.WantsToFight = true;
                return false;
            }
            else
            {
                helper.WantsToFight = false;
                helper.TryRefreshEnemyAllied();
                return helper.lastEnemyGet;
            }
        }

        // REVISED: removed the AimDefend helper (aim-gate by dot product / WeaponDelayClock); no callers remained

        public static void SelfDefend(TankAIHelper helper, Tank tank)
        {
            // Alternative of the above - does not aim at enemies while mining
            if (helper.Obst == null)
            {
                if (AidDefend(helper, tank))
                {
                    AIECore.RequestFocusFirePlayer(tank, helper.lastEnemyGet, RequestSeverity.ThinkMcFly);
                }
                else
                    helper.WantsToFight = false;
            }
            else
                helper.WantsToFight = true;
        }

        /// <summary>
        /// Stay focused on first target if the unit is order to focus-fire
        /// </summary>
        /// <param name="helper"></param>
        /// <param name="tank"></param>
        public static void RTSCombat(TankAIHelper helper, Tank tank)
        {
            // Determines the weapons actions and aiming of the AI
            if (helper.lastEnemyGet != null)
            {   // focus fire like Grudge
                helper.WantsToFight = true;
                if (!helper.lastEnemyGet.isActive)
                    helper.TryRefreshEnemyAllied();
            }
            else
            {
                helper.WantsToFight = false;
                helper.TryRefreshEnemyAllied();
            }
        }

        public static bool GetMineableScenery(TankAIHelper helper, Tank tank, bool includeTradingStations, ref float dist, ref bool hasMessaged, ref EControlOperatorSet direct)
        {
            helper.foundGoal = AIECore.FetchClosestResource(tank.rootBlockTrans.position, helper.JobSearchRange +
                AIGlobals.FindItemScanRangeExtension, helper.lastTechExtents * AIGlobals.WaterDepthTechHeightPercent,
                out var tmpRes);
            helper.theResource = tmpRes;
            // REVISED: now takes the "Found a Resource Node" path on success (foundGoal) instead of on failure, and stamps theResourceNode
            if (helper.foundGoal) helper.theResourceNode = tmpRes;
            if (helper.foundGoal)
            {
                hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Found a Resource Node...");
                direct.SetLastDest(helper.theResource.centrePosition);
                direct.STOP(helper);
                return true;
            }
            else
            { // We failed to find anything, so we just sit back and chill
                hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Scanning for resources...");
                StopByBase(helper, tank, includeTradingStations, ref dist, ref hasMessaged, ref direct);
                return false;
            }
        }

        public static bool GetBase(TankAIHelper helper, Tank tank, bool includeTradingStations, ref float dist, ref bool hasMessaged, ref EControlOperatorSet direct)
        {
            helper.foundBase = AIECore.FetchClosestChunkReceiver(tank.rootBlockTrans.position, helper.JobSearchRange +
                            AIGlobals.FindBaseScanRangeExtension, out helper.lastBasePos, out helper.theBase, tank.Team,
                            includeTradingStations);
            if (helper.foundBase && helper.theBase)
            {
                helper.lastBaseExtremes = helper.theBase.GetCheapBounds();
                direct.SetLastDest(helper.theBase.boundsCentreWorld);
                dist = (tank.boundsCentreWorldNoCheck - helper.lastDestinationCore).magnitude;
                return true;
            }
            else
            {
                hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Searching for nearest base!");
                helper.EstTopSped = 1;//slow down the clock to reduce lagg
                direct.STOP(helper);
                return false; // There's no base!
            }
        }
        public static void GetBaseIfNeeded(TankAIHelper helper, Tank tank, bool includeTradingStations, ref float dist, ref bool hasMessaged, ref EControlOperatorSet direct)
        {
            if (!helper.foundBase)
                GetBase(helper, tank, includeTradingStations, ref dist, ref hasMessaged, ref direct);
        }
        public static void StopByBase(TankAIHelper helper, Tank tank, bool includeTradingStations, ref float dist, ref bool hasMessaged, ref EControlOperatorSet direct)
        {
            GetBaseIfNeeded(helper, tank, includeTradingStations, ref dist, ref hasMessaged, ref direct);
            if (helper.theBase == null)
            {
                helper.foundBase = false;
                direct.STOP(helper);
                return; // There's no base!
            }
            direct.DriveDest = EDriveDest.ToBase;
            float girth = helper.lastBaseExtremes + helper.lastTechExtents;
            helper.theBase.GetHelperInsured().SlowForApproacher(helper);
            if (dist < girth + 3)
            {   // We are at the base, too close so give some space
                hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Giving room to base... |Tech is at " + tank.boundsCentreWorldNoCheck);
                direct.DriveAwayFacingTowards();
                helper.AvoidStuff = false;
                helper.ThrottleState = AIThrottleState.ForceSpeed;
                helper.DriveVar = -1;
                helper.SettleDown();
            }
            else if (dist < girth + 7)
            {   // We are at the base, stop moving and hold pos
                hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Arrived at a base and applying brakes. |Tech is at " + tank.boundsCentreWorldNoCheck);
                direct.DriveToFacingTowards();
                helper.AvoidStuff = false;
                helper.ThrottleState = AIThrottleState.Yield;
                helper.ThrottleState = AIThrottleState.PivotOnly;
                helper.SettleDown();
            }
            else
            {   // Go to the place
                hasMessaged = AIECore.AIMessage(tank, ref hasMessaged, tank.name + ":  Going to base! |Tech is at " + tank.boundsCentreWorldNoCheck);
                direct.DriveToFacingTowards();
                helper.AvoidStuff = true;
            }
        }
        public static void StopByPosition(TankAIHelper helper, Tank tank, Vector3 position, float girth, ref EControlOperatorSet direct)
        {
            Vector3 veloFlat = Vector3.zero;
            if ((bool)tank.rbody)   // So that drifting is minimized
            {
                veloFlat = helper.SafeVelocity;
                veloFlat.y = 0;
            }
            direct.SetLastDest(position);
            float dist = (direct.lastDestination - tank.boundsCentreWorldNoCheck + veloFlat).magnitude;
            direct.DriveDest = EDriveDest.ToLastDestination;
            if (dist < girth + 3)
            {   // We are at the place, too close so give some space
                direct.DriveAwayFacingTowards();
                helper.AvoidStuff = false;
                helper.ThrottleState = AIThrottleState.ForceSpeed;
                helper.DriveVar = -1;
                helper.SettleDown();
            }
            else if (dist < girth + 7)
            {   // We are at the place, stop moving and hold pos
                direct.DriveToFacingTowards();
                helper.AvoidStuff = false;
                helper.ThrottleState = AIThrottleState.Yield;
                helper.ThrottleState = AIThrottleState.PivotOnly;
                helper.SettleDown();
            }
            else
            {   // Go to the place
                direct.DriveToFacingTowards();
                helper.AvoidStuff = true;
            }
        }
    }
}
