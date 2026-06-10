using System;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Scenarios
{
    /// <summary>
    /// L3 planner daemon. 5 s tick while active scenario in {S2, S5}; idle 5 s wait
    /// otherwise. Census via Physics.OverlapSphere runs on MAIN THREAD inside the
    /// SmartForm.Operations executor — this background worker only schedules the
    /// envelope; the main-thread drain does the actual chunk spawn.
    ///
    /// Budget cap: 6 chunks/tick global. S2 boost: 9 chunks/tick.
    /// </summary>
    public static class ChunkRegenWorker
    {
        public const string CanonicalName = "ChunkRegen";
        public const int TickMs = 5000;
        public const int InitWaitPollMs = 1000;
        public const int DefaultChunkBudgetPerTick = 6;
        public const int S2BoostBudgetPerTick = 9;
        public const float CensusRadius = 200f;
        public const float InnerSpawnRadius = 40f;

        private static volatile bool _daemonInitComplete;
        private static volatile bool _enqueuingPermitted;

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
            _daemonInitComplete = false;
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

                int sid = DirectorState.ActiveScenario;
                if (!ScenarioRegistry.RequiresChunkRegen(sid))
                {
                    // Idle.
                    if (cancellation.WaitHandle.WaitOne(TickMs)) return;
                    continue;
                }

                try { Tick(sid); }
                catch (OperationCanceledException) { return; }
                catch (ThreadAbortException)
                {
                    if (AbortGuard.Absorb(CanonicalName) == AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("chunkregen-tick-throw",
                        "[CHUNK-REGEN] tick threw " + ex.GetType().Name + ": " + ex.Message);
                }

                if (cancellation.WaitHandle.WaitOne(TickMs)) return;
            }
        }

        private static void Tick(int sid)
        {
            if (!_enqueuingPermitted) return;
            var recipe = ScenarioRegistry.Lookup(sid);
            if (recipe == null) return;
            int gen = DirectorState.ScenarioGeneration;
            int budget = sid == (int)ScenarioId.S2 ? S2BoostBudgetPerTick : DefaultChunkBudgetPerTick;

            // Centroid defaults to Vector3.zero; the drain-side computes the actual
            // census/spawn at runtime against live chunks in range so the worker just
            // emits the budget envelope.
            var req = new ChunkSpawnRequest(
                centroid: Vector3.zero,
                innerR: InnerSpawnRadius,
                outerR: CensusRadius,
                countBudget: budget,
                scenarioId: sid,
                generation: gen);

            try { WorldEventBus.PublishFromWorker(req); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("chunkregen-emit-throw",
                    "[CHUNK-REGEN] emit threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
