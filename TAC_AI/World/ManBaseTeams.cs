using System;
using System.Collections.Generic;
using System.Linq;
using SafeSaves;
using UnityEngine;
using UnityEngine.Networking;
using TerraTechETCUtil;
using TAC_AI.AI;
using TAC_AI.AI.Enemy;
using TAC_AI.Templates;
using TAC_AI.World;
using Newtonsoft.Json;

namespace TAC_AI
{
    public enum TeamRelations : int
    {
        /// <summary>Attack, but can be swayed with Bribe</summary>
        Enemy,
        /// <summary>Don't attack unless attacked once -> Attack the attacker</summary>
        SubNeutral,
        /// <summary>Don't attack at all.  Usually indestructable.</summary>
        Neutral,
        /// <summary>Fight on the player's side</summary>
        Friendly,
        // Special
        AITeammate,
        SameTeam = 9001,
    }
    public static class TeamBasePointerExt
    {
        public static bool IsValid(this TeamBasePointer point) => point != null && point.valid;
    }
    /// <summary>
    ///  Can represent both a tech that is present and not present
    /// </summary>
    public interface TeamBasePointer
    {
        string Name { get; }
        /// <summary>
        ///  MAY NOT ALWAYS BE PRESENT
        /// </summary>
        Tank tank { get; }
        int BuildBucks { get; }
        void AddBuildBucks(int value);
        void SetBuildBucks(int value);
        int Team { get; }
        WorldPosition WorldPos { get; }
        int BlockCount { get; }
        bool valid { get; }

    }
    [Serializable]
    public class EnemyTeamData
    {
        public int teamID;
        internal int Team => teamID;
        public string teamName;
        /// <summary>
        /// DO NOT MODIFY DIRECTLY, use AddBuildBucks() and related instead!
        /// </summary>
        public int buildBucks;
        internal int BuildBucks => buildBucks;
        public int SetBuildBucks
        {
            set
            {
                if (buildBucks != value)
                {
                    buildBucks = value;
                    bankrupt = buildBucks <= 0;
                    ManBaseTeams.BuildBucksUpdatedEvent.Send(teamID, value);
                }
            }
        }
        private int AddBuildBucks_Internal
        {
            set
            {
                int prevVal = buildBucks;
                checked
                {
                    try
                    {
                        buildBucks += value;
                    }
                    catch (OverflowException)
                    {
                        buildBucks = int.MaxValue;
                    }
                }

                if (buildBucks != prevVal)
                {
                    bankrupt = buildBucks <= 0;
                    ManBaseTeams.BuildBucksUpdatedEvent.Send(teamID, value);
                }
            }
        }
        internal bool bankrupt = false;
        /// <summary> This team has not enough Build Bucks </summary>
        internal bool Bankrupt => bankrupt;

        // REVISED: HQ now persists by saved visible ID (hqVisibleID); the live pointer _HQ is [JsonIgnore] and
        // lazily re-resolved from that ID when invalid (was a plain non-persisted pointer).
        public int hqVisibleID = -1;
        [JsonIgnore] private TeamBasePointer _HQ = null;
        internal TeamBasePointer HQ
        {
            get
            {
                if (_HQ.IsValid()) return _HQ;
                _HQ = null;
                if (hqVisibleID > 0)
                {
                    _HQ = ResolveHQFromSavedID();
                    if (_HQ.IsValid()) return _HQ;
                }
                return _HQ;
            }
        }
        // REVISED: re-finds the HQ pointer matching the saved hqVisibleID across loaded base funders then unloaded NP_BaseUnits.
        private TeamBasePointer ResolveHQFromSavedID()
        {
            foreach (var f in RLoadedBases.IterateTeamBaseFunders(Team))
            {
                if (f != null && f.tank != null && f.tank.visible != null && f.tank.visible.ID == hqVisibleID)
                    return f;
            }
            var pres = ManEnemyWorld.GetTeam(Team);
            if (pres != null)
            {
                foreach (NP_BaseUnit u in pres.EBUs)
                    if (u != null && u.ID == hqVisibleID) return u;
            }
            return null;
        }
        // REVISED: sets the live HQ pointer and captures its visible ID into hqVisibleID for save/restore (replaces direct _HQ writes).
        private void SetHQInternal(TeamBasePointer p)
        {
            _HQ = p;
            if (p is RLoadedBases.EnemyBaseFunder ebf && ebf.tank != null && ebf.tank.visible != null)
                hqVisibleID = ebf.tank.visible.ID;
            else if (p is NP_BaseUnit nbu)
                hqVisibleID = nbu.ID;
            else
                hqVisibleID = -1;
        }
        public int PlayerTeam = int.MinValue;
        /// <summary>
        /// The fallback relation for any unknown relations
        /// </summary>
        public int relationInt = (int)TeamRelations.Enemy;

        /// <summary>
        /// This rises each time the team is attacked by other teams it isn't an Enemy, if it gets too high, relations shall drop
        /// </summary>
        [JsonIgnore]
        public float angerThreshold = 0;
        /// <summary> DO NOT SET - PUBLIC FOR SERIALIZATION </summary>
        public bool Infighting = false;
        internal bool IsInfighting => Infighting;
        /// <summary>This means the team cannot be changed, or have it's relations changed with any other teams</summary>
        public bool IsReadonly = false;

        /// <summary>
        /// Should NEVER be changed under ANY circumstances!
        /// </summary>
        internal TeamRelations defaultRelations
        {
            get => (TeamRelations)relationInt;
            set { relationInt = (int)value; }
        }
        /// <summary> teamID, TeamAlignment</summary>
        public Dictionary<int, TeamRelations> align = new Dictionary<int, TeamRelations>();

        internal IEnumerable<Tank> AllTechsIterator => TankAIManager.GetTeamTanks(teamID);

        public static explicit operator int (EnemyTeamData TR) => TR.teamID;

        public bool HasAnyTechsLeftAlive()
        {
            if (IsReadonly || ManSpawn.IsPlayerTeam(teamID))
                return true;
            // REVISED: a non-null presence no longer counts as alive on its own; now requires it to hold at least one unloaded base unit or mobile.
            var pres = ManEnemyWorld.GetTeam(Team);
            if (pres != null && (pres.EBUs.Count > 0 || pres.EMUs.Count > 0))
                return true;
            if (AllTechsIterator.Any())
                return true;
            foreach (var item in ManVisible.inst.AllTrackedVisibles)
            {
                if (item.ObjectType == ObjectTypes.Vehicle && item.TeamID == teamID)
                    return true;
            }
            return false;
        }

        /// <summary> SERIALIZATION (or default attack-all teams) ONLY </summary>
        public EnemyTeamData() {}
        public EnemyTeamData(int team, bool infighting, TeamRelations defaultRelations = TeamRelations.Enemy)
        {
            teamID = team;
            teamName = TeamNamer.GetTeamName(teamID);
            DebugTAC_AI.LogTeams("New Team of name " + teamName + ", ID " + team + ", alignment(vs player) " +
                Alignment_Internal(ManPlayer.inst.PlayerTeam));
            relationInt = (int)defaultRelations;
            Infighting = infighting;
            IsReadonly = false;
        }
        public EnemyTeamData(bool setReadonly, int team, bool infighting, TeamRelations defaultRelations = TeamRelations.Enemy)
        {
            teamID = team;
            teamName = TeamNamer.GetTeamName(teamID);
            DebugTAC_AI.LogTeams("New Team of name " + teamName + ", ID " + team + ", alignment(vs player) " +
                Alignment_Internal(ManPlayer.inst.PlayerTeam));
            relationInt = (int)defaultRelations;
            Infighting = infighting;
            IsReadonly = setReadonly;
        }
        public TeamRelations GetRelations(int teamOther, TeamRelations fallback = TeamRelations.Enemy) =>
            ManBaseTeams.GetRelationsWritablePriority(teamID, teamOther, fallback);
        /// <summary>
        /// DO NOT CALL THIS FROM ANYWHERE OTHER THAN ManBaseTeams
        /// </summary>
        internal TeamRelations Alignment_Internal(int teamOther)
        {
            switch (teamOther)
            {
                case ManSpawn.NeutralTeam:
                    return TeamRelations.Neutral;
            }
            if (align == null)
                throw new NullReferenceException("EnemyTeamData align IS NULL");
            if (teamID == teamOther)
                return Infighting ? TeamRelations.Enemy : TeamRelations.SameTeam;
            else
            {
                if (align.TryGetValue(teamOther, out var relate))
                    return relate;
            }
            return defaultRelations;
        }
        public EnemyStanding EnemyMindAlignment(int teamOther)
        {
            TeamRelations relations = Alignment_Internal(teamOther);
            switch (relations)
            {
                case TeamRelations.Enemy:
                    return EnemyStanding.Enemy;
                case TeamRelations.SubNeutral:
                    return EnemyStanding.SubNeutral;
                case TeamRelations.Neutral:
                    return EnemyStanding.Neutral;
                case TeamRelations.Friendly:
                    return EnemyStanding.Friendly;
                case TeamRelations.AITeammate:
                    return EnemyStanding.Friendly;
                default:
                    return EnemyStanding.Friendly;
            }
        }

