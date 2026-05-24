using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI.Enemy;
using TAC_AI.Templates;
using TerraTechETCUtil;
using UnityEngine;
using static WaterMod.SurfacePool;

namespace TAC_AI.World
{
    public class UnloadedBases
    {
        public static NP_BaseUnit RefreshTeamMainBaseIfAnyPossible(NP_Presence EP)
        {
            if (EP.EBUs.Count == 0)
            {
                EP.MainBase = null;
                return null;
            }
            if (EP.MainBase != null && EP.MainBase.Exists())
            {
                return EP.MainBase;
            }
            else if (EP.EBUs.Count > 1)
            {
                NP_BaseUnit funder = null;
                int highestFunds = -1;
                foreach (NP_BaseUnit funds in EP.EBUs)
                {
                    if (highestFunds < funds.BuildBucks)
                    {
                        highestFunds = funds.BuildBucks;
                        funder = funds;
                    }
                }
                EP.MainBase = funder;
                return funder;
            }
            EP.MainBase = EP.EBUs.FirstOrDefault();
            return EP.MainBase;
        }

        public static void RecycleLoadedTechToTeam(Tank tank)
        {
            if (ManBaseTeams.BaseTeamExists(tank.Team))
                RLoadedBases.RecycleTechToTeam(tank);
            else
            {
                NP_Presence_Automatic EP = ManEnemyWorld.GetTeam(tank.Team);
                if (EP == null)
                    return;
                NP_BaseUnit EBU = RefreshTeamMainBaseIfAnyPossible(EP);
                if (EBU == null)
                    return;
                int tankCost = RawTechBase.GetBBCost(tank);
                EBU.AddBuildBucks(tankCost);
                AIGlobals.Purge(tank);
            }
        }

        public static bool HasTooMuchOfType(NP_Presence EP, BasePurpose purpose)
        {
            int Count = 0;
            int Team = EP.Team;

            if (purpose == BasePurpose.Defense)
            {
                foreach (NP_TechUnit ETU in EP.EMUs)
                {
                    if (ETU.GetSpeed() < 10)
                    {
                        Count++;
                    }
                }
            }
            else
            foreach (NP_BaseUnit EBU in EP.EBUs)
            {
                switch (purpose) {
                    case BasePurpose.HasReceivers:
                        if (EBU.handlesChunks)
                            Count++;
                        break;
                    case BasePurpose.Autominer:
                        if (EBU.revenue > 1)
                            Count++;
                        break;
                    case BasePurpose.TechProduction:
                        if (EBU.isTechBuilder)
                            Count++;
                        break;
                    case BasePurpose.Headquarters:
                        if (EBU.isSiegeBase)
                            Count++;
                        break;
                }
            }

            bool thisIsTrue;
            if (purpose == BasePurpose.Defense)
            {
                thisIsTrue = Count >= RLoadedBases.MaxDefenses;
                if (thisIsTrue)
                    DebugTAC_AI.Log(KickStart.ModID + ": HasTooMuchOfType - Team " + Team + " already has too many defenses and cannot make more");
            }
            else if (purpose == BasePurpose.Autominer)
            {
                thisIsTrue = Count >= RLoadedBases.MaxAutominers;
                if (thisIsTrue)
                    DebugTAC_AI.Log(KickStart.ModID + ": HasTooMuchOfType - Team " + Team + " already has too many autominers and cannot make more");
            }
            else if (purpose == BasePurpose.HasReceivers && RLoadedBases.FetchNearbyResourceCounts(Team) < AIGlobals.MinResourcesReqToCollect)
            {
                thisIsTrue = true;
                DebugTAC_AI.Log(KickStart.ModID + ": HasTooMuchOfType - Team " + Team + " Does not have enough mineables in range to build Reciever bases.");
            }
            else
            {
                thisIsTrue = Count >= RLoadedBases.MaxSingleBaseType;
                if (thisIsTrue)
                    DebugTAC_AI.Log(KickStart.ModID + ": HasTooMuchOfType - Team " + Team + " already has too many of type " + purpose.ToString() + " and cannot make more");
            }

            return thisIsTrue;
        }

