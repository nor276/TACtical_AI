using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using DevCommands;
using HarmonyLib;
using SafeSaves;
using TAC_AI.Templates;
using TerraTechETCUtil;
using UnityEngine;
using static Rewired.Data.Mapping.HardwareJoystickMap;

namespace TAC_AI
{
    public static class AICommands
    {
        [DevCommand(Name = KickStart.ModCommandID + ".ForceSpawnBase", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn ForceEnemyBaseSpawn()
        {
            RawTechLoader.FORCESpawnBaseAtPositionNoFounder(FactionSubTypes.NULL, ManPointer.inst.targetPosition, -9001, BasePurpose.AnyNonHQ);
            return new CommandReturn
            {
                message = "Spawned new Tech",
                success = true,
            };
        }
        [DevCommand(Name = KickStart.ModCommandID + ".OpenAIDebug", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn OpenDebugMenu()
        {
            DebugRawTechSpawner.LaunchSubMenuClickable(DebugMenus.DebugLog);
            return new CommandReturn
            {
                message = "Opened AI Debug Menu",
                success = true,
            };
        }
        [DevCommand(Name = KickStart.ModCommandID + ".SpawnAIDebug", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn OpenSpawnMenu()
        {
            DebugRawTechSpawner.LaunchSubMenuClickable(DebugMenus.Prefabs);
            return new CommandReturn
            {
                message = "Opened AI Prefab Spawn Menu",
                success = true,
            };
        }
        [DevCommand(Name = KickStart.ModCommandID + ".LocalSpawnAIDebug", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn OpenSpawnLocalMenu()
        {
            DebugRawTechSpawner.LaunchSubMenuClickable(DebugMenus.Local);
            return new CommandReturn
            {
                message = "Opened AI Local Spawn Menu",
                success = true,
            };
        }

        public enum PurgeState
        {
            None = 0,
            PurgeAI,
            PurgeEVERYTHING,
            PurgeFailedToRestore,
        }

        internal static PurgeState AreYouSurePurge = PurgeState.None;

        [DevCommand(Name = KickStart.ModCommandID + ".LogAllSavedSerialized", Access = Access.Public, Users = User.Host)]
        public static CommandReturn LogAllSavedSerialized()
        {
            var jsonTiles = ManSaveGame.inst.CurrentState?.m_StoredTilesJSON;
            if (jsonTiles != null && jsonTiles != null)
            {
                ManSaveGame.StoredTile storedTile = null;
                int TechCounts = 0;
                int BlockCounts = 0;
                int ChunkCounts = 0;
                Dictionary<ObjectTypes, int> others = new Dictionary<ObjectTypes, int>();
                var tiles = ManSaveGame.inst.CurrentState?.m_StoredTiles;
                foreach (var tile in tiles)
                {
                    if (tile.Value == null)
                        continue;
                    storedTile = tile.Value;
                    if (storedTile?.m_StoredVisibles != null)
                    {
                        if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out var techs) && techs != null)
                        {   // Try in the unloaded tile!?
                            for (int step = 0; step < techs.Count; step++)
                            {
                                var val = techs[step] as ManSaveGame.StoredTech;
                                if (val != null)
                                {
                                    TechCounts++;
                                    ManUI.inst.ShowErrorPopup("Tech ID: " + val.m_ID + " of " + val.m_TeamID + ", count: " + val.m_TechData.m_BlockSpecs.Count +
                                        ", BaseTeam: " + ManBaseTeams.IsBaseTeamAny(val.m_TeamID).ToString());
                                }
                            }
                        }
                        if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Chunk, out var chuns) && chuns != null)
                        {   // Try in the unloaded tile!?
                            for (int step = 0; step < chuns.Count; step++)
                            {
                                var val = chuns[step];
                                if (val != null)
                                    ChunkCounts++;
                            }
                        }
                        if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Block, out var blocs) && blocs != null)
                        {   // Try in the unloaded tile!?
                            for (int step = 0; step < blocs.Count; step++)
                            {
                                var val = blocs[step];
                                if (val != null)
                                    BlockCounts++;
                            }
                        }
                        foreach (var item in storedTile.m_StoredVisibles)
                        {
                            if (item.Value != null && item.Key != ((int)ObjectTypes.Vehicle) && item.Key != ((int)ObjectTypes.Block)
                                && item.Key != ((int)ObjectTypes.Chunk))
                            {
                                if (!others.ContainsKey((ObjectTypes)item.Key))
                                    others.Add((ObjectTypes)item.Key, 0);
                                others[(ObjectTypes)item.Key] += item.Value.Count;
                            }
                        }
                    }
                }
                foreach (var tile in jsonTiles)
                {
                    if (tile.Value.NullOrEmpty())
                        continue;
                    storedTile = null;
                    if (!ManSaveGame.inst.CurrentState.m_StoredTiles.TryGetValue(tile.Key, out storedTile))
                    {
                        ManSaveGame.LoadObjectFromRawJson(ref storedTile, tile.Value, false, false);
                    }
                    if (storedTile?.m_StoredVisibles != null)
                    {
                        if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out var techs) && techs != null)
                        {   // Try in the unloaded tile!?
                            for (int step = 0; step < techs.Count; step++)
                            {
                                var val = techs[step] as ManSaveGame.StoredTech;
                                if (val != null)
                                {
                                    TechCounts++;
                                    ManUI.inst.ShowErrorPopup("Tech ID: " + val.m_ID + " of " + val.m_TeamID + ", count: " + val.m_TechData.m_BlockSpecs.Count +
                                        ", BaseTeam: " + ManBaseTeams.IsBaseTeamAny(val.m_TeamID).ToString());
                                }
                            }
                        }
                        if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Chunk, out var chuns) && chuns != null)
                        {   // Try in the unloaded tile!?
                            for (int step = 0; step < chuns.Count; step++)
                            {
                                var val = chuns[step];
                                if (val != null)
                                    ChunkCounts++;
                            }
                        }
                        if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Block, out var blocs) && blocs != null)
                        {   // Try in the unloaded tile!?
                            for (int step = 0; step < blocs.Count; step++)
                            {
                                var val = blocs[step];
                                if (val != null)
                                    BlockCounts++;
                            }
                        }
                        foreach (var item in storedTile.m_StoredVisibles)
                        {
                            if (item.Value != null && item.Key != ((int)ObjectTypes.Vehicle) && item.Key != ((int)ObjectTypes.Block)
                                && item.Key != ((int)ObjectTypes.Chunk))
                            {
                                if (!others.ContainsKey((ObjectTypes)item.Key))
                                    others.Add((ObjectTypes)item.Key, 0);
                                others[(ObjectTypes)item.Key] += item.Value.Count;
                            }
                        }
                    }
                }
                ManUI.inst.ShowErrorPopup("Counted [" + TechCounts + "] Techs");
                ManUI.inst.ShowErrorPopup("Counted [" + BlockCounts + "] loose blocks");
                ManUI.inst.ShowErrorPopup("Counted [" + ChunkCounts + "] loose chunks");
                foreach (var item in others)
                {
                    ManUI.inst.ShowErrorPopup("Counted [" + item.Value + "] Other: " + item.Key);
                }
            }
            return new CommandReturn
            {
                message = "LOGGED ALL SERIALIZED",
                success = true,
            };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".DESTROY_BROKEN_MARKERS", Access = Access.Public, Users = User.Host)]
        public static CommandReturn DESTROY_BROKEN_MARKERS()
        {
            int count = AIGlobals.MassDeleteALLOrphanTrackedVisibles();
            return new CommandReturn
            {
                message = "Removed [" + count + "] broken map markers assigned to nothing",
                success = true,
            };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".DESTROY_MOD_ENEMIES", Access = Access.Public, Users = User.Host)]
        public static CommandReturn DESTROY_MOD_ENEMIES()
        {
            if (AreYouSurePurge != PurgeState.PurgeAI)
            {
                AreYouSurePurge = PurgeState.PurgeAI;
                return new CommandReturn
                {
                    message = "Are you sure you want to do this?  It may mess with any missions you have active so finish them all first.  Call this again to purge all.",
                    success = true,
                };
            }
            AreYouSurePurge = PurgeState.None;
            int count = AIGlobals.MassDeleteALLEnemyTeamTechs();
            return new CommandReturn
            {
                message = "Removed [" + count + "] techs managed by Advanced A.I.",
                success = true,
            };
        }
        private static MethodInfo hack = AccessTools.GetDeclaredMethods(typeof(ManSafeSaves)).First(x => x.Name == "ModeSwitch");
        [DevCommand(Name = KickStart.ModCommandID + ".DESTROY_MOD_SAVEDATA", Access = Access.Public, Users = User.Host)]
        public static CommandReturn DESTROY_MOD_SAVEDATA()
        {
            hack.Invoke(null, Array.Empty<object>());
            return new CommandReturn
            {
                message = "Removed ALL SAVE DATA, INCLUDING THAT OF SOME OTHER MODS",
                success = true,
            };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".DESTROY_EVERYTHING", Access = Access.Public, Users = User.Host)]
        public static CommandReturn DESTROY_EVERYTHING()
        {
            if (AreYouSurePurge != PurgeState.PurgeEVERYTHING)
            {
                AreYouSurePurge = PurgeState.PurgeEVERYTHING;
                return new CommandReturn
                {
                    message = "Are you sure you want to do this?  It WILL mess with any missions you have active so finish them all first.  IT WILL ALSO DESTROY EVERY BLOCK AND CHUNK IN THE WORLD THAT ISN'T STORED IN YOUR INVENTORY!!  Call this again to purge EVERYTHING.",
                    success = true,
                };
            }
            AreYouSurePurge = PurgeState.None;

            var jsonTiles = ManSaveGame.inst.CurrentState?.m_StoredTilesJSON;
            var tiles = ManSaveGame.inst.CurrentState?.m_StoredTiles;
            if (jsonTiles != null && jsonTiles != null)
            {
                ManSaveGame.StoredTile storedTile = null;
                bool change = true;
                while (change)
                {
                    jsonTiles = ManSaveGame.inst.CurrentState?.m_StoredTilesJSON;
                    tiles = ManSaveGame.inst.CurrentState?.m_StoredTiles;
                    change = false;
                    try
                    {
                        foreach (var tile in new Dictionary<IntVector2, ManSaveGame.StoredTile>(tiles))
                        {
                            if (tile.Value == null)
                                continue;
                            storedTile = tile.Value;
                            if (storedTile?.m_StoredVisibles != null)
                            {
                                if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out var techs) && techs != null)
                                {   // Try in the unloaded tile!?
                                    for (int step = 0; step < techs.Count; step++)
                                    {
                                        var val = techs[step] as ManSaveGame.StoredTech;
                                        if (val != null && (AIGlobals.BaseTeamVisibleIsSafelyRemoveable(val.m_ID, val.m_TeamID) || (val.m_TechData?.LocalisedName != null
                                            && AIGlobals.CanPurgeTradingStation(val.m_TeamID, val.m_TechData.LocalisedName.m_Id))))
                                        {
                                            change = true;
                                            techs.RemoveAt(step);
                                        }
                                    }
                                }
                                if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Chunk, out var chuns) && chuns != null)
                                {   // Try in the unloaded tile!?
                                    if (chuns.Any())
                                    {
                                        chuns.Clear();
                                        change = true;
                                    }
                                }
                                if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Block, out var blocs) && blocs != null)
                                {   // Try in the unloaded tile!?
                                    if (blocs.Any())
                                    {
                                        blocs.Clear();
                                        change = true;
                                    }
                                }
                            }
                        }
                        foreach (var tile in new Dictionary<IntVector2, string>(jsonTiles))
                        {
                            if (tile.Value.NullOrEmpty())
                                continue;
                            storedTile = null;
                            if (!ManSaveGame.inst.CurrentState.m_StoredTiles.TryGetValue(tile.Key, out storedTile))
                            {
                                ManSaveGame.LoadObjectFromRawJson(ref storedTile, tile.Value, false, false);
                            }
                            if (storedTile?.m_StoredVisibles != null)
                            {
                                if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Vehicle, out var techs) && techs != null)
                                {   // Try in the unloaded tile!?
                                    for (int step = 0; step < techs.Count; step++)
                                    {
                                        var val = techs[step] as ManSaveGame.StoredTech;
                                        if (val != null && (AIGlobals.BaseTeamVisibleIsSafelyRemoveable(val.m_ID, val.m_TeamID) || (val.m_TechData?.LocalisedName != null
                                            && AIGlobals.CanPurgeTradingStation(val.m_TeamID, val.m_TechData.LocalisedName.m_Id))))
                                        {
                                            change = true;
                                            techs.RemoveAt(step);
                                        }
                                    }
                                }
                                if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Chunk, out var chuns) && chuns != null)
                                {   // Try in the unloaded tile!?
                                    if (chuns.Any())
                                    {
                                        chuns.Clear();
                                        change = true;
                                    }
                                }
                                if (storedTile.m_StoredVisibles.TryGetValue((int)ObjectTypes.Block, out var blocs) && blocs != null)
                                {   // Try in the unloaded tile!?
                                    if (blocs.Any())
                                    {
                                        blocs.Clear();
                                        change = true;
                                    }
                                }
                                if (change)
                                {
                                    ManSaveGame.inst.CurrentState.m_StoredTilesJSON.Remove(tile.Key);
                                    ManSaveGame.inst.CurrentState.m_StoredTilesJSON.Add(tile.Key, ManSaveGame.SaveObjectToRawJson(storedTile));
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        ManUI.inst.ShowErrorPopup(e.ToString());
                        break;
                    }
                }
            }
            LogAllSavedSerialized();
            return new CommandReturn
            {
                message = "PURGED EVERYTHING LOOSE AND ALL MOD TECHS",
                success = true,
            };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".DESTROY_ALL_FAILED", Access = Access.Public, Users = User.Host)]
        public static CommandReturn DESTROY_ALL_FAILED()
        {
            if (AreYouSurePurge != PurgeState.PurgeFailedToRestore)
            {
                AreYouSurePurge = PurgeState.PurgeFailedToRestore;
                return new CommandReturn
                {
                    message = "Are you sure you want to do this?  This will cause anything that failed to spawn to never spawn again!  Call this again to purge everything that failed to spawn.",
                    success = true,
                };
            }
            AreYouSurePurge = PurgeState.None;

            int purgeCount = 0;
            if (ManSaveGame.inst.CurrentState.m_VisiblesFailedToRestore != null)
            {
                purgeCount = ManSaveGame.inst.CurrentState.m_VisiblesFailedToRestore.Count;
                ManSaveGame.inst.CurrentState.m_VisiblesFailedToRestore = null;
            }
            return new CommandReturn
            {
                message = "PURGED [" + purgeCount + "] visibles that failed to restore!",
                success = true,
            };
        }
    }
}