        private void ReadonlyComplaint()
        {
#if DEBUG
  DebugTAC_AI.Assert("Team " + teamID + " has IsReadonly set to true!  Check for this first using ManTeams.CanAlterRelations()");
#endif
        }

        // EnemyPurchase
        public void AddBuildBucks(int toAdd)
        {
            if (IsReadonly)
            {
                ReadonlyComplaint();
                return;
            }
            AddBuildBucks_Internal = toAdd;
        }
        public void SpendBuildBucks(int toUse)
        {
            if (IsReadonly)
            {
                ReadonlyComplaint();
                return;
            }
            AddBuildBucks_Internal = -toUse;
        }
        public int StealBuildBucks(float stealSeverity = 0.2f)
        {
            if (IsReadonly)
            {
                ReadonlyComplaint();
                return 0;
            }
            int stealAmount = Mathf.CeilToInt(BuildBucks * UnityEngine.Random.Range(0, stealSeverity));
            AddBuildBucks_Internal = -stealAmount;
            return stealAmount;
        }
        public bool PurchasePossible(int BBCost)
        {
            if (IsReadonly)
                return true;
            return BBCost <= BuildBucks;
        }
        public bool TryMakePurchase(BlockTypes bloc)
        {
            return TryMakePurchase(Singleton.Manager<RecipeManager>.inst.GetBlockBuyPrice(bloc, true));
        }
        public bool TryMakePurchase(int Pay)
        {
            if (IsReadonly)
            {
                ReadonlyComplaint();
                return true;
            }
            if (PurchasePossible(Pay))
            {
                SpendBuildBucks(Pay);
                return true;
            }
            return false;
        }
        public void FlagBankrupt()
        {
            if (IsReadonly)
            {
                ReadonlyComplaint();
                return;
            }
            bankrupt = true;
        }

        public bool ImproveRelations(int team) => ManBaseTeams.ImproveRelations(teamID, team);
        internal void ImproveRelations_Internal(int team)
        {
            angerThreshold = 0;
            TeamRelations TRP = Alignment_Internal(team);
            if (TRP >= TeamRelations.AITeammate)
                return;
            // REVISED: clamp ceiling is now AITeammate (was the enum value count, which let SameTeam=9001 leak through); same change in DegradeRelations_Internal.
            TeamRelations TRN = (TeamRelations)Mathf.Clamp((int)TRP + 1, (int)TeamRelations.Enemy, (int)TeamRelations.AITeammate);
            if (AIGlobals.ShowDebugFeedBack)
            {
                switch (TRN)
                {
                    case TeamRelations.Enemy:
                        AIGlobals.PopupEnemyInfo(TRP.ToString() + " -> " + TRN.ToString(),
                            WorldPosition.FromScenePosition(Singleton.playerPos +
                            (Singleton.camera.transform.forward * 12)));
                        break;
                    case TeamRelations.SubNeutral:
                    case TeamRelations.Neutral:
                        AIGlobals.PopupSubNeutralInfo(TRP.ToString() + " -> " + TRN.ToString(),
                            WorldPosition.FromScenePosition(Singleton.playerPos +
                            (Singleton.camera.transform.forward * 12)));
                        break;
                    case TeamRelations.Friendly:
                        AIGlobals.PopupAllyInfo(TRP.ToString() + " -> " + TRN.ToString(),
                            WorldPosition.FromScenePosition(Singleton.playerPos +
                            (Singleton.camera.transform.forward * 12)));
                        break;
                    default:
                        AIGlobals.PopupPlayerInfo(TRP.ToString() + " -> " + TRN.ToString(),
                            WorldPosition.FromScenePosition(Singleton.playerPos +
                            (Singleton.camera.transform.forward * 12)));
                        break;
                }
            }
            Set(team, TRN);
        }
        public bool DegradeRelations(int team, float damage = 0) => ManBaseTeams.DegradeRelations(teamID, team, damage);
        internal bool DegradeRelations_Internal(int team, float damage = 0)
        {
            TeamRelations TRP = Alignment_Internal(team);
            if (TRP <= TeamRelations.Enemy)
                return false;
            if (damage != 0)
            {
                angerThreshold += damage;
                if (AIGlobals.DamageAngerDropRelations > angerThreshold)
                    return false;
            }
            TeamRelations TRN = (TeamRelations)Mathf.Clamp((int)TRP - 1, (int)TeamRelations.Enemy, (int)TeamRelations.AITeammate);
            DebugTAC_AI.Log("Degrade in relations between " + TeamNamer.GetTeamName(team) + " and " +
                TeamNamer.GetTeamName(teamID) + ", " + TRP + " -> " + TRN);
            if (AIGlobals.ShowDebugFeedBack)
            {
                switch (TRN)
                {
                    case TeamRelations.Enemy:
                        AIGlobals.PopupEnemyInfo(TRP.ToString() + " -> " + TRN.ToString(),
                            WorldPosition.FromScenePosition(Singleton.playerPos +
                            (Singleton.camera.transform.forward * 12)));
                        break;
                    case TeamRelations.SubNeutral:
                    case TeamRelations.Neutral:
                        AIGlobals.PopupSubNeutralInfo(TRP.ToString() + " -> " + TRN.ToString(),
                            WorldPosition.FromScenePosition(Singleton.playerPos +
                            (Singleton.camera.transform.forward * 12)));
                        break;
                    case TeamRelations.Friendly:
                        AIGlobals.PopupAllyInfo(TRP.ToString() + " -> " + TRN.ToString(),
                            WorldPosition.FromScenePosition(Singleton.playerPos +
                            (Singleton.camera.transform.forward * 12)));
                        break;
                    default:
                        AIGlobals.PopupPlayerInfo(TRP.ToString() + " -> " + TRN.ToString(),
                            WorldPosition.FromScenePosition(Singleton.playerPos +
                            (Singleton.camera.transform.forward * 12)));
                        break;
                }
            }
            Set(team, TRN);
            return true;
        }

        // REVISED: public SetInfighting now guards IsReadonly (was an unguarded direct set); the raw write moved to SetInfighting_Internal for default-team seeding.
        public void SetInfighting(bool state)
        {
            if (IsReadonly)
            {
                ReadonlyComplaint();
                return;
            }
            SetInfighting_Internal(state);
        }
        internal void SetInfighting_Internal(bool state)
        {
            Infighting = state;
        }
        public void Set(int team, TeamRelations relate) => ManBaseTeams.SetRelations(teamID, team, relate);
        internal void Set_Internal(int team, TeamRelations relate)
        {
            if (IsReadonly)
            {
                ReadonlyComplaint();
                return;
            }
            align[team] = relate;
            // REVISED: mirror into the partner's align now skips readonly default teams (added !val.IsReadonly guard).
            if (ManBaseTeams.inst.teams.TryGetValue(team, out var val) && !val.IsReadonly && val.Alignment_Internal(teamID) != relate)
            {
                val.align[teamID] = relate;
            }
            ManBaseTeams.TeamAlignmentDeltaEvent.Send(teamID);
            ManEnemyWorld.UpdateTeam(teamID);
        }
        public void SetNeutral(int team)
        {
            Set(team, TeamRelations.Neutral);
        }
        public void SetHoldFire(int team)
        {
            Set(team, TeamRelations.SubNeutral);
        }
        public void SetFriendly(int team)
        {
            Set(team, TeamRelations.Friendly);
        }
        public void SetEnemy(int team)
        {
            Set(team, TeamRelations.Enemy);
        }