        public static void GetScannedTilesAroundTech(NP_TechUnit ETU)
        {
            NP_Presence_Automatic EP = ETU.teamInst;
            IntVector2 scanPos = ETU.tilePos;
            int dist = ManEnemyWorld.UnitSightRadius;
            if (ETU is NP_BaseUnit)
                dist = ManEnemyWorld.BaseSightRadius;
            GetScannedTilesAtCoord(EP, scanPos, dist);
        }
        public static void GetScannedTilesAtCoord(NP_Presence EP, IntVector2 scanPos, int sightRadTiles = ManEnemyWorld.UnitSightRadius)
        {
            if (EP.attackStarted || EP.scannedPositions.Contains(scanPos))
                return;
            EP.scannedPositions.Add(scanPos);
            SearchPatternCachFetch(scanPos, sightRadTiles, ref EP.scannedEnemyTiles);
        }
        private static List<Vector2> cachSearchPattern = new List<Vector2>();
        private static void SearchPatternCachFetch(IntVector2 tilePos, int Dist, ref HashSet<IntVector2> SearchedTiles)
        {
            cachSearchPattern.Clear();
            int sightRad = Dist;
            int sightRad2 = sightRad * sightRad;
            for (int stepx = -sightRad; stepx < sightRad; stepx++)
            {
                for (int stepy = -sightRad; stepy < sightRad; stepy++)
                {
                    Vector2 V2 = new Vector2(stepx, stepy);
                    if (V2.sqrMagnitude <= sightRad2)
                    {
                        cachSearchPattern.Add(V2 + (Vector2)tilePos);
                    }
                }
            }

            int numScanned = 0;
            foreach (IntVector2 IV2 in cachSearchPattern)
            {
                if (!SearchedTiles.Contains(IV2))
                    SearchedTiles.Add(IV2);
                numScanned++;
            }
        }
        internal static bool SearchPatternCacheNoSort(NP_Presence EP, HashSet<IntVector2> SearchedTiles, out IntVector2 tilePosEnemy)
        {
            tilePosEnemy = IntVector2.zero;
            foreach (IntVector2 IV2 in SearchedTiles)
            {
                if (TileHasTargetableEnemy(EP, IV2))
                {
                    tilePosEnemy = IV2;
                    DebugTAC_AI.Log(KickStart.ModID + ": SearchPatternCacheNoSort - Enemy found at " + tilePosEnemy);
                    return true;
                }
            }
            return false;
        }
        internal static bool SearchPatternCacheSort(NP_Presence EP, IntVector2 tilePos, HashSet<IntVector2> SearchedTiles, out IntVector2 tilePosEnemy)
        {
            tilePosEnemy = IntVector2.zero;
            int numScanned = 0;
            foreach (IntVector2 IV2 in SearchedTiles.ToList().OrderBy(x => new Vector2(x.x - tilePos.x, x.y - tilePos.y).sqrMagnitude))
            {
                if (TileHasTargetableEnemy(EP, IV2))
                {
                    tilePosEnemy = IV2;
                    return true;
                }
                numScanned++;
            }
            return false;
        }
        public static bool TileHasTargetableEnemy(NP_Presence EP, IntVector2 tilePos)
        {
            List<NP_TechUnit> ETUe = ManEnemyWorld.GetUnloadedTechsInTile(tilePos);
            return ETUe.Exists(delegate (NP_TechUnit cand) {
                DebugTAC_AI.Assert(cand == null, "TileHasEnemy - cand IS NULL");
                if (cand.tech == null)
                    return false;
                return ManBaseTeams.IsBaseTeamDynamic(cand.tech.m_TeamID)
                && ManBaseTeams.IsEnemy(EP.Team, cand.tech.m_TeamID);
            });
        }
        public static IntVector2 FindTeamBaseTile(NP_Presence EP)
        {
            try
            {
                return RefreshTeamMainBaseIfAnyPossible(EP).tilePos;
            }
            catch { return IntVector2.zero; }
        }
        public static void RemoteRemove(NP_TechUnit ETU)
        {
            try
            {
                ManEnemyWorld.UnhookUnitFromTile(ETU);
                ManEnemyWorld.StopManagingUnit(ETU);
            }
            catch (Exception e) { DebugTAC_AI.Log(KickStart.ModID + ": RemoteRemove - Fail for " + ETU.Name + " - " + e); }
        }
        public static void RemoteRecycle(NP_TechUnit ETU)
        {
            NP_Presence_Automatic EP = ManEnemyWorld.GetTeam(ETU.tech.m_TeamID);
            if (EP != null)
                EP.AddBuildBucks(RawTechBase.GetBBCost(ETU.tech));
            RemoteRemove(ETU);
        }
        public static void RemoteDestroy(NP_TechUnit ETU)
        {
            ManEnemyWorld.TechDestroyedEvent.Send(ETU.tech.m_TeamID, ETU.ID, false);
            RemoteRemove(ETU);
        }

