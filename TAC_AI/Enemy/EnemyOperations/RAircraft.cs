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
        /*  Attack modes (historic doc; modern enum is EAttackMode in AIEnums.cs):
            Circle,     // D1 REVIVED: AC-130 broadside salvo. Gated on (LargeAircraft || BankOnly) + WeaponAimMod
            Grudge,     // Chase and dogfight whatever hit this aircraft last
            Coward,     // Avoid danger
            Bully,      // Attack other aircraft over ground structures.  If inverted, prioritize ground structures over aircraft
            Pesterer,   // Switch to the next closest possible target after attacking one aircraft.  Do not try to dodge and prioritize attack
            (Spyper)    // DELETED in D1; standoff sniper role belongs to Starship/Chopper, not fixed-wing
        */
        public static void AttackWoosh(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            BGeneral.ResetValues(helper, ref direct);
            helper.Attempt3DNavi = false;
            helper.AvoidStuff = true;

            //Singleton.Manager<ManTechs>.inst.
            if (tank.rbody.IsNull())
            {   // B4: remove aircraft AI from the world because it's outta player range.
                // Original code called Recycle() then fell through ~130 lines that read
                // tank.boundsCentreWorldNoCheck / helper.lastEnemyGet on a pool-returned
                // tank => NRE. Defer the recycle one frame (avoids mutating ManTechs
                // mid-tick / re-entrancy) and bail out of this tick immediately.
                DebugTAC_AI.LogWarnPlayerOncePerKey("RAircraft.AttackWoosh:NoRBody:" + tank.name,
                    KickStart.ModID + ": AttackWoosh on " + tank.name + " with null rbody — deferring Recycle.", null);
                InvokeHelper.InvokeSingle(() => tank.Recycle(), 0f);
                return;
            }

            // P13 BUG-2: lastEnemyGet and lastEnemyGet.tank are both guaranteed non-null here —
            // EnemyOperationsController.Execute now treats a null .tank as "no target" before
            // dispatching, so the old `lastEnemyGet.IsNotNull()` guard (which only validated the
            // Visible, never its .tank) is redundant.
            if (mind.CommanderMind == EnemyAttitude.Homing)
            {
                if ((helper.lastEnemyGet.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).magnitude > mind.MaxCombatRange)
                {
                    // P13 BUG-1: aircraft mend with the air idle, not the ground LollyGag.
                    // LollyGagAir applies the altitude/sea/ground snap (below, ~line 280) that
                    // RGeneral.LollyGag lacks; this matches the Airplane routing in DispatchNoTargetIdle.
                    bool isMending = LollyGagAir(helper, tank, mind, ref direct);
                    if (isMending)
                        return;
                }
            }
            // B7: null-target case centralized in EnemyOperationsController.Execute
            // (which dispatches Airplane/Chopper to LollyGagAir).
            RGeneral.Engadge(helper, tank, mind);

            float enemyExt = helper.lastEnemyGet.GetCheapBounds();
            float dist = helper.GetDistanceFromTask(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck, enemyExt);
            float range = AIGlobals.SpacingRangeAircraft;
            float spacing = helper.lastTechExtents + enemyExt;

            switch (mind.CommanderAttack)
            {
                case EAttackMode.Safety:
                    helper.AISetSettings.ObjectiveRange = spacing + range;   // B9: parallel to RWheeled/RChopper/RStarship writeback
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
                    // D1 revive: AC-130-style broadside salvo for LargeAircraft / BankOnly
                    // planes when WeaponAimMod is loaded. Allied counterpart at BAviator:103.
                    // Small fighters can't stably broadside — fall through to default.
                    {
                        var pilot = helper.MovementController as AIControllerAir;
                        var planeCore = pilot?.AICore as AI.Movement.AICores.AirplaneAICore;
                        bool canBroadside = KickStart.isWeaponAimModPresent && pilot != null
                            && (pilot.LargeAircraft || (planeCore != null && planeCore.BankOnly));
                        if (!canBroadside) goto default;
                        helper.AISetSettings.ObjectiveRange = spacing + range;   // B9
                        helper.AISetSettings.SideToThreat = true;
                        helper.Retreat = RGeneral.CanRetreat(helper, tank, mind);
                        direct.SetLastDest(helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                        if (dist < spacing + range)
                        {
                            RGeneral.MarkRetreating(helper);   // B10
                            direct.DriveAwayFacingPerp();
                        }
                        else
                        {
                            RGeneral.MarkAdvancing(helper);    // B10
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
                // Note: prior `EnemyAttack.Spyper` commented-arm DELETED (D1) — enum value no longer
                // exists; fixed-wing snipers belong under `EAttackMode.Ranged` for `Starship/Chopper`
                // archetypes. Aircraft fall through to `default` for ranged engagement.
                default:    // T2: Chase/Strong/Random/AutoSet share kinematics — target-selection differentiation lives in TankAIHelper.FindEnemy
                    helper.AISetSettings.ObjectiveRange = spacing + range;   // B9: parallel to RWheeled/RChopper/RStarship writeback
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
            mind.MinCombatRange = range;   // B6: publish per-tick combat range to mind for downstream weapon/range checks
        }

        public static bool LollyGagAir(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct, bool holdGround = false)
        {
            bool isRegenerating = false;
            if (mind.Hurt)// && helper.lastDestination.Approximately(tank.boundsCentreWorldNoCheck, 10)
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
                        //helper.AttemptedRepairs++;
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
                            helper.PendingDamageCheck = !RRepair.EnemyInstaRepair(tank, mind);
                            //helper.AttemptedRepairs++;
                        }
                        else
                        {
                            bool venPower = false;
                            if (mind.MainFaction == FactionSubTypes.VEN) venPower = true;
                            helper.PendingDamageCheck = RRepair.EnemyRepairStepper(helper, tank, mind, Super: venPower);
                            //helper.AttemptedRepairs++;
                        }
                        //DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is repairing");
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
                    case EnemyAttitude.Junker:  // Huddle up by blocks on the ground
                        FlutterAround(helper, tank, mind, ref direct);
                        break;
                    // P13 DEAD-3: removed empty OnRails / NPCBaseHost / Boss / Invader / Guardian /
                    // PartTurret / PartStatic / PartMimic cases — all were no-op `break`s identical
                    // to default. Aircraft have no idle behavior for these attitudes; they fall to
                    // default and then the unconditional altitude/sea/ground snap below.
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
            // REVIVED reroll: on expiry pick a new flutter heading + restart (was `== 1`, never hit by the
            // tick steps). Retuned via the actionPause seconds shim: 1000t = 2.0s flutter hold (was 30t = 0.06s). docs/21.
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
                // D1: prior commented AC-130 broadside arm DELETED — the live revival is in
                // RAircraft.AttackWoosh's Circle switch arm (uses AIControllerAir.AICore as
                // AirplaneAICore for the BankOnly check). EnemyDogfighting handles forward-arms only.
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
