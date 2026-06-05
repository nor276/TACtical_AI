using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Threading;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Maintains the world belief state by draining observation intake slots and dead-reckoning
    /// out-of-sight techs. Per AETHER-DESIGN.md "Threading model". Replaces PerceptionWorker.
    ///
    /// Runs on the Threading worker pool. Each tick:
    ///   1. Sample MonoClock.Now() once for consistent in-tick stamps.
    ///   2. Snapshot per-tech entries (WorldModel.SnapshotPerTechState).
    ///   3. For each tech:
    ///        - If intake slot has a newer observation than the prior's LastObservedTickMono,
    ///          read the slot and build a NewlyObserved BeliefState (applying optional smoothing
    ///          per AetherTuning.VelocitySmoothingStrength).
    ///        - Otherwise, coast forward via DeadReckon.Coast.
    ///   4. Publish per-tech belief; fire BeliefUpdated for >1m position shifts.
    ///   5. Build fused snapshot dictionary and publish via DoubleBuffer.Write.
    ///
    /// Cadence: 30 Hz target. Host-only (ARCHITECTURE §3.2; same gate as PerceptionWorker).
    /// </summary>
    public sealed class AetherFuser
    {
        private readonly WorldModel _world;
        private readonly DoubleBuffer<BeliefSnapshot> _snapshotBuffer;

        // Diagnostic accumulators — emitted as one summary line every DiagnosticLogPeriodTicks.
        // Fires AFTER the per-controller dispatch warmup cap so we see steady-state behavior
        // (the dispatch log truncates at 6 ticks per tech). Costs one int per tick + one summary
        // log every ~5 s.
        private const int DiagnosticLogPeriodTicks = 150;       // ~5 s at 30 Hz
        private const float SlowTickMs = 33f;                   // tick budget at 30 Hz
        private int _windowTickCount;
        private long _windowSumStopwatchTicks;
        private long _windowMaxStopwatchTicks;
        private int _windowFresh;
        private int _windowCoast;
        private int _windowSlowTicks;
        private int _windowMaxTechCount;

        // Aether/T4: per-daemon circuit breaker. Trips on sustained exceptions; daemon exits cleanly.
        private readonly CircuitBreaker _breaker = new CircuitBreaker("AetherFuser");

        // P9 Item 32: per-tick spike profiler. Captures snapshots of slow ticks (>100ms) into
        // a ring of 256 entries for post-session inspection. Pure observability.
        private readonly AetherFuserSpikeProfiler _spikeProfiler = new AetherFuserSpikeProfiler();

        /// <summary>P9 Item 32: external accessor for the most-recent spike snapshot.</summary>
        public AetherSpikeReport LastSpike => _spikeProfiler.Snapshot();
        /// <summary>P9 Item 32: total spike count since AetherFuser construction.</summary>
        public long TotalSpikes => _spikeProfiler.TotalSpikes;

        public AetherFuser(WorldModel world, DoubleBuffer<BeliefSnapshot> snapshotBuffer)
        {
            _world = world;
            _snapshotBuffer = snapshotBuffer;
        }

        public void Tick(CancellationToken cancellation)
        {
            CancellationHelpers.ThrowIfCancelled(cancellation);
            _spikeProfiler.OnTickStart();   // P9 Item 32
            long tickStartStopwatch = System.Diagnostics.Stopwatch.GetTimestamp();
            long nowMono = MonoClock.Now();

            var perTech = _world.SnapshotPerTechState();
            var updated = new Dictionary<TechId, BeliefState>(perTech.Count);
            var intake = _world.Intake;
            int freshThisTick = 0;
            int coastThisTick = 0;

            foreach (var pair in perTech)
            {
                CancellationHelpers.ThrowIfCancelled(cancellation);
                var techId = pair.Key;
                var entry = pair.Value;
                var prior = entry.PriorBelief;
                if (prior == null) continue;

                BeliefState newBelief = null;
                ObservationIntakeSlot slot;
                bool consumedFresh = false;
                if (intake != null && intake.TryGet(techId, out slot)
                    && slot.PeekTickMono() > prior.LastObservedTickMono)
                {
                    Vector3 pos, rawVel, fwd;
                    long tickMono;
                    if (slot.TryRead(out pos, out rawVel, out fwd, out tickMono)
                        && tickMono > prior.LastObservedTickMono)
                    {
                        Vector3 vel = rawVel;
                        float smoothing = AetherTuning.VelocitySmoothingStrength;
                        if (smoothing > 0f)
                            vel = Vector3.Lerp(rawVel, prior.LastSeenVelocity, smoothing);

                        newBelief = BeliefStateFactory.NewlyObserved(
                            techId, prior.Team, pos, vel, fwd,
                            prior.MaxAccelerationEstimate, tickMono);
                        consumedFresh = true;
                    }
                }

                if (consumedFresh) freshThisTick++;
                else { newBelief = DeadReckon.Coast(prior, nowMono); coastThisTick++; }

                updated[techId] = newBelief;
                _world.PublishPerTechBelief(techId, newBelief);

                // P4 Item 14 (REV 7): TechLost edge producer. Fire on the Coasting/Stale →
                // Lost transition exactly once per transition. Threading: AetherFuser runs
                // on the WorkerPool, so PublishFromWorker (not Publish) — the relay marshals
                // to the main thread via WorldEventBus.DrainMainThreadQueue per EventBus.cs:218.
                // Inlined edge check (no separate SightStateEdgeDetector helper — over-engineering
                // per REV 7 plan: "9/10 recommended inlining").
                if (prior.Sight != SightState.Lost && newBelief.Sight == SightState.Lost)
                {
                    WorldEventBus.PublishFromWorker(new TechLost(techId, newBelief.LastSeenPosition));
                }

                if (HasSignificantChange(prior, newBelief))
                    WorldEventBus.PublishFromWorker(new BeliefUpdated(techId));
            }

            var snapshot = new BeliefSnapshot(nowMono, updated);
            _snapshotBuffer.Write(snapshot);

            // P9 Item 32: spike-profiler tick-end. perTechCount = total techs processed
            // (fresh + coast). intakeCount = number of fresh-this-tick (intake-drained). The
            // profiler logs a [AETHER-SPIKE] line + records the snapshot only when elapsed
            // ms >= 100 — otherwise no-op fast path.
            _spikeProfiler.OnTickEnd(freshThisTick + coastThisTick, freshThisTick);

            // Update window accumulators.
            long tickEndStopwatch = System.Diagnostics.Stopwatch.GetTimestamp();
            long tickStopwatchTicks = tickEndStopwatch - tickStartStopwatch;
            _windowTickCount++;
            _windowSumStopwatchTicks += tickStopwatchTicks;
            if (tickStopwatchTicks > _windowMaxStopwatchTicks) _windowMaxStopwatchTicks = tickStopwatchTicks;
            _windowFresh += freshThisTick;
            _windowCoast += coastThisTick;
            int techCount = freshThisTick + coastThisTick;
            if (techCount > _windowMaxTechCount) _windowMaxTechCount = techCount;
            float tickMs = (float)(tickStopwatchTicks * 1000.0 * MonoClock.TickFreq);
            if (tickMs > SlowTickMs) _windowSlowTicks++;

            if (_windowTickCount >= DiagnosticLogPeriodTicks)
            {
                EmitWindowSummary();
                ResetWindow();
            }
        }

        private void EmitWindowSummary()
        {
            double avgMs = _windowSumStopwatchTicks * 1000.0 * MonoClock.TickFreq / _windowTickCount;
            double maxMs = _windowMaxStopwatchTicks * 1000.0 * MonoClock.TickFreq;
            int totalProcessed = _windowFresh + _windowCoast;
            float freshPct = totalProcessed > 0 ? (100f * _windowFresh) / totalProcessed : 0f;
            DebugTAC_AI.Log(string.Format(
                "Smart.AetherFuser[diag] ticks={0} maxTechs={1} avg={2:F2}ms max={3:F2}ms slow(>33ms)={4} fresh%={5:F0} smoothing={6:F2}",
                _windowTickCount, _windowMaxTechCount, avgMs, maxMs, _windowSlowTicks, freshPct,
                AetherTuning.VelocitySmoothingStrength));
        }

        private void ResetWindow()
        {
            _windowTickCount = 0;
            _windowSumStopwatchTicks = 0L;
            _windowMaxStopwatchTicks = 0L;
            _windowFresh = 0;
            _windowCoast = 0;
            _windowSlowTicks = 0;
            _windowMaxTechCount = 0;
        }

        private static bool HasSignificantChange(BeliefState prior, BeliefState next)
        {
            float dx = next.PositionMean.x - prior.PositionMean.x;
            float dy = next.PositionMean.y - prior.PositionMean.y;
            float dz = next.PositionMean.z - prior.PositionMean.z;
            return dx * dx + dy * dy + dz * dz > 1f;
        }

        /// <summary>
        /// Worker loop. Same shape as the prior PerceptionWorker.RunLoop: host-gate,
        /// catch-and-continue on exceptions (single fault doesn't terminate the worker).
        /// </summary>
        public void RunLoop(CancellationToken cancellation)
        {
            const int TickPeriodMs = 33; // ~30 Hz target per WORLD §6.2

            while (!cancellation.IsCancellationRequested)
            {
                // L-026: pause-gate ahead of host-gate. Aether's ~30Hz cadence makes this
                // the most CPU-impactful pause yield in the system.
                if (SmartRuntime.IsPaused)
                {
                    if (cancellation.WaitHandle.WaitOne(TickPeriodMs)) return;
                    continue;
                }
                if (!SmartRuntime.IsHost)
                {
                    if (cancellation.WaitHandle.WaitOne(TickPeriodMs)) return;
                    continue;
                }
                try
                {
                    Tick(cancellation);
                }
                catch (System.OperationCanceledException)
                {
                    return;
                }
                // L-024: explicit TAE catch ahead of catch(Exception) so the CLR doesn't
                // auto-rethrow TAE past our generic handler. Storm-exit lets L-023 supervisor
                // respawn us with a fresh CancellationToken; non-storm = absorb + continue.
                catch (System.Threading.ThreadAbortException)
                {
                    if (TAC_AI.AI.Forms.Smart.Threading.AbortGuard.Absorb("AetherFuser")
                        == TAC_AI.AI.Forms.Smart.Threading.AbortGuard.AbortAction.ExitForRespawn) return;
                }
                catch (System.Exception ex)
                {
                    DebugTAC_AI.LogWarning(
                        "Smart.AetherFuser: tick threw " + ex.GetType().Name + ": " + ex.Message);
                    if (_breaker.Tripped()) return;
                }

                if (cancellation.WaitHandle.WaitOne(TickPeriodMs))
                    return;
            }
        }
    }
}
