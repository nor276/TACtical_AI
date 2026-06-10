using System;
using System.Collections.Generic;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning.Features;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // L3 daemon. 1 Hz tick. Reads-only: never mutates TankAIHelper state. Marshals rare
    // Unity reads via GuardEvaluationRequest envelope round-trip; everything else flows
    // through SelfStateProbe.TryRead() + StrategicStateBuffer.TryRead() + BeliefSnapshot.
    //
    // Per (tech, guard) pair: identity + scenario gate → ShouldEvaluate → Evaluate →
    // edge-detect → publish IdentityOutcome(GuardViolation_<bucket>) on rising edges
    // only (NOT per tick).
    public static class BehaviorGuardWorker
    {
        public const string CanonicalName = "GuardWorker";
        public const int TickMs = 1000;
        public const int InitWaitPollMs = 1000;

        private static volatile bool _daemonInitComplete;
        private static volatile bool _running;

        // Per-(tech, guard) "previously fired" latch so we publish on edges only.
        // Lock-free: single-writer (this daemon thread). Lookups + writes scoped to a
        // single tick.
        private static readonly Dictionary<long, bool> _wasFiringPrev =
            new Dictionary<long, bool>();
        // Reusable context allocated once per tick; reassigned per (tech, guard).
        private static readonly GuardContext _ctx = new GuardContext();

        public static void Init(WorkerPool pool)
        {
            if (pool == null) return;
            if (_daemonInitComplete) return;

            _running = true;
            pool.EnqueueLongRunning(RunLoop, CanonicalName);
            var poolRef = pool;
            Func<string> factory = () => poolRef != null ? poolRef.EnqueueLongRunning(RunLoop, CanonicalName) : null;
            DaemonWatchdog.RegisterCanonical(CanonicalName, factory);
            WorkerHealthMonitor.RegisterCanonical(CanonicalName, factory);

            _daemonInitComplete = true;
        }

        public static void BeginShutdown()
        {
            _running = false;
            _daemonInitComplete = false;
            _wasFiringPrev.Clear();
        }

        public static void RunLoop(CancellationToken cancellation)
        {
            while (!_daemonInitComplete && !cancellation.IsCancellationRequested)
            {
                if (cancellation.WaitHandle.WaitOne(InitWaitPollMs)) return;
            }

            while (!cancellation.IsCancellationRequested)
            {
                if (!_running)
                {
                    if (cancellation.WaitHandle.WaitOne(TickMs)) return;
                    continue;
                }
                if (!SmartRuntime.IsHost || SmartRuntime.IsPaused)
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
                    DebugTAC_AI.LogWarnFileOnly("guardworker-tick-throw",
                        "[GUARD-WORKER] tick threw " + ex.GetType().Name + ": " + ex.Message);
                }

                if (cancellation.WaitHandle.WaitOne(TickMs)) return;
            }
        }

        private static void Tick()
        {
            int activeScenario = DirectorState.ActiveScenario;
            long monoTick = MonoClock.Now();
            int liveCount = SmartRuntime.LiveSmartTechCount;

            _ctx.ActiveScenario = activeScenario;
            _ctx.MonoTick = monoTick;
            _ctx.LiveSmartTechCount = liveCount;
            _ctx.OverextendedHunterLeashM = 250f;

            foreach (var team in SmartRuntime.EnumerateTeams())
            {
                if (team == null) continue;
                foreach (var techId in team.SnapshotTechIds())
                {
                    EvaluateTech(team, techId);
                }
            }

            // Drive the hard-correct actuator state machine: ceiling -> ramp-down -> release.
            GuardCorrectiveActuator.SweepExpired();
        }

        private static void EvaluateTech(TeamRuntime team, TechId techId)
        {
            var state = team.TryGetTechState(techId);
            if (state == null) return;

            // Helper / Tank reads here are background-thread - but we only touch fields
            // that are bg-safe (FormState identity, AuthoredTerrain, AIType byte). Any
            // Transform / rb.velocity-dependent value rides through SelfProbeSnapshot.
            var helper = state.Helper;
            var tank = state.Tank;
            if (helper == null || tank == null) return;

            var probe = state.SelfStateProbe;
            SelfProbeSnapshot snap = probe != null ? probe.TryRead() : null;
            if (snap == null) return;

            _ctx.Helper = helper;
            _ctx.Tank = tank;
            _ctx.TechId = techId;
            _ctx.Identity = state.IdentityStamp.Identity;
            _ctx.IdentityStamp = state.IdentityStamp;
            _ctx.Snapshot = snap;
            _ctx.LastDestinationCore = helper.lastDestinationCore;

            // Fill the 30 s scratch from the helper's 4 Hz rings.
            FillScratch(helper, 30);

            for (int g = 0; g < GuardRegistry.Count; g++)
            {
                var guard = GuardRegistry.Get(g);
                if (guard == null) continue;

                if (!MatchesIdentityGate(guard, _ctx.Identity)) continue;
                if (!MatchesScenarioGate(guard, _ctx.ActiveScenario)) continue;
                if (!guard.ShouldEvaluate(_ctx)) continue;

                GuardVerdict verdict;
                try { verdict = guard.Evaluate(_ctx); }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("guard-eval-throw-" + guard.Name,
                        "[GUARD-EVAL] guard=" + guard.Name + " tech=" + techId.Value
                        + " threw " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                if (verdict.ExceptionMatched)
                {
                    GuardTelemetry.RecordExceptionMatched(g);
                }

                bool fireNow = verdict.Fired;
                long latchKey = ((long)techId.Value << 8) | (long)(g & 0xFF);
                bool wasFiring;
                _wasFiringPrev.TryGetValue(latchKey, out wasFiring);
                bool risingEdge = fireNow && !wasFiring;
                bool fallingEdge = !fireNow && wasFiring;

                if (GuardTelemetry.IsSuppressed(techId, g, risingEdge, !fireNow))
                {
                    if (risingEdge) GuardTelemetry.RecordSuppressedByCooldown(g);
                    _wasFiringPrev[latchKey] = fireNow;
                    continue;
                }

                if (risingEdge)
                {
                    GuardTelemetry.RecordFire(g);
                    var bucket = GuardRegistry.GetBucket(g);
                    byte source = GuardRegistry.BuildSource(g, false);
                    float mag = verdict.Magnitude > 0f ? verdict.Magnitude : 1f;
                    var outcome = new IdentityOutcome(techId, _ctx.Identity, bucket, mag, source);
                    try { WorldEventBus.PublishFromWorker(outcome); }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarnFileOnly("guard-publish-throw-" + guard.Name,
                            "[GUARD-PUBLISH] " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                _wasFiringPrev[latchKey] = fireNow;
            }
        }

        private static void FillScratch(TankAIHelper helper, int windowSec)
        {
            // Read the rings without locking — single writer (Physics tick on main
            // thread) + single reader (this tick on bg). Worst case we read a slightly
            // stale slot from the cursor wraparound; the means stay representative.
            int depth = TankAIHelper.GuardRingDepth;
            int wantSamples = Mathf.Min(windowSec * 4, depth); // 4 Hz writer
            bool wrapped = helper.recentSpeedWrapped;
            int cursor = helper.recentSpeedCursor;
            int total = wrapped ? depth : cursor;
            int take = Mathf.Min(wantSamples, total);
            if (take <= 0)
            {
                _ctx.MeanRecentSpeedSigned = 0f;
                _ctx.MeanRecentSpeedXZ = 0f;
                _ctx.MeanRecentSpeedY = 0f;
                _ctx.FractionSpeedSignedBelowZero = 0f;
                _ctx.NetDisplacement = Vector3.zero;
                return;
            }

            double sumSigned = 0, sumXZ = 0, sumY = 0;
            int below = 0;
            int idx = cursor - 1;
            for (int i = 0; i < take; i++)
            {
                if (idx < 0) idx += depth;
                float s = helper.recentSpeedSigned[idx];
                sumSigned += s;
                sumXZ += helper.recentSpeedXZ[idx];
                sumY += helper.recentSpeedY[idx];
                if (s < 0f) below++;
                idx--;
            }

            _ctx.MeanRecentSpeedSigned = (float)(sumSigned / take);
            _ctx.MeanRecentSpeedXZ = (float)(sumXZ / take);
            _ctx.MeanRecentSpeedY = (float)(sumY / take);
            _ctx.FractionSpeedSignedBelowZero = (float)below / take;
            // Approximate net displacement = mean speed over window × elapsed seconds.
            // Direction is unreliable from speeds alone; guards that need exact net
            // displacement compute from snapshot.PositionWorld vs window-start cache.
            float winSec = take * TankAIHelper.GuardRingTickSec;
            _ctx.NetDisplacement = _ctx.Snapshot.ForwardWorld * (_ctx.MeanRecentSpeedSigned * winSec);
        }

        private static bool MatchesIdentityGate(IBehaviorGuard guard, SmartIdentity identity)
        {
            var arr = guard.AppliesToIdentities;
            if (arr == null || arr.Length == 0) return true;
            for (int i = 0; i < arr.Length; i++) if (arr[i] == identity) return true;
            return false;
        }

        private static bool MatchesScenarioGate(IBehaviorGuard guard, int scenario)
        {
            var arr = guard.AppliesToScenarios;
            if (arr == null || arr.Length == 0) return true;
            for (int i = 0; i < arr.Length; i++) if (arr[i] == scenario) return true;
            return false;
        }
    }
}
