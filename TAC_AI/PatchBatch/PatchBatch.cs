using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using TAC_AI.AI;
using TAC_AI.AI.Enemy;
using TAC_AI.Templates;
using TAC_AI.World;

namespace TAC_AI
{
    class PatchBatch
    {
    }

    internal enum AttractType
    {
        Harvester,
        Invader,
        SpaceInvader,
        Dogfight,
        SpaceBattle,
        NavalWarfare,
        BaseSiege,
        BaseVBase,
        Misc,
    }

    internal static class Patches
    {
        [HarmonyPatch(typeof(ManMusic))]
        [HarmonyPatch("SetDanger", new Type[1] { typeof(ManMusic.DangerContext.Circumstance) })]
        private class AdvancedMenuMusiks
        {
            internal static bool Prefix(ManMusic __instance, ManMusic.DangerContext.Circumstance circumstance)
            {
                if (circumstance == ManMusic.DangerContext.Circumstance.Generic)
                {
                    switch (KickStart.factionAttractOST)
                    {
                        case FactionSubTypes.GSO:
                        case FactionSubTypes.GC:
                        case FactionSubTypes.EXP:
                        case FactionSubTypes.VEN:
                        case FactionSubTypes.HE:
                        case FactionSubTypes.BF:
                            __instance.SetDanger(ManMusic.DangerContext.Circumstance.SetPiece, KickStart.factionAttractOST);
                            return false;
                        case FactionSubTypes.NULL:
                        case FactionSubTypes.SPE:
                        default:
                            return true;
                    }
                }
                return true;
            }
        }
        [HarmonyPatch(typeof(ManMusic))]
        [HarmonyPatch("PlayMusicEvent")]
        private class AdvancedMenuMusiks2
        {
            internal static void Prefix(ManMusic __instance, ref ManMusic.MusicTypes musicType)
            {
                if (musicType == ManMusic.MusicTypes.Attract)
                {
                    if (KickStart.factionAttractOST == FactionSubTypes.NULL)
                    {
                        KickStart.factionAttractOST = (FactionSubTypes)UnityEngine.Random.Range(1, Enum.GetValues(typeof(FactionSubTypes)).Length);
                        DebugTAC_AI.Log("Attract OST set to " + KickStart.factionAttractOST);
                    }
                    switch (KickStart.factionAttractOST)
                    {
                        case FactionSubTypes.GSO:
                        case FactionSubTypes.GC:
                        case FactionSubTypes.EXP:
                        case FactionSubTypes.VEN:
                        case FactionSubTypes.HE:
                        case FactionSubTypes.BF:
                            musicType = ManMusic.MusicTypes.Main;
                            __instance.EnableSequencing = true;
                            var prof = ManProfile.inst.GetCurrentUser();
                            if (prof != null)
                            {
                                __instance.SetMusicMixerVolume(prof.m_SoundSettings.m_MusicVolume * 0.75f);
                            }
                            break;
                        case FactionSubTypes.NULL:
                        case FactionSubTypes.SPE:
                        default:
                            break;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ModuleItemHolder.Stack))]
        [HarmonyPatch("Take", new Type[] { typeof(Visible), typeof(bool), typeof(int), typeof(bool) })]
        private class TakeDetect
        {
            internal static void Prefix(ModuleItemHolder __instance, ref Visible item)
            {
                if (__instance.block?.tank &&
                    __instance.block.tank.Team == ManSpawn.NeutralTeam)
                {
                    if (item.holderStack?.myHolder?.block?.tank != null)
                    {
                        int prevHolderTeam = item.holderStack.myHolder.block.tank.Team;
                        if (prevHolderTeam == ManSpawn.NeutralTeam)
                        {
                        }
                        else if (ManBaseTeams.IsBaseTeamDynamic(prevHolderTeam))
                        {
                            if (!ManBaseTeams.inst.TradingSellOffers.ContainsKey(item.ID))
                            {
                                ManBaseTeams.inst.TradingSellOffers.Add(item.ID, prevHolderTeam);
                                item.RecycledEvent.Subscribe(ManBaseTeams.PickupRecycled);
                            }
                        }
                        else if (ManBaseTeams.inst.TradingSellOffers.ContainsKey(item.ID))
                        {
                            ManBaseTeams.inst.TradingSellOffers.Remove(item.ID);
                            item.RecycledEvent.Unsubscribe(ManBaseTeams.PickupRecycled);
                        }
                    }
                }
            }
        }
#if DEBUG
#endif
        [HarmonyPatch(typeof(Tank))]
        [HarmonyPatch("IsEnemy", typeof(int), typeof(int))]
        private static class TankTeamPatch
        {
            internal static bool Prefix(ref bool __result, ref int teamID1, ref int teamID2)
            {
                if (ManBaseTeams.IsUnattackable(teamID1, teamID2))
                {
                    __result = false;
                    return false;
                }
                return true;
            }
        }
        [HarmonyPatch(typeof(Tank))]
        [HarmonyPatch("IsFriendly", typeof(int), typeof(int))]
        private static class TankTeamPatch2
        {
            internal static bool Prefix(ref bool __result, ref int teamID1, ref int teamID2)
            {
                if (ManBaseTeams.IsTeammate(teamID1, teamID2))
                {
                    __result = true;
                    return false;
                }
                return true;
            }
        }


#if !STEAM
        [HarmonyPatch(typeof(ManGameMode))]
        [HarmonyPatch("Awake")]
        private static class StartupSpecialAISpawner
        {
            private static void Postfix()
            {
                if (!KickStart.isPopInjectorPresent)
                {
                    SpecialAISpawner.Initiate();
                    ManEnemyWorld.LateInitiate();
                }
            }
        }
#endif
    }
}
