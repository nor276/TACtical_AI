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
    internal static class RAircraft
    {
        // ENEMY CONTROLLERS
        /*
            Circle,     // Attack like the AC-130 Gunship, broadside while salvoing [BROKEN]
            Grudge,     // Chase and dogfight whatever hit this aircraft last
            Coward,     // Avoid danger
            Bully,      // Attack other aircraft over ground structures.  If inverted, prioritize ground structures over aircraft
            Pesterer,   // Switch to the next closest possible target after attacking one aircraft.  Do not try to dodge and prioritize attack
            Spyper,     // Take aim and fire at the best possible moment in our aiming
        */
        public static void AttackWoosh(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            BGeneral.ResetValues(helper, ref direct);
            helper.Attempt3DNavi = false;
            helper.AvoidStuff = true;

            if (tank.rbody.IsNull())
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey("RAircraft.AttackWoosh:NoRBody:" + tank.name,
                    KickStart.ModID + ": AttackWoosh on " + tank.name + " with null rbody — deferring Recycle.", null);
                InvokeHelper.InvokeSingle(() => tank.Recycle(), 0f);
                return;
            }

            if (mind.CommanderMind == EnemyAttitude.Homing)
            {
                if ((helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).magnitude > mind.MaxCombatRange)
                {
                    bool isMending = LollyGagAir(helper, tank, mind, ref direct);
                    if (isMending)
                        return;
                }
            }
            RGeneral.Engadge(helper, tank, mind);

            float enemyExt = helper.lastEnemyGet.GetCheapBounds();
            float dist = helper.GetDistanceFromTask(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck, enemyExt);
            float range = AIGlobals.SpacingRangeAircraft;
            float spacing = helper.lastTechExtents + enemyExt;

            switch (mind.CommanderAttack)
            {
                case EAttackMode.Safety:
                    helper.AISetSettings.ObjectiveRange = spacing + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = true;
                    direct.DriveDest = EDriveDest.FromLastDestination;
                    if (dist < spacing + (range / 4))
                    {
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        helper.FullBoost = true;
                        if (tank.wheelGrounded)
                        {
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                            else
                                helper.SettleDown();
                        }
                    }
                    else if (dist < spacing + range)
                    {
                        if (tank.wheelGrounded)
                        {
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                            else
                                helper.SettleDown();
                        }
                    }
                    break;
                case EAttackMode.Circle:
                    {
                        var pilot = helper.MovementController as AIControllerAir;
                        var planeCore = pilot?.AICore as AI.Movement.AICores.AirplaneAICore;
                        bool canBroadside = KickStart.isWeaponAimModPresent && pilot != null
                            && (pilot.LargeAircraft || (planeCore != null && planeCore.BankOnly));
                        if (!canBroadside) goto default;
                        helper.AISetSettings.ObjectiveRange = spacing + range;
                        helper.AISetSettings.SideToThreat = true;
                        helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        if (dist < spacing + range)
                        {
                            RGeneral.MarkRetreating(helper);
                            direct.DriveAwayFacingPerp();
                        }
                        else
                        {
                            RGeneral.MarkAdvancing(helper);
                            direct.DriveToFacingPerp();
                            if (dist > spacing + (range * 2))
                                helper.FullBoost = true;
                        }
                        if (tank.wheelGrounded)
                        {
                            if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                                helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                            else
                                helper.SettleDown();
                        }
                    }
                    break;
                default:    // Others
                    helper.AISetSettings.ObjectiveRange = spacing + range;
                    helper.AISetSettings.SideToThreat = false;
                    helper.Retreat = false;
                    direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    if (dist < spacing + range)
                    {
                        direct.DriveDest = EDriveDest.FromLastDestination;
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                    }
                    else if (dist < spacing + (range * 2))
                    {
                        direct.DriveDest = EDriveDest.FromLastDestination;
                    }
                    else if (dist < spacing + (range * 3))
                    {
                        direct.DriveDest = EDriveDest.ToLastDestination;
                    }
                    else
                    {
                        direct.DriveDest = EDriveDest.ToLastDestination;
                        helper.FullBoost = true;
                    }
                    if (tank.wheelGrounded)
                    {
                        if (!helper.IsTechMovingAbs(helper.EstTopSped / AIGlobals.EnemyAISpeedPanicDividend))
                            helper.TryHandleObstruction(!AIECore.Feedback, dist, true, true, ref direct);
                        else
                            helper.SettleDown();
                    }
                    break;
            }
            mind.MinCombatRange = range;
        }

        public static bool LollyGagAir(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct, bool holdGround = false)
        {
            bool isRegenerating = false;
            if (mind.Hurt)
            {
                var energy = tank.EnergyRegulator.Energy(TechEnergy.EnergyType.Electric);
                if (mind.CommanderSmarts >= EnemySmarts.Meh)
                {
                    if (energy.storageTotal > 500)
                    {
                        if (mind.SolarsAvail && tank.Anchors.NumPossibleAnchors > 0 && !tank.IsAnchored)
                        {
                            if (helper.CanAttemptAnchor)
                            {
                                helper.TryInsureAutoAnchor();
                            }
                            else
                            {   //Try to find new spot
                                FlutterAround(helper, tank, mind, ref direct);
                            }
                        }
                        if (energy.storageTotal - 100 < (energy.storageTotal - energy.spareCapacity))
                        {
                            mind.Hurt = false;
                        }
                    }
                    else
                    {
                        //Cannot repair block damage or recharge shields!
                        mind.Hurt = false;
                    }
                    if (tank.IsAnchored && !mind.StartedAnchored)
                    {
                        isRegenerating = true;
                    }
                }
                if (mind.CommanderSmarts == EnemySmarts.Smrt)
                {
                    if (helper.PendingDamageCheck) //&& helper.AttemptedRepairs < 3)
                    {
                        bool venPower = false;
                        if (mind.MainFaction == FactionSubTypes.VEN) venPower = true;
                        helper.PendingDamageCheck = RRepair.EnemyRepairStepper(helper, tank, mind, Super: venPower);
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is repairing");
                        return true;
                    }
                    else
                        mind.Hurt = false;
                }
                if (mind.CommanderSmarts >= EnemySmarts.IntAIligent)
                {
                    if (helper.PendingDamageCheck) //&& helper.AttemptedRepairs < 4)
                    {
                        if ((energy.storageTotal - energy.spareCapacity) / energy.storageTotal > 0.5)
                        {
                            //flex yee building speeds on them players
                            helper.PendingDamageCheck = !RRepair.EnemyInstaRepair(tank, mind);
                        }
                        else
                        {
                            bool venPower = false;
                            if (mind.MainFaction == FactionSubTypes.VEN) venPower = true;
                            helper.PendingDamageCheck = RRepair.EnemyRepairStepper(helper, tank, mind, Super: venPower);
                        }
                        return true;
                    }
                    else
                        mind.Hurt = false;
                }
            }
            else
            {
               // helper.anchorAttempts = 0;
            }

            //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is lollygagging   " + mind.CommanderMind.ToString());

            if (holdGround)
                direct.SetLastDest(mind.sceneStationaryPos);
            else
            {
                switch (mind.CommanderMind)
                {
                    case EnemyAttitude.Default: // do dumb stuff
                        FlutterAround(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Homing:  // Get nearest tech regardless of max combat range and attack them
                        RGeneral.HomingIdle(helper, tank, mind, ref direct);
                        break;
                    case EnemyAttitude.Miner:   // mine resources
                        RMiner.MineYerOwnBusiness(helper, tank, mind, ref direct);
                        break;
                    //The case below I still have to think of a reason for them to do the things
                    case EnemyAttitude.Junker:  // Huddle up by blocks on the ground
                        FlutterAround(helper, tank, mind, ref direct);
                        break;
                    default:
                        break;
                }
            }
            if (mind.EvilCommander == EnemyHandling.Naval)
                direct.SetLastDest(AIEPathing.OffsetToSea(helper.lastDestinationCore, tank, helper));
            else if (mind.EvilCommander == EnemyHandling.Starship)
                direct.SetLastDest(AIEPathing.OffsetFromGroundH(helper.lastDestinationCore, helper));
            else //Snap to ground
                direct.SetLastDest(AIEPathing.OffsetFromGround(helper.lastDestinationCore, helper, tank.blockBounds.size.y));
            return isRegenerating;
        }
        public static void FlutterAround(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            if (helper.ActionPause <= 0)
            {
                if (mind.GetComponent<AIControllerAir>() && UnityEngine.Random.Range(1, 10) < 6)
                {
                    var pilot = mind.GetComponent<AIControllerAir>();
                    direct.SetLastDest(pilot.Tank.boundsCentreWorldNoCheck + (helper.SafeVelocity * Time.fixedDeltaTime * KickStart.AIClockPeriod) + pilot.Tank.rootBlockTrans.forward);
                }
                else
                    direct.SetLastDest(GetRANDPos(tank));
                helper.actionPause = 1000;
            }
            direct.DriveDest = EDriveDest.ToLastDestination;
        }

        public static void EnemyDogfighting(TankAIHelper helper, Tank tank, EnemyMind mind)
        {   // Only accounts for forward weapons

            helper.WantsToFight = false;
            helper.TryRefreshEnemyEnemy(mind.InvertBullyPriority);

            if (helper.lastEnemyGet != null)
            {
                Vector3 aimTo = (helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).normalized;
                helper.Urgency += KickStart.AIClockPeriod / 25f;
                Vector3 foreDirect = tank.rootBlockTrans.InverseTransformDirection(aimTo);
                if ((foreDirect.z > 0.15f && foreDirect.x > -0.5f && foreDirect.x < 0.5f) || helper.Urgency >= 30)
                {
                    helper.WantsToFight = true;
                    helper.SettleDown();
                }
            }
            else
            {
                helper.Urgency = 0;
                helper.WantsToFight = false;
            }
        }

        public static Vector3 GetRANDPos(Tank tank)
        {
            float rangeRAND = 250;
            Vector3 final = tank.boundsCentreWorldNoCheck;

            final.x += UnityEngine.Random.Range(-rangeRAND, rangeRAND);
            final.y += UnityEngine.Random.Range(-rangeRAND, rangeRAND);
            final.z += UnityEngine.Random.Range(-rangeRAND, rangeRAND);

            return final;
        }
    }
}
