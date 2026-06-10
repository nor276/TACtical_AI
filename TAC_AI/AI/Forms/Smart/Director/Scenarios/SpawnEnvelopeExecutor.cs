using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.World;
using TAC_AI.Templates;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Scenarios
{
    /// <summary>
    /// Main-thread executor for the Director's scenario / chunk-regen envelope types.
    /// Subscribes once via
    /// WorldEventBus.Subscribe (so PublishFromWorker → DrainMainThreadQueue → Publish<T>
    /// flows into our handlers). Each handler enqueues into a private buffer; the
    /// MaybeDrain() entrypoint debounces at 200 ms via CompareExchange and walks the
    /// buffers under per-envelope try/catch.
    ///
    /// Stale envelopes (ScenarioGeneration mismatch) are rejected without firing the
    /// engine API.
    /// </summary>
    public static class SpawnEnvelopeExecutor
    {
        public const int DebounceMs = 200;
        public const int PerTickEnvelopeBudget = 24;
        public const int PerTickGuardEvalBudget = 16;
        public const int GuardResultSlotCap = 256;

        private static int _installed;
        private static int _lastSpawnExecMs = int.MinValue;
        private static int _teamSubInstalled;

        private static readonly ConcurrentQueue<ScenarioBootRequest> _bootQ =
            new ConcurrentQueue<ScenarioBootRequest>();
        private static readonly ConcurrentQueue<ScenarioSpawnRequest> _spawnQ =
            new ConcurrentQueue<ScenarioSpawnRequest>();
        private static readonly ConcurrentQueue<ChunkSpawnRequest> _chunkQ =
            new ConcurrentQueue<ChunkSpawnRequest>();
        private static readonly ConcurrentQueue<ScenarioRelationsRequest> _relQ =
            new ConcurrentQueue<ScenarioRelationsRequest>();
        private static readonly ConcurrentQueue<ScenarioAngerResetRequest> _angerQ =
            new ConcurrentQueue<ScenarioAngerResetRequest>();
        private static readonly ConcurrentQueue<ScenarioTeamSubscribeRequest> _teamSubQ =
            new ConcurrentQueue<ScenarioTeamSubscribeRequest>();
        private static readonly ConcurrentQueue<GuardEvaluationRequest> _guardEvalQ =
            new ConcurrentQueue<GuardEvaluationRequest>();
        // Result slot table keyed by (techIdValue << 32) | (monoTickLowBits). GuardWorker
        // consumes by (techId, monoTick); stale entries (> 1 s) get rejected at the read
        // site. Capacity-bounded via opportunistic LRU below.
        private static readonly ConcurrentDictionary<long, GuardEvaluationResult> _guardResultSlots =
            new ConcurrentDictionary<long, GuardEvaluationResult>();

        private static Action<ScenarioBootRequest> _bootHandler;
        private static Action<ScenarioSpawnRequest> _spawnHandler;
        private static Action<ChunkSpawnRequest> _chunkHandler;
        private static Action<ScenarioRelationsRequest> _relHandler;
        private static Action<ScenarioAngerResetRequest> _angerHandler;
        private static Action<ScenarioTeamSubscribeRequest> _teamSubHandler;
        private static Action<GuardEvaluationRequest> _guardEvalHandler;

        public static void MaybeDrain()
        {
            if (Interlocked.CompareExchange(ref _installed, 1, 0) == 0)
            {
                Install();
            }

            int now = Environment.TickCount;
            int last = Volatile.Read(ref _lastSpawnExecMs);
            if (last != int.MinValue && unchecked(now - last) >= 0 && unchecked(now - last) < DebounceMs) return;
            if (Interlocked.CompareExchange(ref _lastSpawnExecMs, now, last) != last) return;

            Drain();
        }

        // Reset both CAS gates so a re-Init after SmartRuntime.Shutdown re-installs the
        // subscriptions. WorldEventBus.ClearAll wipes the handler list, so we just need
        // the gates to fall back to 0 to trigger Install() on the next MaybeDrain.
        public static void Reset()
        {
            Interlocked.Exchange(ref _installed, 0);
            Interlocked.Exchange(ref _teamSubInstalled, 0);
            Interlocked.Exchange(ref _lastSpawnExecMs, int.MinValue);
            ScenarioBootRequest br; while (_bootQ.TryDequeue(out br)) { }
            ScenarioSpawnRequest sr; while (_spawnQ.TryDequeue(out sr)) { }
            ChunkSpawnRequest cr; while (_chunkQ.TryDequeue(out cr)) { }
            ScenarioRelationsRequest rr; while (_relQ.TryDequeue(out rr)) { }
            ScenarioAngerResetRequest ar; while (_angerQ.TryDequeue(out ar)) { }
            ScenarioTeamSubscribeRequest tsr; while (_teamSubQ.TryDequeue(out tsr)) { }
            GuardEvaluationRequest ger; while (_guardEvalQ.TryDequeue(out ger)) { }
            _guardResultSlots.Clear();
        }

        private static void Install()
        {
            _bootHandler = OnBootRequest;
            _spawnHandler = OnSpawnRequest;
            _chunkHandler = OnChunkRequest;
            _relHandler = OnRelationsRequest;
            _angerHandler = OnAngerRequest;
            _teamSubHandler = OnTeamSubscribeRequest;
            _guardEvalHandler = OnGuardEvalRequest;
            WorldEventBus.Subscribe(_bootHandler);
            WorldEventBus.Subscribe(_spawnHandler);
            WorldEventBus.Subscribe(_chunkHandler);
            WorldEventBus.Subscribe(_relHandler);
            WorldEventBus.Subscribe(_angerHandler);
            WorldEventBus.Subscribe(_teamSubHandler);
            WorldEventBus.Subscribe(_guardEvalHandler);
        }

        private static void OnBootRequest(ScenarioBootRequest req) { _bootQ.Enqueue(req); }
        private static void OnSpawnRequest(ScenarioSpawnRequest req) { _spawnQ.Enqueue(req); }
        private static void OnChunkRequest(ChunkSpawnRequest req) { _chunkQ.Enqueue(req); }
        private static void OnRelationsRequest(ScenarioRelationsRequest req) { _relQ.Enqueue(req); }
        private static void OnAngerRequest(ScenarioAngerResetRequest req) { _angerQ.Enqueue(req); }
        private static void OnTeamSubscribeRequest(ScenarioTeamSubscribeRequest req) { _teamSubQ.Enqueue(req); }
        private static void OnGuardEvalRequest(GuardEvaluationRequest req) { _guardEvalQ.Enqueue(req); }

        // GuardWorker consumes a fulfilled result. Returns false when the slot is
        // missing or older than 1 s; caller treats that as "skip this tick".
        public static bool TryTakeGuardResult(TechId techId, long monoTick, out GuardEvaluationResult result)
        {
            long key = PackGuardKey(techId, monoTick);
            GuardEvaluationResult slot;
            if (!_guardResultSlots.TryGetValue(key, out slot))
            {
                result = default(GuardEvaluationResult);
                return false;
            }
            float age = MonoClock.Seconds(slot.FilledMono, MonoClock.Now());
            if (age > 1f)
            {
                _guardResultSlots.TryRemove(key, out slot);
                result = default(GuardEvaluationResult);
                return false;
            }
            // Take semantics: result slot is consumed on read.
            _guardResultSlots.TryRemove(key, out slot);
            result = slot;
            return true;
        }

        private static long PackGuardKey(TechId techId, long monoTick)
        {
            unchecked
            {
                return ((long)techId.Value << 32) ^ (monoTick & 0x7FFFFFFFL);
            }
        }

        private static void Drain()
        {
            int budget = PerTickEnvelopeBudget;
            int curGen = DirectorState.ScenarioGeneration;

            // Team-removed subscribe one-shots first — idempotent inside Install logic.
            ScenarioTeamSubscribeRequest tsr;
            while (_teamSubQ.TryDequeue(out tsr))
            {
                if (Interlocked.CompareExchange(ref _teamSubInstalled, 1, 0) == 0)
                {
                    try { ScenarioWorker.ExecutorInstallTeamRemovedSubscription(); }
                    catch (Exception ex)
                    {
                        DirectorJournal.Append("ENVELOPE-FAILED",
                            "kind=team-sub ex=" + ex.GetType().Name + ":" + ex.Message);
                    }
                }
            }

            // Boot requests next — they mint teams + emit the initial spawn batch via
            // queue-back-into-_spawnQ so subsequent loop iterations pick them up.
            ScenarioBootRequest br;
            while (budget > 0 && _bootQ.TryDequeue(out br))
            {
                budget--;
                if (br.ScenarioGeneration != curGen)
                {
                    DirectorJournal.Append("ENVELOPE-STALE",
                        "kind=boot sid=" + br.ScenarioId + " gen=" + br.ScenarioGeneration
                        + " cur=" + curGen);
                    continue;
                }
                try { ApplyBoot(br); }
                catch (Exception ex)
                {
                    DirectorJournal.Append("ENVELOPE-FAILED",
                        "kind=boot sid=" + br.ScenarioId + " ex=" + ex.GetType().Name + ":" + ex.Message);
                }
            }

            ScenarioAngerResetRequest ar;
            while (budget > 0 && _angerQ.TryDequeue(out ar))
            {
                budget--;
                if (ar.ScenarioGeneration != curGen)
                {
                    DirectorJournal.Append("ENVELOPE-STALE",
                        "kind=anger team=" + ar.TeamId + " gen=" + ar.ScenarioGeneration
                        + " cur=" + curGen);
                    continue;
                }
                try { ApplyAngerReset(ar); }
                catch (Exception ex)
                {
                    DirectorJournal.Append("ENVELOPE-FAILED",
                        "kind=anger team=" + ar.TeamId + " ex=" + ex.GetType().Name + ":" + ex.Message);
                }
            }

            ScenarioRelationsRequest rr;
            while (budget > 0 && _relQ.TryDequeue(out rr))
            {
                budget--;
                if (rr.ScenarioGeneration != curGen)
                {
                    DirectorJournal.Append("ENVELOPE-STALE",
                        "kind=relations a=" + rr.TeamA + " b=" + rr.TeamB
                        + " gen=" + rr.ScenarioGeneration + " cur=" + curGen);
                    continue;
                }
                try { ApplyRelations(rr); }
                catch (Exception ex)
                {
                    DirectorJournal.Append("ENVELOPE-FAILED",
                        "kind=relations a=" + rr.TeamA + " b=" + rr.TeamB
                        + " ex=" + ex.GetType().Name + ":" + ex.Message);
                }
            }

            ScenarioSpawnRequest sr;
            while (budget > 0 && _spawnQ.TryDequeue(out sr))
            {
                budget--;
                if (sr.ScenarioGeneration != curGen)
                {
                    DirectorJournal.Append("ENVELOPE-STALE",
                        "kind=spawn team=" + sr.Team + " gen=" + sr.ScenarioGeneration
                        + " cur=" + curGen);
                    continue;
                }
                try { ApplySpawn(sr); }
                catch (Exception ex)
                {
                    DirectorJournal.Append("SPAWN-FAILED",
                        "team=" + sr.Team + " ex=" + ex.GetType().Name + ":" + ex.Message);
                }
            }

            ChunkSpawnRequest cr;
            while (budget > 0 && _chunkQ.TryDequeue(out cr))
            {
                budget--;
                if (cr.ScenarioGeneration != curGen)
                {
                    DirectorJournal.Append("ENVELOPE-STALE",
                        "kind=chunk gen=" + cr.ScenarioGeneration + " cur=" + curGen);
                    continue;
                }
                try { ApplyChunkSpawn(cr); }
                catch (Exception ex)
                {
                    DirectorJournal.Append("ENVELOPE-FAILED",
                        "kind=chunk ex=" + ex.GetType().Name + ":" + ex.Message);
                }
            }

            // Guard-eval requests on a separate per-tick budget. Stale slots get evicted
            // by the read side; we cap the slot dict size opportunistically here.
            int guardBudget = PerTickGuardEvalBudget;
            GuardEvaluationRequest ger;
            while (guardBudget > 0 && _guardEvalQ.TryDequeue(out ger))
            {
                guardBudget--;
                try { ApplyGuardEval(ger); }
                catch (Exception ex)
                {
                    DirectorJournal.Append("ENVELOPE-FAILED",
                        "kind=guard-eval tech=" + ger.TechId.Value
                        + " ex=" + ex.GetType().Name + ":" + ex.Message);
                }
            }
            if (_guardResultSlots.Count > GuardResultSlotCap) EvictStaleGuardResults();
        }

        private static void EvictStaleGuardResults()
        {
            long now = MonoClock.Now();
            foreach (var kv in _guardResultSlots)
            {
                if (MonoClock.Seconds(kv.Value.FilledMono, now) > 1f)
                {
                    GuardEvaluationResult drop;
                    _guardResultSlots.TryRemove(kv.Key, out drop);
                }
            }
        }

        private static void ApplyBoot(ScenarioBootRequest br)
        {
            var recipe = ScenarioRegistry.Lookup(br.ScenarioId);
            if (recipe == null) return;

            // A near-zero centroid hint means "spawn around the player" — the operator
            // panel and the autonomous Director both pass zero (the cross-thread Vector3
            // write is deliberately avoided). Resolve here on the main thread so the
            // scenario rings form around whatever tech the operator is driving.
            Vector3 centroid = ResolveOperatorCentroid(br.Centroid);

            // 1. Mint hostile teams (main-thread; ManBaseTeams.GetNewBaseTeam mutates
            //    non-concurrent dict, safe here).
            var hostileTeams = new List<int>(recipe.HostileTeamCount);
            for (int i = 0; i < recipe.HostileTeamCount; i++)
            {
                int tid = AIGlobals.GetRandomEnemyBaseTeam(true);
                if (tid == int.MinValue) continue;
                hostileTeams.Add(tid);
                DirectorState.ScenarioOwnedTeams.TryAdd(tid, (byte)br.ScenarioId);
            }
            // 2. Mint SubNeutral teams.
            var subNeutralTeams = new List<int>(recipe.SubNeutralTeamCount);
            for (int i = 0; i < recipe.SubNeutralTeamCount; i++)
            {
                int tid = AIGlobals.GetRandomSubNeutralBaseTeam(true);
                if (tid == int.MinValue) continue;
                subNeutralTeams.Add(tid);
                DirectorState.ScenarioOwnedTeams.TryAdd(tid, (byte)br.ScenarioId);
            }
            // 3. Mint player AITeammate team if recipe needs one and player exists.
            int allyTeam = int.MinValue;
            if (recipe.UsesPlayerAITeammate && ManPlayer.inst != null)
            {
                int playerTeam = ManPlayer.inst.PlayerTeam;
                if (AIGlobals.IsPlayerTeam(playerTeam))
                {
                    try
                    {
                        var etd = ManBaseTeams.GetTeamAIBaseTeam(playerTeam);
                        if (etd != null)
                        {
                            allyTeam = etd.teamID;
                            DirectorState.ScenarioOwnedTeams.TryAdd(allyTeam, (byte)br.ScenarioId);
                        }
                    }
                    catch (Exception ex)
                    {
                        DirectorJournal.Append("ENVELOPE-FAILED",
                            "kind=boot reason=GetTeamAIBaseTeam-threw ex=" + ex.GetType().Name + ":" + ex.Message);
                    }
                }
            }

            // 4. Initial spawns. Hostile mobiles ring the centroid.
            int gen = br.ScenarioGeneration;
            for (int t = 0; t < hostileTeams.Count; t++)
            {
                int team = hostileTeams[t];
                Vector3 ringPos = RingPos(centroid, recipe.SpawnRadiusM, t, hostileTeams.Count);
                Vector3 fwd = SafeForward(centroid - ringPos);
                int per = ScaleByIntensity(recipe.MobilePerHostileTeam, br.Intensity);
                for (int k = 0; k < per; k++)
                {
                    EmitInitialSpawn(SpreadOnRing(centroid, ringPos, k, per, 30f), fwd, team,
                        recipe.Terrain, BasePurpose.NotStationary, recipe, gen, CombatFolder(recipe));
                }
            }
            // SubNeutral mobiles cluster near centroid.
            for (int t = 0; t < subNeutralTeams.Count; t++)
            {
                int team = subNeutralTeams[t];
                Vector3 basePos = RingPos(centroid, recipe.SpawnRadiusM * 0.7f, t,
                    subNeutralTeams.Count);
                int per = ScaleByIntensity(recipe.MobilePerHostileTeam, br.Intensity);
                for (int k = 0; k < per; k++)
                {
                    EmitInitialSpawn(basePos + JitterOffset(k, per, 18f),
                        Vector3.forward, team, recipe.Terrain, BasePurpose.NotStationary, recipe, gen,
                        DirectorTechFolders.Harvester);
                }
                // Harvesting base — direct main-thread call, no envelope round-trip.
                try
                {
                    RawTechLoader.FORCESpawnBaseAtPositionNoFounder(
                        FactionSubTypes.NULL, basePos, team, BasePurpose.Harvesting,
                        recipe.BaseGrade);
                }
                catch (Exception ex)
                {
                    DirectorJournal.Append("SPAWN-FAILED",
                        "kind=base team=" + team + " ex=" + ex.GetType().Name + ":" + ex.Message);
                }
            }
            // Ally team mobiles + base for S3/S4/S5/S6prime.
            if (allyTeam != int.MinValue && recipe.UsesPlayerAITeammate)
            {
                // Base at centroid for S3/S5; S4 lands base at centroid too.
                if (recipe.BasePerScenario > 0)
                {
                    BasePurpose bp = recipe.RequiresChunkRegen ? BasePurpose.Harvesting
                        : BasePurpose.Defense;
                    try
                    {
                        RawTechLoader.FORCESpawnBaseAtPositionNoFounder(
                            FactionSubTypes.NULL, centroid, allyTeam, bp, recipe.BaseGrade);
                    }
                    catch (Exception ex)
                    {
                        DirectorJournal.Append("SPAWN-FAILED",
                            "kind=ally-base team=" + allyTeam + " ex=" + ex.GetType().Name + ":" + ex.Message);
                    }
                }
                // Escorts ring the centroid.
                int per = ScaleByIntensity(recipe.EscortPerBase, br.Intensity);
                for (int k = 0; k < per; k++)
                {
                    float angle = (Mathf.PI * 2f * k) / Math.Max(1, per);
                    Vector3 epos = new Vector3(
                        centroid.x + Mathf.Cos(angle) * 50f,
                        centroid.y,
                        centroid.z + Mathf.Sin(angle) * 50f);
                    Vector3 fwd = SafeForward(centroid - epos);
                    EmitInitialSpawn(epos, fwd, allyTeam, recipe.Terrain, BasePurpose.NotStationary,
                        recipe, gen, CombatFolder(recipe));
                }
            }
            DirectorJournal.Append("SCENARIO-BOOT",
                "id=" + br.ScenarioId + " name=" + recipe.Name
                + " hostile=" + hostileTeams.Count
                + " subNeutral=" + subNeutralTeams.Count
                + " ally=" + (allyTeam != int.MinValue ? allyTeam.ToString() : "-")
                + " gen=" + gen);
        }

        // Player tech position for the "spawn around me" centroid, resolved main-thread.
        // World origin is the last resort when there's no player tech yet.
        private static Vector3 ResolveOperatorCentroid(Vector3 hint)
        {
            if (hint.sqrMagnitude >= 1f) return hint;
            try
            {
                if (Singleton.playerTank != null)
                    return Singleton.playerTank.boundsCentreWorldNoCheck;
            }
            catch { /* defensive — engine state mid-teardown */ }
            return Vector3.zero;
        }

        private static void EmitInitialSpawn(Vector3 pos, Vector3 forward, int team,
            BaseTerrain terrain, BasePurpose purpose, ScenarioRecipe recipe, int gen, string folder)
        {
            // Boot-time spawns route through the same code path as wave spawns so they
            // share the same MatchCount degrade gate + journaling. We re-enqueue rather
            // than synchronously invoke RawTechLoader so the per-envelope budget bounds
            // boot-storm cost.
            var sr = new ScenarioSpawnRequest(
                pos: pos, fwd: forward, team: team,
                scenarioId: recipe != null ? (int)recipe.Id : 0,
                generation: gen,
                terrain: terrain, purpose: purpose,
                progression: recipe != null ? recipe.Progression : FactionLevel.VEN,
                faction: FactionSubTypes.NULL,
                maxPrice: recipe != null ? recipe.MaxPrice : 10000,
                grade: recipe != null ? recipe.Grade : 3,
                offset: recipe != null ? recipe.MobileOffset : RawTechOffset.RaycastTerrainAndScenery,
                excludeErad: true,
                directorFolder: folder, isBase: false);
            _spawnQ.Enqueue(sr);
        }

        // Curated combat folder for a scenario's mobiles: air scenarios pull from the Air
        // pool, everything else from Brawl. Gatherers always pull from Harvester.
        private static string CombatFolder(ScenarioRecipe r)
        {
            return r != null && r.Terrain == BaseTerrain.Air
                ? DirectorTechFolders.Air : DirectorTechFolders.Brawl;
        }

        private static void ApplyAngerReset(ScenarioAngerResetRequest ar)
        {
            EnemyTeamData etd;
            if (!ManBaseTeams.TryGetBaseTeamAny(ar.TeamId, out etd) || etd == null) return;
            if (etd.angerThreshold > 0f)
            {
                etd.angerThreshold = 0f;
            }
        }

        private static void ApplyRelations(ScenarioRelationsRequest rr)
        {
            TeamRelations target = (TeamRelations)rr.RelationCode;
            EnemyTeamData etdA;
            if (ManBaseTeams.TryGetBaseTeamAny(rr.TeamA, out etdA) && etdA != null
                && etdA.align != null)
            {
                TeamRelations cur;
                if (etdA.align.TryGetValue(rr.TeamB, out cur) && cur == target)
                {
                    return; // already at desired relation
                }
            }
            ManBaseTeams.SetRelations(rr.TeamA, rr.TeamB, target);
        }

        private static void ApplySpawn(ScenarioSpawnRequest sr)
        {
            var filter = RawTechPopParams.Default;
            filter.Faction = sr.Faction;
            filter.Progression = sr.Progression;
            filter.Terrain = sr.Terrain;
            filter.Purpose = sr.Purpose;
            filter.MaxPrice = sr.MaxPrice;
            filter.TargetFactionGrade = sr.Grade;
            filter.Offset = sr.Offset;
            filter.ExcludeErad = sr.ExcludeErad;
            filter.IsPopulation = true;

            // Curated path: pull only from the role folder. Empty/under-capped folder =>
            // nothing spawns; tell the operator once (deduped) which folder to populate.
            if (!string.IsNullOrEmpty(sr.DirectorFolder))
            {
                Tank ft = RawTechLoader.SpawnScenarioFromFolder(
                    sr.Position, sr.Forwards, sr.Team, filter, sr.ScenarioId, sr.DirectorFolder, sr.IsBase);
                if (ft == null)
                {
                    string prefix = sr.DirectorFolder.Replace("Director_", "");
                    DebugTAC_AI.LogWarnFileOnly("director-folder-empty-" + sr.DirectorFolder,
                        "[FOLDER-EMPTY] " + sr.DirectorFolder + " has no usable tech under the price/grade cap"
                        + " — name a snapshot '" + prefix + " - YourTech' and click To Enemy to curate it.");
                }
                return;
            }

            // Legacy path (no folder): global-library selection.
            Tank theTech = RawTechLoader.SpawnScenarioMobileTech(
                sr.Position, sr.Forwards, sr.Team, filter, sr.ScenarioId);
            if (theTech == null)
            {
                DirectorJournal.Append("SPAWN-FAILED",
                    "scenario=" + sr.ScenarioId + " team=" + sr.Team + " reason=null-tech");
            }
        }

        private static void ApplyChunkSpawn(ChunkSpawnRequest cr)
        {
            int spawned = 0;
            try
            {
                var manLooseBlocks = Singleton.Manager<ManLooseBlocks>.inst;
                if (manLooseBlocks == null) return;

                Vector3 centroid = cr.Centroid;
                Collider[] hits;
                int existing = GathererCensus.ChunksInRange(centroid, cr.OuterRadius, out hits);
                if (GathererCensus.PickupLayerMask == 0) return;

                var recipe = ScenarioRegistry.Lookup(cr.ScenarioId);
                if (recipe == null) return;
                int target = Mathf.RoundToInt(recipe.InitialChunkTarget * DirectorState.Intensity);
                if (target <= 0) return;
                int deficit = target - existing;
                if (deficit <= 0) return;

                int planned = Mathf.Min(cr.CountBudget, Mathf.Max(1, (deficit + 3) / 4));

                var chunkTypes = Enemy.RLoadedBases.TryGetBiomeResource(centroid);
                if (chunkTypes == null || chunkTypes.Length == 0) return;

                for (int i = 0; i < planned; i++)
                {
                    float angle = UnityEngine.Random.value * Mathf.PI * 2f;
                    float r = cr.InnerRadius +
                        UnityEngine.Random.value * (cr.OuterRadius - cr.InnerRadius);
                    Vector3 pos = new Vector3(
                        centroid.x + Mathf.Cos(angle) * r,
                        centroid.y + 10f,
                        centroid.z + Mathf.Sin(angle) * r);
                    RaycastHit hit;
                    if (Physics.Raycast(pos, Vector3.down, out hit, 60f))
                    {
                        pos.y = hit.point.y + 1.5f;
                    }
                    ChunkTypes ct = chunkTypes[UnityEngine.Random.Range(0, chunkTypes.Length)];
                    var rp = manLooseBlocks.HostSpawnChunk(ct, pos, Quaternion.identity, true,
                        Vector3.zero, Vector3.zero);
                    if (rp != null) spawned++;
                }
            }
            catch (Exception ex)
            {
                DirectorJournal.Append("ENVELOPE-FAILED",
                    "kind=chunk inner=ApplyChunkSpawn ex=" + ex.GetType().Name + ":" + ex.Message);
            }
            if (spawned > 0)
            {
                DirectorJournal.Append("CHUNK-SPAWN",
                    "scenario=" + cr.ScenarioId + " spawned=" + spawned + " budget=" + cr.CountBudget);
            }
        }

        // Main-thread fill for one guard evaluation request. Reads the rare Unity-only
        // fields (terrain height, AILimitSettings flags, FrustrationMeter snapshot) and
        // posts the result into _guardResultSlots. Stale results are read-side rejected.
        private static void ApplyGuardEval(GuardEvaluationRequest req)
        {
            var result = new GuardEvaluationResult();
            result.TechId = req.TechId;
            result.MonoTick = req.MonoTick;
            result.FilledMono = MonoClock.Now();
            result.RequestFields = req.RequestFields;

            // Resolve the tech via SmartRuntime to get position + helper.
            TankAIHelper helper = null;
            Tank tank = null;
            try
            {
                helper = SmartRuntime.LookupHelperByTechId(req.TechId);
                if (helper != null) tank = helper.tank;
            }
            catch { /* lookup defensive; helper may be null */ }

            if (helper == null || tank == null)
            {
                result.Valid = false;
                _guardResultSlots[PackGuardKey(req.TechId, req.MonoTick)] = result;
                return;
            }

            result.Valid = true;
            result.HelperPosition = tank.boundsCentreWorldNoCheck;
            result.FrustrationMeterSnapshot = helper.PublishedFrustrationMeter;

            // Terrain height at helper position. onTile=false → caller treats as noisy.
            try
            {
                bool onTile;
                float h = ManWorld.inst.TileManager.GetTerrainHeightAtPosition(
                    result.HelperPosition, out onTile);
                result.TerrainHeight = h;
                result.TerrainHeightOnTile = onTile;
            }
            catch
            {
                result.TerrainHeightOnTile = false;
            }

            _guardResultSlots[PackGuardKey(req.TechId, req.MonoTick)] = result;
        }

        private static int ScaleByIntensity(int n, float intensity)
        {
            float scaled = n * intensity;
            int rounded = Mathf.RoundToInt(scaled);
            if (rounded < 1) rounded = 1;
            return rounded;
        }

        // ~22.5 deg base rotation so 4 teams don't land on the cardinal axes.
        private const float RingBaseAngle = 0.3927f;

        private static Vector3 RingPos(Vector3 centroid, float radius, int index, int total)
        {
            if (total <= 0) total = 1;
            float angle = RingBaseAngle + (Mathf.PI * 2f * index) / total;
            float r = radius * (0.9f + UnityEngine.Random.value * 0.2f);
            return new Vector3(centroid.x + Mathf.Cos(angle) * r,
                               centroid.y,
                               centroid.z + Mathf.Sin(angle) * r);
        }

        // Line a team's mobiles up side-by-side along the ring tangent so they don't
        // spawn stacked on the same point. k = tech index, per = team size.
        private static Vector3 SpreadOnRing(Vector3 centroid, Vector3 ringPos, int k, int per, float spacing)
        {
            Vector3 outward = ringPos - centroid;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f) outward = Vector3.forward;
            outward = outward.normalized;
            Vector3 tangent = Vector3.Cross(Vector3.up, outward);
            float lateral = (k - (per - 1) * 0.5f) * spacing;
            float radial = (UnityEngine.Random.value - 0.5f) * spacing * 0.5f;
            return ringPos + tangent * lateral + outward * radial;
        }

        private static Vector3 JitterOffset(int k, int total, float spread)
        {
            float dx = (UnityEngine.Random.value - 0.5f) * spread;
            float dz = (UnityEngine.Random.value - 0.5f) * spread;
            return new Vector3(dx, 0f, dz);
        }

        private static Vector3 SafeForward(Vector3 fromCentroid)
        {
            if (fromCentroid.sqrMagnitude < 0.001f) return Vector3.forward;
            return fromCentroid.normalized;
        }
    }

    // Result for a GuardEvaluationRequest. Filled main-thread; consumed bg-thread by
    // BehaviorGuardWorker via SpawnEnvelopeExecutor.TryTakeGuardResult. Valid=false
    // means the executor could not resolve the tech (likely despawned mid-request).
    public struct GuardEvaluationResult
    {
        public TechId TechId;
        public long MonoTick;
        public long FilledMono;
        public ushort RequestFields;
        public bool Valid;
        public Vector3 HelperPosition;
        public float TerrainHeight;
        public bool TerrainHeightOnTile;
        public int FrustrationMeterSnapshot;
    }
}
