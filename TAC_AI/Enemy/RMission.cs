using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.Templates;

namespace TAC_AI.AI.Enemy
{
    public static class RMission
    {
        public class OnRailsActions : MonoBehaviour
        {   // Will sit on standby for MissionManager
            public Tank Tank;
            public TankAIHelper AIControl;
            public int MissionAIID = 0;

            //public MissionManager.Mission Mission;
            public static Event<Tank, OnRailsActions> MissionAIStatus;

            public static void Initiate()
            {
                Singleton.Manager<ManTechs>.inst.TankDestroyedEvent.Subscribe(TankDestruction);
            }
            public static void TankDestruction(Tank tank, ManDamage.DamageInfo oof)
            {
                var rails = tank.GetComponent<OnRailsActions>();
                if (rails.IsNotNull())
                    MissionAIStatus.Send(tank, rails);
            }

            public void InitiateForTank()
            {
                Tank = gameObject.GetComponent<Tank>();
                AIControl = gameObject.GetComponent<TankAIHelper>();
                //Tank.DamageEvent.Subscribe(OnHit);
                //Tank.DetachEvent.Subscribe(OnBlockLoss);
            }
            public void Remove()
            {
                //Tank.DamageEvent.Unsubscribe(OnHit);
                //Tank.DetachEvent.Unsubscribe(OnBlockLoss);
                DestroyImmediate(this);
            }
            public void Reset()
            {
            }
            public static void OnHit(Tank tank, OnRailsActions mAIState)
            {   // compile relivant information here and deliver it to the MissionManager
                MissionAIStatus.Send(tank, mAIState);
            }
            public static void ArrivalAtDest(Tank tank, OnRailsActions mAIState)
            {   // compile relivant information here and deliver it to the MissionManager
                MissionAIStatus.Send(tank, mAIState);
            }
        }

        internal static bool SpecificNameCases(TankAIHelper helper, Tank tank, EnemyMind mind)
        {   // Handle specific enemy names to tailor the AI into working order
            int name = tank.name.GetHashCode();
            bool DidFire = false;

            if (name == "Missile Defense".GetHashCode())
            {   // The GSO Missile Turret
                mind.AllowRepairsOnFly = true;
                mind.EvilCommander = EnemyHandling.Stationary;
                mind.CommanderAttack = EAttackMode.Strong;
                mind.CommanderMind = EnemyAttitude.Homing;
                mind.CommanderSmarts = EnemySmarts.Mild;
                DidFire = true;
            }
            else if (name == "Wing-nut".GetHashCode())
            {   // Wing-nut mission
                mind.AllowRepairsOnFly = true;
                mind.InvertBullyPriority = true;
                mind.EvilCommander = EnemyHandling.Stationary;
                mind.CommanderAttack = EAttackMode.Strong;
                mind.CommanderMind = EnemyAttitude.Homing;
                mind.CommanderSmarts = EnemySmarts.IntAIligent;
                DidFire = true;
            }
            else if (name == "Spider King".GetHashCode())
            {   // Spider King mission
                mind.AllowRepairsOnFly = true;
                mind.InvertBullyPriority = true;
                mind.EvilCommander = EnemyHandling.Stationary;
                mind.CommanderAttack = EAttackMode.Strong;
                mind.CommanderMind = EnemyAttitude.Homing;
                mind.CommanderSmarts = EnemySmarts.IntAIligent;
                DidFire = true;
            }
            else if (name == "Fly".GetHashCode())
            {   // Spider King mission
                mind.AllowRepairsOnFly = true;
                mind.InvertBullyPriority = true;
                mind.EvilCommander = EnemyHandling.Starship;
                mind.CommanderAttack = EAttackMode.Random;
                mind.CommanderMind = EnemyAttitude.Homing;
                RCore.AutoSetIntelligence(mind);
                DidFire = true;
            }
            else if (name == "Enemy HQ".GetHashCode())
            {   //Base where enemies spawn from
                mind.AllowInvBlocks = true;
                mind.AllowRepairsOnFly = true;
                mind.InvertBullyPriority = true;
                mind.EvilCommander = EnemyHandling.Stationary;
                mind.CommanderAttack = EAttackMode.Strong;
                mind.CommanderMind = EnemyAttitude.Homing;
                mind.CommanderSmarts = EnemySmarts.IntAIligent;
                mind.CommanderBolts = EnemyBolts.AtFull;
                DidFire = true;
            }
            else if (name == "Missile #2".GetHashCode())
            {
                mind.AllowRepairsOnFly = true;
                mind.InvertBullyPriority = true;
                mind.EvilCommander = EnemyHandling.SuicideMissile;
                mind.CommanderAttack = EAttackMode.Chase;
                mind.CommanderMind = EnemyAttitude.Homing;
                mind.CommanderSmarts = EnemySmarts.IntAIligent;
                mind.CommanderBolts = EnemyBolts.MissionTrigger;
                DidFire = true;
            }
            else if (name == "DPS Target".GetHashCode())
            {   // R&D Target
                mind.AIControl.RunState = AIRunState.Default;
                mind.StartedAnchored = true;
                mind.EvilCommander = EnemyHandling.Stationary;
                mind.CommanderAttack = EAttackMode.Safety;
                mind.CommanderMind = EnemyAttitude.Default;
                mind.CommanderSmarts = EnemySmarts.Default;
                mind.CommanderBolts = EnemyBolts.MissionTrigger;
                DidFire = true;
            }
            /*
            else if (name == "TAC InvaderAttract")
            {
                mind.AllowInvBlocks = true;
                mind.AllowRepairsOnFly = true;
                mind.InvertBullyPriority = true;
                mind.EvilCommander = EnemyHandling.Starship;
                mind.CommanderAttack = EnemyAttack.Grudge;
                mind.CommanderMind = EnemyAttitude.Homing;
                mind.CommanderSmarts = EnemySmarts.IntAIligent;
                mind.CommanderBolts = EnemyBolts.MissionTrigger;
                DidFire = true;
            }
            */

            return DidFire;
        }