        /// <summary>
        /// This is VERY lazy.  There's only a small chance it will actually transfer the funds to the real strongest base.
        /// <para>This is normally handled automatically by the manager, but you can call this if you want to move the cash NOW</para>
        /// </summary>
        /// REVISED: a stale 'funds' param doc was removed here - this overload is parameterless (the funds-taking version is the EnemyBaseFunder extension in RLoadedBases).
        /// <returns>True if it actually moved the money</returns>
        public bool SetHQToStrongestOrRandomBase()
        {
            if (IsReadonly)
                throw new InvalidOperationException("What's this? Team " + teamID +
                    " has IsReadonly set to true! This team should NEVER be updated under any circumstances!");

            TeamBasePointer prevHQ = _HQ;
            int baseSize = 0;
            foreach (RLoadedBases.EnemyBaseFunder item in RLoadedBases.IterateTeamBaseFunders(Team))
            {
                int blockC = item.BlockCount;
                if (baseSize < blockC)
                {
                    baseSize = blockC;
                    SetHQInternal(item);
                }
            }
            var teamInstU = ManEnemyWorld.GetTeam(Team);
            if (teamInstU != null)
            {
                foreach (TeamBasePointer fundC in teamInstU.EBUs)
                {
                    int blockC = fundC.BlockCount;
                    if (baseSize < blockC)
                    {
                        baseSize = blockC;
                        SetHQInternal(fundC);
                    }
                }
            }
            // REVISED: HQ writes go through SetHQInternal (captures hqVisibleID); the "new HQ" log is now null-guarded so a team with no bases doesn't NRE.
            if (prevHQ != _HQ && _HQ != null)
                DebugTAC_AI.LogTeams(KickStart.ModID + ": Base " + _HQ.Name + " is assigned as new HQ for team " + teamID);
            return true;
        }

        internal void ManageBases()
        {
            if (IsReadonly)
                throw new InvalidOperationException("What's this? Team " + teamID +
                    " has IsReadonly set to true! This team should NEVER be updated under any circumstances!");
            if (HQ == null)
            {
                SetHQToStrongestOrRandomBase();
            }
            else
            {
                if (HQ is RLoadedBases.EnemyBaseFunder funder)
                {
                    if (funder)
                    {
                        EnemyMind mind = funder.GetComponent<EnemyMind>();
                        if (mind)
                            RLoadedBases.UpdateBaseOperations(mind);
                    }
                }
                else if (HQ is NP_BaseUnit funderU)
                {
                }
            }
        }
    }

    /// REVISED (overview): HQ is now persisted by visible ID (hqVisibleID) and lazily re-resolved, not held by transient pointer.
    /// Dynamic team-ID allocation gained a dead-team GC sweep (TryReclaimDeadTeamID) and throws on exhaustion instead of wrapping.
    /// Session reset is centralized in ResetSessionState (clears teams, HiddenVisibles, seededSpawnCoords, TradingSellOffers).
    /// seededSpawnCoords is a new [SSaveField]; orphan TradingSellOffers are purged on save/load. Networked team deltas now
    /// priority-drain and slice into packets of MaxPerPacket rather than a single 255-capped packet.
    [AutoSaveManager]
    public class ManBaseTeams
    {
        public static float PercentChanceExisting = 0.35f;

        [SSManagerInst]
        public static ManBaseTeams inst = null;
        [SSaveField]
        public Dictionary<int, EnemyTeamData> teams = null;
        [SSaveField]
        public Dictionary<int, int> TradingSellOffers = new Dictionary<int, int>();
        [SSaveField]
        public HashSet<int> HiddenVisibles = new HashSet<int>();
        // REVISED: new persisted per-coord dedup set for LastSecondAddBaseToWorldTile spawns (prevents recycled-tile re-spawn).
        [SSaveField]
        public HashSet<IntVector2> seededSpawnCoords = new HashSet<IntVector2>();

        public static void PickupRecycled(Visible vis)
        {
            vis.RecycledEvent.Unsubscribe(PickupRecycled);
            if (inst != null && inst.TradingSellOffers != null)
                inst.TradingSellOffers.Remove(vis.ID);
        }

        // REVISED: new sweep removing TradingSellOffers whose visible is no longer tracked; called on save and post-load.
        private static int PurgeOrphanedSellOffers(string phase)
        {
            if (inst?.TradingSellOffers == null || inst.TradingSellOffers.Count == 0)
                return 0;
            var manVis = Singleton.Manager<ManVisible>.inst;
            if (manVis == null) return 0;
            List<int> dead = null;
            foreach (var kv in inst.TradingSellOffers)
            {
                TrackedVisible tv = manVis.GetTrackedVisible(kv.Key);
                if (tv == null)
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
            }
            if (dead == null) return 0;
            foreach (int id in dead) inst.TradingSellOffers.Remove(id);
            DebugTAC_AI.Log("ManBaseTeams - " + phase + " purged " + dead.Count + " orphan TradingSellOffers.");
            return dead.Count;
        }

        public int ready = 0;
        [SSaveField]
        private int lowTeam = AIGlobals.EnemyTeamsRangeStart;
        /// <summary> Team, Amount </summary>
        public static Event<int, int> BuildBucksUpdatedEvent = new Event<int, int>();
        public static Event<int> TeamAlignmentDeltaEvent = new Event<int>();
        public static Event<int> TeamRemovedEvent = new Event<int>();

        public static void Initiate()
        {
            if (inst != null)
                return;
            inst = new ManBaseTeams();
            ManGameMode.inst.ModeStartEvent.Subscribe(OnModeStart);
            ManGameMode.inst.ModeSwitchEvent.Subscribe(OnModeSwitch);
            TankAIManager.TeamDestroyedEvent.Subscribe(OnTeamDestroyedCheck);
            ManEnemyWorld.TeamDestroyedEvent.Subscribe(OnTeamDestroyedCheck);
            InsureNetHooks();
            InsureDefaultTeams(false);
        }
        private static void CreateDefaultTeam(int team, TeamRelations defaultRelations, bool infighting, bool lockedReadOnly, bool fixup)
        {
            if (!inst.teams.TryGetValue(team, out var ETDG))
            {
                EnemyTeamData ETD = new EnemyTeamData(lockedReadOnly, team, infighting, defaultRelations);
                ETD.SetInfighting_Internal(infighting);
                inst.teams.Add(team, ETD);
            }
            else if (fixup)
                SetDefaultTeam(ETDG, defaultRelations, infighting, lockedReadOnly);
        }
        private static void SetDefaultTeam(EnemyTeamData ETD, TeamRelations defaultRelations, bool infighting, bool lockedReadOnly)
        {
            bool doFixup = false;
            if (ETD.relationInt != (int)defaultRelations)
            {
                DebugTAC_AI.Assert("Somehow our default team " + ETD.teamName + " was set to an ILLEGAL VALUE " +
                    (TeamRelations)ETD.relationInt + " when it SHOULD be " + defaultRelations + "!");
                doFixup = true;
            }
            else if (ETD.IsInfighting != infighting)
            {
                DebugTAC_AI.Assert("Somehow our default team " + ETD.teamName + " was set to INFIGHTING " +
                    ETD.IsInfighting + " when it SHOULD be " + infighting + "!");
                doFixup = true;
            }

            // REVISED: a readonly team's align is now scrubbed unconditionally (was nested inside the doFixup branch); doFixup only re-applies infighting + defaultRelations.
            if (lockedReadOnly && ETD.align.Count > 0)
                ETD.align.Clear();

            if (doFixup)
            {
                ETD.SetInfighting_Internal(infighting);
                ETD.defaultRelations = defaultRelations;
            }
        }
        private static void SanityCheckTeam(int team)
        {
            if (TryGetBaseTeamAny(team, out var teamInst))
            {
                bool didClean = false;
                if (teamInst.align.Remove(AIGlobals.DefaultEnemyTeam))
                {
                    TankAIManager.UpdateEntireTeam(AIGlobals.DefaultEnemyTeam);
                    didClean = true;
                }
                if (teamInst.align.Remove(AIGlobals.LonerEnemyTeam))
                {
                    TankAIManager.UpdateEntireTeam(AIGlobals.LonerEnemyTeam);
                    didClean = true;
                }
                if (teamInst.align.Remove(ManSpawn.NeutralTeam))
                {
                    TankAIManager.UpdateEntireTeam(ManSpawn.NeutralTeam);
                    didClean = true;
                }
                if (teamInst.align.Remove(SpecialAISpawner.trollTeam))
                {
                    TankAIManager.UpdateEntireTeam(SpecialAISpawner.trollTeam);
                    didClean = true;
                }
                if (didClean)
                    TankAIManager.UpdateEntireTeam(team);
            }
        }
        public static void InsureDefaultTeams(bool fixup)
        {
            if (inst.teams == null)
                inst.teams = new Dictionary<int, EnemyTeamData>();
            CreateDefaultTeam(ManSpawn.DefaultPlayerTeam, TeamRelations.Enemy, false, false, fixup);
            CreateDefaultTeam(AIGlobals.DefaultEnemyTeam, TeamRelations.Enemy, false, true, fixup);
            CreateDefaultTeam(AIGlobals.LonerEnemyTeam, TeamRelations.Enemy, true, true, fixup);
            CreateDefaultTeam(ManSpawn.NeutralTeam, TeamRelations.Neutral, false, true, fixup);
            CreateDefaultTeam(SpecialAISpawner.trollTeam, TeamRelations.Enemy, false, true, fixup);

            if (ManNetwork.IsNetworked)
            {
                CreateDefaultTeam(1073741824, TeamRelations.Enemy, false, false, fixup);
                CreateDefaultTeam(1073741825, TeamRelations.Enemy, false, false, fixup);
                CreateDefaultTeam(1073741826, TeamRelations.Enemy, false, false, fixup);
                CreateDefaultTeam(1073741827, TeamRelations.Enemy, false, false, fixup);
                CreateDefaultTeam(ModeCoOpCreative.NeutralTeam, TeamRelations.Neutral, false, false, fixup);
            }
            if (fixup)
            {
                SanityCheckTeam(ManSpawn.DefaultPlayerTeam);
                if (ManNetwork.IsNetworked)
                {
                    SanityCheckTeam(1073741824);
                    SanityCheckTeam(1073741825);
                    SanityCheckTeam(1073741826);
                    SanityCheckTeam(1073741827);
                    SanityCheckTeam(ModeCoOpCreative.NeutralTeam);
                }
            }
        }
        // REVISED: removed the static SanityCheckIfDefaultTeam(EnemyTeamData) switch; default-team fixup now flows solely through CreateDefaultTeam/SetDefaultTeam.
        public static void DeInit()
        {
            if (inst == null)
                return;
            ManEnemyWorld.TeamDestroyedEvent.Unsubscribe(OnTeamDestroyedCheck);
            TankAIManager.TeamDestroyedEvent.Unsubscribe(OnTeamDestroyedCheck);
            ManGameMode.inst.ModeSwitchEvent.Unsubscribe(OnModeSwitch);
            ManGameMode.inst.ModeStartEvent.Unsubscribe(OnModeStart);
            inst = null;
        }

