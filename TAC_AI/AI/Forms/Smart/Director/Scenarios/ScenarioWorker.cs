using System;
using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;
using TAC_AI.Templates;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Scenarios
{
    /// <summary>
    /// L3 planner daemon. Owns scenario instance state. Composes recipe inputs +
    /// emits envelopes onto WorldEventBus.PublishFromWorker. NEVER calls
    /// RawTechLoader, ManBaseTeams, or AIGlobals.GetRandom*Team directly from
    /// background — main-thread executor in SmartForm.Operations drains and performs
    /// the engine-side writes (including team-id minting).
    ///
    /// Tick cadence: 1 Hz. When ActiveScenario == 0, the loop idle-waits 1s.
    /// </summary>
    public static class ScenarioWorker
    {
        public const string CanonicalName = "ScenarioWorker";
        public const int TickMs = 1000;
        public const int InitWaitPollMs = 1000;

        // Background-safe PRNG. UnityEngine.Random is a main-thread-only static facade;
        // calling it from this daemon races the engine state. Mirrors SamplingMPC.cs.
        private static readonly System.Random _rng = new System.Random(Environment.TickCount);

        // Per-scenario instance state. Lives until scenario_set rotates id.
        private static volatile int _activeScenarioId;
        private static volatile float _intensity = 1f;
        // Phase 4 invariant: ScenarioSetAction always passes Vector3.zero. Vector3 writes
        // aren't atomic — future phases that send a real centroid hint must marshal the
        // write to this thread or split into three Interlocked.Exchange float fields.
        private static Vector3 _centroid;
        private static long _scenarioStartMono;
        private static long _lastMaintenanceMono;
        private static long _lastWaveMono;
        private static int _waveCount;
        // Bootstrap latch: false until executor has minted the scenario's teams + done
        // first spawn. ScenarioBootRequest carries the boot intent; the executor sets
        // _bootCompleted via Acknowledge() when ScenarioOwnedTeams is populated.
        private static long _lastBootEmitMono;
        private static volatile bool _bootRequested;

        private static volatile bool _enqueuingPermitted;
        private static volatile bool _daemonInitComplete;

        private static Action<int> _teamRemovedHandler;
        private static int _teamRemovedSubscribed;

        public static bool HasActiveTemplate
        {
            get { return _activeScenarioId != 0; }
        }

        public static int ActiveScenarioId
        {
            get { return _activeScenarioId; }
        }

        public static long ScenarioStartMono
        {
            get { return _scenarioStartMono; }
        }

        public static void Init(WorkerPool pool)
        {
            if (pool == null) return;
            if (_daemonInitComplete) return;

            _enqueuingPermitted = true;
            pool.EnqueueLongRunning(RunLoop, CanonicalName);
            var poolRef = pool;
            Func<string> factory = () => poolRef != null ? poolRef.EnqueueLongRunning(RunLoop, CanonicalName) : null;
            DaemonWatchdog.RegisterCanonical(CanonicalName, factory);
            WorkerHealthMonitor.RegisterCanonical(CanonicalName, factory);

            _daemonInitComplete = true;
        }

        public static void BeginShutdown()
        {
            _enqueuingPermitted = false;
            try
            {
                if (Interlocked.Exchange(ref _teamRemovedSubscribed, 0) == 1 && _teamRemovedHandler != null)
                {
                    ManBaseTeams.TeamRemovedEvent.Unsubscribe(_teamRemovedHandler);
                }
            }
            catch { /* defensive on engine teardown */ }
            _daemonInitComplete = false;
        }

        /// <summary>
        /// Apply a new scenario id + intensity. Bumps ScenarioGeneration so any in-flight
        /// envelopes are rejected at drain time. The actual mint + first spawn is
        /// marshaled to main thread via ScenarioBootRequest emitted on the next tick.
        /// </summary>
        public static bool RequestScenarioSet(int scenarioId, float intensity, Vector3 centroidHint)
        {
            var recipe = ScenarioRegistry.Lookup(scenarioId);
            if (recipe == null) return false;
            if (intensity < 0.25f) intensity = 0.25f;
            if (intensity > 2.0f) intensity = 2.0f;

            Interlocked.Increment(ref DirectorState.ScenarioGeneration);

            _centroid = centroidHint;
            _intensity = intensity;
            _scenarioStartMono = MonoClock.Now();
            _lastMaintenanceMono = 0L;
            _lastWaveMono = 0L;
            _lastBootEmitMono = 0L;
            _waveCount = 0;
            _bootRequested = false;
            _activeScenarioId = scenarioId;
            DirectorState.ActiveScenario = scenarioId;
            DirectorState.Intensity = intensity;

            // Drop prior owned-teams record; new mints populate on boot.
            DirectorState.ScenarioOwnedTeams.Clear();
            DirectorState.ScenarioOwnedTechs.Clear();

            return true;
        }

        /// <summary>
        /// Re-run active scenario without changing id. Bumps ScenarioGeneration and
        /// re-emits ScenarioBootRequest.
        /// </summary>
        public static bool RequestRespawn()
        {
            int sid = _activeScenarioId;
            if (sid == 0) return false;
            // No generation bump: respawn tops up the SAME scenario, so in-flight spawns
            // must NOT be invalidated (that was the spawn/despawn thrash). Only scenario_set
            // bumps the generation. Resetting the start stamp re-arms the scenario warmup.
            _scenarioStartMono = MonoClock.Now();
            _lastMaintenanceMono = 0L;
            _lastWaveMono = 0L;
            _lastBootEmitMono = 0L;
            _waveCount = 0;
            _bootRequested = false;
            // ScenarioOwnedTeams kept — same scenario keeps minted teams; respawn just
            // re-fires the wave/initial population top-up via a fresh BootRequest.
            return true;
        }

        /// <summary>
        /// Operator stop. Clears the active scenario so the worker idles. Bumps
        /// ScenarioGeneration so in-flight spawn envelopes are rejected at drain. Minted
        /// teams are left for TeamReaper to retire naturally.
        /// </summary>
        public static bool RequestStop()
        {
            if (_activeScenarioId == 0) return false;
            Interlocked.Increment(ref DirectorState.ScenarioGeneration);
            _activeScenarioId = 0;
            DirectorState.ActiveScenario = 0;
            _bootRequested = false;
            _lastBootEmitMono = 0L;
            return true;
        }

        public static void RunLoop(CancellationToken cancellation)
        {
            while (!_daemonInitComplete && !cancellation.IsCancellationRequested)
            {
                if (cancellation.WaitHandle.WaitOne(InitWaitPollMs)) return;
            }

            while (!cancellation.IsCancellationRequested)
            {
                if (!_enqueuingPermitted)
                {
                    if (cancellation.WaitHandle.WaitOne(TickMs)) return;
                    continue;
                }
                if (!SmartRuntime.IsHost || SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(TickMs)) return;
                    continue;
                }
                if (_activeScenarioId == 0)
                {
                    if (cancellation.WaitHandle.WaitOne(TickMs)) return;
                    continue;
                }

                try { Tick(); }
                catch (OperationCanceledException) { return; }
                catch (ThreadAbortException)
                {
                    if (AbortGuard.Absorb(CanonicalName) == AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("scenario-worker-tick-throw",
                        "[SCENARIO] tick threw " + ex.GetType().Name + ": " + ex.Message);
                }

                if (cancellation.WaitHandle.WaitOne(TickMs)) return;
            }
        }

        private static void Tick()
        {
            var recipe = ScenarioRegistry.Lookup(_activeScenarioId);
            if (recipe == null) return;
            int gen = DirectorState.ScenarioGeneration;
            long now = MonoClock.Now();

            // Boot phase: emit ScenarioBootRequest until executor has minted teams.
            // We re-emit on a 3 s heartbeat in case the first envelope was dropped.
            if (DirectorState.ScenarioOwnedTeams.IsEmpty)
            {
                float sinceBootEmit = _lastBootEmitMono == 0L ? 100f
                    : MonoClock.Seconds(_lastBootEmitMono, now);
                if (!_bootRequested || sinceBootEmit > 3f)
                {
                    EmitBootRequest(gen);
                    _bootRequested = true;
                    _lastBootEmitMono = now;
                }
                return;
            }

            // Anti-degradation: re-anchor SubNeutral relations every tick when scenario asks.
            if (recipe.AnchorSubNeutralRelations)
            {
                EmitAnchorRelations(gen);
            }

            // Live population — gates spawning so the world doesn't fill up unbounded.
            int live = SmartRuntime.LiveSmartTechCount;

            // Wave timers. Pulses pause once population is well over target (techs aren't
            // dying); they resume as the player thins them out.
            if (recipe.WaveIntervalSec > 0)
            {
                float sinceLastWave = _lastWaveMono == 0L
                    ? recipe.WaveIntervalSec
                    : MonoClock.Seconds(_lastWaveMono, now);
                if (sinceLastWave >= recipe.WaveIntervalSec)
                {
                    if (live < recipe.TargetTechCount * 2)
                        EmitWave(recipe, gen);
                    _lastWaveMono = now;
                }
            }

            // Maintenance top-up. Only refills when below target — no endless growth.
            if (recipe.MaintenanceCheckSec > 0)
            {
                float sinceMaint = _lastMaintenanceMono == 0L
                    ? recipe.MaintenanceCheckSec
                    : MonoClock.Seconds(_lastMaintenanceMono, now);
                if (sinceMaint >= recipe.MaintenanceCheckSec)
                {
                    if (live < recipe.TargetTechCount)
                        EmitMaintenanceTopUp(recipe, gen);
                    _lastMaintenanceMono = now;
                }
            }

            EnsureTeamRemovedSubscriptionDeferred();
        }

        private static void EmitBootRequest(int gen)
        {
            if (!_enqueuingPermitted) return;
            try
            {
                WorldEventBus.PublishFromWorker(new ScenarioBootRequest(
                    _activeScenarioId, gen, _intensity, _centroid));
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("scenario-boot-throw",
                    "[SCENARIO] boot emit threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EmitWave(ScenarioRecipe recipe, int gen)
        {
            // Pull hostile teams from ownership map (filled by executor on boot).
            var hostileTeams = SnapshotOwnedTeams();
            if (hostileTeams.Count == 0) return;
            int per = recipe.MobilePerHostileTeam;
            int stagger = recipe.SpawnWaveStaggered ? (_waveCount % hostileTeams.Count) : -1;
            for (int t = 0; t < hostileTeams.Count; t++)
            {
                if (stagger >= 0 && t != stagger) continue;
                Vector3 spawnPos = JitterRingPosition(_centroid, recipe.SpawnRadiusM * 1.1f,
                    t + _waveCount, hostileTeams.Count);
                if (recipe.Terrain == BaseTerrain.Air && recipe.AttackerAltitudeY > 0f)
                {
                    spawnPos.y = _centroid.y + recipe.AttackerAltitudeY;
                }
                Vector3 forward = (_centroid - spawnPos).normalized;
                if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
                for (int k = 0; k < per; k++)
                {
                    EmitMobileSpawn(SpreadOnRing(_centroid, spawnPos, k, per, 30f),
                        forward, hostileTeams[t], recipe, gen);
                }
            }
            _waveCount++;
        }

        private static void EmitMaintenanceTopUp(ScenarioRecipe recipe, int gen)
        {
            if (recipe.Id != ScenarioId.S1) return;
            var hostileTeams = SnapshotOwnedTeams();
            if (hostileTeams.Count == 0) return;
            for (int t = 0; t < hostileTeams.Count; t++)
            {
                Vector3 spawnPos = JitterRingPosition(_centroid, recipe.SpawnRadiusM,
                    t + _waveCount, hostileTeams.Count);
                Vector3 forward = (_centroid - spawnPos).normalized;
                if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
                EmitMobileSpawn(spawnPos, forward, hostileTeams[t], recipe, gen);
            }
        }

        private static void EmitMobileSpawn(Vector3 pos, Vector3 forward, int team,
            ScenarioRecipe recipe, int gen)
        {
            if (!_enqueuingPermitted) return;
            // Waves pull from the same curated combat folder as the boot hostiles.
            string folder = recipe.Terrain == BaseTerrain.Air
                ? DirectorTechFolders.Air : DirectorTechFolders.Brawl;
            var req = new ScenarioSpawnRequest(
                pos: pos, fwd: forward, team: team,
                scenarioId: _activeScenarioId, generation: gen,
                terrain: recipe.Terrain, purpose: BasePurpose.NotStationary,
                progression: recipe.Progression, faction: FactionSubTypes.NULL,
                maxPrice: recipe.MaxPrice, grade: recipe.Grade,
                offset: recipe.MobileOffset, excludeErad: true,
                directorFolder: folder, isBase: false);
            try { WorldEventBus.PublishFromWorker(req); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("scenario-emit-throw",
                    "[SCENARIO] emit threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EmitAnchorRelations(int gen)
        {
            if (!_enqueuingPermitted) return;
            // Anchor each (a,b) pair amongst SubNeutral-scenario teams + zero anger per team.
            // The drain-side predicate "current_relation != SubNeutral || angerThreshold > 0"
            // gates the actual SetRelations write so we don't thrash the engine cascade.
            var teams = SnapshotOwnedTeams();
            if (teams.Count < 2) return;
            byte relCode = (byte)TeamRelations.SubNeutral;
            for (int i = 0; i < teams.Count; i++)
            {
                int a = teams[i];
                try { WorldEventBus.PublishFromWorker(new ScenarioAngerResetRequest(a, gen)); }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("scenario-anger-throw",
                        "[SCENARIO] anger emit threw " + ex.GetType().Name + ": " + ex.Message);
                }
                for (int j = i + 1; j < teams.Count; j++)
                {
                    int b = teams[j];
                    try { WorldEventBus.PublishFromWorker(new ScenarioRelationsRequest(a, b, relCode, gen)); }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarnFileOnly("scenario-rel-throw",
                            "[SCENARIO] relations emit threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        private static List<int> SnapshotOwnedTeams()
        {
            var ids = new List<int>(8);
            foreach (var kv in DirectorState.ScenarioOwnedTeams)
            {
                ids.Add(kv.Key);
            }
            return ids;
        }

        private static float NextRand()
        {
            // System.Random isn't thread-safe across calls but this daemon's only writer
            // is its own RunLoop; lock kept tiny in case a future caller crosses threads.
            lock (_rng) { return (float)_rng.NextDouble(); }
        }

        // ~22.5 deg base rotation so waves don't land on the cardinal axes.
        private const float RingBaseAngle = 0.3927f;

        private static Vector3 JitterRingPosition(Vector3 centroid, float radius, int index, int total)
        {
            if (total <= 0) total = 1;
            float angle = RingBaseAngle + (Mathf.PI * 2f * index) / total;
            float r = radius * (0.9f + NextRand() * 0.2f);
            return new Vector3(centroid.x + Mathf.Cos(angle) * r,
                               centroid.y,
                               centroid.z + Mathf.Sin(angle) * r);
        }

        // Side-by-side spread along the ring tangent so a wave's techs don't stack.
        // Background-thread-safe (NextRand, not UnityEngine.Random).
        private static Vector3 SpreadOnRing(Vector3 centroid, Vector3 ringPos, int k, int per, float spacing)
        {
            Vector3 outward = ringPos - centroid;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f) outward = Vector3.forward;
            outward = outward.normalized;
            Vector3 tangent = Vector3.Cross(Vector3.up, outward);
            float lateral = (k - (per - 1) * 0.5f) * spacing;
            float radial = (NextRand() - 0.5f) * spacing * 0.5f;
            return ringPos + tangent * lateral + outward * radial;
        }

        private static Vector3 JitterOffset(int k, int total, float spread)
        {
            float dx = (NextRand() - 0.5f) * spread;
            float dz = (NextRand() - 0.5f) * spread;
            return new Vector3(dx, 0f, dz);
        }

        // Subscribe to TeamRemovedEvent the first time we see a chance to. The event
        // surface is main-thread, but our worker runs background — we can't subscribe
        // from here without crossing threads. Defer via a one-shot envelope: the boot
        // executor handles the subscription itself.
        private static void EnsureTeamRemovedSubscriptionDeferred()
        {
            if (Interlocked.CompareExchange(ref _teamRemovedSubscribed, 1, 0) != 0) return;
            _teamRemovedHandler = OnTeamRemoved;
            // Executor draws the subscription on its next tick via a deferred install.
            // Keep our handler stable so the install can grab it. Until the executor
            // performs the subscribe, the handler simply waits — long sessions that
            // never recycle team ids are unaffected.
            try
            {
                WorldEventBus.PublishFromWorker(new ScenarioTeamSubscribeRequest());
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("scenario-team-sub-throw",
                    "[SCENARIO] team-sub emit threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Called by the main-thread executor to actually attach the handler.
        internal static void ExecutorInstallTeamRemovedSubscription()
        {
            if (_teamRemovedHandler == null) _teamRemovedHandler = OnTeamRemoved;
            try { ManBaseTeams.TeamRemovedEvent.Subscribe(_teamRemovedHandler); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("scenario-team-removed-subscribe-throw",
                    "[SCENARIO] TeamRemovedEvent.Subscribe threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void OnTeamRemoved(int team)
        {
            byte _;
            DirectorState.ScenarioOwnedTeams.TryRemove(team, out _);
        }
    }
}