        // T2: returns MissionSetupResult instead of bool. FullyConfigured = "all four canonical
        // fields explicitly set, skip the AutoSet/Handling/Smart chain". PartialMind = "one or
        // two fields nudged, still need the chain to fill in the rest". None = "not a mission
        // tech, run the full chain normally". Replaces previous bool which silently lost the
        // partial-vs-full distinction (Ω/⦲ branches got skipped despite only setting
        // CommanderMind, leaving the other 3 fields at construction defaults).
        internal static MissionSetupResult SetupBaseOrMissionAI(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            string name = tank.name;
            MissionSetupResult result = RLoadedBases.SetupBaseAI(helper, tank, mind)
                ? MissionSetupResult.FullyConfigured
                : MissionSetupResult.None;
            if (!(bool)tank)
                return MissionSetupResult.FullyConfigured;
            tank.AI.TryGetCurrentAIType(out AITreeType.AITypes tree1);
            DebugTAC_AI.LogAISetup(KickStart.ModID + ": AI " + tank.name + ":  AI Tree is " + tree1.ToString());

            if (result == MissionSetupResult.None)
            {
                if (name.Contains('Ω'))
                {   // Base host NPC — only sets CommanderMind; chain must still run for the rest.
                    mind.CommanderMind = EnemyAttitude.NPCBaseHost;
                    result = MissionSetupResult.PartialMind;
                }
                else if (name.Contains('⦲'))
                {   // Boss — same partial pattern as Ω.
                    mind.CommanderMind = EnemyAttitude.Boss;
                    result = MissionSetupResult.PartialMind;
                }
                else if (SpecificNameCases(helper, tank, mind))
                {
                    // SpecificNameCases hand-tunes all 4 canonical fields per name.
                    result = MissionSetupResult.FullyConfigured;
                }
                else
                {
                    if (tank.AI.TryGetCurrentAIType(out AITreeType.AITypes tree))
                    {
                        if (tree == AITreeType.AITypes.Flee)
                        {   // setup for runner — full 4 fields + intelligence
                            mind.AllowRepairsOnFly = true;
                            mind.EvilCommander = EnemyHandling.Wheeled;
                            mind.CommanderAttack = EAttackMode.Safety;
                            mind.CommanderMind = EnemyAttitude.Homing;
                            RCore.AutoSetIntelligence(mind);
                            result = MissionSetupResult.FullyConfigured;
                        }
                        else if (tree == AITreeType.AITypes.ChargeAtSKU)
                        {   // setup for Sumo — full 4 fields + IntAIligent
                            mind.AllowRepairsOnFly = false;
                            mind.EvilCommander = EnemyHandling.Wheeled;
                            mind.CommanderAttack = EAttackMode.Chase;
                            mind.CommanderMind = EnemyAttitude.Homing;
                            mind.CommanderSmarts = EnemySmarts.IntAIligent;
                            result = MissionSetupResult.FullyConfigured;
                        }
                        else if (tree == AITreeType.AITypes.Invader)
                        {   // setup for Invaders — full 4 fields via handling + intelligence
                            mind.AllowRepairsOnFly = false;
                            RCore.GetOrCalculateEnemyHandling(tank, mind);
                            if (KickStart.Difficulty > 100)
                            {
                                mind.CommanderAttack = EAttackMode.Ranged;
                                mind.CommanderMind = EnemyAttitude.Invader;
                            }
                            else
                            {
                                mind.CommanderAttack = EAttackMode.Chase;
                                mind.CommanderMind = EnemyAttitude.Invader;
                            }
                            RCore.AutoSetIntelligence(mind);
                            result = MissionSetupResult.FullyConfigured;
                        }
                        else if (tree == AITreeType.AITypes.Specific || tree == AITreeType.AITypes.FacePlayer)
                        {   // Only sets RunState — needs chain to fill in combat fields.
                            helper.RunState = AIRunState.Default;
                            result = MissionSetupResult.PartialMind;
                        }
                    }
                }
            }

            if (name.Contains('⟰'))
            {   // Spawned as a Tech Fragment
                mind.BuildAssist = true;
            }
            if (result != MissionSetupResult.None && mind.CommanderMind == EnemyAttitude.OnRails)
            {
                var rails = tank.GetComponent<OnRailsActions>();
                if (rails.IsNull())
                {
                    rails = tank.gameObject.AddComponent<OnRailsActions>();
                    rails.InitiateForTank();
                }
                rails.Reset();
            }
            else   // remove uneeded module
            {
                var rails = tank.GetComponent<OnRailsActions>();
                if (rails.IsNotNull())
                    rails.Remove();
            }

            return result;
        }

        public static void MissionHandler(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            var AISettings = tank.GetComponent<AIBookmarker>();
            if (AISettings.IsNotNull())
            {
                mind.EvilCommander = AISettings.commander;
                mind.CommanderAttack = AISettings.attack;
                mind.CommanderBolts = AISettings.bolts;
                mind.CommanderMind = AISettings.attitude;
                mind.CommanderSmarts = AISettings.smarts;
                UnityEngine.Object.DestroyImmediate(AISettings);
            }
            return;
        }
        public static bool ADVMissionHandler(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            return true;
        }
    }
}