        // REVISED: extracted shared session reset (used by OnModeSwitch + OnWorldPreLoad); now also clears HiddenVisibles, seededSpawnCoords and TradingSellOffers (previously only teams/ready/lowTeam were reset).
        private static void ResetSessionState(string reason)
        {
            if (inst == null) return;
            int teamCount   = inst.teams?.Count             ?? 0;
            int hiddenCount = inst.HiddenVisibles?.Count    ?? 0;
            int seededCount = inst.seededSpawnCoords?.Count ?? 0;
            int offersCount = inst.TradingSellOffers?.Count ?? 0;
            if (inst.teams != null)             inst.teams.Clear();
            if (inst.HiddenVisibles != null)    inst.HiddenVisibles.Clear();
            if (inst.seededSpawnCoords != null) inst.seededSpawnCoords.Clear();
            if (inst.TradingSellOffers != null) inst.TradingSellOffers.Clear();
            inst.ready   = 0;
            inst.lowTeam = AIGlobals.EnemyTeamsRangeStart;
            DebugTAC_AI.Log("ManBaseTeams.ResetSessionState(" + reason + ") - cleared "
                + teamCount + " teams, " + hiddenCount + " hidden visibles, "
                + seededCount + " seeded coords, " + offersCount + " sell offers.");
        }

        public static void OnModeSwitch()
        {
            try { ResetSessionState("OnModeSwitch"); }
            catch (Exception e) { DebugTAC_AI.LogError("ManBaseTeams.OnModeSwitch failed", e); }
        }
        public static void OnModeStart(Mode mode)
        {
            try
            {
                InsureDefaultTeams(false);
                CheckNeedNetworkHooks(mode);
            }
            catch (Exception e) { DebugTAC_AI.LogError("ManBaseTeams.OnModeStart failed", e); }
        }

        public static void OnTeamDestroyedCheck(int team)
        {
            if (inst.teams != null && inst.teams.TryGetValue(team, out var teamInst) && !teamInst.HasAnyTechsLeftAlive())
            {
                DebugTAC_AI.Log("OnTeamDestroyedCheck - Team " + TeamNamer.GetTeamName(team) + " has been completely obliterated!");
                TeamRemovedEvent.Send(team);
                inst.teams.Remove(team);
            }
        }
        public static void OnTeamDestroyedRemoteClient(int team)
        {
            DebugTAC_AI.Log("OnTeamDestroyedRemote - Team " + TeamNamer.GetTeamName(team) + " has been completely obliterated!");
            TeamRemovedEvent.Send(team);
            inst.teams.Remove(team);
        }

        private static IEnumerable<EnemyTeamData> IterateALLTeams(Func<EnemyTeamData, bool> ETD)
        {
            foreach (var item in inst.teams.Values)
            {
                if (item != null && ETD(item))
                    yield return item;
            }
        }
        private static IEnumerable<EnemyTeamData> IterateBaseTeams(Func<EnemyTeamData,bool> ETD)
        {
            foreach (var item in inst.teams.Values)
            {
                if (item != null && !item.IsReadonly && ETD(item))
                    yield return item;
            }
        }
        private static IEnumerable<EnemyTeamData> IterateBaseTeams()
        {
            foreach (var item in inst.teams.Values)
            {
                if (item != null && !item.IsReadonly)
                    yield return item;
            }
        }
        private const int LowTeamFloor = int.MinValue + 1;

        // REVISED: new GC sweep - reaps dynamic non-readonly, non-player teams below the range start that have no techs left, recycling the highest freed ID.
        private static bool TryReclaimDeadTeamID(out int reclaimed)
        {
            List<int> dead = null;
            foreach (var kv in inst.teams)
            {
                var ETD = kv.Value;
                if (ETD == null || ETD.IsReadonly) continue;
                if (AIGlobals.IsPlayerTeam(kv.Key)) continue;
                if (kv.Key > AIGlobals.EnemyTeamsRangeStart) continue;
                if (!ETD.HasAnyTechsLeftAlive())
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
            }
            if (dead == null) { reclaimed = 0; return false; }
            foreach (var id in dead)
            {
                TeamRemovedEvent.Send(id);
                inst.teams.Remove(id);
            }
            reclaimed = dead.Max();
            return true;
        }

        // REVISED: now bounded by LowTeamFloor; on exhaustion it attempts TryReclaimDeadTeamID then throws InvalidOperationException (was a try/finally that simply kept decrementing past int.MinValue and overflowed).
        private static int GetNewTeamID()
        {
            while (inst.lowTeam > LowTeamFloor && inst.teams.ContainsKey(inst.lowTeam))
                inst.lowTeam--;

            if (inst.lowTeam <= LowTeamFloor || inst.teams.ContainsKey(inst.lowTeam))
            {
                if (TryReclaimDeadTeamID(out int recycled))
                {
                    DebugTAC_AI.LogError("ManBaseTeams: recycled dead team ID " + recycled +
                        " (live teams: " + inst.teams.Count + ")");
                    return recycled;
                }
                throw new InvalidOperationException(
                    "ManBaseTeams: dynamic team ID space exhausted (" +
                    inst.teams.Count + " live teams, lowTeam=" + inst.lowTeam + ")");
            }

            int id = inst.lowTeam;
            inst.lowTeam--;
            return id;
        }
        // REVISED: dropped the checked{}/OverflowException-fallback wrapper; ID exhaustion is now handled (reclaim-or-throw) inside GetNewTeamID.
        public static EnemyTeamData GetNewBaseTeam(TeamRelations defaultRelations)
        {
            int newTeamID = GetNewTeamID();
            var valNew = new EnemyTeamData(newTeamID, false, defaultRelations);
            inst.teams.Add(newTeamID, valNew);
            return valNew;
        }
        // REVISED: dropped the checked{}/OverflowException wrapper; post-create verification now resolves against the passed-in team rather than the static playerTeam field.
        public static EnemyTeamData GetTeamAIBaseTeam(int team)
        {
            var findable = IterateBaseTeams(x => x.Alignment_Internal(team) == TeamRelations.AITeammate).FirstOrDefault();
            if (findable != null)
                return findable;
            int newTeamID = GetNewTeamID();
            var valNew = new EnemyTeamData(newTeamID, false);
            valNew.PlayerTeam = team;
            inst.teams.Add(newTeamID, valNew);
            SetRelations(valNew.teamID, team, TeamRelations.AITeammate);
            DebugTAC_AI.Assert("Team " + valNew.teamName + " has spawned as a player auto team!");
            if (!IsPlayerOwnedAIBaseTeam(valNew.teamID))
                DebugTAC_AI.FatalError("Team " + valNew.teamName + " is player auto team but not properly marked as player's AI base team");
            if (GetRelationsWithWriteablePriority(valNew.teamID, team, out EnemyTeamData ETD))
            {
                TeamRelations TR = ETD.Alignment_Internal(ETD.teamID == team ? valNew.teamID : team);
                if (TR != TeamRelations.AITeammate)
                    DebugTAC_AI.FatalError("Team " + valNew.teamName + " is player auto team but marked as " + TR);
            }
            else
                DebugTAC_AI.FatalError("Team " + valNew.teamName + " is player auto team but is NOT REGISTERED IN TEAMS");
            return valNew;
        }
        internal static EnemyTeamData InsureBaseTeam(int team)
        {
            if (inst.teams.TryGetValue(team, out var val))
                return val;
            else
            {
                if (AIGlobals.IsPlayerTeam(team))
                    throw new InvalidOperationException("Player team cannot be assigned as a BaseTeam");
                if (ManSpawn.NeutralTeam == team)
                    throw new InvalidOperationException("Neutral team cannot be assigned as a BaseTeam");
                var valNew = new EnemyTeamData(team, false);
                inst.teams.Add(team, valNew);
                return valNew;
            }
        }
        internal static bool TryInsureBaseTeam(int team, out EnemyTeamData ETD)
        {
            if (!inst.teams.TryGetValue(team, out ETD))
            {
                if (AIGlobals.IsPlayerTeam(team) || ManSpawn.NeutralTeam == team)
                    return false;
                ETD = InsureBaseTeam(team);
            }
            return true;
        }
        public static bool BaseTeamExists(int team) => inst.teams.TryGetValue(team, out var ETD) && !ETD.IsReadonly;
        public static bool TryGetBaseTeamAny(int team, out EnemyTeamData ETD) => inst.teams.TryGetValue(team, out ETD);
        public static bool TryGetBaseTeamDynamicOnly(int team, out EnemyTeamData ETD)
        {
            if (inst.teams.TryGetValue(team, out ETD) && !ETD.IsReadonly)
                return true;
            ETD = null;
            return false;
        }
        public static bool TryGetBaseTeamStaticOnly(int team, out EnemyTeamData ETD)
        {
            if (inst.teams.TryGetValue(team, out ETD) && ETD.IsReadonly)
                return true;
            ETD = null;
            return false;
        }
        // REVISED: snapshots to a List once and indexes Random.Range(0, Count) - the upper bound is now Count (exclusive), so the last team is now selectable (was Count-1, which never returned the final entry); same change in TryGetExistingBaseTeamWithPlayerAlignment.
        public static EnemyTeamData GetRandomExistingBaseTeam()
        {
            List<EnemyTeamData> teams = IterateBaseTeams().ToList();
            if (teams.Count < 1)
                return null;
            return teams[UnityEngine.Random.Range(0, teams.Count)];
        }
        public static bool TryGetExistingBaseTeamWithPlayerAlignment(TeamRelations relations, out EnemyTeamData data)
        {
            List<EnemyTeamData> teams = IterateBaseTeams(x => x.Alignment_Internal(playerTeam) == relations).ToList();
            if (teams.Count < 1)
            {
                data = null;
                return false;
            }
            data = teams[UnityEngine.Random.Range(0, teams.Count)];
            return data != null;
        }

