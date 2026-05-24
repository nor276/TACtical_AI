using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI
{
    public class AIECore
    {

        internal static HashSet<SceneryTypes> IndestructableScenery = new HashSet<SceneryTypes>
        {
            SceneryTypes.Pillar, SceneryTypes.ScrapPile,
        };

        public static Event<Tank, string> AIMessageEvent = new Event<Tank, string>();

        private static List<TankAIHelper> AllHelpers = new List<TankAIHelper>();
        internal static int HelperCountNoCheck => AllHelpers.Count;
        internal static int HelperCountChecked => IterateAllHelpers().Count();
        internal static void AddHelper(TankAIHelper helper) => AllHelpers.Add(helper);
        internal static void DestroyAllHelpers()
        {
            foreach (var item in new List<TankAIHelper>(AllHelpers))
            {
                UnityEngine.Object.Destroy(item);
            }
            AllHelpers.Clear();
        }
        internal static IEnumerable<TankAIHelper> IterateAllHelpers()
        {
            foreach (var item in AllHelpers)
            {
                if (item != null && item.enabled)
                    yield return item;
            }
        }
        internal static IEnumerable<TankAIHelper> IterateAllHelpers(Func<TankAIHelper, bool> delegator)
        {
            foreach (var item in AllHelpers)
            {
                if (item != null && delegator(item))
                    yield return item;
            }
        }
        internal static List<Visible> Minables;
        internal static List<ModuleHarvestReciever> Depots;
        internal static List<ModuleHarvestReciever> BlockHandlers;
        internal static List<ModuleChargerTracker> Chargers;
        internal static HashSet<int> RetreatingTeams;
        public static bool PlayerIsInNonCombatZone => _playerIsInNonCombatZone;
        internal static bool _playerIsInNonCombatZone = false;
        internal static bool PlayerCombatLastState = false;

        internal static bool Feedback = false;

        public static Func<int, IEnumerable<Tank>> GetTeamTanks => TankAIManager.GetTeamTanks;
        public static Func<int, IEnumerable<Tank>> GetNonEnemyTanks => TankAIManager.GetNonEnemyTanks;
        public static Func<int, IEnumerable<Tank>> GetTargetTanks => TankAIManager.GetTargetTanks;

        public static bool FetchClosestChunkReceiver(Vector3 tankPos, float MaxScanRange, out IAIFollowable finalPos, out Tank theBase, int team, bool includeTradingStations = false)
        {
            bool fired = false;
            theBase = null;
            finalPos = null;
            float bestValue = MaxScanRange * MaxScanRange;
            foreach (ModuleHarvestReciever reciever in Depots)
            {
                if (!reciever.tank.boundsCentreWorldNoCheck.Approximately(tankPos, 1) && (reciever.tank.Team == team ||
                    (includeTradingStations && reciever.tank.Team == ManSpawn.NeutralTeam)))
                {
                    float temp = (reciever.trans.position - tankPos).sqrMagnitude;
                    if (bestValue > temp && temp != 0)
                    {
                        fired = true;
                        theBase = reciever.tank;
                        bestValue = temp;
                        finalPos = reciever;
                    }
                }
            }
            return fired;
        }
        public static bool FetchClosestResource(Vector3 tankPos, float MaxScanRange, float MaxDepth, out Visible theResource)
        {
            bool fired = false;
            theResource = null;
            float bestValue = MaxScanRange * MaxScanRange;
            int run = Minables.Count;
            for (int step = 0; step < run; step++)
            {
                var trans = Minables.ElementAt(step);
                if (trans.isActive && trans.trans.position.y <= MaxDepth)
                {
                    var res = trans.GetComponent<ResourceDispenser>();
                    if (!res.IsDeactivated && res.visible.isActive)
                    {
                        if (!trans.GetComponent<Damageable>().Invulnerable &&
                            !IndestructableScenery.Contains(res.GetSceneryType()))
                        {
                            float temp = (trans.trans.position - tankPos).sqrMagnitude;
                            if (bestValue > temp && temp != 0)
                            {
                                theResource = trans;
                                fired = true;
                                bestValue = temp;
                            }
                            continue;
                        }
                    }
                }
                Minables.RemoveAt(step);
                step--;
                run--;
            }
            return fired;
        }


        public static bool FetchClosestBlockReceiver(Vector3 tankPos, float MaxScanRange, out IAIFollowable finalPos, out Tank theBase, int team)
        {
            bool fired = false;
            theBase = null;
            finalPos = null;
            float bestValue = MaxScanRange * MaxScanRange;
            foreach (ModuleHarvestReciever reciever in BlockHandlers)
            {
                if (!reciever.tank.boundsCentreWorldNoCheck.Approximately(tankPos, 1) && reciever.tank.Team == team)
                {
                    float temp = (reciever.trans.position - tankPos).sqrMagnitude;
                    if (bestValue > temp && temp != 0)
                    {
                        fired = true;
                        theBase = reciever.trans.root.GetComponent<Tank>();
                        bestValue = temp;
                        finalPos = reciever;
                    }
                }
            }
            return fired;
        }
        public static bool FetchLooseBlocks(Vector3 tankPos, float MaxScanRange, out Visible theResource)
        {
            bool fired = false;
            theResource = null;
            float bestValue = MaxScanRange * MaxScanRange;
            foreach (Visible vis in ManWorld.inst.TileManager.IterateVisibles(ObjectTypes.Block, tankPos, MaxScanRange))
            {
                if (!vis?.block || !vis.isActive)
                    continue;
                if (vis.block.IsAttached || vis.InBeam || !vis.IsInteractible || ManPointer.inst.DraggingItem == vis)
                    continue;
                try
                {
                    float temp = (vis.centrePosition - tankPos).sqrMagnitude;
                    if (bestValue > temp && temp > 1)
                    {
                        fired = true;
                        bestValue = temp;
                        theResource = vis;
                    }
                }
                catch { }
            }
            return fired;
        }

        internal static FieldInfo blocksGet = typeof(TankControl).GetField("m_ControlState", BindingFlags.NonPublic | BindingFlags.Instance);

        public static bool FetchCopyableAlly(Vector3 tankPos, TankAIHelper helper, out float distanceSqr, out Visible ToFetch)
        {
            distanceSqr = 62500;
            Tank bestStep = null;
            ToFetch = null;
            try
            {
                foreach (var ally in TankAIManager.GetTeamTanks(helper.tank.Team))
                {
                    if (ally != null && ally.visible.isActive && ally != helper.tank &&
                        ally.GetHelperInsured().CanCopyControls)
                    {
                        float temp = (ally.boundsCentreWorldNoCheck - tankPos).sqrMagnitude;
                        if (temp < distanceSqr)
                        {
                            distanceSqr = temp;
                            bestStep = ally;
                        }
                    }
                }
                if (bestStep == null)
                    return false;
                ToFetch = bestStep.visible;
                return true;
            }
            catch (Exception e)
            {
                throw new NullReferenceException(KickStart.ModID + ": Crash on FetchCopyableAlly", e);
            }
        }

        public static bool ChargedChargerExists(Tank tank, float MaxScanRange, int team)
        {
            if (team == -2)
                team = Singleton.Manager<ManPlayer>.inst.PlayerTeam;
            Vector3 tankPos = tank.boundsCentreWorldNoCheck;

            float scanRange = Mathf.Pow(MaxScanRange, 2);
            foreach (ModuleChargerTracker charge in Chargers)
            {
                if (charge.tank != tank && charge.tank.Team == team && charge.CanTransferCharge(tank))
                {
                    float temp = (charge.trans.position - tankPos).sqrMagnitude;
                    if (scanRange > temp && temp > 1)
                        return true;
                }
            }
            return false;
        }
        public static bool FetchChargedChargers(Tank tank, float MaxScanRange, out IAIFollowable finalPos, out Tank theBase, int team)
        {
            if (team == -2)
                team = Singleton.Manager<ManPlayer>.inst.PlayerTeam;
            Vector3 tankPos = tank.boundsCentreWorldNoCheck;

            bool fired = false;
            theBase = null;
            finalPos = null;
            float bestValue = Mathf.Pow(MaxScanRange, 2);
            foreach (ModuleChargerTracker charge in Chargers)
            {
                if (charge.tank != tank && charge.tank.Team == team && charge.CanTransferCharge(tank))
                {
                    float temp = (charge.trans.position - tankPos).sqrMagnitude;
                    if (bestValue > temp && temp > 1)
                    {
                        fired = true;
                        theBase = charge.tank;
                        bestValue = temp;
                        finalPos = charge;
                    }
                }
            }
            return fired;
        }
        public static bool FetchLowestChargeAlly(Vector3 tankPos, TankAIHelper helper, out Visible toCharge)
        {
            float Range = 62500;
            Tank bestStep = null;
            toCharge = null;
            try
            {
                foreach (var ally in AIEPathing.AllyList(helper.tank))
                {
                    float temp = (ally.boundsCentreWorldNoCheck - tankPos).sqrMagnitude;
                    TechEnergy.EnergyState eState = ally.EnergyRegulator.Energy(TechEnergy.EnergyType.Electric);
                    bool hasCapacity = eState.storageTotal > 200;
                    bool needsCharge = (eState.storageTotal - eState.spareCapacity) / eState.storageTotal < AIGlobals.minimumChargeFractionToConsider;
                    if (hasCapacity && needsCharge)
                    {
                        if (Range > temp && temp > 1)
                        {
                            Range = temp;
                            bestStep = ally;
                        }
                    }
                }
                if (bestStep == null)
                    return false;
                toCharge = bestStep.visible;
                return bestStep != null;
            }
            catch
            {
            }
            return false;
        }

        public static bool FindTarget(Tank tank, TankAIHelper helper, Visible targetIn, out Visible target)
        {

            float TargetRange = helper.MaxCombatRange * 2;
            TargetRange *= TargetRange;
            Vector3 scanCenter = tank.boundsCentreWorldNoCheck;
            target = targetIn;
            if (target != null)
            {
                if (target.tank == null)
                {
                    target = null;
                    return false;
                }
                else if ((target.tank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude > TargetRange)
                    target = null;
            }

            foreach (var cTank in TankAIManager.GetTargetTanks(tank.Team))
            {
                float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                if (dist <= TargetRange)
                {
                    TargetRange = dist;
                    target = cTank.visible;
                }
            }

            return target;
        }

        public static float ExtremesAbs(Vector3 input)
        {
            return Mathf.Max(Mathf.Max(Mathf.Abs(input.x), Mathf.Abs(input.y)), Mathf.Abs(input.z));
        }
        public static bool AIMessage(Tank tech, ref bool hasMessaged, string message)
        {
            AIMessageEvent.Send(tech, message);
            if (!hasMessaged && Feedback)
            {
                hasMessaged = true;
                DebugTAC_AI.Log(KickStart.ModID + ": AI " + message);
            }
            return hasMessaged;
        }
        public static void AIMessage(Tank tech, string message)
        {
            AIMessageEvent.Send(tech, message);
            if (Feedback)
                DebugTAC_AI.Log(KickStart.ModID + ": AI " + message);
        }
        public static void TeamRetreat(int Team, bool Retreat, bool Sending = false)
        {
            try
            {
                if (Retreat)
                {
                    if (!RetreatingTeams.Contains(Team))
                    {
                        RetreatingTeams.Add(Team);
                        if (Sending && ManNetwork.IsNetworked)
                            NetworkHandler.TryBroadcastNewRetreatState(Team, true);
                        AIWiki.hintNPTRetreat.Show();
                        foreach (Tank tech in TankAIManager.TeamActiveMobileTechs(Team))
                        {
                            WorldPosition worPos = Singleton.Manager<ManOverlay>.inst.WorldPositionForFloatingText(tech.visible);
                            AIGlobals.PopupColored("Fall back!", Team, worPos);
                        }
                    }
                }
                else
                {
                    if (RetreatingTeams.Remove(Team))
                    {
                        if (Sending && ManNetwork.IsNetworked)
                            NetworkHandler.TryBroadcastNewRetreatState(Team, false);
                        AIWiki.hintNPTRetreat.Show();
                        foreach (Tank tech in TankAIManager.TeamActiveMobileTechs(Team))
                        {
                            WorldPosition worPos = Singleton.Manager<ManOverlay>.inst.WorldPositionForFloatingText(tech.visible);
                            AIGlobals.PopupColored("Engage!", Team, worPos);
                        }
                    }
                }
            }
            catch { DebugTAC_AI.Log(KickStart.ModID + ": TeamRetreat encountered an error, perhaps in Attract?"); }
        }
        internal static void ToggleTeamRetreat(int Team)
        {
            if (RetreatingTeams.Contains(Team))
            {
                TeamRetreat(Team, false, true);
            }
            else
            {
                TeamRetreat(Team, true, true);
            }
        }

        public static bool IsTankValid(Transform trans)
        {
            var AICommand = trans.root.GetComponent<TankAIHelper>();
            if (AICommand.IsNotNull() && !KickStart.isWeaponAimModPresent)
            {
                var tank = trans.root.GetComponent<Tank>();
                if (tank.IsNotNull())
                    return true;
            }
            return false;
        }
        public static bool IsTechAimingScenery(Transform trans)
        {
            var AICommand = trans.root.GetComponent<TankAIHelper>();
            if (AICommand.IsNotNull() && !KickStart.isWeaponAimModPresent)
            {
                var tank = trans.root.GetComponent<Tank>();
                if (tank.IsNotNull())
                    if (!tank.PlayerFocused && AICommand.ActiveAimState == AIWeaponState.Obsticle)
                        return true;
            }
            return false;
        }

        public static bool HasOmniCore(TankBlock block)
        {
            BlockDetails BD = new BlockDetails(block.BlockType);
            return BD.IsOmniDirectional && !BD.HasWheels;
        }

        public static bool ShouldBeStationary(Tank tank, TankAIHelper helper)
        {
            if (helper.AnchorState == AIAnchorState.Unanchor)
                return false;
            if (helper.AutoAnchor)
            {
                if (tank.IsAnchored && !helper.PlayerAllowAutoAnchoring)
                    return true;
            }
            else if (tank.IsAnchored)
            {
                return true;
            }
            return false;
        }
        public static AIDriverType HandlingDetermine(Tank tank, TankAIHelper helper)
        {
            var BM = tank.blockman;

            if (ShouldBeStationary(tank, helper))
            {
                helper.AnchorStatic();
                if (helper.DriverType != AIDriverType.AutoSet)
                    return helper.DriverType;
            }

            if (KickStart.IsRandomAdditionsPresent)
            {
                try
                {
                    foreach (var item in BM.IterateBlocks())
                    {
                        if (HasOmniCore(item))
                            return AIDriverType.Astronaut;
                    }
                }
                catch { };
            }

            bool canFloat = false;
            bool isFlying = false;
            bool isFlyingDirectionForwards = true;
            bool isOmniEngine = false;
            Vector3 biasDirection = Vector3.zero;
            Vector3 boostBiasDirection = Vector3.zero;

            int FoilCount = 0;
            int MovingFoilCount = 0;

            int modBoostCount = 0;
            int modHoverCount = 0;
            int modGyroCount = 0;
            int modWheelCount = 0;
            int modAGCount = 0;
            int modGunCount = 0;
            int modDrillCount = 0;

            foreach (TankBlock bloc in BM.IterateBlocks())
            {
                BlockDetails BD = new BlockDetails(bloc.BlockType);
                if (BD.IsBasic)
                    continue;
                if (BD.DoesMovement)
                {
                    if (BD.HasWings)
                        modBoostCount++;
                    if (BD.HasHovers)
                        modHoverCount++;
                    if (BD.FloatsOnWater)
                        canFloat = true;
                    if (BD.IsGyro)
                        modGyroCount++;
                    if (BD.HasWheels)
                        modWheelCount++;
                    else if (BD.IsOmniDirectional)
                        isOmniEngine = true;
                    if (BD.HasAntiGravity)
                        modAGCount++;
                    if (BD.HasWings)
                    {
                        var aero = bloc.GetComponent<ModuleWing>();
                        if (aero)
                        {
                            foreach (ModuleWing.Aerofoil Afoil in aero.m_Aerofoils)
                            {
                                if (Afoil.flapAngleRangeActual > 0 && Afoil.flapTurnSpeed > 0)
                                    MovingFoilCount++;
                                FoilCount++;
                            }
                        }
                    }
                    bool boosters = false;
                    if (BD.HasFans)
                    {
                        foreach (FanJet jet in bloc.transform.GetComponentsInChildren<FanJet>())
                        {
                            if ((float)RawTechBase.spinDat.GetValue(jet) <= 10)
                            {
                                biasDirection -= tank.rootBlockTrans.InverseTransformDirection(jet.EffectorForward) * (float)RawTechBase.thrustRate.GetValue(jet);
                            }
                        }
                        boosters = true;
                    }
                    if (BD.HasBoosters)
                    {
                        foreach (BoosterJet boost in bloc.transform.GetComponentsInChildren<BoosterJet>())
                        {
                            float boostForce = (float)AIControllerDefault.boostGet.GetValue(boost);
                            boostBiasDirection -= tank.rootBlockTrans.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection)) * boostForce;
                        }
                        boosters = true;
                    }
                    if (boosters)
                        modBoostCount++;
                }
                if (BD.IsWeapon)
                {
                    if (bloc.GetComponent<ModuleWeaponGun>())
                        modGunCount++;
                    if (BD.IsMelee)
                        modDrillCount++;
                }
            }
            DebugTAC_AI.Info(KickStart.ModID + ": Tech " + tank.name + " Has bias of" + biasDirection + " and a boost bias of" + boostBiasDirection);

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

            if (tank.IsAnchored)
            {
                return AIDriverType.Tank;
            }
            else if (BM.blockCount == 1)
            {
                if (isOmniEngine)
                    return AIDriverType.Astronaut;
                else if (isFlyingDirectionForwards && MovingFoilCount > 3)
                    return AIDriverType.Pilot;
                else if (!isFlyingDirectionForwards)
                    return AIDriverType.Pilot;
                else if (canFloat && modWheelCount == 0)
                    return AIDriverType.Sailor;
                return AIDriverType.Tank;
            }
            else if ((modHoverCount > 3) || (modBoostCount > 2 && (modHoverCount > 2 || modAGCount > 0)))
            {
                return AIDriverType.Astronaut;
            }
            else if (MovingFoilCount > 4 && isFlying && isFlyingDirectionForwards)
            {
                return AIDriverType.Pilot;
            }
            else if (modGyroCount > 0 && isFlying && !isFlyingDirectionForwards)
            {
                return AIDriverType.Pilot;
            }
            else if (KickStart.isWaterModPresent && FoilCount > 0 && modGyroCount > 0 && modBoostCount > 0 && (modWheelCount < 4 || modHoverCount > 1))
            {
                return AIDriverType.Sailor;
            }
            else
                return AIDriverType.Tank;
        }

        public static void RequestFocusFire(Tank tank, Visible target, RequestSeverity priority)
        {
            if (target.IsNull() || tank.IsNull())
                return;
            if (tank.Team == ManPlayer.inst.PlayerTeam)
                RequestFocusFirePlayer(tank, target, priority);
            else
            {
                var mind = tank.GetComponent<EnemyMind>();
                if (mind)
                    RLoadedBases.RequestFocusFireNPTs(mind, target, priority);
            }
        }
        public static void RequestFocusFirePlayer(Tank tank, Visible target, RequestSeverity priority)
        {
            if (target.IsNull() || tank.IsNull())
                return;
            if (target.tank.IsNull())
                return;
            int Team = tank.Team;
            if (tank.IsAnchored)
                AIMessage(tank, "Player Base " + tank.name + " is under attack!  Concentrate all fire on " + target.tank.name + "!");
            else
                AIMessage(tank, tank.name + ": Requesting assistance!  Cover me!");
            if (!TankAIManager.targetingRequests.ContainsKey(Team))
                TankAIManager.targetingRequests.Add(Team, new KeyValuePair<RequestSeverity, Visible>(priority, target));
        }
    }
}
