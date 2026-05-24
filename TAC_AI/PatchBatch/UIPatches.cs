using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FMOD.Studio;
using HarmonyLib;
using TAC_AI.AI;
using TAC_AI.AI.Enemy;
using TAC_AI.Templates;
using TAC_AI.World;
using TerraTechETCUtil;
using UnityEngine;
using UnityEngine.UI;
using static WobblyLaser;

namespace TAC_AI
{
    internal class UIPatches
    {
        internal static class CursorPatches
        {
            internal static Type target = typeof(GameCursor);

            [HarmonyPriority(-200)]
            internal static void GetCursorState_Postfix(ref GameCursor.CursorState __result)
            {
                if (!CursorChanger.AddedNewCursors)
                    return;
                if (ManWorldRTS.PlayerIsInRTS)
                {
                    switch (ManWorldRTS.cursorState)
                    {
                        case RTSCursorState.Empty:
                            break;
                        case RTSCursorState.Moving:
                            __result = CursorChanger.CursorIndexCache[2];
                            break;
                        case RTSCursorState.Attack:
                            __result = CursorChanger.CursorIndexCache[0];
                            break;
                        case RTSCursorState.Select:
                            __result = CursorChanger.CursorIndexCache[3];
                            break;
                        case RTSCursorState.Fetch:
                            __result = CursorChanger.CursorIndexCache[4];
                            break;
                        case RTSCursorState.Mine:
                            __result = CursorChanger.CursorIndexCache[5];
                            break;
                        case RTSCursorState.Protect:
                            __result = CursorChanger.CursorIndexCache[6];
                            break;
                        case RTSCursorState.Scout:
                            __result = CursorChanger.CursorIndexCache[7];
                            break;
                        default:
                            break;
                    }
                }
                else if (ManWorldRTS.PlayerRTSOverlay && __result == GameCursor.CursorState.Default)
                {
                    switch (ManWorldRTS.cursorState)
                    {
                        case RTSCursorState.Empty:
                            break;
                        case RTSCursorState.Moving:
                            __result = CursorChanger.CursorIndexCache[2];
                            break;
                        case RTSCursorState.Attack:
                            __result = CursorChanger.CursorIndexCache[0];
                            break;
                        case RTSCursorState.Select:
                            __result = CursorChanger.CursorIndexCache[3];
                            break;
                        case RTSCursorState.Fetch:
                            __result = CursorChanger.CursorIndexCache[4];
                            break;
                        case RTSCursorState.Mine:
                            __result = CursorChanger.CursorIndexCache[5];
                            break;
                        case RTSCursorState.Protect:
                            __result = CursorChanger.CursorIndexCache[6];
                            break;
                        case RTSCursorState.Scout:
                            __result = CursorChanger.CursorIndexCache[7];
                            break;
                        default:
                            break;
                    }
                }
            }

        }

        internal static class TankDescriptionOverlayPatches
        {
            internal static Type target = typeof(TankDescriptionOverlay);