        public static bool IsBaseTeamAny(int team)
        {
            return team == SpecialAISpawner.trollTeam ||
                (!AIGlobals.IsPlayerTeam(team) && inst.teams.ContainsKey(team));
        }
        public static bool IsBaseTeamDynamic(int team)
        {
            return team == SpecialAISpawner.trollTeam ||
                (!AIGlobals.IsPlayerTeam(team) && inst.teams.TryGetValue(team, out var ETD) && !ETD.IsReadonly);
        }
        public static bool IsBaseTeamDynamicOrUnregistered(int team)
        {
            return team == SpecialAISpawner.trollTeam ||
                (!AIGlobals.IsPlayerTeam(team) && (inst.teams.TryGetValue(team, out var ETD) ? !ETD.IsReadonly : team <= AIGlobals.EnemyTeamsRangeStart));
        }
        public static bool IsBaseTeamStatic(int team)
        {
            return team == SpecialAISpawner.trollTeam ||
                (!AIGlobals.IsPlayerTeam(team) && inst.teams.TryGetValue(team, out var ETD) && ETD.IsReadonly);
        }

        public static bool IsTeamHQ(TeamBasePointer pointer)
        {
            if (TryGetBaseTeamDynamicOnly(pointer.Team, out var ETD))
                return ETD == pointer;
            return false;
        }
        public static int GetTeamMoney(int team)
        {
            if (TryGetBaseTeamDynamicOnly(team, out var ETD))
                return ETD.BuildBucks;
            return 0;
        }

        // Relations
        // REVISED: removed the unused near-duplicate GetRelationsOnlyWritable; GetRelationsWithWriteablePriority is the sole writable-priority resolver.
        private static bool GetRelationsWithWriteablePriority(int teamID1, int teamID2, out EnemyTeamData ETD)
        {
            if (teamID2 < teamID1)
            {   // Lowest team gets searched first
                int swapper = teamID1;
                teamID1 = teamID2;
                teamID2 = swapper;
            }
            // ALWAYS prioritize the non-immutable ones to get the correct alignment!
            if (inst.teams.TryGetValue(teamID1, out ETD) && !ETD.IsReadonly)
                return true;
            if (inst.teams.TryGetValue(teamID2, out ETD))
                return true;
            if (inst.teams.TryGetValue(teamID1, out ETD))
                return true;
            return false;
        }
        private static bool GetRelationsWithReadonlyPriority(int teamID1, int teamID2, out EnemyTeamData ETD)
        {
            if (teamID2 < teamID1)
            {   // Lowest team gets searched first
                int swapper = teamID1;
                teamID1 = teamID2;
                teamID2 = swapper;
            }
            // ALWAYS prioritize the immutable ones to get the correct alignment!
            // REVISED: first hit checks ETD.IsReadonly (the immutable team); stray "non-immutable" copy from the writable-priority variant removed.
            if (inst.teams.TryGetValue(teamID1, out ETD) && ETD.IsReadonly)
                return true;
            if (inst.teams.TryGetValue(teamID2, out ETD))
                return true;
            if (inst.teams.TryGetValue(teamID1, out ETD))
                return true;
            return false;
        }
        public static TeamRelations GetRelationsWritablePriority(int teamID1, int teamID2, TeamRelations fallback)
        {
            if (GetRelationsWithWriteablePriority(teamID1, teamID2, out var ETD))
            {
                if (ETD.PlayerTeam != int.MinValue && inst.teams.TryGetValue(ETD.PlayerTeam, out var ETD2))
                    ETD = ETD2;
                return ETD.Alignment_Internal(ETD.teamID == teamID2 ? teamID1 : teamID2);
            }
            return fallback;
        }
        public static TeamRelations GetRelationsReadonlyPriority(int teamID1, int teamID2, TeamRelations fallback)
        {
            if (GetRelationsWithReadonlyPriority(teamID1, teamID2, out var ETD))
            {
                if (ETD.PlayerTeam != int.MinValue && inst.teams.TryGetValue(ETD.PlayerTeam, out var ETD2))
                    ETD = ETD2;
                return ETD.Alignment_Internal(ETD.teamID == teamID2 ? teamID1 : teamID2);
            }
            return fallback;
        }
        /// <summary>
        /// MUTED for now.  We will now use boolean checking to insure this NEVER happens
        /// </summary>
        private static void ComplainOnIllegalTeamModification()
        {
#if DEBUG
                    DebugTAC_AI.FatalError("We tried to change relations of a READONLY faction - this should be impossible!");
#else
#endif
        }
        public static bool CanAlterRelations(int teamID1, int teamID2)
        {
            if (GetRelationsWithReadonlyPriority(teamID1, teamID2, out var ETD))
                return !ETD.IsReadonly;
            return false;
        }
        public static bool SetRelations(int teamID1, int teamID2, TeamRelations set)
        {
            if (GetRelationsWithWriteablePriority(teamID1, teamID2, out var ETD))
            {
                if (!CanAlterRelations(teamID1, teamID2))
                    ComplainOnIllegalTeamModification();
                else
                {
                    ETD.Set_Internal(ETD.teamID == teamID2 ? teamID1 : teamID2, set);
                    return true;
                }
            }
            return false;
        }
        public static bool ImproveRelations(int teamID1, int teamID2)
        {
            if (GetRelationsWithWriteablePriority(teamID1, teamID2, out var ETD))
            {
                if (!CanAlterRelations(teamID1, teamID2))
                    ComplainOnIllegalTeamModification();
                else
                {
                    ETD.ImproveRelations_Internal(ETD.teamID == teamID2 ? teamID1 : teamID2);
                    return true;
                }
            }
            return false;
        }
        public static bool DegradeRelations(int teamID1, int teamID2, float damage = 0)
        {
            if (GetRelationsWithWriteablePriority(teamID1, teamID2, out var ETD))
            {
                if (!CanAlterRelations(teamID1, teamID2))
                    ComplainOnIllegalTeamModification();
                else
                {
                    ETD.DegradeRelations_Internal(ETD.teamID == teamID2 ? teamID1 : teamID2, damage);
                    return true;
                }
            }
            return false;
        }

