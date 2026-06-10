using System;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// Stateless evaluator. Reads TrainerStats / IdentityOutcomeConsumer /
    /// SmartRuntime.LiveSmartTechCount; emits journal entries through DirectorJournal.
    /// Phase 2 ships the read side + ActionPicker journal stub
    /// "ACTION-WOULD-HAVE-FIRED:&lt;verb&gt;" — no Action verbs fire yet (Phase 3 wires them).
    ///
    /// Cold-start warmup: 60 s per constraint anchored off TrainingDirector init epoch.
    /// 5-min post-fire observation window logic ships now (only re-evaluable for
    /// ESCALATION); Phase 3+ verbs slot in cleanly.
    /// </summary>
    public static class ConstraintEngine
    {
        // Wall-clock seconds — set by EvaluateAll's caller (TrainingDirector) once when the
        // first ConstraintTick runs after init. Used for cold-start warmup gating.
        private static long _engineInitMono;

        // 5-minute post-fire observation. Per-constraint last-fire timestamp; only
        // ESCALATION re-evaluable while in observation.
        public const int PostFireObservationSec = 300;
        private static readonly long[] _lastFireMono = new long[ConstraintRegistry.ConstraintCount];

        // Last-known health per constraint — used for edge detection.
        private static readonly ConstraintHealth[] _lastHealth =
            new ConstraintHealth[ConstraintRegistry.ConstraintCount];

        // Last computed metric + consecutive-violation tick count, cached for the digest
        // CONSTRAINTS section. Written on each EvaluateOne; read by DigestBuilder.
        private static readonly float[] _lastMetric = new float[ConstraintRegistry.ConstraintCount];
        private static readonly int[] _violTickCount = new int[ConstraintRegistry.ConstraintCount];

        // External singleton — set by SmartRuntime/LearningService Install path. Phase 2
        // never null in practice; defensive null check in EvaluateAll.
        private static IdentityOutcomeConsumer _outcomeConsumer;

        // Last mono tick at which any constraint was Violated. AUTO-HEALTHY checkpoint
        // scheduler reads SecondsAllHealthy to gate the in-memory ring snapshot.
        private static long _lastUnhealthyMono;

        // Sliding 60 s window for damage_events_per_min: cumulative counter snapshot at
        // window start + the mono tick of that snapshot.
        private static long _dmgWindowStartCount;
        private static long _dmgWindowStartMono;

        private static float DamageRatePerMin(out bool insufficient)
        {
            insufficient = false;
            long countNow = TAC_AI.AI.Forms.Smart.Integration.SmartEventBridge.DamageEventsCounter;
            long now = MonoClock.Now();
            if (_dmgWindowStartMono == 0L)
            {
                _dmgWindowStartMono = now;
                _dmgWindowStartCount = countNow;
                insufficient = true;
                return 0f;
            }
            float elapsed = MonoClock.Seconds(_dmgWindowStartMono, now);
            if (elapsed < 1f) { insufficient = true; return 0f; }
            float rate = (countNow - _dmgWindowStartCount) * 60f / elapsed;
            // Roll the window on a 60 s sliding schedule.
            if (elapsed >= 60f)
            {
                _dmgWindowStartMono = now;
                _dmgWindowStartCount = countNow;
            }
            return rate;
        }

        public static void Reset()
        {
            _engineInitMono = 0L;
            for (int i = 0; i < _lastFireMono.Length; i++) _lastFireMono[i] = 0L;
            for (int i = 0; i < _lastHealth.Length; i++) _lastHealth[i] = ConstraintHealth.WarmupSilent;
            for (int i = 0; i < _lastMetric.Length; i++) { _lastMetric[i] = 0f; _violTickCount[i] = 0; }
            Interlocked.Exchange(ref _lastUnhealthyMono, 0L);
            _dmgWindowStartMono = 0L;
            _dmgWindowStartCount = 0L;
        }

        // ---- digest snapshot accessors (read-only, no lock) ----
        public static ConstraintHealth GetHealth(int idx)
        {
            if (idx < 0 || idx >= _lastHealth.Length) return ConstraintHealth.WarmupSilent;
            return _lastHealth[idx];
        }

        public static float GetLastMetric(int idx)
        {
            if (idx < 0 || idx >= _lastMetric.Length) return 0f;
            return _lastMetric[idx];
        }

        public static int GetViolTickCount(int idx)
        {
            if (idx < 0 || idx >= _violTickCount.Length) return 0;
            return _violTickCount[idx];
        }

        public static int OkCount()
        {
            int ok = 0;
            for (int i = 0; i < _lastHealth.Length; i++)
                if (_lastHealth[i] == ConstraintHealth.Healthy) ok++;
            return ok;
        }

        // Seconds since the last Violated edge across all constraints. Returns -1 until
        // warmup completes (no all-healthy claim during cold start). The Director's
        // AUTO-HEALTHY trigger checks this >= 300.
        public static float SecondsAllHealthy()
        {
            if (_engineInitMono == 0L) return -1f;
            long now = MonoClock.Now();
            if (MonoClock.Seconds(_engineInitMono, now) < ConstraintRegistry.ColdStartWarmupSec) return -1f;
            long lastBad = Interlocked.Read(ref _lastUnhealthyMono);
            // Anchor the all-healthy window on engine-init when nothing has gone bad yet.
            long anchor = lastBad != 0L ? lastBad : _engineInitMono;
            return MonoClock.Seconds(anchor, now);
        }

        public static void SetOutcomeConsumer(IdentityOutcomeConsumer c)
        {
            _outcomeConsumer = c;
        }

        /// <summary>
        /// One pass over every constraint. Called from TrainingDirector.ConstraintTick at 0.2 Hz.
        /// </summary>
        public static void EvaluateAll()
        {
            long now = MonoClock.Now();
            if (_engineInitMono == 0L) _engineInitMono = now;
            float ageSec = MonoClock.Seconds(_engineInitMono, now);

            for (int i = 0; i < ConstraintRegistry.All.Length; i++)
            {
                var c = ConstraintRegistry.All[i];
                try
                {
                    EvaluateOne(i, c, now, ageSec);
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("constraint-engine-eval-" + c.Name,
                        "[AIWARN] ConstraintEngine: " + c.Name + " threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void EvaluateOne(int idx, Constraint c, long now, float ageSec)
        {
            // Cold-start warmup: silent until init epoch ≥ 60 s.
            if (ageSec < ConstraintRegistry.ColdStartWarmupSec)
            {
                _lastHealth[idx] = ConstraintHealth.WarmupSilent;
                return;
            }

            // Per-scenario warmup: silent for 60 s after a scenario_set/respawn. Without
            // this the population/damage constraints fire scenario_respawn the instant a
            // scenario starts (pop still 0) and churn the spawns into a thrash loop.
            if (DirectorState.ActiveScenario != 0)
            {
                long sStart = Scenarios.ScenarioWorker.ScenarioStartMono;
                if (sStart != 0L && MonoClock.Seconds(sStart, now) < ConstraintRegistry.ScenarioWarmupSec)
                {
                    _lastHealth[idx] = ConstraintHealth.WarmupSilent;
                    return;
                }
            }

            // Compute the metric value or INSUFFICIENT_DATA.
            bool insufficient;
            float metric = ComputeMetric(c, out insufficient);
            if (insufficient)
            {
                _lastHealth[idx] = ConstraintHealth.InsufficientData;
                return;
            }

            // Band check.
            ConstraintHealth newHealth = ClassifyAgainstBand(metric, c);
            ConstraintHealth oldHealth = _lastHealth[idx];
            _lastMetric[idx] = metric;
            if (newHealth == ConstraintHealth.Violated)
                _violTickCount[idx] = (oldHealth == ConstraintHealth.Violated) ? _violTickCount[idx] + 1 : 1;
            else
                _violTickCount[idx] = 0;

            // Post-fire observation: while in window, only ESCALATION (Violated → still Violated
            // but more extreme, or a different kind of deviation) is journaled. Plain
            // Violated → Violated edges suppress to avoid digest spam.
            long lastFire = Interlocked.Read(ref _lastFireMono[idx]);
            bool inObservationWindow = lastFire != 0L
                && MonoClock.Seconds(lastFire, now) < PostFireObservationSec;

            // Track the most recent unhealthy moment for the AUTO-HEALTHY scheduler.
            if (newHealth == ConstraintHealth.Violated)
                Interlocked.Exchange(ref _lastUnhealthyMono, now);

            // Healthy → Violated edge: pick action, dispatch to ActionPicker.
            if (newHealth == ConstraintHealth.Violated)
            {
                bool isFreshTrigger = oldHealth != ConstraintHealth.Violated;
                if (isFreshTrigger || !inObservationWindow)
                {
                    float dev = ComputeDeviation(metric, c);
                    float severity = dev * c.DependsOnPublisherWeight;
                    ActionPicker.Dispatch(c, metric, dev, severity, FormatBand(c));
                    Interlocked.Exchange(ref _lastFireMono[idx], now);
                }
            }

            _lastHealth[idx] = newHealth;
        }

        private static ConstraintHealth ClassifyAgainstBand(float metric, Constraint c)
        {
            switch (c.Shape)
            {
                case BandShape.TwoSided:
                    return (metric >= c.BandMin && metric <= c.BandMax)
                        ? ConstraintHealth.Healthy : ConstraintHealth.Violated;
                case BandShape.MinOnly:
                    return metric >= c.BandMin ? ConstraintHealth.Healthy : ConstraintHealth.Violated;
                case BandShape.MaxOnly:
                    return metric <= c.BandMax ? ConstraintHealth.Healthy : ConstraintHealth.Violated;
            }
            return ConstraintHealth.Healthy;
        }

        private static float ComputeDeviation(float metric, Constraint c)
        {
            switch (c.Shape)
            {
                case BandShape.TwoSided:
                {
                    float width = Math.Max(c.BandMax - c.BandMin, 1e-6f);
                    if (metric < c.BandMin) return (c.BandMin - metric) / width;
                    if (metric > c.BandMax) return (metric - c.BandMax) / width;
                    return 0f;
                }
                case BandShape.MinOnly:
                {
                    float width = Math.Max(Math.Abs(c.BandMin), 1e-6f);
                    return metric < c.BandMin ? (c.BandMin - metric) / width : 0f;
                }
                case BandShape.MaxOnly:
                {
                    float width = Math.Max(Math.Abs(c.BandMax), 1e-6f);
                    return metric > c.BandMax ? (metric - c.BandMax) / width : 0f;
                }
            }
            return 0f;
        }

        private static string FormatBand(Constraint c)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (c.Shape)
            {
                case BandShape.TwoSided:
                    return "[" + c.BandMin.ToString("F3", inv) + ".." + c.BandMax.ToString("F3", inv) + "]";
                case BandShape.MinOnly:
                    return ">=" + c.BandMin.ToString("F3", inv);
                case BandShape.MaxOnly:
                    return "<=" + c.BandMax.ToString("F3", inv);
            }
            return "?";
        }

        // ---- Metric dispatch ----
        private static float ComputeMetric(Constraint c, out bool insufficient)
        {
            insufficient = false;
            switch (c.Kind)
            {
                case ConstraintKind.ModelEntropyRatio:
                {
                    if (TrainerStats.EntropySampleCount(c.Model) < 8) { insufficient = true; return 0f; }
                    return TrainerStats.EntropyMean(c.Model);
                }
                case ConstraintKind.ModelLossVarianceRatio:
                {
                    int n = TrainerStats.LossSampleCount(c.Model);
                    if (n < 8) { insufficient = true; return 0f; }
                    float mean = TrainerStats.LossMean(c.Model);
                    float var = TrainerStats.LossVariance(c.Model);
                    float denom = mean > 1e-6f ? mean : 1e-6f;
                    return var / denom;
                }
                case ConstraintKind.ModelParamDeltaL2:
                {
                    int n = TrainerStats.ParamDeltaSampleCount(c.Model);
                    if (n < 32) { insufficient = true; return 0f; }
                    return TrainerStats.ParamDeltaP99(c.Model);
                }
                case ConstraintKind.IdentityRatePerMin:
                {
                    var consumer = _outcomeConsumer;
                    if (consumer == null) { insufficient = true; return 0f; }
                    float rate = consumer.GetRatePerMin(c.Identity, c.OutcomeKindRef, c.WindowSec);
                    if (rate < 0f) { insufficient = true; return 0f; }
                    return rate;
                }
                case ConstraintKind.IdentityWeightedRate:
                {
                    var consumer = _outcomeConsumer;
                    if (consumer == null) { insufficient = true; return 0f; }
                    // Default identity bag for ally_protected — Phase 3+ will accept a richer
                    // descriptor via constraint config. For now, query the constraint's
                    // primary identity row only.
                    var bag = new SmartIdentity[] { c.Identity };
                    float rate = consumer.GetWeightedRatePerMin(bag, c.OutcomeKindRef, c.WindowSec);
                    if (rate < 0f) { insufficient = true; return 0f; }
                    int live = SmartRuntime.LiveSmartTechCountFor(c.Identity);
                    if (live <= 0) live = 1;
                    return rate / live;
                }
                case ConstraintKind.BaseHpRetention:
                {
                    var consumer = _outcomeConsumer;
                    if (consumer == null) { insufficient = true; return 0f; }
                    long held = consumer.GetCount(SmartIdentity.Base, OutcomeKind.BaseHeld);
                    long lost = consumer.GetCount(SmartIdentity.Base, OutcomeKind.BaseLost);
                    if (held + lost < 3) { insufficient = true; return 0f; }
                    return (float)held / Math.Max(held + lost, 1);
                }
                case ConstraintKind.LiveSmartTechFloor:
                {
                    int live = SmartRuntime.LiveSmartTechCount;
                    // Floor only meaningful when a scenario is active; Phase 2 has no scenario
                    // publishers wired so ActiveScenario==0 paths get INSUFFICIENT_DATA.
                    if (DirectorState.ActiveScenario == 0) { insufficient = true; return 0f; }
                    return live;
                }
                case ConstraintKind.DamageEventsPerMin:
                {
                    // Two-snapshot diff of the cumulative DamageObserved counter, scaled to
                    // per-min over the sampling interval. Gated on an active engaged scenario.
                    if (DirectorState.ActiveScenario == 0) { insufficient = true; return 0f; }
                    return DamageRatePerMin(out insufficient);
                }
                case ConstraintKind.TtlDiscardRate:
                {
                    // TtlDiscardsTotal lives on TrainerWorker; we have no LearningService
                    // accessor surface for it yet (Phase 3 wires up). Until then, this
                    // constraint reports INSUFFICIENT_DATA so the ttl diag verb does not
                    // false-trip on cold start.
                    insufficient = true;
                    return 0f;
                }
                case ConstraintKind.SubNeutralRelationsIntact:
                {
                    // Director-owned SubNeutral pair tracking lands with ScenarioWorker
                    // (Phase 3+). Until then report 0 (healthy).
                    return 0f;
                }
                case ConstraintKind.GuardBucketRate:
                {
                    // Phase 5 fills — no guard publishers exist yet so the rate query
                    // returns INSUFFICIENT_DATA from the consumer side.
                    var consumer = _outcomeConsumer;
                    if (consumer == null) { insufficient = true; return 0f; }
                    float rate = consumer.GetWeightedRatePerMin(
                        new SmartIdentity[] { c.Identity }, c.OutcomeKindRef, c.WindowSec);
                    if (rate < 0f) { insufficient = true; return 0f; }
                    int live = SmartRuntime.LiveSmartTechCount;
                    if (live < 3) { insufficient = true; return 0f; }
                    return rate / live;
                }
                case ConstraintKind.GuardInterventionRate:
                {
                    // Phase 5 wires GuardCorrectiveActuator journal feed; Phase 2 has nothing
                    // to count.
                    insufficient = true;
                    return 0f;
                }
            }
            insufficient = true;
            return 0f;
        }
    }
}