        public static void PurgeAllUnder(NP_Presence EP)
        {
            int count = EP.EBUs.Count;
            for (int step = 0; step < count; count--)
            {
                NP_BaseUnit EBUcase = EP.EBUs.ElementAt(0);
                RemoteRemove(EBUcase);
            }
            int count2 = EP.EMUs.Count;
            for (int step = 0; step < count2; count2--)
            {
                NP_TechUnit ETUcase = EP.EMUs.ElementAt(0);
                RemoteRemove(ETUcase);
            }
        }

        public static bool IsPlayerWithinProvokeDist(IntVector2 tilePos, out Tank offender)
        {
            var list = AIGlobals.GetAllPlayerControlledTechs();
            if (list.Any())
            {
                list.Shuffle();
                foreach (var item in list)
                {
                    if (item != null && (tilePos - WorldPosition.FromScenePosition(item.boundsCentreWorld).TileCoord).
                        WithinBox(ManEnemyWorld.EnemyRaidProvokeExtents))
                    {
                        offender = item;
                        return true;
                    }
                }
            }
            offender = null;
            return false;
        }
        public static bool IsPlayerWithinProvokeDist(IntVector2 tilePos, Tank offender)
        {
            return offender != null && (tilePos - WorldPosition.FromScenePosition(offender.boundsCentreWorld).TileCoord).
                WithinBox(ManEnemyWorld.EnemyRaidProvokeExtents);
        }