        public static bool RelationsMatch(int teamID1, int teamID2, TeamRelations relation) =>
            GetRelationsWritablePriority(teamID1, teamID2, (TeamRelations)(-1)) == relation;
        public static bool RelationTeamGreaterOrEqual(int teamID1, int teamID2, TeamRelations relationsIn, TeamRelations fallback = TeamRelations.Enemy)
        {
            return GetRelationsWritablePriority(teamID1, teamID2, fallback) >= relationsIn;
        }
        public static bool RelationTeamLessOrEqual(int teamID1, int teamID2, TeamRelations relationsIn, TeamRelations fallback = TeamRelations.AITeammate)
        {
            return GetRelationsWritablePriority(teamID1, teamID2, fallback) <= relationsIn;
        }
        public static bool IsEnemy(int teamID1, int teamID2)
        {
            if (DebugRawTechSpawner.AINoAttackPlayer &&
                (teamID1 == ManPlayer.inst.PlayerTeam || teamID2 == ManPlayer.inst.PlayerTeam))
                return false;
            return RelationTeamLessOrEqual(teamID1, teamID2, TeamRelations.Enemy, TeamRelations.Enemy);
        }
        public static bool IsFriendly(int teamID1, int teamID2)
        {
            if (DebugRawTechSpawner.AINoAttackPlayer &&
                (teamID1 == ManPlayer.inst.PlayerTeam || teamID2 == ManPlayer.inst.PlayerTeam))
                return true;
            return RelationTeamGreaterOrEqual(teamID1, teamID2, TeamRelations.Friendly);
        }
        public static bool ShouldNotAttack(int teamID1, int teamID2)
        {
            if (DebugRawTechSpawner.AINoAttackPlayer &&
                (teamID1 == ManPlayer.inst.PlayerTeam || teamID2 == ManPlayer.inst.PlayerTeam))
                return true;
            return RelationTeamGreaterOrEqual(teamID1, teamID2, TeamRelations.SubNeutral);
        }
        public static bool IsUnattackable(int teamID1, int teamID2)
        {
            if (DebugRawTechSpawner.AINoAttackPlayer &&
                (teamID1 == ManPlayer.inst.PlayerTeam || teamID2 == ManPlayer.inst.PlayerTeam))
                return true;
            return RelationTeamGreaterOrEqual(teamID1, teamID2, TeamRelations.Neutral);
        }
        public static bool IsTeammate(int teamID1, int teamID2)
        {
            if (DebugRawTechSpawner.AllowPlayerBuildEnemies &&
                (teamID1 == ManPlayer.inst.PlayerTeam || teamID2 == ManPlayer.inst.PlayerTeam))
                return true;
            return RelationTeamGreaterOrEqual(teamID1, teamID2, TeamRelations.AITeammate);
        }
        public static bool IsNonAggressiveTeam(int team)
        {
            return RelationTeamGreaterOrEqual(team, playerTeam, TeamRelations.SubNeutral);
        }

        public static int playerTeam => ManPlayer.inst.PlayerTeam;
        public static bool IsEnemyBaseTeam(int team)
        {
            return GetRelationsWithWriteablePriority(playerTeam, team, out var val) &&
                val.Alignment_Internal(val.teamID == playerTeam ? team : playerTeam) == TeamRelations.Enemy;
        }
        public static bool IsSubNeutralBaseTeam(int team)
        {
            return GetRelationsWithWriteablePriority(playerTeam, team, out var val) &&
                val.Alignment_Internal(val.teamID == playerTeam ? team : playerTeam) == TeamRelations.SubNeutral;
        }
        public static bool IsNeutralBaseTeam(int team)
        {
            return GetRelationsWithWriteablePriority(playerTeam, team, out var val) &&
                val.Alignment_Internal(val.teamID == playerTeam ? team : playerTeam) == TeamRelations.Neutral;
        }
        public static bool IsFriendlyBaseTeam(int team)
        {
            return GetRelationsWithWriteablePriority(playerTeam, team, out var val) &&
                val.Alignment_Internal(val.teamID == playerTeam ? team : playerTeam) == TeamRelations.Friendly;
        }
        public static bool IsAlliedPlayerAIBaseTeam(int team)
        {
            return GetRelationsWithWriteablePriority(playerTeam, team, out var val) &&
                val.Alignment_Internal(val.teamID == playerTeam ? team : playerTeam) == TeamRelations.AITeammate;
        }
        public static bool IsPlayerOwnedAIBaseTeam(int team)
        {
            return GetRelationsWithWriteablePriority(playerTeam, team, out var val) &&
                val.PlayerTeam != int.MinValue;
        }

        private static float LastTechBuildTime = AIGlobals.SLDBeforeBuilding;
        public static SpecialUpdateType SpecialUpdate = SpecialUpdateType.None;
        internal static void UpdateTeams()
        {
            SpecialUpdate = SpecialUpdateType.None;
            if (Time.time >= LastTechBuildTime)
            {
                LastTechBuildTime = Time.time + AIGlobals.DelayBetweenBuilding;
                SpecialUpdate = SpecialUpdateType.Building;
            }
            foreach (var item in inst.teams.Values)
            {
                if (item.IsReadonly)
                    continue;
                int Team = item.Team;
                // REVISED: anger decay is now rate*tick-interval (DamageAngerCoolRatePerSec * EnemyTeamAwarenessUpdateDelay) instead of a flat per-tick DamageAngerCoolPerSec, so cooldown tracks the actual update cadence.
                if (item.angerThreshold > 0)
                    item.angerThreshold = Mathf.Max(0, item.angerThreshold - AIGlobals.DamageAngerCoolRatePerSec * AIGlobals.EnemyTeamAwarenessUpdateDelay);
                if (AIECore.RetreatingTeams.Contains(Team))
                {
                    float averageTechDMG = 0;
                    int count = 0;
                    foreach (var item2 in TankAIManager.TeamActiveMobileTechs(Team))
                    {
                        averageTechDMG += item2.GetHelperInsured().DamageThreshold;
                        count++;
                    }
                    // REVISED: zero-damage now continues to the next team instead of return-ing, so one team with no active techs no longer aborts the whole per-tick team loop (skipping anger decay + ManageBases for the rest).
                    if (averageTechDMG == 0)
                        continue;
                    averageTechDMG /= count;
                    if (averageTechDMG <= AIGlobals.RetreatBelowTeamDamageThreshold)
                        AIECore.TeamRetreat(Team, false, true);
                }
                item.ManageBases();
            }
        }

        private static float lastComplainTime = 0;
        private static List<string> Complaints = new List<string>()
        {
                "Rude!",
                "Watch it!",
                "Ouch!",
                "Oof!",
                "Look out!",
                "Stop!",
                "Whyy!",
        };
        internal static void AttackComplainPlayer(Vector3 scenePos, int team)
        {
            if (Time.time > lastComplainTime)
            {
                lastComplainTime = Time.time + 2;
                AIGlobals.PopupColored(Complaints.GetRandomEntry(), team, WorldPosition.FromScenePosition(scenePos));
            }
        }

