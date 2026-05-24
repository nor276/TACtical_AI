using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using System.Reflection;
using TerraTechETCUtil;
using TAC_AI.AI;
using TAC_AI.World;
using HarmonyLib;

namespace TAC_AI
{
    internal class ManagerPatches
    {
        internal static class ManSpawnPatches
        {
            internal static Type target = typeof(ManSpawn);
            private static void OnDLCLoadComplete_Postfix(ManSpawn __instance)
            {
                try
                {
                    ModStatusChecker.EncapsulateSafeInit("Advanced AI", ManWorldRTS.DelayedInitiate, KickStart.DeInitALL);
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogError(KickStart.ModID + ": OnDLCLoadComplete_Postfix - ManWorldRTS.DelayedInitiate failed: " + e);
                }
            }
        }

        internal static class ManLooseBlocksPatches
        {
            internal static Type target = typeof(ManLooseBlocks);

            private static bool OnServerAttachBlockRequest_Prefix(ManLooseBlocks __instance, ref NetworkMessage netMsg)
            {
                if (AIERepair.NonPlayerAttachAllow)
                {
                    BlockAttachedMessage BAM = netMsg.ReadMessage<BlockAttachedMessage>();
                    NetTech NetT = NetworkServer.FindLocalObject(BAM.m_TechNetId).GetComponent<NetTech>();
                    TankBlock canidate = ManLooseBlocks.inst.FindTankBlock(BAM.m_BlockPoolID);
                    bool attached;
                    if (NetT == null)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": BlockAttachNetworkOverrideServer - NetTech is NULL!");
                    }
                    else if (canidate == null)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": BlockAttachNetworkOverrideServer - BLOCK IS NULL!");
                    }
                    else
                    {
                        Tank tank = NetT.tech;
                        NetBlock netBlock = canidate.netBlock;
                        if (netBlock.IsNull())
                        {
                            DebugTAC_AI.Log(KickStart.ModID + ": BlockAttachNetworkOverrideServer - NetBlock could not be found on AI block attach attempt!");
                        }
                        else
                        {
                            OrthoRotation OR = new OrthoRotation((OrthoRotation.r)BAM.m_BlockOrthoRotation);
                            attached = tank.blockman.AddBlockToTech(canidate, BAM.m_BlockPosition, OR);
                            if (attached)
                            {
                                Singleton.Manager<ManNetwork>.inst.ServerNetBlockAttachedToTech.Send(tank, netBlock, canidate);
                                tank.GetHelperInsured().dirtyExtents = true;

                                Singleton.Manager<ManNetwork>.inst.SendToAllExceptHost(TTMsgType.BlockAttach, BAM);
                                if (netBlock.block != null)
                                {
                                    netBlock.Disconnect();
                                }
                                if (Singleton.Manager<ManNetwork>.inst.IsServer)
                                {
                                    netBlock.RemoveClientAuthority();
                                    NetworkServer.UnSpawn(netBlock.gameObject);
                                }
                                netBlock.transform.Recycle(worldPosStays: false);
                            }
                        }
                    }
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Ensures every tech registered with <c>ManTechs.RegisterTank</c> has a
        /// <see cref="TankAIHelper"/> attached before any other system queries it.
        /// </summary>
        internal static class ManTechsPatches
        {
            internal static Type target = typeof(ManTechs);

            private static void RegisterTank_Postfix(ManTechs __instance, ref Tank t)
            {
                t.GetHelperInsured();
            }
        }

        // Suppresses vanilla camera-distance sleep for mod-managed hostile techs within EnemyKeepAwakeRange,
        // so distant enemies keep ticking and moving instead of going kinematic and freezing in place.
        // Vanilla still handles wake-up (TestWakeSleepingTechs) and MP-skip and friendly-skip naturally.
        internal static class ManTechsSleepPatches
        {
            internal static Type target = typeof(ManTechs);

            private static bool CheckSleepRange_Prefix(ManTechs __instance, Tank tech)
            {
                if (!KickStart.EnableBetterAI) return true;
                if (tech == null || tech.IsSleeping) return true;
                if (!ManBaseTeams.IsEnemyBaseTeam(tech.Team)) return true;  // hostile mod teams only

                float d2 = (tech.boundsCentreWorld - Singleton.cameraTrans.position).sqrMagnitude;
                if (d2 > AIGlobals.EnemyKeepAwakeRange * AIGlobals.EnemyKeepAwakeRange)
                    return true;  // beyond cap -> let vanilla put it to sleep (NP_* sim takes over)

                return false;  // suppress vanilla sleep this frame
            }
        }

        internal static class ManSaveGamePatches
        {
            internal static Type target = typeof(ManSaveGame);

            private static void RestoreVisible_Postfix(ManSaveGame.StoredTile __instance,
                ref ManSaveGame.StoredVisible storedVisible, ref Visible __result)
            {
                if (__result)
                    ManEnemyWorld.VisibleLoaded(__result);
            }
            private static void CreateStoredVisible_Postfix(Visible visible,
                ManSaveGame.StoredVisible __result)
            {
                if (__result != null)
                    ManEnemyWorld.VisibleUnloaded(__result);
            }
        }

        internal static class TileManagerPatches
        {
            internal static Type target = typeof(TileManager);

            private static void UpdateTileRequestStates_Postfix(TileManager __instance,
                ref List<IntVector2> tileCoordsToCreate)
            {
                ManEnemyWorld.OnBeforeTilesSpawn(tileCoordsToCreate);
            }
        }

        internal static class ManNetworkPatches
        {
            internal static Type target = typeof(ManNetwork);
            // Multi-Player
            private static void AddPlayer_Postfix(ManNetwork __instance)
            {
                // Setup aircraft if Population Injector is N/A
                try
                {
                    if (ManNetwork.IsHost && KickStart.EnableBetterAI)
                        TankAIManager.inst.Invoke("WarnPlayers", 16);
                }
                catch { }
            }
        }
    }
}