        public static bool CanPurgeTeam(NP_Presence EP, NP_BaseUnit EBU)
        {
            return KickStart.CullFarEnemyBases && AIGlobals.CanPurgeTeamNotPlayerOwned(EP.Team) &&
                (EBU.tilePos - WorldPosition.FromScenePosition(Singleton.playerPos).TileCoord).WithinBox(
                    AIGlobals.IgnoreBaseCullingTilesFromOrigin);
        }
        public static bool PurgeIfNeeded(NP_Presence EP, NP_BaseUnit EBU)
        {
            try
            {
                if (EBU != null && CanPurgeTeam(EP, EBU))
                {
                    if (!(EBU.tilePos - WorldPosition.FromScenePosition(Singleton.playerPos).TileCoord).
                        WithinBox(KickStart.CullFarEnemyBasesDistance))
                    {
                        DebugTAC_AI.Log("Removing team at " + EBU.tilePos);
                        PurgeAllUnder(EP);
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                throw e;
            }
            return false;
        }

        internal static void TryUnloadedBaseOperations(NP_Presence EP)
        {
            RefreshTeamMainBaseIfAnyPossible(EP);
            if (EP.MainBase != null)
            {
                if (PurgeIfNeeded(EP, EP.MainBase))
                    return;

                if (AIGlobals.TurboAICheat)
                {
                    if (EP.MainBase.BuildBucks < 1000000)
                        EP.MainBase.AddBuildBucks(AIGlobals.MinimumBBToTryExpand);
                }
                if (ManEnemyWorld.SpecialUpdate == SpecialUpdateType.Building &&
                    EP.MainBase.BuildBucks >= AIGlobals.MinimumBBToTryExpand)
                {
                    if (EP.MainBase != null &&
                        !ManEnemyWorld.IsProcessingTech && UnityEngine.Random.Range(1, 100) <=
                        AIGlobals.BaseExpandChance + (EP.BuildBucks() / 10000))
                        ImTakingThatExpansion(EP, EP.MainBase);
                }
            }
        }

        internal static void ImTakingThatExpansion(NP_Presence EP, NP_BaseUnit EBU)
        {
            try
            {
                if (AIGlobals.IsAttract)
                    return;

                FactionLevel lvl = RawTechLoader.TryGetPlayerLicenceLevel();
                int grade = 99;
                try
                {
                    if (!SpecialAISpawner.CreativeMode)
                        grade = Singleton.Manager<ManLicenses>.inst.GetCurrentLevel(EBU.Faction);
                }
                catch { }

                int Cost = EP.BuildBucks();
                if (EP.GlobalMakerBaseCount() >= KickStart.MaxBasesPerTeam || UnityEngine.Random.Range(0,100) > 45)
                {
                    TryFreeUpBaseSlots(EP, lvl);
                    if (EP.GlobalMobileTechCount() > KickStart.EnemyTeamTechLimit)
                        return;
                    if (!IsActivelySieging(EP))
                    {
                        RawTechPopParams RTF = RawTechPopParams.Default;
                        RTF.Faction = EBU.Faction;
                        RTF.Terrain = BaseTerrain.AnyNonSea;
                        RTF.Purpose = BasePurpose.NotStationary;
                        RTF.Progression = lvl;
                        RTF.TargetFactionGrade = grade;
                        RTF.MaxPrice = Cost;
                        if (RawTechLoader.ShouldUseCustomTechs(out int spawnIndex, RTF))
                        {
                            RawTech BTemp = ModTechsDatabase.ExtPopTechsAllLookup(spawnIndex);
                            ManEnemyWorld.ConstructNewTechExt(EBU, EP, BTemp);
                            DebugTAC_AI.LogDevOnly(KickStart.ModID + ": ImTakingThatExpansion(EXT) - Team " + EP.Team + ": Built new mobile tech " + BTemp.techName);
                            return;
                        }
                        SpawnBaseTypes type = RawTechLoader.GetEnemyBaseType(RTF);
                        if (RawTechLoader.IsFallback(type))
                            return;
                        ManEnemyWorld.ConstructNewTech(EBU, EP, type, !AIGlobals.PlayerCanDetectTile(EBU.tilePos));
                        DebugTAC_AI.LogDevOnly(KickStart.ModID + ": ImTakingThatExpansion - Team " + EP.Team + ": Built new mobile tech " + type);
                    }
                    return;
                }

                BasePurpose reason;
                BaseTerrain Terra;
                ManSaveGame.StoredTile ST = Singleton.Manager<ManSaveGame>.inst.GetStoredTile(EBU.tilePos, true);
                if (ST != null && ManEnemyWorld.FindFreeSpaceOnTileCircle(EBU, ST, out Vector2 newPosOff))
                {
                    Vector3 pos = ManWorld.inst.TileManager.CalcTileOriginScene(ST.coord) + newPosOff.ToVector3XZ();
                    reason = PickBuildBasedOnPriorities(EP, lvl);
                    Terra = RawTechLoader.GetTerrain(pos);
                    RawTechPopParams RTF = RawTechPopParams.Default;
                    RTF.Faction = EBU.Faction;
                    RTF.Terrain = Terra;
                    RTF.Purpose = reason;
                    RTF.Progression = lvl;
                    RTF.TargetFactionGrade = grade;
                    RTF.MaxPrice = Cost;
                    if (RawTechLoader.ShouldUseCustomTechs(out int spawnIndex, RTF))
                    {
                        RawTech BTemp = ModTechsDatabase.ExtPopTechsAllLookup(spawnIndex);
                        ManEnemyWorld.ConstructNewBaseExt(pos, EBU, EP, BTemp);
                        DebugTAC_AI.LogDevOnly(KickStart.ModID + ": ImTakingThatExpansion(EXT) - Team " + EP.Team + ": That expansion is mine!");
                        return;
                    }
                    SpawnBaseTypes type = RawTechLoader.GetEnemyBaseType(RTF);
                    if (RawTechLoader.IsFallback(type))
                        return;
                    ManEnemyWorld.ConstructNewBase(pos, EBU, EP, type, !AIGlobals.PlayerCanDetectTile(EBU.tilePos));
                    DebugTAC_AI.LogDevOnly(KickStart.ModID + ": ImTakingThatExpansion - Team " + EP.Team + ": That expansion is mine!");
                }
                else
                {
                    TryFreeUpBaseSlots(EP, lvl);
                    RefreshTeamMainBaseIfAnyPossible(EP);
                    if (EP.GlobalMobileTechCount() > KickStart.EnemyTeamTechLimit)
                        return;
                    if (!IsActivelySieging(EP))
                    {
                        Terra = RawTechLoader.GetTerrain(EBU.tech.GetBackwardsCompatiblePosition());
                        RawTechPopParams RTF = RawTechPopParams.Default;
                        RTF.Faction = EBU.Faction;
                        RTF.Terrain = Terra;
                        RTF.Purpose = BasePurpose.NotStationary;
                        RTF.Progression = lvl;
                        RTF.TargetFactionGrade = grade;
                        RTF.MaxPrice = Cost;
                        if (RawTechLoader.ShouldUseCustomTechs(out int spawnIndex, RTF))
                        {
                            RawTech BTemp = ModTechsDatabase.ExtPopTechsAllLookup(spawnIndex);
                            ManEnemyWorld.ConstructNewTechExt(EBU, EP, BTemp);
                            DebugTAC_AI.LogDevOnly(KickStart.ModID + ": ImTakingThatExpansion(EXT) - Team " + EP.Team + ": Built new mobile tech " + BTemp.techName);
                            return;
                        }
                        SpawnBaseTypes type = RawTechLoader.GetEnemyBaseType(RTF);
                        if (RawTechLoader.IsFallback(type))
                            return;
                        ManEnemyWorld.ConstructNewTech(EBU, EP, type, !AIGlobals.PlayerCanDetectTile(EBU.tilePos));
                        DebugTAC_AI.LogDevOnly(KickStart.ModID + ": ImTakingThatExpansion - Team " + EP.Team + ": Built new mobile tech " + type);
                    }
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": ImTakingThatExpansion - Error on execution: " + e);
            }
        }
        internal static void TryFreeUpBaseSlots(NP_Presence EP, FactionLevel lvl)
        {
            try
            {
                NP_BaseUnit Main = RefreshTeamMainBaseIfAnyPossible(EP);
                int TeamBaseCount = EP.GlobalMakerBaseCount();
                bool RemoveSpenders = EP.BuildBucks() < RawTechLoader.CheapestAutominerPrice(Main.Faction, lvl) / 2;
                bool ForceRemove = TeamBaseCount > KickStart.MaxBasesPerTeam;

                int attempts = 1;
                int step = 0;

                if (ForceRemove)
                {
                    attempts = KickStart.MaxBasesPerTeam - TeamBaseCount;
                }

                foreach (NP_BaseUnit fund in EP.EBUs.ToList().OrderBy((F) => F.MaxHealth))
                {
                    if (fund != Main)
                    {
                        if (ForceRemove)
                        {
                            RemoteRecycle(fund);
                            if (step >= attempts)
                                return;
                        }
                        if (RemoveSpenders && fund.Health < fund.MaxHealth
                            && fund.isTechBuilder && !fund.handlesChunks)
                        {
                            RemoteRecycle(fund);
                            if (step >= attempts)
                                return;
                        }
                        step++;
                    }
                }
            }
            catch
            {
                DebugTAC_AI.Log(KickStart.ModID + ": TryFreeUpBaseSlots - game is being stubborn");
            }
        }
        private static BasePurpose PickBuildBasedOnPriorities(NP_Presence EP, FactionLevel lvl)
        {
            if (EP.BuildBucks() <= RawTechLoader.CheapestAutominerPrice(RefreshTeamMainBaseIfAnyPossible(EP).Faction, lvl) &&
                !HasTooMuchOfType(EP, BasePurpose.Autominer))
            {
                return BasePurpose.Autominer;
            }
            else if (EP.WasInCombat())
            {
                switch (UnityEngine.Random.Range(1, 7))
                {
                    case 1:
                        if (HasTooMuchOfType(EP, BasePurpose.Defense))
                            return BasePurpose.TechProduction;
                        return BasePurpose.Defense;
                    case 2:
                        if (HasTooMuchOfType(EP, BasePurpose.Harvesting))
                            return BasePurpose.TechProduction;
                        return BasePurpose.Harvesting;
                    case 3:
                        if (HasTooMuchOfType(EP, BasePurpose.HasReceivers))
                            return BasePurpose.TechProduction;
                        return BasePurpose.HasReceivers;
                    case 4:
                        return BasePurpose.TechProduction;
                    case 5:
                        if (HasTooMuchOfType(EP, BasePurpose.Autominer))
                            return BasePurpose.TechProduction;
                        return BasePurpose.Autominer;
                    default:
                        if (HasTooMuchOfType(EP, BasePurpose.Defense))
                            return BasePurpose.TechProduction;
                        return BasePurpose.Defense;
                }
            }
            else
            {
                switch (UnityEngine.Random.Range(0, 5))
                {
                    case 1:
                        if (HasTooMuchOfType(EP, BasePurpose.Defense))
                            return BasePurpose.TechProduction;
                        return BasePurpose.Defense;
                    case 2:
                        if (HasTooMuchOfType(EP, BasePurpose.Harvesting))
                            return BasePurpose.TechProduction;
                        return BasePurpose.Harvesting;
                    case 3:
                        if (HasTooMuchOfType(EP, BasePurpose.HasReceivers))
                            return BasePurpose.TechProduction;
                        return BasePurpose.HasReceivers;
                    case 4:
                        return BasePurpose.TechProduction;
                    case 5:
                        if (HasTooMuchOfType(EP, BasePurpose.Autominer))
                            return BasePurpose.TechProduction;
                        return BasePurpose.Autominer;
                    default:
                        if (HasTooMuchOfType(EP, BasePurpose.Harvesting))
                            return BasePurpose.TechProduction;
                        return BasePurpose.AnyNonHQ;
                }
            }
        }
        private static BasePurpose PickBuildNonDefense(NP_Presence EP)
        {
            switch (UnityEngine.Random.Range(0, 5))
            {
                case 2:
                    if (HasTooMuchOfType(EP, BasePurpose.Harvesting))
                        return BasePurpose.TechProduction;
                    return BasePurpose.Harvesting;
                case 3:
                    if (HasTooMuchOfType(EP, BasePurpose.HasReceivers))
                        return BasePurpose.TechProduction;
                    return BasePurpose.HasReceivers;
                case 4:
                case 5:
                    return BasePurpose.TechProduction;
                default:
                    if (HasTooMuchOfType(EP, BasePurpose.Autominer))
                        return BasePurpose.TechProduction;
                    return BasePurpose.Autominer;
            }
        }


        private static bool TryFindExpansionLocation(NP_TechUnit tank, WorldPosition WP, out Vector3 pos)
        {
            bool chained = false;
            Quaternion quat = tank.tech.m_Rotation;
            if (WP == null)
            {
                WP = WorldPosition.FromScenePosition(tank.tech.GetBackwardsCompatiblePosition());
            }
            if (IsLocationValid(WP.TileCoord, WP.TileRelativePos + ((quat * Vector3.forward) * 64), ref chained))
            {
                pos = WP.ScenePosition + ((quat * Vector3.forward) * 64);
                return true;
            }
            else if (IsLocationValid(WP.TileCoord, WP.TileRelativePos - ((quat * Vector3.forward) * 64), ref chained))
            {
                pos = WP.ScenePosition - ((quat * Vector3.forward) * 64);
                return true;
            }
            else if (IsLocationValid(WP.TileCoord, WP.TileRelativePos - ((quat * Vector3.right) * 64), ref chained))
            {
                pos = WP.ScenePosition - ((quat * Vector3.right) * 64);
                return true;
            }
            else if (IsLocationValid(WP.TileCoord, WP.TileRelativePos + ((quat * Vector3.right) * 64), ref chained))
            {
                pos = WP.ScenePosition + ((quat * Vector3.right) * 64);
                return true;
            }
            else
            {
                pos = WP.ScenePosition;
                return false;
            }
        }
        private static bool TryFindExpansionLocation2(NP_TechUnit tank, WorldPosition WP, out Vector3 pos)
        {
            bool chained = false;
            Quaternion quat = tank.tech.m_Rotation;
            if (WP == null)
            {
                WP = WorldPosition.FromScenePosition(tank.tech.GetBackwardsCompatiblePosition());
            }
            if (IsLocationValid(WP.TileCoord, WP.TileRelativePos + (((quat * Vector3.right) + (quat * Vector3.forward)) * 64), ref chained))
            {
                pos = WP.ScenePosition + (((quat * Vector3.right) + (quat * Vector3.forward)) * 64);
                return true;
            }
            else if (IsLocationValid(WP.TileCoord, WP.TileRelativePos - (((quat * Vector3.right) + (quat * Vector3.forward)) * 64), ref chained))
            {
                pos = WP.ScenePosition - (((quat * Vector3.right) + (quat * Vector3.forward)) * 64);
                return true;
            }
            else if (IsLocationValid(WP.TileCoord, WP.TileRelativePos + (((quat * Vector3.right) - (quat * Vector3.forward)) * 64), ref chained))
            {
                pos = WP.ScenePosition + (((quat * Vector3.right) - (quat * Vector3.forward)) * 64);
                return true;
            }
            else if (IsLocationValid(WP.TileCoord, WP.TileRelativePos - (((quat * Vector3.right) - (quat * Vector3.forward)) * 64), ref chained))
            {
                pos = WP.ScenePosition - (((quat * Vector3.right) - (quat * Vector3.forward)) * 64);
                return true;
            }
            else
            {
                pos = WP.ScenePosition;
                return false;
            }
        }
        private static bool IsLocationValid(IntVector2 TileCoord, Vector3 posInTile, ref bool ChainCancel)
        {
            if (ChainCancel)
                return false;
            bool validLocation = true;

            foreach (NP_TechUnit ETU in ManEnemyWorld.GetTechsInTile(TileCoord, posInTile, 32))
            {
                if (ETU is NP_BaseUnit EBU)
                {
                    if (EBU.Health < EBU.MaxHealth)
                        ChainCancel = true;
                }
                validLocation = false;
            }
            return validLocation;
        }

        private static bool IsActivelySieging(NP_Presence EP)
        {
            if (ManEnemySiege.SiegingEnemyTeam != null)
            {
                if (ManEnemySiege.SiegingEnemyTeam == EP)
                    return true;
            }
            return false;
        }

        private static readonly FieldInfo ProdSys = typeof(ModuleRecipeProvider).GetField("m_RecipeLists", BindingFlags.NonPublic | BindingFlags.Instance);
        private static List<RecipeListWrapper> chunkConverter;
        private static readonly List<RecipeTable.Recipe> chunkConversion = new List<RecipeTable.Recipe>();
        public static ChunkTypes TransChunker(ChunkTypes CT)
        {
            if (chunkConverter == null)
            {
                chunkConverter = ((RecipeListWrapper[])ProdSys.GetValue(
                    ManSpawn.inst.GetBlockPrefab(BlockTypes.GSORefinery_222).GetComponent<ModuleRecipeProvider>())).ToList();
                foreach (RecipeListWrapper RLW in chunkConverter)
                {
                    chunkConversion.AddRange(RLW.target.m_Recipes);
                }
            }
            if (CT == ChunkTypes._deprecated_Stone)
                return ChunkTypes._deprecated_Stone;
            try
            {
                return (ChunkTypes)chunkConversion.Find(x => x.InputsContain(new ItemTypeInfo(ObjectTypes.Chunk, (int)CT))).m_OutputItems.FirstOrDefault().m_Item.ItemType;
            }
            catch { }
            return ChunkTypes._deprecated_Stone;
        }
        public static ChunkTypes[] GetBiomeResourcesSurface(Vector3 pos)
        {
            switch (ManWorld.inst.GetBiomeWeightsAtScenePosition(pos).Biome(0).BiomeType)
            {
                case BiomeTypes.Grassland:
                    return new ChunkTypes[12] {
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.RubberJelly,
                        ChunkTypes.RubberJelly,
                        ChunkTypes.LuxiteShard,
                        ChunkTypes.LuxiteShard,
                        ChunkTypes.PlumbiteOre,
                        ChunkTypes.TitaniteOre,
                        ChunkTypes.EruditeShard,
                    };
                case BiomeTypes.Desert:
                    return new ChunkTypes[11] {
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.RubberJelly,
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.RubberJelly,
                        ChunkTypes.OleiteJelly,
                        ChunkTypes.OleiteJelly,
                        ChunkTypes.IgniteShard
                    };
                case BiomeTypes.Mountains:
                    return new ChunkTypes[9] {
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.RubberJelly,
                        ChunkTypes.PlumbiteOre,
                        ChunkTypes.TitaniteOre,
                        ChunkTypes.PlumbiteOre,
                        ChunkTypes.TitaniteOre,
                        ChunkTypes.RoditeOre,
                    };
                case BiomeTypes.SaltFlats:
                case BiomeTypes.Ice:
                    return new ChunkTypes[9] {
                        ChunkTypes.PlumbiteOre,
                        ChunkTypes.TitaniteOre,
                        ChunkTypes.PlumbiteOre,
                        ChunkTypes.TitaniteOre,
                        ChunkTypes.PlumbiteOre,
                        ChunkTypes.TitaniteOre,
                        ChunkTypes.CarbiteOre,
                        ChunkTypes.CarbiteOre,
                        ChunkTypes.CelestiteShard };

                case BiomeTypes.Pillars:
                    return new ChunkTypes[10] {
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.Wood,
                        ChunkTypes.RubberJelly,
                        ChunkTypes.CelestiteShard,
                        ChunkTypes.IgniteShard,
                        ChunkTypes.EruditeShard,
                        ChunkTypes.CelestiteShard,
                        ChunkTypes.IgniteShard,
                        ChunkTypes.EruditeShard,
                    };

                default:
                    return new ChunkTypes[2] { ChunkTypes.PlumbiteOre, ChunkTypes.TitaniteOre };
            }
        }
    }
}