        // REVISED: hardened - snapshots teams up front and restores it on a fatal exception (rollback); the per-tech mapping is now a shared MigrateOne local wrapped in its own try/catch (per-tech failures counted, not fatal), and StoredTilesJSON parse failures skip the bad tile instead of aborting.
        public void MigrateTeamsToNewSaveFormat()
        {
            int count = 0, perTechFailures = 0;
            var snapshot = new Dictionary<int, EnemyTeamData>(inst.teams);
            int pTeam;
            HashSet<int> loaded;
            try
            {
                pTeam = ManPlayer.inst.PlayerTeam;
                loaded = new HashSet<int>();
                foreach (var item in ManTechs.inst.IterateTechs())
                    loaded.Add(item.visible.ID);
            }
            catch (Exception ePre)
            {
                DebugTAC_AI.LogError(KickStart.ModID + ": MigrateTeamsToNewSaveFormat ABORTED pre-scan", ePre);
                return;
            }

            void MigrateOne(ManSaveGame.StoredTech tech, IntVector2 coord)
            {
                try
                {
                    if (loaded.Contains(tech.m_ID)) return;
                    switch (GetLegacyNPTTeamType(tech.m_TeamID))
                    {
                        case NP_Types.Friendly:   InsureBaseTeam(tech.m_TeamID).SetFriendly(pTeam); break;
                        case NP_Types.Neutral:    InsureBaseTeam(tech.m_TeamID).defaultRelations = TeamRelations.Neutral; break;
                        case NP_Types.SubNeutral: InsureBaseTeam(tech.m_TeamID).defaultRelations = TeamRelations.SubNeutral; break;
                        case NP_Types.Enemy:      InsureBaseTeam(tech.m_TeamID).SetEnemy(pTeam); break;
                    }
                    count++;
                }
                catch (Exception ePer)
                {
                    perTechFailures++;
                    DebugTAC_AI.LogError(KickStart.ModID + ": Migrate skipped tech ID=" + tech.m_ID +
                        " team=" + tech.m_TeamID + " tile=" + coord, ePer);
                }
            }

            try
            {
                var state = Singleton.Manager<ManSaveGame>.inst.CurrentState;
                if (state.m_StoredTiles != null)
                {
                    foreach (var item in new Dictionary<IntVector2, ManSaveGame.StoredTile>(state.m_StoredTiles))
                    {
                        ManSaveGame.StoredTile storedTile = item.Value;
                        if (storedTile != null && storedTile.m_StoredVisibles.TryGetValue(1, out List<ManSaveGame.StoredVisible> techs)
                            && techs.Count > 0)
                        {
                            foreach (ManSaveGame.StoredVisible Vis in techs)
                                if (Vis is ManSaveGame.StoredTech t) MigrateOne(t, item.Key);
                        }
                    }
                }
                if (state.m_StoredTilesJSON != null)
                {
                    foreach (var item in new Dictionary<IntVector2, string>(state.m_StoredTilesJSON))
                    {
                        ManSaveGame.StoredTile storedTile = null;
                        try { ManSaveGame.LoadObjectFromRawJson(ref storedTile, item.Value, false, false); }
                        catch (Exception eJson)
                        {
                            perTechFailures++;
                            DebugTAC_AI.LogError(KickStart.ModID + ": Migrate JSON tile parse failed at " + item.Key, eJson);
                            continue;
                        }
                        if (storedTile != null && storedTile.m_StoredVisibles.TryGetValue(1, out List<ManSaveGame.StoredVisible> techs)
                            && techs.Count > 0)
                        {
                            foreach (ManSaveGame.StoredVisible Vis in techs)
                                if (Vis is ManSaveGame.StoredTech t) MigrateOne(t, item.Key);
                        }
                    }
                }
                if (count > 0 || perTechFailures > 0)
                    DebugTAC_AI.Log(KickStart.ModID + ": MigrateTeamsToNewSaveFormat Handled " + count +
                        " Techs (" + perTechFailures + " per-tech failures)");
            }
            catch (Exception e)
            {
                inst.teams = snapshot;
                DebugTAC_AI.LogError(KickStart.ModID + ": MigrateTeamsToNewSaveFormat FATAL at " + count +
                    " Techs - rolled back to " + snapshot.Count + " teams", e);
            }
        }

        public static void OnWorldSave()
        {
            try
            {
                if (inst == null)
                {
                    DebugTAC_AI.LogError("ManBaseTeams - Save failed, saving instance null??");
                    return;
                }
                // REVISED: now purges orphaned TradingSellOffers before saving (and returns early on a null instance).
                PurgeOrphanedSellOffers("pre-save");
                if (inst.teams != null)
                {
                    foreach (var item in inst.teams)
                    {
                        DebugTAC_AI.LogDevOnly("  Team " + item.Value.teamName + ", relation " + item.Value.Alignment_Internal(playerTeam));
                    }
                }
                DebugTAC_AI.Log("ManBaseTeams - Saved " + inst.teams.Count + " NPT base teams.");
            }
            catch (Exception e) { DebugTAC_AI.LogError("ManBaseTeams.OnWorldSave failed", e); }
        }
        public static void OnWorldFinishSave()
        {
            try
            {
                if (inst?.teams != null)
                    DebugTAC_AI.Log("ManBaseTeams - Saved " + inst.teams.Count + " NPT base teams.");
            }
            catch (Exception e) { DebugTAC_AI.LogError("ManBaseTeams.OnWorldFinishSave failed", e); }
        }
        public static void OnWorldPreLoad()
        {
            try
            {
                // REVISED: now runs the full ResetSessionState before nulling teams, so stale hidden visibles / seeded coords / sell offers don't bleed across loads.
                ResetSessionState("OnWorldPreLoad");
                inst.teams = null;
            }
            catch (Exception e) { DebugTAC_AI.LogError("ManBaseTeams.OnWorldPreLoad failed", e); }
        }
        public static void OnWorldLoad()
        {
            try
            {
                if (inst.HiddenVisibles == null)
                    inst.HiddenVisibles = new HashSet<int>();
                // REVISED: also null-guard the new seededSpawnCoords and TradingSellOffers for legacy saves; orphaned sell offers are purged post-load (see end of try).
                if (inst.seededSpawnCoords == null)
                    inst.seededSpawnCoords = new HashSet<IntVector2>();
                if (inst.TradingSellOffers == null)
                    inst.TradingSellOffers = new Dictionary<int, int>();
                if (inst.teams == null)
                {
                    InsureDefaultTeams(false);
                    inst.MigrateTeamsToNewSaveFormat();
                    if (inst.teams.Count > 0)
                        DebugTAC_AI.Log("ManBaseTeams.MigrateTeamsToNewSaveFormat - Migrating " + inst.teams.Count + " NPT base teams.");
                }
                else
                {
                    // Clean up any "corrupted" teams
                    InsureDefaultTeams(true);
                    // Continue with loading
                    foreach (var item in inst.teams)
                    {
                        TankAIManager.UpdateEntireTeam(item.Key);
                    }
                    DebugTAC_AI.Log("ManBaseTeams - Loaded " + inst.teams.Count + " NPT base teams.");
                }
                PurgeOrphanedSellOffers("post-load");
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogError("ManBaseTeams.OnWorldLoad FAILED", e);
            }
        }

        internal class GUIManaged
        {
            private static bool typesDisp = false;
            private static HashSet<int> enabledTabs = null;
            public static void GUIGetTotalManaged()
            {
                if (enabledTabs == null)
                {
                    enabledTabs = new HashSet<int>();
                }
                GUILayout.Box("--- Team Alignments --- ");
                if (GUILayout.Button("  Total: " + inst.teams.Count))
                    typesDisp = !typesDisp;
                if (typesDisp)
                {
                    foreach (var item in inst.teams)
                    {
                        if (GUILayout.Button("    Team: " + item.Key.ToString() + " - " + item.Value.teamName))
                        {
                            if (enabledTabs.Contains(item.Key))
                                enabledTabs.Remove(item.Key);
                            else
                                enabledTabs.Add(item.Key);
                        }
                        if (enabledTabs.Contains(item.Key))
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label("Relations: [To Player: ");
                            GUILayout.Label(item.Value.Alignment_Internal(playerTeam).ToString());
                            GUILayout.FlexibleSpace();
                            GUILayout.Label("Other: ");
                            GUILayout.Label(item.Value.defaultRelations.ToString());
                            GUILayout.Label("]");
                            GUILayout.EndHorizontal();
                            GUILayout.BeginHorizontal();
                            GUILayout.Label("Infighting: ");
                            GUILayout.Label(item.Value.IsInfighting.ToString());
                            GUILayout.FlexibleSpace();
                            GUILayout.EndHorizontal();
                            GUILayout.BeginHorizontal();
                            GUILayout.Label("Money: ");
                            GUILayout.FlexibleSpace();
                            GUILayout.Label(item.Value.BuildBucks.ToString());
                            GUILayout.EndHorizontal();
                            foreach (var item2 in item.Value.align)
                            {
                                GUILayout.BeginHorizontal();
                                GUILayout.Label("Team: ");
                                GUILayout.Label(item2.Key.ToString());
                                GUILayout.Label(" | ");
                                GUILayout.Label(item2.Value.ToString());
                                GUILayout.FlexibleSpace();
                                GUILayout.EndHorizontal();
                            }
                        }
                    }
                }
            }
        }

        private static bool NetReady = false;
        private static Dictionary<int, byte> ToSend = null;
        public static void CheckNeedNetworkHooks(Mode mode)
        {
            if (mode.IsMultiplayer && ManNetwork.IsHostOrWillBe)
            {
                if (!NetReady)
                {
                    NetReady = true;
                    ToSend = new Dictionary<int, byte>();
                    BuildBucksUpdatedEvent.Subscribe(OnNetTeamBBChange);
                    TeamAlignmentDeltaEvent.Subscribe(OnNetTeamAlignChange);
                    TeamRemovedEvent.Subscribe(OnNetTeamDestroyed);
                    InvokeHelper.InvokeSingleRepeat(PushTeamDeltasToClients, 1);
                }
            }
            else
            {
                if (NetReady)
                {
                    NetReady = false;
                    InvokeHelper.CancelInvokeSingleRepeat(PushTeamDeltasToClients);
                    TeamRemovedEvent.Unsubscribe(OnNetTeamDestroyed);
                    TeamAlignmentDeltaEvent.Unsubscribe(OnNetTeamAlignChange);
                    BuildBucksUpdatedEvent.Unsubscribe(OnNetTeamBBChange);
                    ToSend = null;
                }
            }
        }
        public static void OnNetTeamBBChange(int team, int bbSet)
        {
            if (ToSend.ContainsKey(team))
                return;
            ToSend[team] = 0;
        }
        public static void OnNetTeamAlignChange(int team)
        {
            if (ToSend.TryGetValue(team, out byte val) && val > 0)
                return;
            ToSend[team] = 1;
        }
        public static void OnNetTeamDestroyed(int team)
        {
            ToSend[team] = 2;
        }

