using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement;
using TAC_AI.World;
using TerraTech.Network;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI
{
    internal class TankAIManager : MonoBehaviour
    {
        internal static FieldInfo rangeOverride = typeof(ManTechs).GetField("m_SleepRangeFromCamera", BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo liveSetPieces = typeof(ManWorld).GetField("m_SetPiecesPlacement", BindingFlags.NonPublic | BindingFlags.Instance);

        internal static TankAIManager inst;
        private static Tank lastPlayerTech;
        public static Event<TankAIHelper> AILoadedEvent = new Event<TankAIHelper>();
        public static Event<int> TeamCreatedEvent = new Event<int>();
        public static Event<int> TeamDestroyedEvent = new Event<int>();
        internal static List<ManWorld.TerrainSetPiecePlacement> SetPieces = null;

        internal static Dictionary<int, KeyValuePair<RequestSeverity, Visible>> targetingRequests = new Dictionary<int, KeyValuePair<RequestSeverity, Visible>>();

        public static HashSet<int> MissionTechs = new HashSet<int>();

        private static Dictionary<int, TeamIndex> teamsIndexed;
        public static Event<TankAIHelper> TechRemovedEvent = new Event<TankAIHelper>();
        public static Event<int> MissionTechRemovedEvent = new Event<int>();
        private static float lastCombatTime = 0;
        internal static float terrainHeight = 0;
        private static float TargetTime = DefaultTime;
        internal static float LastRealTime = 0;
        internal static float DeltaRealTime = 0;

        internal static Vector3 GravVector = Physics.gravity;
        internal static float GravMagnitude = Physics.gravity.magnitude;

        private const float DefaultTime = 1.0f;
        private const float SlowedTime = 0.25f;
        private const float FastTime = 3f; // fooling around
        private const float ChangeRate = 1.5f;
        //public static AbilityToggle toggleAuto;
        public static ManToolbar.ToolbarToggle toggleAuto;
        internal static void ButtonTogglePlayerAutopilot(bool state)
        {
            KickStart.AutopilotPlayerMain = state;
            toggleAuto.SetToggleState(KickStart.AutopilotPlayerMain);
        }

        internal static void Initiate()
        {
            if (inst)
                return;
            inst = new GameObject("AIManager").AddComponent<TankAIManager>();
            //Allies = new List<Tank>();
            AIECore.Minables = new List<Visible>();
            AIECore.Depots = new List<ModuleHarvestReciever>();
            AIECore.BlockHandlers = new List<ModuleHarvestReciever>();
            AIECore.Chargers = new List<ModuleChargerTracker>();
            AIECore.RetreatingTeams = new HashSet<int>();
            teamsIndexed = new Dictionary<int, TeamIndex>();
            Singleton.Manager<ManPauseGame>.inst.PauseEvent.Subscribe(inst.OnPaused);
            Singleton.Manager<ManTechs>.inst.TankPostSpawnEvent.Subscribe(OnTankAddition);
            Singleton.Manager<ManTechs>.inst.TankTeamChangedEvent.Subscribe(OnTankChange);
            Singleton.Manager<ManTechs>.inst.PlayerTankChangedEvent.Subscribe(OnPlayerTechChange);
            Singleton.Manager<ManVisible>.inst.OnStoppedTrackingVisible.Subscribe(OnVisibleNoLongerTracked);
            Singleton.Manager<ManGameMode>.inst.ModeStartEvent.Subscribe(OnStartup);
            InvokeHelper.Invoke(GatherAllMissionTechs, 0.1f);
            DebugTAC_AI.Log(KickStart.ModID + ": Created AIECore Manager.");

            // B3: reflection-acquired FieldInfo can be null if vanilla TerraTech renames the
            // private field. Match the OverrideEnemyMax (KickStart.cs:986) precedent: FatalError
            // popup so the user sees the vanilla-API mismatch, but degrade gracefully so the rest
            // of the mod still loads.
            if (rangeOverride == null)
            {
                DebugTAC_AI.FatalError(KickStart.ModID + ": TankAIManager - ManTechs.m_SleepRangeFromCamera field not found (vanilla API change?). Enemy interaction range will stay at vanilla 200m; far-range AI engagements will not work.");
            }
            else
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Current AI interaction range is " + (float)rangeOverride.GetValue(ManTechs.inst) + ".");
                if ((float)rangeOverride.GetValue(ManTechs.inst) == 200f)
                {   // more than twice the range
                    rangeOverride.SetValue(ManTechs.inst, AIGlobals.EnemyExtendActionRange);
                    DebugTAC_AI.Log(KickStart.ModID + ": Extended enemy Tech interaction range to " + AIGlobals.EnemyExtendActionRange + ".");
                }
            }
            if (liveSetPieces == null)
            {
                DebugTAC_AI.FatalError(KickStart.ModID + ": TankAIManager - ManWorld.m_SetPiecesPlacement field not found (vanilla API change?). Set-piece obstacle avoidance disabled; AI may path through monuments.");
                SetPieces = new List<ManWorld.TerrainSetPiecePlacement>(); // empty so AIEPathing stays safe
            }
            else
            {
                SetPieces = (List<ManWorld.TerrainSetPiecePlacement>)liveSetPieces.GetValue(ManWorld.inst);
            }
        }
#if STEAM
        internal void CheckNextFrameNeedsDeInit()
        {
            Invoke("DeInitCallToKickStart", 0.001f);
        }
        internal void DeInitCallToKickStart()
        {
            if (!KickStart.ShouldBeActive)
                KickStart.DeInitALL();
        }
        internal static void DeInit()
        {
            if (!inst)
                return;
            AIEPathMapper.ResetAll();
            Singleton.Manager<ManPauseGame>.inst.PauseEvent.Unsubscribe(inst.OnPaused);
            Singleton.Manager<ManTechs>.inst.TankPostSpawnEvent.Unsubscribe(OnTankAddition);
            Singleton.Manager<ManTechs>.inst.TankTeamChangedEvent.Unsubscribe(OnTankChange);
            Singleton.Manager<ManTechs>.inst.PlayerTankChangedEvent.Unsubscribe(OnPlayerTechChange);
            Singleton.Manager<ManVisible>.inst.OnStoppedTrackingVisible.Unsubscribe(OnVisibleNoLongerTracked);
            Singleton.Manager<ManGameMode>.inst.ModeStartEvent.Unsubscribe(OnStartup);
            teamsIndexed = null;
            AIECore.RetreatingTeams = null;
            AIECore.Chargers = null;
            AIECore.BlockHandlers = null;
            AIECore.Depots = null;
            AIECore.Minables = null;
            AIECore.DestroyAllHelpers();
            inst.enabled = false;
            Destroy(inst.gameObject);
            inst = null;
            DebugTAC_AI.Log(KickStart.ModID + ": De-Init AIECore Manager.");

            // B3: symmetric null-guard for DeInit — if Initiate skipped the override due to a
            // renamed field, the restore must skip too instead of NREing.
            if (rangeOverride != null && (float)rangeOverride.GetValue(ManTechs.inst) == AIGlobals.EnemyExtendActionRange)
            {   // more than twice the range
                rangeOverride.SetValue(ManTechs.inst, 200);
                DebugTAC_AI.Log(KickStart.ModID + ": Un-Extended enemy Tech interaction range to default 200.");
            }
        }
#endif

        public static void OnStartup(Mode mode)
        {
            InvokeHelper.Invoke(InsureLoadedCorrectly, 1f);
        }
        private static void InsureLoadedCorrectly()
        {
            //ForceReloadAll();
            AICommands.AreYouSurePurge = AICommands.PurgeState.None;
        }
        public static void RegisterMissionTechVisID(int vis)
        {
            MissionTechs.Add(vis);
        }
        private static void GatherAllMissionTechs()
        {
            foreach (var item in ManEncounter.inst.ActiveEncounters)
            {
                foreach (var item2 in item.GetVisibleNamesWithPrefix(string.Empty))
                {
                    EncounterVisibleData EVD = item.GetVisible(item2);
                    if (EVD.ObjectType == ObjectTypes.Vehicle)
                        RegisterMissionTechVisID(EVD.m_VisibleId);
                }
            }
        }
        private static void OnVisibleNoLongerTracked(TrackedVisible TV)
        {
            if (MissionTechs.Remove(TV.ID))
            {
                MissionTechRemovedEvent.Send(TV.ID);
            }
        }

        private static void OnTankAddition(Tank tonk)
        {
            // T4: log+destroy extras instead of throw (a throw here corrupts vanilla TankPostSpawnEvent
            // dispatch for subsequent listeners). EnforceSingleComponent keeps the first instance.
            var helper = tonk.gameObject.EnforceSingleComponent<TankAIHelper>("OnTankAddition")
                          ?? tonk.GetHelperInsured();

            // Indexing + alignment rebuild are deferred: dirtyAI=Dirty causes the next
            // CheckRebuildAlignment tick to call TankAIManager.UpdateTechTeam, and the
            // TankTeamChangedEvent that follows spawn routes through OnTankChange.
            helper.dirtyExtents = true;
            helper.dirtyAI = TankAIHelper.AIDirtyState.Dirty;
            // T8: RunState ownership belongs to CheckRebuildAlignment, not this listener.
            // The field default is Advanced for fresh helpers; for recycled helpers, the dirtyAI
            // flag above triggers a rebuild that re-derives RunState. Removing the unconditional
            // overwrite eliminates a race that clobbered HandOffToVanillaForNeutral state.
            helper.enabled = true;
            DebugTAC_AI.LogAISetup(KickStart.ModID + ": AI Helper " + tonk.name + ":  OnTankAddition - Spawned!");
        }
        private static void OnTankChange(Tank tonk, ManTechs.TeamChangeInfo info)
        {
            if (tonk == null)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": OnTankChange tonk is NULL");
                return;
            }
            var helper = tonk.GetHelperInsured();
            RemoveTech(tonk);
            helper.OnTechTeamChange();
            // T2: drop MissionTechs membership when the tech leaves its mission context (e.g.
            // captured-and-converted to player team, or faction-flipped). Without this, the sticky
            // HashSet keeps the tech flagged forever and SetupBaseOrMissionAI keeps short-circuiting
            // even when the original mission state is gone.
            if (tonk.visible != null)
                MissionTechs.Remove(tonk.visible.ID);
            // B1: removed the `if (tonk.FirstUpdateAfterSpawn)` gate. Mid-game team flips (player
            // capture, faction flip) must also re-mark dirty so CheckRebuildAlignment re-evaluates;
            // the previous gate left stale RunState=Default + AIAlign=Static for non-first-frame flips.
            // RunState is no longer written here either (CheckRebuildAlignment owns it).
            helper.dirtyExtents = true;
            helper.dirtyAI = TankAIHelper.AIDirtyState.Dirty;
            helper.enabled = true;
            if (tonk.FirstUpdateAfterSpawn)
                DebugTAC_AI.LogAISetup(KickStart.ModID + ": AI Helper " + tonk.name + ":  Spawned!");
            IndexTech(tonk, info.m_NewTeam);
            CheckDestroyedTeams();
        }
        private static void OnTankRecycled(Tank recycledTech)
        {
            var helper = recycledTech.GetHelperInsured();
            recycledTech.TankRecycledEvent.Unsubscribe(OnTankRecycled);
            TechRemovedEvent.Send(helper);
            if (MissionTechs.Remove(recycledTech.visible.ID))
                MissionTechRemovedEvent.Send(recycledTech.visible.ID);
            helper.Recycled();
            RemoveTech(recycledTech);
            CheckDestroyedTeams();
            DebugTAC_AI.LogDevOnlyAssert(KickStart.ModID + ": Allied AI " + recycledTech.name + ":  Called OnTankRecycled");

            if (helper.AIControlOverride != null)
            {
                helper.AIControlOverride(helper, TankAIHelper.ExtControlStatus.Recycle);
                helper.AIControlOverride = null;
            }

            var mind = recycledTech.GetComponent<EnemyMind>();
            if ((bool)mind)
            {
#if !STEAM
                    ALossReact loss = ALossReact.Land;
                    switch (mind.EvilCommander)
                    {
                        case EnemyHandling.Naval:
                            loss = ALossReact.Sea;
                            break;
                        case EnemyHandling.Stationary:
                            loss = ALossReact.Base;
                            break;
                        case EnemyHandling.Airplane:
                        case EnemyHandling.Chopper:
                            loss = ALossReact.Air;
                            break;
                        case EnemyHandling.Starship:
                            loss = ALossReact.Space;
                            break;
                    }
                    AnimeAICompat.RespondToLoss(recycledTech, loss);
#endif
            }
        }
        private static void OnPlayerTechChange(Tank nextTonk, bool yes)
        {
            if (lastPlayerTech != nextTonk)
            {
                TankAIHelper helper;
                if (nextTonk != null)
                {
                    helper = nextTonk.GetHelperInsured();
                    // D3: removed `//helper.OnTechTeamChange()` — ForceRebuildAlignment below
                    // already sets dirtyAI and runs the rebuild, which subsumes its work.
                    helper.SuppressFiring(false);
                    helper.ForceRebuildAlignment();
                }
                else
                {
                    try
                    {
                        if (lastPlayerTech)
                        {
                            helper = lastPlayerTech.GetHelperInsured();
                            helper.OnTechTeamChange();
                        }
                    }
                    catch { }
                }
                lastPlayerTech = nextTonk;
            }
        }

        private static readonly HashSet<Tank> emptyHash = new HashSet<Tank>();
        public static IEnumerable<Tank> GetTeamTanks(int Team)
        {
            if (teamsIndexed.TryGetValue(Team, out TeamIndex TIndex))
            {
                //RemoveAllInvalid(TIndex.Teammates);
                return TIndex.Teammates;
            }
            return emptyHash;
        }
        public static HashSet<Tank> GetNonEnemyTanks(int Team)
        {
            if (teamsIndexed.TryGetValue(Team, out TeamIndex TIndex))
            {
                //RemoveAllInvalid(TIndex.NonHostile);
                return TIndex.NonHostile;
            }
            return emptyHash;
        }
        public static IEnumerable<Tank> GetTargetTanks(int Team)
        {
            if (teamsIndexed.TryGetValue(Team, out TeamIndex TIndex))
            {
                //RemoveAllInvalid(TIndex.Targets);
                return TIndex.Targets;
            }
            return emptyHash;
        }
        private static void RemoveAllInvalid(HashSet<Tank> list)
        {
            for (int step = list.Count - 1; step > -1; step--)
            {
                var ele = list.ElementAt(step);
                if (ele?.visible == null || !ele.visible.isActive)
                {
                    DebugTAC_AI.Assert(KickStart.ModID + ": RemoveAllInvalid - Tech indexes were desynced - a Tech that was null or had no blocks was in the collection!");
                    list.Remove(ele);
                }
            }
        }
        internal static void ForceReloadAll()
        {
            foreach (var item in ManTechs.inst.IterateTechs())
            {
                if (item == null)
                    continue;
                RemoveTech(item);
                IndexTech(item, item.Team);
            }
            CheckDestroyedTeams();
        }
        internal static void UpdateTechTeam(Tank tonk)
        {
            RemoveTech(tonk);
            IndexTech(tonk, tonk.Team);
            CheckDestroyedTeams();
        }

        private static List<Tank> TempReloaderCached = new List<Tank>();
        internal static void UpdateEntireTeam(int team)
        {
            TempReloaderCached.AddRange(GetTeamTanks(team));
            foreach (var item in TempReloaderCached)
            {
                RemoveTech(item);
                IndexTech(item, team);
            }
            TempReloaderCached.Clear();
            CheckDestroyedTeams();
        }
        private static void CacheTeam(int Team)
        {
            TeamIndex TI = new TeamIndex();
            foreach (var item in ManTechs.inst.IterateTechs())
            {
                if (item == null)
                    continue;
                if (ManBaseTeams.IsEnemy(item.Team, Team))
                    TI.Targets.Add(item);
                else
                    TI.NonHostile.Add(item);
            }
            //RemoveAllInvalid(TI.Targets);
            //RemoveAllInvalid(TI.NonHostile);
            teamsIndexed.Add(Team, TI);
            TeamCreatedEvent.Send(Team);
        }
        private static void IndexTech(Tank tonk, int Team)
        {
            if (tonk?.visible == null || !tonk.visible.isActive)
                return;
            //if (ManBaseTeams.IsEnemy(Team, Team))
            //    throw new InvalidOperationException("Cannot add tech which fights amongst selves - team " + TeamNamer.GetTeamName(Team));
            tonk.TankRecycledEvent.Subscribe(OnTankRecycled);
            try
            {
                if (!teamsIndexed.TryGetValue(Team, out TeamIndex TIndex))
                    CacheTeam(Team);
                foreach (KeyValuePair<int, TeamIndex> TI in teamsIndexed)
                {
                    if (TI.Key == Team)
                        TI.Value.Teammates.Add(tonk);
                    if (ManBaseTeams.IsEnemy(TI.Key, Team))
                        TI.Value.Targets.Add(tonk);
                    else
                        TI.Value.NonHostile.Add(tonk);
                }
                //DebugTAC_AI.Log("IndexTech added " + tonk.name + " of team " + Team);
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log("Error in IndexTech " + e);
            }
        }
        private static void RemoveTech(Tank tonk)
        {
            if (tonk != null)
                tonk.TankRecycledEvent.Unsubscribe(OnTankRecycled);
            foreach (var TI in teamsIndexed)
            {
                TI.Value.Teammates.Remove(tonk);
                TI.Value.Targets.Remove(tonk);
                TI.Value.NonHostile.Remove(tonk);
            }
            //DebugTAC_AI.Log("RemoveTech " + tonk.name);
        }
        private static void CheckDestroyedTeams()
        {
            for (int step = teamsIndexed.Count - 1; 0 <= step; step--)
            {
                KeyValuePair<int, TeamIndex> TI = teamsIndexed.ElementAt(step);
                if (!TI.Value.Teammates.Any())
                {
                    //DebugTAC_AI.Assert("OnTeamDestroyedCheck - removed active team " + TI.Key + " due to no more teammates");
                    TeamDestroyedEvent.Send(TI.Key);
                    teamsIndexed.Remove(TI.Key);
                }
            }
            //DebugTAC_AI.Log("RemoveTech " + tonk.name);
        }

        internal void WarnPlayers()
        {
            try
            {
                //Singleton.Manager<UIMPChat>.inst.AddMissionMessage("Warning: This server is using Advanced AI!  If you are new to the game, I would suggest you play safe. Enemies RTS Mode: " + KickStart.AllowStrategicAI + "");
                SendChatServer("Warning: This server is using Advanced AI!  If you are new to the game, I would suggest you play safe. Enemies RTS Mode: " + KickStart.AllowStrategicAI + "");
            }
            catch { }
        }
        internal static string SendChatServer(string chatMsg)
        {
            try
            {
                if (ManNetwork.IsHost)
                    Singleton.Manager<ManNetworkLobby>.inst.LobbySystem.CurrentLobby.SendChat("[SERVER] " + chatMsg, -1, (uint)TTNetworkID.Invalid.GetHashCode());
            }
            catch { }
            return chatMsg;
        }
        internal void CorrectBlocksList()
        {
            BlockIndexer.ConstructBlockLookupListDelayed();
        }

        internal static ManTechs.TechIterator TeamActiveTechs(int Team)
        {
            return ManTechs.inst.IterateTechsWhere(x => x.Team == Team);
        }
        internal static ManTechs.TechIterator TeamActiveMobileTechs(int Team)
        {
            return ManTechs.inst.IterateTechsWhere(x => x.Team == Team && !x.IsBase());
        }
        internal static ManTechs.TechIterator TeamActiveMobileTechsInCombat(int Team)
        {
            return ManTechs.inst.IterateTechsWhere(x => x.Team == Team && !x.IsBase() && 
            x.GetHelperInsured() is TankAIHelper helper && helper && helper.WantsToFight && helper.lastEnemyGet);
        }
        private void RunFocusFireRequests()
        {
            foreach (KeyValuePair<int, KeyValuePair<RequestSeverity, Visible>> request in targetingRequests)
            {
                ProcessFocusFireRequestAllied(request.Key, request.Value.Value, request.Value.Key);
            }
            targetingRequests.Clear();
        }
        private static void ProcessFocusFireRequestAllied(int requestingTeam, Visible Target, RequestSeverity Priority)
        {
            // B12: was a blanket `try { switch } catch { }`. Any throw on tank #1 (e.g.
            // GetComponent<TankAIHelper>() returning null → NRE on .Retreat) aborted the
            // broadcast for every remaining ally on the team, silently. Now uses
            // GetHelperInsured() (heals missing helpers), per-tank inner try/catch
            // (one bad tech can't kill the broadcast), outer dedup-logged catch for
            // structural failures (e.g. teamsIndexed enumerator mutation), and .ToList()
            // snapshot so SetPursuit's downstream state changes can't corrupt iteration.
            if (Target == null || Target.tank == null)
                return;
            try
            {
                IEnumerable<Tank> roster;
                switch (Priority)
                {
                    case RequestSeverity.ThinkMcFly:
                    case RequestSeverity.SameTeam:
                        roster = GetTeamTanks(requestingTeam);
                        break;
                    case RequestSeverity.Warn:
                    case RequestSeverity.AllHandsOnDeck:
                        roster = GetNonEnemyTanks(requestingTeam);
                        break;
                    default:
                        return;
                }

                foreach (Tank tech in roster.ToList())
                {
                    if (tech == null) continue;
                    try
                    {
                        var helper = tech.GetHelperInsured();
                        if (helper == null) continue;

                        bool engage;
                        switch (Priority)
                        {
                            case RequestSeverity.ThinkMcFly:
                                engage = !tech.IsAnchored && !helper.Retreat && helper.DediAI == AIType.Aegis;
                                if (engage) helper.Provoked = AIGlobals.ProvokeTimeShort;
                                break;
                            case RequestSeverity.Warn:
                                engage = !tech.IsAnchored && !helper.Retreat
                                      && (!ManSpawn.IsPlayerTeam(tech.Team) || helper.DediAI == AIType.Aegis);
                                if (engage) helper.Provoked = AIGlobals.ProvokeTime;
                                break;
                            case RequestSeverity.SameTeam:
                                engage = true;
                                helper.Provoked = AIGlobals.ProvokeTime;
                                break;
                            case RequestSeverity.AllHandsOnDeck:
                                engage = !tech.IsAnchored
                                      || (ManSpawn.IsPlayerTeam(tech.Team)
                                          && (helper.DediAI == AIType.Aegis || helper.AdvancedAI));
                                if (engage) helper.Provoked = AIGlobals.ProvokeTime;
                                break;
                            default:
                                engage = false;
                                break;
                        }
                        if (engage && !(bool)helper.lastEnemyGet)
                            helper.SetPursuit(Target);
                    }
                    catch (Exception ePerTank)
                    {
                        DebugTAC_AI.LogWarnPlayerOncePerKey(
                            "FocusFire:" + Priority + ":" + (tech != null ? tech.name : "<null>"),
                            "FocusFire dispatch failed for one teammate; other allies still notified", ePerTank);
                    }
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "FocusFire:" + Priority + ":Team" + requestingTeam,
                    "FocusFire roster lookup failed; entire request dropped for this team/priority", e);
            }
        }

        private void OnPaused(bool state)
        {
            inst.enabled = !state;
        }
        private void ManageTimeRunner()
        {
            if (Time.timeScale > 0)
            {
                DeltaRealTime = Time.realtimeSinceStartup - LastRealTime;
                if (ManWorldRTS.PlayerIsInRTS && !ManNetwork.IsNetworked)
                {
                    if (Input.GetKey(KeyCode.LeftControl))
                    {
                        TargetTime = SlowedTime;
                    }
                    else if (Input.GetKey(KeyCode.RightControl))
                    {
                        TargetTime = FastTime;
                    }
                    else
                    {
                        TargetTime = DefaultTime;
                    }
                }
                else
                {
                    TargetTime = DefaultTime;
                }
                Time.timeScale = Mathf.MoveTowards(Time.timeScale, TargetTime, ChangeRate * DeltaRealTime);
            }
            LastRealTime = Time.realtimeSinceStartup;
        }
        public static bool IsPlayerRTSControlled(Tank tank) => PlayerControlledTanks.Contains(tank);
        private static HashSet<Tank> PlayerControlledTanks = new HashSet<Tank>();
        private void Update()
        {
            PlayerControlledTanks.Clear();
            if (ManNetwork.IsNetworked)
            {
                foreach (var item in ManNetwork.inst.GetAllPlayerTechs())
                    PlayerControlledTanks.Add(item);
            }
            else if (Singleton.playerTank)
                PlayerControlledTanks.Add(Singleton.playerTank);
            AIGlobals.SceneTechCount = -1;
            AIGlobals.HideHud = AIGlobals.GetHideHud;
            if (GravVector != Physics.gravity)
            {
                GravVector = Physics.gravity;
                GravMagnitude = GravVector.magnitude;
            }

            // if (Input.GetKeyDown(KeyCode.Quote))  AIECore.debugVisuals = !AIECore.debugVisuals;

            if (Input.GetKeyDown(KickStart.RetreatHotkey) && ManHUD.inst.HighlightedOverlay == null)
                AIECore.ToggleTeamRetreat(Singleton.Manager<ManPlayer>.inst.PlayerTeam);
            if (Singleton.playerTank)
            {
                var helper = Singleton.playerTank.GetComponent<TankAIHelper>();
                if (Input.GetMouseButton(0) && Singleton.playerTank.control.FireControl && ManPointer.inst.targetVisible)
                {
                    Visible couldBeObst = ManPointer.inst.targetVisible;
                    if (couldBeObst.GetComponent<ResourceDispenser>())
                    {
                        if ((couldBeObst.centrePosition - Singleton.playerTank.visible.centrePosition).sqrMagnitude <= 10000)
                        {
                            if (!Singleton.playerTank.Vision.GetFirstVisibleTechIsEnemy(Singleton.playerTank.Team))
                            {
                                helper.Obst = couldBeObst.transform;
                                helper.ActiveAimState = AIWeaponState.Obsticle;
                                goto conclusion; // I hate doing gotos but this is the only "fast" way
                            }
                        }
                    }
                }
                if (helper.Obst != null)
                {
                    helper.Obst = null;
                    helper.ActiveAimState = AIWeaponState.Normal;
                }
                conclusion:;
            }
            if (lastCombatTime > 6)
            {
                if (ManEncounterPlacement.IsOverlappingEncounter(Singleton.playerPos, 64, false))
                    AIECore._playerIsInNonCombatZone = true;
                if (AIECore.PlayerCombatLastState != AIECore._playerIsInNonCombatZone)
                {
                    AIECore.PlayerCombatLastState = AIECore._playerIsInNonCombatZone;
                }
                lastCombatTime = 0;
                if (Singleton.playerTank)
                if (ManWorld.inst.GetTerrainHeight(Singleton.playerTank.boundsCentreWorldNoCheck, out float height))
                    terrainHeight = height;
                if (!AIWiki.TooHighed && terrainHeight > AIGlobals.AirPromoteSpaceHeight)
                {
                    if (AIWiki.hintSpaceTooHigh.Show())
                        AIWiki.TooHighed = true;
                }
            }
            else
                lastCombatTime += Time.deltaTime;
            ManageTimeRunner();
            RunFocusFireRequests();
        }

        private List<TankAIHelper> helpersActive = new List<TankAIHelper>();
        // P08 G.6: replaced raw round-robin indices with identity-based resume tracking. The old
        // `clockHelperStepDirectors/Operations` indices persisted across frames into a list that
        // gets rebuilt every FixedUpdate; mid-cycle removal of a helper caused the index to point
        // at a DIFFERENT helper, silently re-skipping/double-firing some. Tracking the last-
        // processed helper by GetInstanceID() (stable per-MonoBehaviour, unique) and resuming
        // from the next position survives list churn cleanly.
        private int lastDirectorHelperId = 0;  // 0 = "start from beginning"
        private int lastOpHelperId = 0;
        private static int FindResumeIndex(List<TankAIHelper> list, int lastId)
        {
            if (lastId == 0 || list.Count == 0) return 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].GetInstanceID() == lastId)
                    return (i + 1) % list.Count;  // resume from the helper AFTER the last-processed one
            }
            return 0;  // last-processed helper is no longer in the list; start fresh
        }

        private float DirectorUpdateClock = 0;
        private float OperationsUpdateClock = 0;
        // T1: force every helper's Operations pass on the first FixedUpdate so AI state
        // (targets, paths, role) is fully primed before gameplay sees the tank. Replaces
        // the magic `OperationsUpdateClock = 500` "kick" — explicit + bound-safe regardless
        // of helper count + survives any future change to AIClockPeriod or the Mathf.Min cap.
        private bool warmStartOperations = true;
        private int DirectorsToUpdateThisFrame()
        {
            DirectorUpdateClock += (float)helpersActive.Count / KickStart.AIDodgeCheapness;
            int count = Mathf.FloorToInt(DirectorUpdateClock);
            DirectorUpdateClock -= count;
            return count;
        }
        private int OperationsToUpdateThisFrame()
        {
            if (warmStartOperations)
            {
                warmStartOperations = false;
                return helpersActive.Count;
            }
            OperationsUpdateClock += (float)helpersActive.Count / KickStart.AIClockPeriod;
            int count = Mathf.FloorToInt(OperationsUpdateClock);
            OperationsUpdateClock -= count;
            return count;
        }
        private void UpdateAllHelpers()
        {
            if (!KickStart.EnableBetterAI)
                return;
            foreach (var helper in AIECore.IterateAllHelpers())
                helpersActive.Add(helper);
            try
            {
                foreach (var item in helpersActive)
                    item.OnPreUpdate();
            }
            catch (Exception e)
            {
                throw new Exception("A significant cascading failiure happened while updating all TankAIHelper.OnPreUpdate()", e);
            }
            StaggerUpdateAllHelpersDirAndOps();
            try
            {
                foreach (var item in helpersActive)
                    item.OnPostUpdate();
            }
            catch (Exception e)
            {
                throw new Exception("A significant cascading failiure happened while updating all TankAIHelper.OnPostUpdate()", e);
            }
            helpersActive.Clear();
        }
        private void StaggerUpdateAllHelpersDirAndOps()
        {
            int numDirUpdate = Mathf.Min(helpersActive.Count, DirectorsToUpdateThisFrame());
            int numOpUpdate = Mathf.Min(helpersActive.Count, OperationsToUpdateThisFrame());
            // P08 G.6: identity-resume — find where the last-processed helper sits in the (possibly
            // rebuilt) list and walk forward from the NEXT slot. Worst case after mid-cycle churn:
            // one helper double-processed or skipped for one frame, never silently/cumulatively.
            int dirStart = FindResumeIndex(helpersActive, lastDirectorHelperId);
            int opStart = FindResumeIndex(helpersActive, lastOpHelperId);
            if (ManNetwork.IsHost)
            {
                try
                {
                    for (int n = 0; n < numDirUpdate && helpersActive.Count > 0; n++)
                    {
                        int i = (dirStart + n) % helpersActive.Count;
                        var h = helpersActive[i];
                        h.OnUpdateHostAIDirectors();
                        lastDirectorHelperId = h.GetInstanceID();
                    }
                }
                catch (Exception e)
                {
                    throw new Exception("(Networked: " + ManNetwork.IsNetworked + ") A significant cascading failiure happened while updating all TankAIHelper.OnUpdateHostAIDirectors()", e);
                }
                try
                {
                    for (int n = 0; n < numOpUpdate && helpersActive.Count > 0; n++)
                    {
                        int i = (opStart + n) % helpersActive.Count;
                        var h = helpersActive[i];
                        h.OnUpdateHostAIOperations();
                        lastOpHelperId = h.GetInstanceID();
                    }
                }
                catch (Exception e)
                {
                    throw new Exception("(Networked: " + ManNetwork.IsNetworked + ") A significant cascading failiure happened while updating all TankAIHelper.OnUpdateHostAIOperations()", e);
                }
            }
            else
            {
                try
                {
                    for (int n = 0; n < numDirUpdate && helpersActive.Count > 0; n++)
                    {
                        int i = (dirStart + n) % helpersActive.Count;
                        var h = helpersActive[i];
                        h.OnUpdateClientAIDirectors();
                        lastDirectorHelperId = h.GetInstanceID();
                    }
                }
                catch (Exception e)
                {
                    throw new Exception("(Networked: " + ManNetwork.IsNetworked + ") A significant cascading failiure happened while updating all TankAIHelper.OnUpdateClientAIDirectors()", e);
                }
                try
                {
                    for (int n = 0; n < numOpUpdate && helpersActive.Count > 0; n++)
                    {
                        int i = (opStart + n) % helpersActive.Count;
                        var h = helpersActive[i];
                        h.OnUpdateClientAIOperations();
                        lastOpHelperId = h.GetInstanceID();
                    }
                }
                catch (Exception e)
                {
                    throw new Exception("(Networked: " + ManNetwork.IsNetworked + ") A significant cascading failiure happened while updating all TankAIHelper.OnUpdateClientAIOperations()", e);
                }
            }
        }
        private void FixedUpdate()
        {
            if (!ManPauseGame.inst.IsPaused && KickStart.EnableBetterAI)
            {
                try
                {
                    UpdateAllHelpers();
                }
                catch (Exception e)
                {
                    // B1+B8: keyed dedup replaces the static updateErrored latch.
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "TankAIManager.FixedUpdate",
                        "TankAIManager.FixedUpdate() Critical error", e);
                }
            }
        }

        internal class GUIManaged
        {
            private static bool controlledDisp = false;
            private static bool typesDisp = false;
            private static HashSet<AIType> enabledTabs = null;
            public static void GUIGetTotalManaged()
            {
                if (enabledTabs == null)
                {
                    enabledTabs = new HashSet<AIType>();
                }
                GUILayout.Box("--- Helpers --- ");
                int activeCount = 0;
                int baseCount = 0;
                Dictionary<AIAlignment, int> alignments = new Dictionary<AIAlignment, int>();
                foreach (AIAlignment item in Enum.GetValues(typeof(AIAlignment)))
                {
                    alignments.Add(item, 0);
                }
                Dictionary<AIType, int> types = new Dictionary<AIType, int>();
                foreach (AIType item in Enum.GetValues(typeof(AIType)))
                {
                    types.Add(item, 0);
                }
                foreach (var helper in AIECore.IterateAllHelpers())
                {
                    activeCount++;
                    alignments[helper.AIAlign]++;
                    types[helper.DediAI]++;
                    if (helper.tank.IsAnchored)
                        baseCount++;
                }
                GUILayout.Label("  Capacity: " + KickStart.MaxEnemyWorldCapacity);
                GUILayout.Label("  Num Bases: " + baseCount);
                if (GUILayout.Button("Pooled: " + AIECore.HelperCountNoCheck + " | Active: " + activeCount))
                    controlledDisp = !controlledDisp;
                if (controlledDisp)
                {
                    foreach (var item in alignments)
                    {
                        GUILayout.Label("  Alignment: " + item.Key.ToString() + " - " + item.Value);
                    }
                }
                if (GUILayout.Button("Types: " + types.Count))
                    typesDisp = !typesDisp;
                if (typesDisp)
                {
                    foreach (var item in types)
                    {
                        if (GUILayout.Button("Type: " + item.Key.ToString() + " - " + item.Value))
                        {
                            if (enabledTabs.Contains(item.Key))
                                enabledTabs.Remove(item.Key);
                            else
                                enabledTabs.Add(item.Key);
                        }
                        if (enabledTabs.Contains(item.Key))
                        {
                            foreach (var item2 in AIECore.IterateAllHelpers(x => x.DediAI == item.Key))
                            {
                                Vector3 pos = item2.tank.boundsCentreWorldNoCheck;
                                GUILayout.Label("  Tech: " + item2.tank.name + " | Pos: " + pos);
                                DebugExtUtilities.DrawDirIndicator(pos, pos + new Vector3(0, 32, 0), Color.white);
                            }
                        }
                    }
                }
            }
        }
    }
}
