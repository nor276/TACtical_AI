using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI.Movement;
using TAC_AI.Templates;
using TAC_AI.World;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI.Enemy
{
    /// <summary> RELIES ON EVERYTHING IN THE "AI" FOLDER TO FUNCTION PROPERLY!!!
    ///            [excluding the Designators in said folder]
    /// </summary>
    /// REVISED (overview): Mind-resolution extracted into EnsureEnemyMind (shared by BeEvil/BeEvilLight) with a latched one-shot
    /// GenerateEnemyAI regen and full null-guarding. Per-tick housekeeping + alignment switch extracted into CommonEvilOp, which
    /// both light and full ops now delegate to. GenerateEnemyAI restructured around RMission's new MissionSetupResult flags.
    /// Movement-controller swaps now go through RequestMovementControllerSwap. FinalInitialization dropped its old "Default enemy"
    /// early-out. Booster bias now thrust-weighted; control-block/scheme handling moved into SetFromScheme.
    public static class RCore
    {
        internal static FieldInfo charge = typeof(ModuleShieldGenerator).GetField("m_EnergyDeficit", BindingFlags.NonPublic | BindingFlags.Instance);
        internal static FieldInfo charge2 = typeof(ModuleShieldGenerator).GetField("m_State", BindingFlags.NonPublic | BindingFlags.Instance);
        internal static FieldInfo charge3 = typeof(ModuleShieldGenerator).GetField("m_Shield", BindingFlags.NonPublic | BindingFlags.Instance);
        internal static FieldInfo generator = typeof(ModuleEnergy).GetField("m_OutputConditions", BindingFlags.NonPublic | BindingFlags.Instance);

        // ----------------------------  New Enemy  ----------------------------
        public static void GenerateEnemyAI(this TankAIHelper helper, Tank tank)
        {
            // host-only: enemy AI setup subscribes Tank.DamageEvent + drives host-authoritative
            // mutations (DegradeRelations, focus-fire dispatch, network broadcasts). Running on
            // a client desyncs team relations and double-handles OnHit (BUG-056 / BUG-021).
            if (ManNetwork.IsNetworked && !ManNetwork.IsHost)
                return;
            DebugTAC_AI.BeginAICalculationTimer(tank);
            var newMind = tank.gameObject.GetComponent<EnemyMind>();
            if (!newMind)
            {
                newMind = tank.gameObject.AddComponent<EnemyMind>();
                newMind.Initiate();
            }
            helper.lastPlayer = null;

            // REVISED: movement-controller rebind now requested via RequestMovementControllerSwap (deferred/reasoned swap)
            helper.RequestMovementControllerSwap(TankAIHelper.MovementSwapReason.EnemyGenerate);

            newMind.sceneStationaryPos = tank.boundsCentreWorldNoCheck;
            newMind.Refresh();

            // REVISED: SetupBaseOrMissionAI now returns a MissionSetupResult flag set instead of a plain bool. The auto-config
            // chain (AutoSetIntelligence/GetOrCalculateEnemyHandling/SetSmartAIStats) is skipped when FullyConfigured; the
            // intelligence step alone is skipped when PartialMind. FinalInitialization now runs for all techs on a single path
            // (the former mission-tech early-return that duplicated FinalInitialization is gone).
            var missionResult = RMission.SetupBaseOrMissionAI(helper, tank, newMind);
            if (!(bool)tank)
                return;

            if ((missionResult & MissionSetupResult.FullyConfigured) == 0)
            {
                if ((missionResult & MissionSetupResult.PartialMind) == 0)
                    AutoSetIntelligence(newMind);
                GetOrCalculateEnemyHandling(tank, newMind);
                if (!SetSmartAIStats(tank, newMind))
                    RandomSetMindAttack(newMind, tank);
            }

            FinalInitialization(helper, newMind, tank);
            if (ManNetwork.IsNetworked && ManNetwork.IsHost)
            {
                NetworkHandler.TryBroadcastNewEnemyState(tank.netTech.netId.Value, newMind.CommanderSmarts);
            }
        }

        // ----------------------------  Setup  ----------------------------
        internal static void AutoSetIntelligence(this EnemyMind newMind)
        {
            if (!KickStart.enablePainMode)
                newMind.CommanderSmarts = EnemySmarts.Default; // Use defaults
            //add Smartness
            int randomNum = UnityEngine.Random.Range(KickStart.LowerDifficulty, KickStart.UpperDifficulty);
            if (randomNum < 35)
                newMind.CommanderSmarts = EnemySmarts.Default;
            else if (randomNum < 60)
                newMind.CommanderSmarts = EnemySmarts.Mild;
            else if (randomNum < 80)
                newMind.CommanderSmarts = EnemySmarts.Meh;
            else if (randomNum < 92)
                newMind.CommanderSmarts = EnemySmarts.Smrt;
            else
                newMind.CommanderSmarts = EnemySmarts.IntAIligent;
            if (randomNum > 92)
            {
                newMind.AllowRepairsOnFly = true;
                newMind.InvertBullyPriority = true;
            }
        }
        private static bool SetSmartAIStats(Tank tank, EnemyMind newMind)
        {
            bool fired = false;
            var BM = tank.blockman;

            // CHECK before burning processing
            if (AIGlobals.IsAttract)
            {
                return false;
            }
            else if (Singleton.Manager<ManGameMode>.inst.IsCurrent<ModeSumo>())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a sumo brawler");
                newMind.CommanderSmarts = EnemySmarts.Default;
                newMind.CommanderMind = EnemyAttitude.Homing;
                newMind.EvilCommander = EnemyHandling.Wheeled;
                newMind.CommanderAttack = EAttackMode.Chase;
                return true;
            }

            //Determine Attitude
            bool playerIsSmall = false;
            if (Singleton.playerTank)
                playerIsSmall = Singleton.playerTank.blockman.blockCount < BM.blockCount + 5;
            if (!playerIsSmall && BM.blockCount + BM.IterateBlockComponents<ModuleMeleeWeapon>().Count() <=
                BM.IterateBlockComponents<ModuleTechController>().Count())
            {   // Unarmed - Runner
                newMind.CommanderMind = EnemyAttitude.Default;
                newMind.CommanderAttack = EAttackMode.Safety;
            }
            else if (BM.blockCount > AIGlobals.BossTechSize && KickStart.MaxEnemyHQLimit > RLoadedBases.GetAllTeamsEnemyHQCount())
            {   // Boss
                newMind.InvertBullyPriority = true;
                newMind.CommanderMind = EnemyAttitude.Boss;
                newMind.CommanderAttack = EAttackMode.Strong;
            }
            else if (TAC_AI.AI.Forms.AIFormRegistry.Active?.EnemyHasArtillery(BM) ?? false)
            {   // Artillery
                newMind.CommanderMind = EnemyAttitude.Homing;
                newMind.CommanderAttack = EAttackMode.Ranged;
                fired = true;
            }
            else if (IsCollector(BM, out bool chunkHarvester) || newMind.MainFaction == FactionSubTypes.GC)
            {
                if (tank.IsPopulation && AIGlobals.EnemyBaseMakerChance >= UnityEngine.Random.Range(0, 100))
                {   // Base-Building Rival
                    newMind.CommanderMind = EnemyAttitude.NPCBaseHost;
                    newMind.CommanderAttack = (TAC_AI.AI.Forms.AIFormRegistry.Active?.SelectEnemyAttackStrat(tank, newMind) ?? EAttackMode.Chase);
                    newMind.InvertBullyPriority = true;
                }
                else
                {   // Miner
                    if (chunkHarvester)
                        newMind.CommanderMind = EnemyAttitude.Miner;
                    else
                        newMind.CommanderMind = EnemyAttitude.Junker;
                    newMind.CommanderAttack = (TAC_AI.AI.Forms.AIFormRegistry.Active?.SelectEnemyAttackStrat(tank, newMind) ?? EAttackMode.Chase);
                }
                fired = true;
            }
            else if (BM.IterateBlockComponents<ModuleWeapon>().Count() > AIGlobals.HomingWeaponCount)
            {   // Over-armed
                newMind.CommanderMind = EnemyAttitude.Homing;
                newMind.CommanderAttack = (TAC_AI.AI.Forms.AIFormRegistry.Active?.SelectEnemyAttackStrat(tank, newMind) ?? EAttackMode.Chase);
                fired = true;
            }
            else if (BM.IterateBlockComponents<ModuleWeapon>().Count() >= AIGlobals.DefenderWeaponCount)
            {   // Can defend
                newMind.CommanderMind = EnemyAttitude.Guardian;
                newMind.CommanderAttack = (TAC_AI.AI.Forms.AIFormRegistry.Active?.SelectEnemyAttackStrat(tank, newMind) ?? EAttackMode.Chase);
                fired = true;
            }
            else if (newMind.MainFaction == FactionSubTypes.VEN)
            {   // Ven
                newMind.CommanderAttack = EAttackMode.Circle;
                newMind.CommanderAttack = (TAC_AI.AI.Forms.AIFormRegistry.Active?.SelectEnemyAttackStrat(tank, newMind) ?? EAttackMode.Chase);
                fired = true;
            }
            else if (newMind.MainFaction == FactionSubTypes.HE)
            {   // Assault
                newMind.CommanderMind = EnemyAttitude.Homing;
                newMind.CommanderAttack = (TAC_AI.AI.Forms.AIFormRegistry.Active?.SelectEnemyAttackStrat(tank, newMind) ?? EAttackMode.Chase);
                fired = true;
            }
            if (BM.blockCount >= AIGlobals.LethalTechSize)
            {   // DEATH TO ALL
                if (!SpecialAISpawner.Eradicators.Contains(tank))
                    SpecialAISpawner.Eradicators.Add(tank);
            }
            return fired;
        }
        private static List<FanJet> jetGetCache = new List<FanJet>();
        private static List<BoosterJet> boosterGetCache = new List<BoosterJet>();
        internal static void GetOrCalculateEnemyHandling(Tank tank, EnemyMind newMind, bool ForceAllBubblesUp = false)
        {
            if (RawTechLoader.TryGetRawTechFromName(tank.name, out RawTech RTT))
            {
                var BM = tank.blockman;
                if (RTT.purposes.Contains(BasePurpose.NotStationary))
                {
                    switch (RTT.terrain)
                    {
                        case BaseTerrain.Sea:
                            newMind.EvilCommander = EnemyHandling.Naval;
                            break;
                        case BaseTerrain.Air:
                            newMind.EvilCommander = EnemyHandling.Airplane;
                            break;
                        case BaseTerrain.Chopper:
                            newMind.EvilCommander = EnemyHandling.Chopper;
                            break;
                        case BaseTerrain.Space:
                            newMind.EvilCommander = EnemyHandling.Starship;
                            break;
                        default:
                            newMind.EvilCommander = EnemyHandling.Wheeled;
                            break;
                    }
                }
                else
                {
                    newMind.StartedAnchored = true;
                    newMind.EvilCommander = EnemyHandling.Stationary;
                    newMind.CommanderBolts = EnemyBolts.AtFull;
                }
                if (newMind.MainFaction == FactionSubTypes.GC || (BM.IterateBlockComponents<ModuleMeleeWeapon>().Count() +
                    (BM.IterateBlockComponents<ModuleWeaponTeslaCoil>().Count() * 25) > BM.IterateBlockComponents<ModuleWeaponGun>().Count()))
                    newMind.LikelyMelee = true;
                if (RTT.purposes.Contains(BasePurpose.NANI) || RTT.blockCount >= RawTechUtil.FrameImpactingTechBlockCount)
                {   // DEATH TO ALL
                    if (!SpecialAISpawner.Eradicators.Contains(tank))
                        SpecialAISpawner.Eradicators.Add(tank);
                }
                return;
            }
            BlockSetEnemyHandling(tank, newMind, ForceAllBubblesUp);
            // REVISED: block-based handling is now overridden by the active control scheme via SetFromScheme (added)
            SetFromScheme(newMind, tank);
        }
        // REVISED: now internal (was private) so other setup paths can reuse the block-scan locomotion classifier
        internal static void BlockSetEnemyHandling(Tank tank, EnemyMind newMind, bool ForceAllBubblesUp)
        {
            // We have to do it this way since modded blocks don't work well with the defaults
            var BM = tank.blockman;

            bool canFloat = false;
            bool isFlying = false;
            bool isFlyingDirectionForwards = true;
            bool isOmniEngine = false;
            Vector3 biasDirection = Vector3.zero;
            Vector3 boostBiasDirection = Vector3.zero;

            int FoilCount = 0;
            int MovingFoilCount = 0;
            int modControlCount = 0;
            int modBoostCount = 0;
            int modHoverCount = 0;
            int modGyroCount = 0;
            int modWheelCount = 0;
            int modAGCount = 0;
            int modGunCount = 0;
            int modDrillCount = 0;
            int modTeslaCount = 0;

            foreach (TankBlock bloc in BM.IterateBlocks())
            {
                BlockDetails BD = new BlockDetails(bloc.BlockType);
                if (BD.DoesMovement)
                {
                    if (BD.HasBoosters || BD.HasFans)
                    {
                        var booster = bloc.GetComponent<ModuleBooster>();
                        if (booster)
                        {
                            modBoostCount++;
                            //Get the slowest spooling one
                            booster.transform.GetComponentsInChildren(jetGetCache);
                            foreach (FanJet jet in jetGetCache)
                            {
#if DEV
                                if (jet == null)
                                    throw new Exception("BlockDetails SAID there was a FanJet in this block but that was incorrect!" +
                                        " Somehow GetComponentsInChildren returned a NULL entry.  We should not be checking for this!");
#endif
                                if ((float)RawTechBase.spinDat.GetValue(jet) <= 10)
                                {
                                    biasDirection -= tank.rootBlockTrans.InverseTransformDirection(jet.EffectorForward) *
                                        (float)RawTechBase.thrustRate.GetValue(jet);
                                }
                            }
                            jetGetCache.Clear();
                            booster.transform.GetComponentsInChildren(boosterGetCache);
                            foreach (BoosterJet boost in boosterGetCache)
                            {
#if DEV
                                if (boost == null)
                                    throw new Exception("BlockDetails SAID there was a BoosterJet in this block but that was incorrect!" +
                                        " Somehow GetComponentsInChildren returned a NULL entry.  We should not be checking for this!");
#endif
                                // REVISED: boost bias is now weighted by each booster's thrust force (was unweighted unit vectors),
                                // so stronger boosters dominate the locomotion-direction estimate
                                float boostForce = (float)AIGlobals.BoostForceField.GetValue(boost);
                                boostBiasDirection -= tank.rootBlockTrans.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection)) * boostForce;
                            }
                            boosterGetCache.Clear();
                        }
                    }
                    if (BD.HasWings)
                    {
                        ModuleWing.Aerofoil[] foils = bloc.GetComponent<ModuleWing>().m_Aerofoils;
                        FoilCount += foils.Length;
                        foreach (ModuleWing.Aerofoil Afoil in foils)
                        {
                            if (Afoil.flapAngleRangeActual > 0 && Afoil.flapTurnSpeed > 0)
                                MovingFoilCount++;
                        }
                    }

                    if (BD.HasHovers || BD.HasFloaters)
                        modHoverCount++;
                    if (BD.HasFloaters)
                        isFlying = true;
                    if (BD.IsGyro)
                        modGyroCount++;
                    if (BD.HasWheels)
                        modWheelCount++;
                    else if (BD.IsOmniDirectional)
                        isOmniEngine = true;
                    if (BD.FloatsOnWater)
                        canFloat = true;
                    if (BD.HasAntiGravity)
                        modAGCount++;
                }
                if (ForceAllBubblesUp && BD.IsBubble)
                {
                    var buubles = bloc.GetComponent<ModuleShieldGenerator>();
#if DEV
                    if (buubles == null)
                        throw new Exception("BlockDetails SAID there was a ModuleShieldGenerator in this block but that was incorrect!  We should not be checking for this!");
#endif
                    charge.SetValue(buubles, 0);
                    charge2.SetValue(buubles, 2);
                    BubbleShield shield = (BubbleShield)charge3.GetValue(buubles);
                    shield.SetTargetScale(buubles.m_Radius);
                }

                if (BD.IsGenerator)
                {
#if DEV
#endif
                    var modE = bloc.GetComponent<ModuleEnergy>();
                    if (modE)
                    {
                        ModuleEnergy.OutputConditionFlags flagG = (ModuleEnergy.OutputConditionFlags)generator.GetValue(bloc.GetComponent<ModuleEnergy>());
                        if (flagG.HasFlag(ModuleEnergy.OutputConditionFlags.Anchored) && flagG.HasFlag(ModuleEnergy.OutputConditionFlags.DayTime))
                            newMind.SolarsAvail = true;
                    }
                }

                if (bloc.GetComponent<ModulePacemaker>())
                    tank.Holders.SetHeartbeatSpeed(TechHolders.HeartbeatSpeed.Fast);

                if (BD.IsCab)
                {
                    modControlCount++;
                }
                else if (BD.IsWeapon)
                {
                    if (bloc.GetComponent<ModuleWeaponGun>())
                        modGunCount++;
                    if (BD.IsMelee)
                        modDrillCount++;
                    if (bloc.GetComponent<ModuleWeaponTeslaCoil>())
                        modTeslaCount++;
                }
                try
                {
                    CheckAndHandleControlBlocks(newMind, bloc);
                }
                // REVISED: control-block failures are now logged (was a silent catch{}) so misbehaving blocks surface
                catch (Exception eCtrl) { DebugTAC_AI.LogWarnPlayerOnce("[TAC_AI:catch:Enemy] CheckAndHandleControlBlocks failed", eCtrl); }
            }

            boostBiasDirection.Normalize();
            biasDirection.Normalize();

            if (biasDirection == Vector3.zero && boostBiasDirection != Vector3.zero)
            {
                isFlying = true;
                if (boostBiasDirection.y > 0.6)
                    isFlyingDirectionForwards = false;
            }
            else if (biasDirection != Vector3.zero)
            {
                isFlying = true;
                if (biasDirection.y > 0.6)
                    isFlyingDirectionForwards = false;
            }
            DebugTAC_AI.Info(KickStart.ModID + ": Tech " + tank.name + " Has bias of" + biasDirection + " and a boost bias of" + boostBiasDirection);

            DebugTAC_AI.Info(KickStart.ModID + ": Tech " + tank.name + "  Has block count " + BM.blockCount + "  | " + modBoostCount + " | " + modAGCount);

            if (tank.IsAnchored)
            {
                newMind.StartedAnchored = true;
                newMind.EvilCommander = EnemyHandling.Stationary;
                newMind.CommanderBolts = EnemyBolts.AtFull;
            }
            else if (BM.blockCount == 1)
            {
                if (isOmniEngine)
                    newMind.EvilCommander = EnemyHandling.Starship;
                else if (isFlyingDirectionForwards && MovingFoilCount > 3)
                    newMind.EvilCommander = EnemyHandling.Airplane;
                else if (!isFlyingDirectionForwards)
                    newMind.EvilCommander = EnemyHandling.Chopper;
                else if (tank.IsAnchored)
                    newMind.EvilCommander = EnemyHandling.Stationary;
                else if (canFloat && modWheelCount == 0)
                    newMind.EvilCommander = EnemyHandling.Naval;
                else
                    newMind.EvilCommander = EnemyHandling.Wheeled;
            }
            else if (isOmniEngine && BM.blockCount == 1)
            {
                newMind.EvilCommander = EnemyHandling.Wheeled;
            }
            else if (MovingFoilCount > 4 && isFlying && isFlyingDirectionForwards)
            {
                if ((modHoverCount > 2 && modWheelCount > 2) || modAGCount > 0)
                {
                    newMind.EvilCommander = EnemyHandling.Starship;
                }
                else
                    newMind.EvilCommander = EnemyHandling.Airplane;
            }
            else if ((modGyroCount > 0 || modWheelCount < modBoostCount) && isFlying && !isFlyingDirectionForwards)
            {
                if ((modHoverCount > 2 && modWheelCount > 2) || modAGCount > 0)
                {
                    newMind.EvilCommander = EnemyHandling.Starship;
                }
                else
                    newMind.EvilCommander = EnemyHandling.Chopper;
            }
            else if (modBoostCount > 2 && modAGCount > 0)
            {
                newMind.EvilCommander = EnemyHandling.Starship;
            }
            else if (KickStart.isWaterModPresent && modGyroCount > 0 && modBoostCount > 0 && modWheelCount < 4 + FoilCount)
            {
                newMind.EvilCommander = EnemyHandling.Naval;
            }
            else if ((modBoostCount > 2 && modHoverCount > 2) || isOmniEngine)
            {
                newMind.EvilCommander = EnemyHandling.Starship;
            }
            else if (modGunCount < 1 && modDrillCount < 1 && modBoostCount > 0)
            {
                newMind.EvilCommander = EnemyHandling.SuicideMissile;
            }
            // REVISED: explicit hover-without-wheels branch added - currently resolves to Wheeled (same as the else fallthrough),
            // carving out the case as a named hook for future hover-specific handling
            else if (modHoverCount > 2 && modWheelCount == 0)
            {
                newMind.EvilCommander = EnemyHandling.Wheeled;
            }
            else
                newMind.EvilCommander = EnemyHandling.Wheeled;

            if (modDrillCount + (modTeslaCount * 25) > modGunCount|| newMind.MainFaction == FactionSubTypes.GC)
                newMind.LikelyMelee = true;

            if (BM.blockCount >= RawTechUtil.FrameImpactingTechBlockCount)
            {   // DEATH TO ALL
                if (!SpecialAISpawner.Eradicators.Contains(tank))
                    SpecialAISpawner.Eradicators.Add(tank);
            }
        }
        internal static void RandomSetMindAttack(EnemyMind newMind, Tank tank)
        {
            //add Attitude
            if (IsUnarmedAndCanRunAway(tank))
            {
                newMind.CommanderAttack = EAttackMode.Safety;
                newMind.CommanderMind = EnemyAttitude.Default;
                newMind.CommanderAlignment = EnemyStanding.SubNeutral;
                DebugTAC_AI.Log("Tech " + tank.name + " is unarmed, assuming SubNeutral");
            }
            else
            {
                // REVISED: roll widened to 0..9 inclusive (was 0..8) so the case 8/9 (Miner/Default) arm can actually be selected
                int randomNum2 = UnityEngine.Random.Range(0, 10);
                switch (randomNum2)
                {
                    case 0:
                    case 1:
                    case 2:
                    case 3:
                        newMind.CommanderMind = EnemyAttitude.Default;
                        break;
                    case 4:
                    case 5:
                        newMind.CommanderMind = EnemyAttitude.Junker;
                        break;
                    case 6:
                    case 7:
                        newMind.CommanderMind = EnemyAttitude.Homing;
                        break;
                    case 8:
                    case 9:
                        if (tank.blockman.IterateBlockComponents<ModuleItemHolder>().Count() > 0 && RawTechLoader.CanBeMiner(newMind))
                            newMind.CommanderMind = EnemyAttitude.Miner;
                        else
                            newMind.CommanderMind = EnemyAttitude.Default;
                        break;
                }
            }
            //add Attack
            newMind.CommanderAttack = (TAC_AI.AI.Forms.AIFormRegistry.Active?.SelectEnemyAttackStrat(tank, newMind) ?? EAttackMode.Chase);
        }

        // ----------------------------  Operations  ----------------------------
        internal static void ScarePlayer(EnemyMind mind, TankAIHelper helper, Tank tank)
        {
            try
            {
                if (ManBaseTeams.IsEnemy(ManPlayer.inst.PlayerTeam, tank.Team))
                {
                    Tank target = helper.lastEnemyGet?.tank;
                    if (target != null)
                    {
                        bool targetIsPlayer = target.PlayerFocused;
                        bool weInDanger = false;
                        if (targetIsPlayer && Singleton.playerTank)
                        {
                            if (KickStart.WarnOnEnemyLock && Mode<ModeMain>.inst != null)
                            {
                                switch (mind.EvilCommander)
                                {
                                    case EnemyHandling.Chopper:
                                    case EnemyHandling.Airplane:
                                        AIWiki.hintAirDanger.Show();
                                        break;
                                    case EnemyHandling.Starship:
                                        AIWiki.hintSpaceDanger.Show();
                                        break;
                                    case EnemyHandling.SuicideMissile:
                                        AIWiki.hintMissileDanger.Show();
                                        break;
                                    default:
                                        break;
                                }
                            }
                            weInDanger = true;
                        }
                        else if (ManWorldRTS.PlayerIsInRTS)
                        {
                            if (target.Team == ManPlayer.inst.PlayerTeam)
                                weInDanger = true;  // Player unit under attack
                        }

                        if (weInDanger)
                        {
                            if (targetIsPlayer && Mode<ModeMain>.inst != null) // Updates the campaign emergency snapshot handler
                                Mode<ModeMain>.inst.SetPlayerInDanger(true, true);

                            ManMusic.inst.SetDanger(ManMusic.DangerContext.Circumstance.Enemy, tank, target);
                        }
                        else if (target != Singleton.playerTank && target.IsEnemy(tank.Team) && target.netTech?.NetPlayer != null)
                        {   // Send it to the non-host client that they are under attack
                            ManMusic.inst.SetDangerClient(ManMusic.DangerContext.Circumstance.Enemy, tank, target);
                        }
                    }
                }
            }
            catch (Exception e) { DebugTAC_AI.Log("ScarePlayer(): error - " + e); }
        }

        // REVISED: new shared Mind-resolution helper extracted from the old BeEvil/BeEvilLight bodies. Null-guards the tech,
        // returns the live EnemyMind when present, else attempts a single GenerateEnemyAI regen guarded by helper.beEvilRegenAttempted
        // (latched so a failed regen does not retry every tick - the tech goes inert with a once-per-key warning instead).
        private static EnemyMind EnsureEnemyMind(TankAIHelper helper, Tank tank)
        {
            if (!tank || !tank.visible || tank.blockman == null || helper.MovementController == null)
                return null;

            var mind = helper.MovementController.EnemyMind;
            if (mind.IsNotNull())
            {
                helper.beEvilRegenAttempted = false;
                return mind;
            }
            mind = tank.GetComponent<EnemyMind>();
            if (mind == null)
            {
                if (helper.beEvilRegenAttempted)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "EnsureEnemyMind:latched:" + tank.name,
                        KickStart.ModID + ": " + tank.name +
                        " ticking without EnemyMind (one-shot regen previously failed; tech is inert).", null);
                    return null;
                }
                helper.beEvilRegenAttempted = true;
                DebugTAC_AI.Assert(KickStart.ModID + ": EnsureEnemyMind() on " + tank.name +
                    " has NO EnemyMind. Attempting one-shot regen via GenerateEnemyAI.");
                try { GenerateEnemyAI(helper, tank); }
                catch (Exception e)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": EnsureEnemyMind regen FAILED for " + tank.name + ": " + e);
                    return null;
                }
                mind = tank.GetComponent<EnemyMind>();
                if (mind == null)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "EnsureEnemyMind:postRegen:" + tank.name,
                        KickStart.ModID + ": GenerateEnemyAI did not attach EnemyMind to " + tank.name, null);
                    return null;
                }
            }
            helper.beEvilRegenAttempted = false;
            DebugTAC_AI.Log(KickStart.ModID + ": Updating MovementController for " + tank.name);
            helper.MovementController.UpdateEnemyMind(mind);
            return mind;
        }
        // REVISED: both gates now resolve the Mind via EnsureEnemyMind and silently return when it is null
        // (no ScarePlayer on a mindless tech), instead of the old inline lookup/regen each performed separately.
        internal static void BeEvil(TankAIHelper helper, Tank tank)
        {
            var Mind = EnsureEnemyMind(helper, tank);
            if (Mind == null) return;
            RunEvilOperations(Mind, helper, tank);
            ScarePlayer(Mind, helper, tank);
        }
        internal static void BeEvilLight(TankAIHelper helper, Tank tank)
        {
            var Mind = EnsureEnemyMind(helper, tank);
            if (Mind == null) return;
            RunLightEvilOp(Mind, helper, tank);
            ScarePlayer(Mind, helper, tank);
        }
        // REVISED: shared per-tick housekeeping extracted here (anchor-force, bolts, die-check, on-tile repair stepper,
        // Provoked decay with EndAggro on expiry, then the CommanderAlignment stance switch). Both the light and full
        // ops delegate to this; the light path stops after it, the full path continues with dispatch + retreat.
        private static void CommonEvilOp(EnemyMind mind, TankAIHelper helper, Tank tank)
        {
            if (mind.StartedAnchored)
            {
                if (mind.EvilCommander != EnemyHandling.Stationary)
                    mind.EvilCommander = EnemyHandling.Stationary;// NO MOVE FOOL
                if (!tank.IsAnchored && !mind.Hurt)
                {
                    if (helper.CanAttemptAnchor)
                    {
                        helper.AnchorIgnoreChecks();
                    }
                }
            }

            TAC_AI.AI.Forms.AIFormRegistry.Active?.RunEnemyBolts(helper, tank, mind);
            TestShouldCommitDie(tank, mind);
            if (ManWorld.inst.CheckIsTileAtPositionLoaded(tank.boundsCentreWorldNoCheck))
            {
                if ((mind.AllowRepairsOnFly || mind.StartedAnchored || mind.BuildAssist) && helper.TechMemor != null)
                {
                    bool venPower = false;
                    if (mind.MainFaction == FactionSubTypes.VEN) venPower = true;
                    TAC_AI.AI.Forms.AIFormRegistry.Active?.RunEnemyRepairStep(helper, tank, mind, venPower);// longer while fighting
                }
            }
            if (mind.AIControl.Provoked <= 0)
            {
                if (helper.lastEnemyGet && helper.lastEnemyGet.isActive)
                {
                    if (!mind.InMaxCombatRangeOfTarget())
                    {
                        mind.EndAggro();
                    }
                }
                else
                    mind.EndAggro();
                mind.AIControl.Provoked = 0;
            }
            else
                mind.AIControl.Provoked -= KickStart.AIClockPeriod;

            // Attack handling
            switch (mind.CommanderAlignment)
            {
                case EnemyStanding.Enemy:
                    BeHostile(mind, helper, tank);
                    break;
                case EnemyStanding.SubNeutral:
                    BeSubNeutral(mind, helper, tank);
                    break;
                case EnemyStanding.Neutral:
                    BeNeutral(mind, helper, tank);
                    break;
                case EnemyStanding.Friendly:
                    BeFriendly(mind, helper, tank);
                    break;
            }
        }

        // REVISED: light path is now just the shared housekeeping (no dispatch), where the old RunLightEvilOp duplicated
        // the anchor-force/repair/stance logic inline
        private static void RunLightEvilOp(EnemyMind mind, TankAIHelper helper, Tank tank)
        {
            CommonEvilOp(mind, helper, tank);
        }
        private static void RunEvilOperations(EnemyMind mind, TankAIHelper helper, Tank tank)
        {
            CommonEvilOp(mind, helper, tank);

            if (helper.RTSControlled && !helper.IsMultiTech)
            {
                // REVISED: RTS detour now emits a once-per-key warning when an enemy tech bypasses EnemyOpsController.Execute
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "EnemyRTSDetour:" + tank.name,
                    "AI " + tank.name + ": RTSControlled enemy tech routed through RunRTSNaviEnemy instead of EnemyOpsController.Execute.", null);
                helper.RunRTSNaviEnemy(mind);
            }
            else
                // v2 isolation: inlined the deleted EnemyOperationsController pass-through (it only called ProfileRunner).
                TAC_AI.AI.Engine.ProfileRunner.RunEnemy(new TAC_AI.AI.Engine.AIContext(mind.AIControl, mind), mind);
            //CommanderMind is handled in each seperate class
            if (ManBaseTeams.IsBaseTeamDynamic(tank.Team))
            {
                EControlOperatorSet direct = helper.GetDirectedControl();
                if (ProcessIfRetreat(helper, tank, mind, ref direct))
                {
                    helper.SetDirectedControl(direct);
                    return;
                }
                helper.SetDirectedControl(direct);
            }
        }

        public static Vector3 GetTargetCoordinates(TankAIHelper helper, Visible target, EnemyMind mind)
        {
            if (mind.CommanderSmarts >= EnemySmarts.Smrt)   // Rough Target leading
            {
                return helper.RoughPredictTarget(target.tank);
            }
            else
                return target.tank.boundsCentreWorldNoCheck;
        }
        // v2 isolation: CombatChecking (the enemy firing dispatch) moved to ModifiedForm.RunEnemyCombatChecking - it's
        // form combat policy. BeHostile/BeSubNeutral now call AIFormRegistry.Active.RunEnemyCombatChecking.
        private static bool ProcessIfRetreat(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            if (helper.Retreat)
            {
                return GetRetreatLocation(helper, tank, mind, ref direct);
            }
            return false;
        }
        public static bool GetRetreatLocation(TankAIHelper helper, Tank tank, EnemyMind mind, ref EControlOperatorSet direct)
        {
            TeamBasePointer funds = RLoadedBases.GetTeamHQ(tank.Team);
            if (funds.IsValid())
            {
                BGeneral.ResetValues(helper, ref direct);
                if (funds.tank?.visible != null)
                    BGeneral.StopByPosition(helper, tank, funds.WorldPos.ScenePosition,
                        funds.tank.visible.GetCheapBounds(), ref direct);
                else // It's not loaded, we space based on a fallback scale assumption
                    BGeneral.StopByPosition(helper, tank, funds.WorldPos.ScenePosition, 32, ref direct);
                return true;
            }
            else
            {
                if (helper.lastEnemy?.tank)
                {
                    if (mind.CanCallRetreat)
                    {
                        BGeneral.ResetValues(helper, ref direct);
                        Vector3 runVec = (tank.boundsCentreWorldNoCheck - helper.lastEnemy.tank.boundsCentreWorldNoCheck).normalized;
                        direct.SetLastDest(tank.boundsCentreWorldNoCheck + (runVec * 150));
                        direct.DriveToFacingTowards();
                        return true;
                    }
                }
                else
                {
                    NP_Presence_Automatic EP = ManEnemyWorld.GetTeam(tank.Team);
                    if (EP != null)
                    {
                        NP_BaseUnit EBU = UnloadedBases.RefreshTeamMainBaseIfAnyPossible(EP);
                        if (EBU != null)
                        {
                            BGeneral.ResetValues(helper, ref direct);
                            direct.DriveToFacingTowards();
                            direct.SetLastDest(EBU.PosScene);
                            // yes we will drive off-scene to retreat home
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static void BeHostile(EnemyMind mind, TankAIHelper helper, Tank tank)
        {
            TAC_AI.AI.Forms.AIFormRegistry.Active?.RunEnemyCombatChecking(helper, tank, mind);
        }
        private static void BeSubNeutral(EnemyMind mind, TankAIHelper helper, Tank tank)
        {
            if (helper.Provoked > 0)
                TAC_AI.AI.Forms.AIFormRegistry.Active?.RunEnemyCombatChecking(helper, tank, mind);
            else if (AIGlobals.BaseSubNeutralsCuriousFollow)
                TAC_AI.AI.Forms.AIFormRegistry.Active?.RunEnemyMonitor(helper, tank, mind);
        }
        private static void BeNeutral(EnemyMind mind, TankAIHelper helper, Tank tank)
        {
            if (mind.Hurt && helper.PendingDamageCheck && mind.AIControl.Provoked > 0)
            {   // If we were hit & lost blocks, then we fight back the attacker
                if (helper.lastEnemyGet?.tank)
                {
                    int teamAttacker = helper.lastEnemyGet.tank.Team;
                    if (ManBaseTeams.IsBaseTeamDynamic(teamAttacker) || teamAttacker == ManPlayer.inst.PlayerTeam)
                    {
                        // host-only mutation: ManBaseTeams replicates host->client only; a
                        // client running this would desync team relations (BUG-021).
                        if (ManNetwork.IsHost
                            && ManBaseTeams.TryGetBaseTeamDynamicOnly(tank.Team, out var ETD))
                            ETD.DegradeRelations(teamAttacker);
                        helper.EndPursuit();
                        return;
                    }
                }
            }
        }
        private static void BeFriendly(EnemyMind mind, TankAIHelper helper, Tank tank)
        {
            // Can't really damage an ally
            TAC_AI.AI.Forms.AIFormRegistry.Active?.RunEnemyCombatChecking(helper, tank, mind);
        }

        // ----------------------------  Checks  ----------------------------
        public static bool IsCollector(BlockManager BM, out bool chunkHarvester)
        {
            ModuleItemHolder.AcceptFlags flag = ModuleItemHolder.AcceptFlags.Chunks;
            chunkHarvester = false;
            bool found = false;
            foreach (var item in BM.IterateBlockComponents<ModuleItemHolder>())
            {
                if (item.IsFlag(ModuleItemHolder.Flags.Collector))
                {
                    if ((item.Acceptance & flag) > 0)
                        chunkHarvester = true;
                    found = true;
                }
            }
            return found;
        }
        public static bool IsUnarmedAndCanRunAway(Tank tank)
        {
            return !tank.IsAnchored && tank.blockman.IterateBlockComponents<ModuleTechController>().Count() >=
                tank.blockman.IterateBlockComponents<ModuleWeapon>().Count() +
                tank.blockman.IterateBlockComponents<ModuleMeleeWeapon>().Count();
        }
        private static void TestShouldCommitDie(Tank tank, EnemyMind mind)
        {
            bool minion = tank.name.Contains("Minion");
            if (!tank.IsPopulation && !minion)
                return;
            if(tank.blockman.blockCount < 3 && !mind.StartedAnchored)
            {
                foreach (TankBlock lastBlock in tank.blockman.IterateBlocks())
                {
                    if (!lastBlock.damage.AboutToDie)
                        lastBlock.damage.SelfDestruct(2f);
                }
            }
        }
        private static void CheckAndHandleControlBlocks(EnemyMind mind, TankBlock block)
        {
            if (!KickStart.isControlBlocksPresent)
                return;
            else
                HandleUnsetControlBlocks(mind, block);
        }
        private static void HandleUnsetControlBlocks(EnemyMind mind, TankBlock block)
        {
            try
            {
                FieldInfo wasSet = typeof(Control_Block.ModuleBlockMover).GetField("Deserialized", BindingFlags.NonPublic | BindingFlags.Instance);
                Control_Block.ModuleBlockMover mover = block.GetComponent<Control_Block.ModuleBlockMover>();
                if (!(bool)mover)
                    return;
                if ((bool)wasSet.GetValue(mover))
                    return;
                DebugTAC_AI.Log(KickStart.ModID + ": Setting a Control Block...");

                mover.ProcessOperations.Clear();
                if (mover.IsPlanarVALUE)
                {
                    mover.ProcessOperations.Add(new Control_Block.InputOperator()
                    {
                        m_InputKey = KeyCode.Space,
                        m_InputType = Control_Block.InputOperator.InputType.EnemyTechIsNear,
                        m_InputParam = mind.MaxCombatRange,
                        m_OperationType = Control_Block.InputOperator.OperationType.IfThen,
                        m_Strength = 0
                    });
                    mover.ProcessOperations.Add(new Control_Block.InputOperator()
                    {
                        m_InputKey = KeyCode.Space,
                        m_InputType = Control_Block.InputOperator.InputType.AlwaysOn,
                        m_InputParam = mind.MaxCombatRange,
                        m_OperationType = Control_Block.InputOperator.OperationType.TargetPointPredictive,
                        m_Strength = 125
                    });
                    mover.ProcessOperations.Add(new Control_Block.InputOperator()
                    {
                        m_InputKey = KeyCode.Space,
                        m_InputType = Control_Block.InputOperator.InputType.AlwaysOn,
                        m_InputParam = 0,
                        m_OperationType = Control_Block.InputOperator.OperationType.ElseThen,
                        m_Strength = 0
                    });
                    mover.ProcessOperations.Add(new Control_Block.InputOperator()
                    {
                        m_InputKey = KeyCode.Space,
                        m_InputType = Control_Block.InputOperator.InputType.AlwaysOn,
                        m_InputParam = 0,
                        m_OperationType = Control_Block.InputOperator.OperationType.SetPos,
                        m_Strength = 0
                    });
                    mover.ProcessOperations.Add(new Control_Block.InputOperator()
                    {
                        m_InputKey = KeyCode.Space,
                        m_InputType = Control_Block.InputOperator.InputType.AlwaysOn,
                        m_InputParam = 0,
                        m_OperationType = Control_Block.InputOperator.OperationType.EndIf,
                        m_Strength = 0
                    });
                }
                else
                {
                    mover.ProcessOperations.Add(new Control_Block.InputOperator()
                    {
                        m_InputKey = KeyCode.Space,
                        m_InputType = Control_Block.InputOperator.InputType.EnemyTechIsNear,
                        m_InputParam = mind.MaxCombatRange,
                        m_OperationType = Control_Block.InputOperator.OperationType.ShiftPos,
                        m_Strength = 2
                    });
                    mover.ProcessOperations.Add(new Control_Block.InputOperator()
                    {
                        m_InputKey = KeyCode.Space,
                        m_InputType = Control_Block.InputOperator.InputType.AlwaysOn,
                        m_InputParam = 0,
                        m_OperationType = Control_Block.InputOperator.OperationType.ShiftPos,
                        m_Strength = -1
                    });
                }
            }
            catch { }
        }
        private static void CheckShouldMakeBase(TankAIHelper helper, EnemyMind newMind, Tank tank)
        {
            switch (newMind.CommanderMind)
            {
                case EnemyAttitude.NPCBaseHost:
                    if (!tank.name.Contains('Ω'))
                        tank.SetName(tank.name + " Ω");
                    newMind.CommanderMind = EnemyAttitude.NPCBaseHost;
                    if (!ManBaseTeams.IsBaseTeamDynamic(tank.Team))
                    {
                        if (tank.blockman.IterateBlockComponents<ModuleItemHolder>().Count() > 0)
                        {
                            if (RawTechLoader.TryStartBase(tank, helper, BasePurpose.HarvestingNoHQ))
                                DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a base prospector tech!!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                            else
                                DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a raid prospector tech!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                        }
                        else if (RawTechLoader.TryStartBase(tank, helper, BasePurpose.TechProduction))
                            DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a base hosting tech!!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                        else
                            DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a base raider tech!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                    }
                    break;
                case EnemyAttitude.Boss:
                    if (!tank.name.Contains('⦲'))
                        tank.SetName(tank.name + " ⦲");
                    if (!ManBaseTeams.IsBaseTeamDynamic(tank.Team) && RawTechLoader.TryStartBase(tank, helper, BasePurpose.Headquarters))
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a base boss with dangerous potential!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                    else
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a raid boss with dangerous potential!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                    break;
                case EnemyAttitude.Invader:
                    if (RawTechLoader.TryStartBase(tank, helper, BasePurpose.AnyNonHQ))
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a base invader looking to take over your world!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                    else
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a normal invader looking to take over your world!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                    break;
                case EnemyAttitude.Miner:
                    if (newMind.CommanderAttack == EAttackMode.Circle)// Circle breaks the harvester AI in some attack cases
                    {
                        switch (newMind.EvilCommander)
                        {
                            case EnemyHandling.Naval:
                                newMind.CommanderAttack = EAttackMode.Ranged;
                                break;
                            case EnemyHandling.Starship:
                            case EnemyHandling.Airplane:
                            case EnemyHandling.Chopper:
                                newMind.CommanderAttack = EAttackMode.Random;
                                break;
                            default:
                                newMind.CommanderAttack = EAttackMode.Safety;
                                break;
                        }
                    }
                    DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a harvester!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                    break;
                default:
                    DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is ready to roll!  " + newMind.EvilCommander.ToString() + " based " + newMind.CommanderAlignment.ToString() + " with attitude " + newMind.CommanderAttack.ToString() + " | Mind " + newMind.CommanderMind.ToString() + " | Smarts " + newMind.CommanderSmarts.ToString() + " inbound!");
                    break;
            }
        }
        internal static void SetFromScheme(EnemyMind newMind, Tank tank)
        {
            try
            {
                ControlSchemeCategory Schemer = tank.control.ActiveScheme.Category;
                switch (Schemer)
                {
                    case ControlSchemeCategory.Car:
                        newMind.EvilCommander = EnemyHandling.Wheeled;
                        break;
                    case ControlSchemeCategory.Aeroplane:
                        newMind.EvilCommander = EnemyHandling.Airplane;
                        break;
                    case ControlSchemeCategory.Helicopter:
                        newMind.EvilCommander = EnemyHandling.Chopper;
                        break;
                    case ControlSchemeCategory.AntiGrav:
                        newMind.EvilCommander = EnemyHandling.Starship;
                        break;
                    case ControlSchemeCategory.Rocket:
                        newMind.EvilCommander = EnemyHandling.Starship;
                        break;
                    case ControlSchemeCategory.Hovercraft:
                        // REVISED: hovercraft scheme now drives the Wheeled handler (was Starship)
                        newMind.EvilCommander = EnemyHandling.Wheeled;
                        break;
                    default:
                        string name = tank.control.ActiveScheme.CustomName;
                        if (name == "Ship" || name == "ship" || name == "Naval" || name == "naval" || name == "Boat" || name == "boat")
                        {
                            newMind.EvilCommander = EnemyHandling.Naval;
                        }
                        //Else we just default to Wheeled
                        break;
                }
            }
            catch { }//some population techs are devoid of schemes
        }

        // ----------------------------  Conclusion  ----------------------------
        private static void FinalInitialization(TankAIHelper helper, EnemyMind newMind, Tank tank)
        {
            if (tank.Anchors.NumAnchored > 0 || tank.GetComponent<RequestAnchored>())
            {
                newMind.StartedAnchored = true;
                newMind.EvilCommander = EnemyHandling.Stationary;
                helper.TryInsureManualAnchor();
                helper.AnchorIgnoreChecks();
            }
            if (newMind.CommanderSmarts > EnemySmarts.Meh)
            {
                helper.InsureTechMemor("FinalInitialization", true);
                newMind.CommanderBolts = EnemyBolts.AtFullOnAggro;// allow base function
            }

            // REVISED: removed the old "Default enemy with Default everything" fast-path that forced RunState=Default and
            // early-returned for plain wheeled default-smarts non-base techs; all techs now fall through the full init below.
            if (AIGlobals.IsAttract)
            {
                if (KickStart.SpecialAttractNum == AttractType.Harvester)
                {
                    newMind.CommanderSmarts = EnemySmarts.IntAIligent;
                    if (newMind.StartedAnchored)
                    {
                        tank.FixupAnchors(true);
                        newMind.CommanderMind = EnemyAttitude.Default;
                        newMind.EvilCommander = EnemyHandling.Stationary;
                        newMind.CommanderBolts = EnemyBolts.MissionTrigger;
                        if (!tank.IsAnchored && tank.Anchors.NumPossibleAnchors > 0)
                        {
                            helper.AnchorIgnoreChecks();
                        }
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a base Tech");
                    }
                    else
                    {
                        newMind.CommanderMind = EnemyAttitude.Miner;
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is a harvester Tech");
                    }
                }
                else
                {
                    if (newMind.StartedAnchored)
                    {
                        RLoadedBases.SetupBaseAI(helper, tank, newMind);
                        if (ManBaseTeams.TryGetBaseTeamDynamicOnly(tank.Team, out var teamInst))
                            teamInst.AddBuildBucks(4000000);
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + tank.name + " is is a base Tech");
                    }
                    if (newMind.EvilCommander != EnemyHandling.Wheeled)
                        newMind.CommanderAttack = EAttackMode.Chase;
                    if (newMind.CommanderAttack == EAttackMode.Safety)
                        newMind.CommanderAttack = EAttackMode.Circle;
                    if (newMind.CommanderAttack == EAttackMode.Ranged)
                        newMind.CommanderAttack = EAttackMode.Chase;
                    if (newMind.CommanderMind == EnemyAttitude.Miner)
                        newMind.CommanderMind = EnemyAttitude.Homing;
                }
            }
            else
            {
                CheckShouldMakeBase(helper, newMind, tank);

                int Team = tank.Team;
                if (ManBaseTeams.TryGetBaseTeamDynamicOnly(Team, out var ETD))
                {
                    newMind.CommanderAlignment = ETD.EnemyMindAlignment(ManPlayer.inst.PlayerTeam);
                }

                if (RawTechLoader.ShouldDetonateBoltsNow(newMind) && tank.FirstUpdateAfterSpawn)
                {
                    newMind.BlowBolts();
                }

                if (!KickStart.AllowEnemiesToMine && newMind.CommanderMind == EnemyAttitude.Miner)
                    newMind.CommanderMind = EnemyAttitude.Default;
            }
            if (AIGlobals.ShowDebugFeedBack)
                AIGlobals.PopupColored(newMind.EvilCommander.ToString(), tank.Team, WorldPosition.FromScenePosition(tank.boundsCentreWorld + (Vector3.up * helper.lastTechExtents)));

            helper.SecondAvoidence = newMind.CommanderSmarts >= EnemySmarts.Smrt;
            helper.AISetSettings.AdvancedAI = newMind.CommanderSmarts >= EnemySmarts.Meh;
            if (newMind.CommanderMind == EnemyAttitude.Homing)
                helper.AISetSettings.ScanRange = AIGlobals.EnemyExtendActionRange;
            else
                helper.AISetSettings.ScanRange = AIGlobals.DefaultEnemyScanRange;

            switch (newMind.CommanderMind)
            {
                case EnemyAttitude.Homing:
                    newMind.MaxCombatRange = AIGlobals.EnemyExtendActionRange;
                    break;
                case EnemyAttitude.Miner:
                case EnemyAttitude.Junker:
                    newMind.MaxCombatRange = AIGlobals.PassiveMaxCombatRange;
                    break;
                case EnemyAttitude.NPCBaseHost:
                    newMind.MaxCombatRange = AIGlobals.BaseFounderMaxCombatRange;
                    break;
                case EnemyAttitude.Boss:
                    newMind.MaxCombatRange = AIGlobals.BossMaxCombatRange;
                    break;
                case EnemyAttitude.Invader:
                    newMind.MaxCombatRange = AIGlobals.InvaderMaxCombatRange;
                    break;
                default:
                    if (helper.AttackMode == EAttackMode.Ranged)
                        newMind.MaxCombatRange = AIGlobals.SpyperMaxCombatRange;
                    else
                        newMind.MaxCombatRange = AIGlobals.DefaultEnemyMaxCombatRange;

                    break;
            }
            if (helper.AttackMode == EAttackMode.Ranged)
                newMind.MinCombatRange = AIGlobals.MinCombatRangeSpyper;
            else if (helper.Attempt3DNavi)
                newMind.MinCombatRange = AIGlobals.SpacingRangeHoverer;
            else
                newMind.MinCombatRange = AIGlobals.MinCombatRangeDefault;

            // REVISED: final movement-controller rebind now routed through RequestMovementControllerSwap (deferred/reasoned swap)
            helper.RequestMovementControllerSwap(TankAIHelper.MovementSwapReason.EnemyMindSetup);
            DebugTAC_AI.FinishAICalculationTimer(tank);
        }
    }
}