        private const int MaxPerPacket = 200;

        // REVISED: drain is now priority-ordered (removals, then BB, then align) and sliced into multiple packets of up to MaxPerPacket entries each, instead of one packet hard-capped at byte.MaxValue (which silently dropped the overflow).
        public static void PushTeamDeltasToClients()
        {
            if (ToSend.Count == 0) return;
            var ordered = new List<KeyValuePair<int, byte>>(ToSend);
            ordered.Sort((a, b) =>
            {
                int ra = a.Value == 2 ? 0 : (a.Value == 0 ? 1 : 2);
                int rb = b.Value == 2 ? 0 : (b.Value == 0 ? 1 : 2);
                return ra.CompareTo(rb);
            });
            ToSend.Clear();
            for (int i = 0; i < ordered.Count; i += MaxPerPacket)
            {
                int take = Math.Min(MaxPerPacket, ordered.Count - i);
                netHook.TryBroadcast(new NetworkedAITeamUpdate(ordered, i, take));
            }
        }

        public class NetworkedAITeamUpdate : MessageBase
        {
            public NetworkedAITeamUpdate() { }
            // REVISED: now carries an explicit payload slice (list + start + len) so Serialize writes just this packet's window; previously it serialized straight from the static ToSend dictionary and cleared it in a finally.
            private readonly List<KeyValuePair<int, byte>> _payload;
            private readonly int _start, _len;
            public NetworkedAITeamUpdate(List<KeyValuePair<int, byte>> payload, int start, int len)
            {
                _payload = payload; _start = start; _len = len;
            }

            public override void Serialize(NetworkWriter write)
            {
                int count = _len;
                write.Write((byte)count);
                for (int i = 0; i < count; i++)
                {
                    var item = _payload[_start + i];
                    if (inst.teams.TryGetValue(item.Key, out EnemyTeamData ETD))
                    {
                        switch (item.Value)
                        {
                            case 0: PackTeamBBInfo(ref write, ETD); break;
                            case 1: PackTeamInfo(ref write, ETD); break;
                            case 2: PackTeamRemovedInfo(ref write, item.Key); break;
                            default: break;
                        }
                    }
                    else
                    {
                        PackTeamRemovedInfo(ref write, item.Key);
                    }
                }
            }
            public override void Deserialize(NetworkReader read)
            {
                int count = read.ReadByte();
                int step = 0;
                try
                {
                    for (; step < count; step++)
                    {
                        UnpackTeamInfo(ref read);
                    }
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log("TAC_AI: FAILED to process NetworkedAITeamUpdate.Deserialize() on step [" + step + "], teams might be corrupted!!! - " + e);
                }
            }

            private void PackTeamInfo(ref NetworkWriter write, EnemyTeamData ETD)
            {
                write.WritePackedInt32(ETD.teamID);
                write.WritePackedInt32(ETD.BuildBucks);
                PackTeamAlignmentInfo(ref write, ETD.align);
            }
            private void PackTeamBBInfo(ref NetworkWriter write, EnemyTeamData ETD)
            {
                write.WritePackedInt32(0);
                write.WritePackedInt32(ETD.BuildBucks);
                write.WritePackedInt32(ETD.teamID);
            }
            private void PackTeamRemovedInfo(ref NetworkWriter write, int team)
            {
                write.WritePackedInt32(int.MinValue);
                write.WritePackedInt32(team);
            }
            private void UnpackTeamInfo(ref NetworkReader read)
            {
                int team = read.ReadPackedInt32();
                int BB = read.ReadPackedInt32();
                if (BB == int.MinValue)
                {   // REMOVE
                    OnTeamDestroyedRemoteClient(team);
                }
                else
                {   // Add/Update
                    var teamInst = InsureBaseTeam(team);
                    teamInst.SetBuildBucks = BB;
                    UnpackTeamAlignmentInfo(ref read, ref teamInst.align);
                }
                TankAIManager.UpdateEntireTeam(team);
            }
            private void PackTeamAlignmentInfo(ref NetworkWriter write, Dictionary<int,TeamRelations> input)
            {
                write.WritePackedInt32(input.Count);
                foreach (var item in input)
                {
                    write.WritePackedInt32(item.Key);
                    write.WritePackedInt32((int)item.Value);
                }
            }
            private void UnpackTeamAlignmentInfo(ref NetworkReader read, ref Dictionary<int, TeamRelations> input)
            {
                try
                {
                    int unpack = read.ReadPackedInt32();
                    for (int step = 0; step < unpack; step++)
                    {
                        int teamID = read.ReadPackedInt32();
                        TeamRelations val = (TeamRelations)read.ReadPackedInt32();
                        input[teamID] = val;
                    }
                }
                catch (Exception e)
                {
                    DebugTAC_AI.Log("TAC_AI: FAILED to process UnpackTeamAlignmentInfo(), teams might be corrupted!!! - " + e);
                }
            }
            // REVISED: removed the unused serialized fields PackingInfo / TeamID / BribeAmount (never read or written by the packers).
        }
        private static NetworkHook<NetworkedAITeamUpdate> netHook = new NetworkHook<NetworkedAITeamUpdate>(
            "TAC_AI.NetworkedAITeamUpdate", OnReceiveTeamUpdate, NetMessageType.ToClientsOnly);

        internal static void InsureNetHooks()
        {
            netHook.Enable();
        }
        /// <summary>
        /// Just permits it to carry on
        /// </summary>
        /// <param name="update"></param>
        /// <param name="isServer"></param>
        /// <returns></returns>
        internal static bool OnReceiveTeamUpdate(NetworkedAITeamUpdate update, bool isServer)
        {
            return true;
        }


        // ------------------------------------
        //               LEGACY
        // ------------------------------------

        private static bool IsLegacyBaseTeam(int team)
        {
            return (team >= BaseTeamsStart && team <= BaseTeamsEnd) || team == SpecialAISpawner.trollTeam;
        }
        private static NP_Types GetLegacyNPTTeamType(int team)
        {
            if (team == ManPlayer.inst.PlayerTeam)
                return NP_Types.Player;
            else if (IsLegacyBaseTeam(team))
            {
                if (IsLegacyEnemyBaseTeam(team))
                    return NP_Types.Enemy;
                else if (IsLegacyNeutralBaseTeam(team))
                    return NP_Types.Neutral;
                else if (IsLegacyNonAggressiveTeam(team))
                    return NP_Types.NonAggressive;
                else if (IsLegacySubNeutralBaseTeam(team))
                    return NP_Types.SubNeutral;
                else
                    return NP_Types.Friendly;
            }
            else
                return NP_Types.NonNPT;
        }

        private const int EnemyBaseTeamsStart = 256;
        private const int EnemyBaseTeamsEnd = 356;

        private const int SubNeutralBaseTeamsStart = 357;
        private const int SubNeutralBaseTeamsEnd = 406;

        private const int NeutralBaseTeamsStart = 407;
        private const int NeutralBaseTeamsEnd = 456;

        private const int FriendlyBaseTeamsStart = 457;
        private const int FriendlyBaseTeamsEnd = 506;

        private const int BaseTeamsStart = 256;
        private const int BaseTeamsEnd = 506;

        private static bool IsLegacyEnemyBaseTeam(int team)
        {
            return (team >= EnemyBaseTeamsStart && team <= EnemyBaseTeamsEnd) || team == SpecialAISpawner.trollTeam;
        }
        private static bool IsLegacyNonAggressiveTeam(int team)
        {
            return team >= SubNeutralBaseTeamsStart && team <= NeutralBaseTeamsEnd;
        }
        private static bool IsLegacySubNeutralBaseTeam(int team)
        {
            return team >= SubNeutralBaseTeamsStart && team <= SubNeutralBaseTeamsEnd;
        }
        private static bool IsLegacyNeutralBaseTeam(int team)
        {
            return team >= NeutralBaseTeamsStart && team <= NeutralBaseTeamsEnd;
        }
        private static bool IsLegacyFriendlyBaseTeam(int team)
        {
            return team >= FriendlyBaseTeamsStart && team <= FriendlyBaseTeamsEnd;
        }

    }
}