            static readonly FieldInfo tech = typeof(TankDescriptionOverlay).GetField("m_Tank", BindingFlags.NonPublic | BindingFlags.Instance),
                panel = typeof(TankDescriptionOverlay).GetField("m_PanelInst", BindingFlags.NonPublic | BindingFlags.Instance),
                back = typeof(LocatorPanel).GetField("m_Pin", BindingFlags.NonPublic | BindingFlags.Instance),
                icon = typeof(LocatorPanel).GetField("m_FactionIcon", BindingFlags.NonPublic | BindingFlags.Instance),
                lowerName = typeof(LocatorPanel).GetField("m_BottomName", BindingFlags.NonPublic | BindingFlags.Instance);
            internal static void RefreshMarker_Postfix(TankDescriptionOverlay __instance)
            {
                if (KickStart.EnableBetterAI)
                {
                    try
                    {
                        Tank tank = (Tank)tech.GetValue(__instance);

                        LocatorPanel Panel = (LocatorPanel)panel.GetValue(__instance);

                        if (tank.IsNotNull() && Panel.IsNotNull())
                        {
                            TankAIHelper lastTech = tank.GetHelperInsured();
                            if (lastTech.IsNotNull())
                            {
                                Image cache = (Image)icon.GetValue(Panel);
                                Image cacheB = (Image)back.GetValue(Panel);

                                int Team = lastTech.tank.Team;

                                Panel.BottomName = TeamNamer.GetTeamName(Team).ToString();
                                if (Team == ManSpawn.NeutralTeam)
                                {
                                }
                                else if (ManBaseTeams.IsAlliedPlayerAIBaseTeam(Team))
                                {
                                    cache.color = AIGlobals.PlayerAutoColor;
                                    cacheB.color = AIGlobals.PlayerAutoColor;
                                    back.SetValue(Panel, cacheB);
                                }
                                else if (ManBaseTeams.IsFriendlyBaseTeam(Team))
                                {
                                    cache.color = AIGlobals.FriendlyColor;
                                    cacheB.color = AIGlobals.FriendlyColor;
                                    back.SetValue(Panel, cacheB);
                                }
                                else if (ManBaseTeams.IsNeutralBaseTeam(Team))
                                {
                                    cache.color = AIGlobals.NeutralColor;
                                    cacheB.color = AIGlobals.NeutralColor;
                                    back.SetValue(Panel, cacheB);
                                }
                                else if (ManBaseTeams.IsSubNeutralBaseTeam(Team))
                                {
                                    cache.color = AIGlobals.SubNeutralColor;
                                    cacheB.color = AIGlobals.SubNeutralColor;
                                    back.SetValue(Panel, cacheB);
                                }
                                else
                                {
                                }
                                if (tank.IsAnchored)
                                {

                                }
                                else if (lastTech.AIAlign == AIAlignment.Player)
                                {
                                    if (lastTech.SetToActive)
                                    {
                                        Sprite sprite;
                                        if (RawTechExporter.aiBackplates.TryGetValue(lastTech.DriverType, out sprite))
                                        {
                                            cacheB.sprite = sprite;
                                        }
                                        if (RawTechExporter.aiIcons.TryGetValue(KickStart.GetLegacy(lastTech.DediAI, lastTech.DriverType), out sprite))
                                        {
                                            cache.sprite = sprite;
                                        }
                                    }
                                }
                                else if (lastTech.AIAlign == AIAlignment.NonPlayer)
                                {
                                    if (KickStart.enablePainMode)
                                    {
                                        var mind = lastTech.GetComponent<EnemyMind>();
                                        if ((bool)mind)
                                        {
                                            Sprite sprite;
                                            if (RawTechExporter.aiBackplates.TryGetValue(lastTech.DriverType, out sprite))
                                            {
                                                cacheB.sprite = sprite;
                                            }
                                            if (mind.CommanderSmarts < EnemySmarts.Smrt)
                                            {
                                                switch (mind.CommanderMind)
                                                {
                                                    case EnemyAttitude.Homing:
                                                        if (RawTechExporter.aiIcons.TryGetValue(AIType.Assault, out sprite))
                                                        {
                                                            cache.sprite = sprite;
                                                        }
                                                        break;
                                                    case EnemyAttitude.Junker:
                                                        if (RawTechExporter.aiIcons.TryGetValue(AIType.Scrapper, out sprite))
                                                        {
                                                            cache.sprite = sprite;
                                                            icon.SetValue(Panel, cache);
                                                        }
                                                        break;
                                                    case EnemyAttitude.Miner:
                                                        if (RawTechExporter.aiIcons.TryGetValue(AIType.Prospector, out sprite))
                                                        {
                                                            cache.sprite = sprite;
                                                        }
                                                        break;
                                                    case EnemyAttitude.NPCBaseHost:
                                                    case EnemyAttitude.Boss:
                                                        if (RawTechExporter.aiIcons.TryGetValue(AIType.Aegis, out sprite))
                                                        {
                                                            cache.sprite = sprite;
                                                        }
                                                        break;
                                                    default:
                                                        switch (mind.EvilCommander)
                                                        {
                                                            case EnemyHandling.Airplane:
                                                            case EnemyHandling.Chopper:
                                                                if (RawTechExporter.aiIcons.TryGetValue(AIType.Aviator, out sprite))
                                                                {
                                                                    cache.sprite = sprite;
                                                                }
                                                                break;
                                                            case EnemyHandling.Naval:
                                                                if (RawTechExporter.aiIcons.TryGetValue(AIType.Buccaneer, out sprite))
                                                                {
                                                                    cache.sprite = sprite;
                                                                }
                                                                break;
                                                            case EnemyHandling.Starship:
                                                                if (RawTechExporter.aiIcons.TryGetValue(AIType.Astrotech, out sprite))
                                                                {
                                                                    cache.sprite = sprite;
                                                                }
                                                                break;
                                                            default:
                                                                if (RawTechExporter.aiIconsEnemy.TryGetValue(mind.CommanderSmarts, out sprite))
                                                                {
                                                                    cache.sprite = sprite;
                                                                }
                                                                break;
                                                        }
                                                        break;
                                                }
                                            }
                                            else
                                            {
                                                switch (mind.CommanderMind)
                                                {
                                                    case EnemyAttitude.NPCBaseHost:
                                                    case EnemyAttitude.Boss:
                                                        if (RawTechExporter.aiIcons.TryGetValue(AIType.Aegis, out sprite))
                                                        {
                                                            cache.sprite = sprite;
                                                        }
                                                        break;
                                                    default:
                                                        if (RawTechExporter.aiIconsEnemy.TryGetValue(mind.CommanderSmarts, out sprite))
                                                        {
                                                            cache.sprite = sprite;
                                                        }
                                                        break;
                                                }
                                            }
                                        }
                                    }
                                }

                                icon.SetValue(Panel, cache);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": SendUpdateAIDisp - Player not close enough - " + e);
                        throw e;
                    }
                }
            }

        }
        internal static class UIMiniMapLayerTechPatches
        {
            internal static Type target = typeof(UIMiniMapLayerTech);

            private static void TryGetIconForTrackedVisible_Postfix(ref TrackedVisible trackedVisible, ref Color iconColour)
            {
                switch (trackedVisible.RadarType)
                {
                    case RadarTypes.Base:
                    case RadarTypes.Vehicle:
                        if (trackedVisible.RadarTeamID == ManSpawn.NeutralTeam)
                        {
                        }
                        else if (ManBaseTeams.IsAlliedPlayerAIBaseTeam(trackedVisible.RadarTeamID))
                        {
                            iconColour = AIGlobals.PlayerAutoColor;
                        }
                        else if (ManBaseTeams.IsFriendlyBaseTeam(trackedVisible.RadarTeamID))
                        {
                            iconColour = AIGlobals.FriendlyColor;
                        }
                        else if (ManBaseTeams.IsNeutralBaseTeam(trackedVisible.RadarTeamID))
                        {
                            iconColour = AIGlobals.NeutralColor;
                        }
                        else if (ManBaseTeams.IsSubNeutralBaseTeam(trackedVisible.RadarTeamID))
                        {
                            iconColour = AIGlobals.SubNeutralColor;
                        }
                        break;
                }
            }
        }

        internal static class UIRadialTechControlMenuPatches
        {
            internal static Type target = typeof(UIRadialTechControlMenu);
            internal static void Show_Prefix(UIRadialTechControlMenu __instance, ref object context)
            {
                OpenMenuEventData nabData = (OpenMenuEventData)context;
                TankBlock thisBlock = nabData.m_TargetTankBlock;
                if (thisBlock.tank.IsNotNull())
                {
                    DebugTAC_AI.Info(KickStart.ModID + ": grabbed tank data = " + thisBlock.tank.name.ToString());
                    GUIAIManager.GetTank(thisBlock.tank);
                }
                else
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": TANK IS NULL!");
                }
            }
            internal static void OnAIOptionSelected_Prefix(UIRadialTechControlMenu __instance, ref UIRadialTechControlMenu.PlayerCommands command)
            {
                if ((int)command == 3)
                {
                    if (GUIAIManager.IsTankNull())
                    {
                        FieldInfo currentTreeActual = typeof(UIRadialTechControlMenu).GetField("m_TargetTank", BindingFlags.NonPublic | BindingFlags.Instance);
                        Tank tonk = (Tank)currentTreeActual.GetValue(__instance);
                        GUIAIManager.GetTank(tonk);
                        if (GUIAIManager.IsTankNull())
                        {
                            DebugTAC_AI.Log(KickStart.ModID + ": TANK IS NULL AFTER SEVERAL ATTEMPTS!!!");
                        }
                    }
                    GUIAIManager.LaunchSubMenuClickable();
                }

            }
        }

        internal static class UIScreenPauseMenuPatches
        {
            internal static Type target = typeof(UIScreenPauseMenu);

            static readonly FieldInfo rtsCam = typeof(UIScreenPauseMenu).GetField("m_FreeCam", BindingFlags.NonPublic | BindingFlags.Instance);
            private static void Show_Postfix(UIScreenPauseMenu __instance)
            {
                Toggle cam = (Toggle)rtsCam.GetValue(__instance);
                if (ManGameMode.inst.IsCurrent<ModeMain>() || ManGameMode.inst.IsCurrent<ModeCoOpCampaign>())
                {
                    cam.gameObject.SetActive(true);
                    cam.SetValue(ManPauseGame.inst.PhotoCamToggle);
                }
            }
        }
    }
}
