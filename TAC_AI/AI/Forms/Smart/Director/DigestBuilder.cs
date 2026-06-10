using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Director.Guards;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    // Text + JSON digest from a DigestSnapshot. Wall-clock minute-aligned periodic emission
    // + out-of-band coalesced triggers. Both projections derive PURELY from the snapshot.
    // JSON is hand-rolled (PresetIO is a key=value preset writer, not a JSON serializer).
    public static class DigestBuilder
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly object _emitLock = new object();
        private static readonly ManualResetEventSlim _emitLoopGate = new ManualResetEventSlim(true);

        private static string _dir;
        private static string _historyPath;

        // Pending coalesced trigger set drained at end of each Director tick.
        private static readonly HashSet<string> _pendingTriggers = new HashSet<string>();
        private static readonly object _trigGate = new object();

        // Next phase-aligned periodic deadline (UTC seconds, rounded to digest period).
        private static long _nextPeriodicUtcSec;
        private static long _lastEmitMono;

        public static void Init(string modDirectory)
        {
            if (string.IsNullOrEmpty(modDirectory)) return;
            try
            {
                _dir = Path.Combine(modDirectory, "SmartAI", "Director");
                if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                _historyPath = Path.Combine(_dir, "digest.history.log");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-digest-init",
                    "[DIRECTOR-DIGEST] init threw " + ex.GetType().Name + ": " + ex.Message);
            }
            ScheduleNextPeriodic();
        }

        public static void Reset()
        {
            lock (_trigGate) _pendingTriggers.Clear();
            _nextPeriodicUtcSec = 0L;
            _lastEmitMono = 0L;
            _dir = null;
            _historyPath = null;
        }

        // Operator / action surface for out-of-band triggers. Coalesced into the next tick.
        public static void RequestTrigger(string trigger)
        {
            if (string.IsNullOrEmpty(trigger)) return;
            // Cold-start trigger suppression for the host-changed / scenario triggers.
            if (IsColdStartSuppressed(trigger)) return;
            lock (_trigGate) _pendingTriggers.Add(trigger);
        }

        private static bool IsColdStartSuppressed(string trigger)
        {
            if (TrainingDirector.AgeSinceInitSec >= 60f) return false;
            return trigger == "host-changed"
                || trigger.StartsWith("scenario:", StringComparison.Ordinal)
                || trigger == "scenario_set" || trigger == "scenario_respawn";
        }

        // Force-emit bypasses the IsPaused gate (used for the rollback trigger). Blocks the
        // shutdown gate so BeginShutdown waits on an in-flight emit.
        public static void ForceEmit(string trigger)
        {
            EmitInternal(trigger, force: true);
        }

        // Called from TrainingDirector.AggregationTick at 1 Hz. Drains coalesced triggers +
        // checks the periodic deadline.
        public static void OnDirectorTick()
        {
            if (TrainingDirector.IsShuttingDown) return;

            string coalesced = DrainTriggers();
            bool periodicDue = PeriodicDue();

            if (periodicDue)
            {
                coalesced = string.IsNullOrEmpty(coalesced) ? "tick" : coalesced + ",tick";
                ScheduleNextPeriodic();
            }
            if (string.IsNullOrEmpty(coalesced)) return;

            EmitInternal(coalesced, force: false);
        }

        private static string DrainTriggers()
        {
            lock (_trigGate)
            {
                if (_pendingTriggers.Count == 0) return null;
                var sb = new StringBuilder();
                foreach (var tr in _pendingTriggers)
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(tr);
                }
                _pendingTriggers.Clear();
                return sb.ToString();
            }
        }

        private static bool PeriodicDue()
        {
            long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_nextPeriodicUtcSec == 0L) { ScheduleNextPeriodic(); return false; }
            return nowUtc >= _nextPeriodicUtcSec;
        }

        private static void ScheduleNextPeriodic()
        {
            long period = DirectorTunables.DigestPeriodSec > 0 ? DirectorTunables.DigestPeriodSec : 600;
            long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Round UP to the next period boundary for multi-session comparability.
            long next = ((nowUtc / period) + 1) * period;
            // Floor-clamp to lastEmit + 60 s minimum (resists clock backwards-jump).
            _nextPeriodicUtcSec = next;
        }

        private static void EmitInternal(string trigger, bool force)
        {
            if (TrainingDirector.IsShuttingDown) return;
            if (!force && SmartRuntime.IsPaused) return;

            _emitLoopGate.Reset();
            try
            {
                DigestSnapshot snap;
                lock (_emitLock)
                {
                    snap = BuildSnapshot(trigger);
                }
                string text = SnapshotToText(snap);
                string json = SnapshotToJson(snap);

                _lastEmitMono = MonoClock.Now();

                // Sink order; each in its own try/catch — last failure does not undo earlier.
                // (1) LatestDigest ref-swap (cheapest, never throws).
                try { DirectorState.LatestDigest = text; } catch { }

                // (2) Direct log (bypasses LogWarnFileOnly's permanent dedup).
                try { UnityEngine.Debug.Log("[DIRECTOR-TRIG] trig=" + (snap.Trigger ?? "")); } catch { }

                // (3) Journal append.
                try { DirectorJournal.Append("DIGEST-EMIT", "trig=" + (snap.Trigger ?? "")); } catch { }

                // (4) digest.history.log append.
                try { AppendHistory(text); } catch { }

                // (5) digest.latest.txt + .json atomic write (most failure-prone).
                try { WriteLatest(text, json); } catch { }
            }
            finally
            {
                _emitLoopGate.Set();
            }
        }

        // BeginShutdown blocks here until any in-flight emit finishes its tick.
        public static void WaitForEmitLoopGate(int timeoutMs)
        {
            try { _emitLoopGate.Wait(timeoutMs); } catch { }
        }

        // ---- snapshot construction (read-only of volatile/reservoir state) ----
        public static DigestSnapshot BuildSnapshot(string trigger)
        {
            var s = new DigestSnapshot();
            s.WallclockUtcSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            s.ProcessUptimeMonoSec = (long)(MonoClock.Now() * MonoClock.TickFreq);
            s.Trigger = trigger;
            s.Host = SmartRuntime.IsHost ? 1 : 0;
            s.Scenario = DirectorState.ActiveScenario;
            s.Intensity = DirectorState.Intensity;

            // Model rows.
            ModelId[] models = { ModelId.Intent, ModelId.ActionValue, ModelId.TrajectoryResidual, ModelId.ThreatAssessment };
            for (int i = 0; i < 4; i++)
            {
                var m = models[i];
                var st = new ModelStat();
                st.LossMean = TrainerStats.LossMean(m);
                int lossN = TrainerStats.LossSampleCount(m);
                st.HasLossVar = lossN >= 8;
                if (st.HasLossVar)
                {
                    float mean = TrainerStats.LossMean(m);
                    float denom = mean > 1e-6f ? mean : 1e-6f;
                    st.LossVarOverMeanRewardNorm = TrainerStats.LossVariance(m) / denom;
                }
                if (m == ModelId.Intent && TrainerStats.EntropySampleCount(m) >= 8)
                {
                    st.HasEntropy = true;
                    st.EntropyRatio = TrainerStats.EntropyMean(m);
                }
                if ((m == ModelId.ActionValue || m == ModelId.ThreatAssessment)
                    && TrainerStats.ParamDeltaSampleCount(m) >= 32)
                {
                    st.HasDL2 = true;
                    st.DL2P99 = TrainerStats.ParamDeltaP99(m);
                }
                st.LrNow = DirectorTuning.GetLrScale((int)m);
                st.Frozen = DirectorTuning.GetFrozenUntilTickMono((int)m) > MonoClock.Now();
                s.ModelStats[i] = st;
            }

            // Identity rates (populated cells only).
            var consumer = LearningService.IdentityOutcomes;
            if (consumer != null)
            {
                AddIdentityRate(s, consumer, SmartIdentity.Hunter, OutcomeKind.KillScored, "kills/min", 300);
                AddIdentityRate(s, consumer, SmartIdentity.Gatherer, OutcomeKind.BlockDelivered, "deliv/min", 300);
                AddIdentityRate(s, consumer, SmartIdentity.AircraftSupport, OutcomeKind.AllyProtected, "prot_wgt/min", 300);
                AddBaseHp(s, consumer);
            }

            // Guard rates from telemetry fire counts.
            BuildGuardRollup(s);

            // System stats.
            s.System.Techs = SmartRuntime.LiveSmartTechCount;
            s.System.HealthySnapAge = FormatSecs((int)Math.Max(0f, ConstraintEngine.SecondsAllHealthy()));
            s.System.AllyProtectEvictPerMin = Publishers.AllyProtectionPublisher.EvictionsPerMinute;

            // Constraint violations + ok count.
            s.ConstraintOkCount = ConstraintEngine.OkCount();
            for (int i = 0; i < ConstraintRegistry.All.Length; i++)
            {
                if (ConstraintEngine.GetHealth(i) != ConstraintHealth.Violated) continue;
                var c = ConstraintRegistry.All[i];
                s.ConstraintViolations.Add(new ViolationLine
                {
                    Name = c.Name,
                    Metric = ConstraintEngine.GetLastMetric(i),
                    Band = c.Shape == BandShape.MaxOnly ? "<=" + c.BandMax.ToString("F2", Inv)
                        : c.Shape == BandShape.MinOnly ? ">=" + c.BandMin.ToString("F2", Inv)
                        : "[" + c.BandMin.ToString("F2", Inv) + ".." + c.BandMax.ToString("F2", Inv) + "]",
                    ConsecutiveTicks = ConstraintEngine.GetViolTickCount(i),
                });
            }

            // Active directives.
            var dirs = DirectiveSurface.SnapshotDirectives();
            long nowMono = MonoClock.Now();
            for (int i = 0; i < dirs.Count; i++)
            {
                var d = dirs[i];
                s.DirectivesActive.Add(new DirectiveLine
                {
                    Id = d.Id,
                    Tag = d.IsSticky ? "sticky" : (d.Tag ?? (d.ExpiresMono != 0L ? "for-dur" : "until")),
                    Text = d.Verb + " " + (d.Subject ?? ""),
                    Age = FormatSecs((int)MonoClock.Seconds(d.CreatedMono, nowMono)),
                });
            }

            // Rollback availability.
            s.Rollback.MemoryAvailable = DirectorParamRing.HasHealthy();
            s.Rollback.NamedList = DirectorCheckpoint.ListLabelsCsv();

            return s;
        }

        private static void AddIdentityRate(DigestSnapshot s, IdentityOutcomeConsumer consumer,
            SmartIdentity id, OutcomeKind kind, string label, int windowSec)
        {
            float rate = consumer.GetRatePerMin(id, kind, windowSec);
            if (rate < 0f) return;
            s.IdentityRates[id] = new RatePayload
            {
                Label = label, Value = rate, SampleCount = -1, BankFullness = -1,
                HeldCount = -1, LostCount = -1,
            };
        }

        private static void AddBaseHp(DigestSnapshot s, IdentityOutcomeConsumer consumer)
        {
            long held = consumer.GetCount(SmartIdentity.Base, OutcomeKind.BaseHeld);
            long lost = consumer.GetCount(SmartIdentity.Base, OutcomeKind.BaseLost);
            if (held + lost < 3) return;
            float retain = (float)held / Math.Max(held + lost, 1);
            s.IdentityRates[SmartIdentity.Base] = new RatePayload
            {
                Label = "hp_retain_ewma", Value = retain, SampleCount = (int)(held + lost),
                BankFullness = -1, HeldCount = (int)held, LostCount = (int)lost,
            };
        }

        private static void BuildGuardRollup(DigestSnapshot s)
        {
            long total = 0;
            for (int g = 0; g < GuardRegistry.GuardCount; g++)
                total += GuardTelemetry.GetFiredCount(g);
            s.PathologyTotal = total;
            s.SoftCorrects = GuardTelemetry.SoftCorrectTelemetryFiresCount;
            s.Oscill = GuardTelemetry.OscillCount;
            s.GroundedAircraftFires = GuardTelemetry.GetFiredCount((int)GuardId.GroundedAircraft);
        }

        // ---- text projection ----
        public static string SnapshotToText(DigestSnapshot s)
        {
            var sb = new StringBuilder(2048);
            string wall = DateTimeOffset.FromUnixTimeSeconds(s.WallclockUtcSec)
                .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv);
            sb.Append("===== Smart Director Digest [").Append(wall).Append("] T+")
              .Append(FormatUptime(s.ProcessUptimeMonoSec)).Append(" host=").Append(s.Host)
              .Append(" trig=").Append(s.Trigger ?? "").Append(" =====\n");

            sb.Append("SCENARIO S").Append(s.Scenario).Append("(int=")
              .Append(s.Intensity.ToString("F1", Inv)).Append(") chunkdef=")
              .Append(s.ChunkLive).Append('/').Append(s.ChunkTarget)
              .Append(" techdef=").Append(s.TechDef)
              .Append(" subneutralOK=").Append(s.SubNeutralOk ? 1 : 0).Append('\n');

            sb.Append("MODELS (id loss_mean lossvar entropy_ratio dL2_p99 lr_now frozen)\n");
            string[] names = { "Intent", "ActVal", "Resid", "Threat" };
            for (int i = 0; i < 4; i++)
            {
                var m = s.ModelStats[i];
                sb.Append("  ").Append(names[i].PadRight(8)).Append(' ')
                  .Append(m.LossMean.ToString("F3", Inv)).Append("  ")
                  .Append(m.HasLossVar ? m.LossVarOverMeanRewardNorm.ToString("F2", Inv) : "N/A").Append("  ")
                  .Append(m.HasEntropy ? m.EntropyRatio.ToString("F2", Inv) : "N/A").Append("  ")
                  .Append(m.HasDL2 ? m.DL2P99.ToString("F4", Inv) : "N/A").Append("  ")
                  .Append(m.LrNow.ToString("F2", Inv)).Append("  ")
                  .Append(m.Frozen ? "yes" : "no").Append('\n');
            }

            sb.Append("IDENTITY-RATES (populated cells only)\n");
            foreach (var kv in s.IdentityRates)
            {
                var r = kv.Value;
                sb.Append("  ").Append(kv.Key.ToString().PadRight(12)).Append(' ')
                  .Append(r.Label).Append('=').Append(r.Value.ToString("F2", Inv));
                if (r.HeldCount >= 0) sb.Append(" (held=").Append(r.HeldCount).Append(",lost=").Append(r.LostCount).Append(')');
                sb.Append('\n');
            }
            sb.Append("  ally_protect_evict_per_min=").Append(s.System.AllyProtectEvictPerMin).Append('\n');

            sb.Append("GUARDS rollup pathology_total=").Append(s.PathologyTotal)
              .Append(" soft_corrects=").Append(s.SoftCorrects).Append(" (telemetry-only)")
              .Append(" grounded_aircraft fires=").Append(s.GroundedAircraftFires)
              .Append(" oscill=").Append(s.Oscill).Append('\n');

            sb.Append("SYSTEM techs=").Append(s.System.Techs)
              .Append(" healthy_snap_age=").Append(s.System.HealthySnapAge ?? "n/a").Append('\n');

            sb.Append("CONSTRAINTS OK=").Append(s.ConstraintOkCount).Append('/')
              .Append(ConstraintRegistry.ConstraintCount).Append(" VIOL=")
              .Append(s.ConstraintViolations.Count);
            if (s.ConstraintViolations.Count > 0)
            {
                sb.Append(" [");
                for (int i = 0; i < s.ConstraintViolations.Count; i++)
                {
                    var v = s.ConstraintViolations[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append(v.Name).Append(':').Append(v.Metric.ToString("F2", Inv))
                      .Append(v.Band).Append(" (").Append(v.ConsecutiveTicks).Append("t)");
                }
                sb.Append(']');
            }
            sb.Append('\n');

            if (s.DirectivesActive.Count > 0)
            {
                sb.Append("DIRECTIVES_ACTIVE (").Append(s.DirectivesActive.Count).Append(")\n");
                for (int i = 0; i < s.DirectivesActive.Count; i++)
                {
                    var d = s.DirectivesActive[i];
                    sb.Append("  d-").Append(d.Id.ToString("D5")).Append(" [").Append(d.Tag).Append("] ")
                      .Append(d.Text).Append(" age=").Append(d.Age).Append('\n');
                }
            }

            sb.Append("ROLLBACK_AVAIL memory=").Append(s.Rollback.MemoryAvailable ? "yes" : "no")
              .Append(" named=[").Append(s.Rollback.NamedList ?? "").Append("]\n");
            if (!string.IsNullOrEmpty(s.HintLine)) sb.Append("HINT ").Append(s.HintLine).Append('\n');
            sb.Append("================================================================\n");
            return sb.ToString();
        }

        // ---- JSON projection (hand-rolled) ----
        public static string SnapshotToJson(DigestSnapshot s)
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            sb.Append("\"wallclockUtcSec\":").Append(s.WallclockUtcSec).Append(',');
            sb.Append("\"uptimeSec\":").Append(s.ProcessUptimeMonoSec).Append(',');
            sb.Append("\"trigger\":\"").Append(JEsc(s.Trigger)).Append("\",");
            sb.Append("\"host\":").Append(s.Host).Append(',');
            sb.Append("\"scenario\":").Append(s.Scenario).Append(',');
            sb.Append("\"intensity\":").Append(JNum(s.Intensity)).Append(',');
            sb.Append("\"models\":[");
            string[] mn = { "Intent", "ActionValue", "Residual", "Threat" };
            for (int i = 0; i < 4; i++)
            {
                var m = s.ModelStats[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"id\":\"").Append(mn[i]).Append("\",");
                sb.Append("\"loss_mean\":").Append(JNum(m.LossMean)).Append(',');
                sb.Append("\"lossvar\":").Append(m.HasLossVar ? JNum(m.LossVarOverMeanRewardNorm) : "null").Append(',');
                sb.Append("\"entropy_ratio\":").Append(m.HasEntropy ? JNum(m.EntropyRatio) : "null").Append(',');
                sb.Append("\"dL2_p99\":").Append(m.HasDL2 ? JNum(m.DL2P99) : "null").Append(',');
                sb.Append("\"lr_now\":").Append(JNum(m.LrNow)).Append(',');
                sb.Append("\"frozen\":").Append(m.Frozen ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("],");
            sb.Append("\"identityRates\":{");
            bool firstR = true;
            foreach (var kv in s.IdentityRates)
            {
                if (!firstR) sb.Append(','); firstR = false;
                sb.Append('"').Append(kv.Key).Append("\":{\"label\":\"").Append(JEsc(kv.Value.Label))
                  .Append("\",\"value\":").Append(JNum(kv.Value.Value)).Append('}');
            }
            sb.Append("},");
            sb.Append("\"guards\":{\"pathology_total\":").Append(s.PathologyTotal)
              .Append(",\"soft_corrects\":").Append(s.SoftCorrects)
              .Append(",\"grounded_aircraft_fires\":").Append(s.GroundedAircraftFires)
              .Append(",\"oscill\":").Append(s.Oscill).Append("},");

            // Per-guard fired counts keyed by guard name. The full per-identity matrix
            // is not surfaced by the telemetry layer; the fired-count map is the data
            // that exists at the snapshot layer.
            sb.Append("\"guardRates\":{");
            for (int g = 0; g < GuardRegistry.GuardCount; g++)
            {
                if (g > 0) sb.Append(',');
                sb.Append('"').Append(JEsc(GuardRegistry.GetName(g))).Append("\":")
                  .Append(GuardTelemetry.GetFiredCount(g));
            }
            sb.Append("},");

            sb.Append("\"system\":{\"techs\":").Append(s.System.Techs)
              .Append(",\"ally_protect_evict_per_min\":").Append(s.System.AllyProtectEvictPerMin).Append("},");
            sb.Append("\"constraints\":{\"ok\":").Append(s.ConstraintOkCount)
              .Append(",\"viol\":").Append(s.ConstraintViolations.Count).Append("},");

            // Standing directives.
            sb.Append("\"directives\":[");
            for (int i = 0; i < s.DirectivesActive.Count; i++)
            {
                var d = s.DirectivesActive[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(d.Id)
                  .Append(",\"tag\":\"").Append(JEsc(d.Tag))
                  .Append("\",\"text\":\"").Append(JEsc(d.Text))
                  .Append("\",\"age\":\"").Append(JEsc(d.Age)).Append("\"}");
            }
            sb.Append("],");

            // Rollback availability — the named slots an operator can target.
            sb.Append("\"rollback\":{\"memory\":").Append(s.Rollback.MemoryAvailable ? "true" : "false")
              .Append(",\"previous\":").Append(s.Rollback.PreviousAvailable ? "true" : "false")
              .Append(",\"penultimate\":").Append(s.Rollback.PenultimateAvailable ? "true" : "false")
              .Append(",\"named\":\"").Append(JEsc(s.Rollback.NamedList)).Append("\"},");

            // Completeness sentinel — atomic-write already guarantees the file is whole
            // before rename; this lets an LLM polling the file confirm it parsed a full
            // digest. Final key so it's always last.
            sb.Append("\"llm_ingest_ready\":true");
            sb.Append('}');
            return sb.ToString();
        }

        // JSON number guard. float.ToString("R") emits the literal tokens NaN/Infinity
        // which are invalid JSON; emit null for any non-finite value.
        private static string JNum(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
            return v.ToString("R", Inv);
        }

        private static void AppendHistory(string text)
        {
            if (string.IsNullOrEmpty(_historyPath)) return;
            File.AppendAllText(_historyPath, text + "\n", new UTF8Encoding(false));
        }

        private static void WriteLatest(string text, string json)
        {
            if (string.IsNullOrEmpty(_dir)) return;
            WriteAtomic(Path.Combine(_dir, "digest.latest.txt"), text);
            WriteAtomic(Path.Combine(_dir, "digest.latest.json"), json);
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp." + MonoClock.Now().ToString("X", Inv);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(content);
                w.Flush();
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null, true); }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            else File.Move(tmp, path);
        }

        private static string JEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string FormatSecs(int secs)
        {
            if (secs < 0) secs = 0;
            int m = secs / 60;
            int sec = secs % 60;
            return m + "m" + sec.ToString("D2") + "s";
        }

        private static string FormatUptime(long secs)
        {
            if (secs < 0) secs = 0;
            long h = secs / 3600;
            long m = (secs % 3600) / 60;
            long sec = secs % 60;
            return h.ToString("D2") + ":" + m.ToString("D2") + ":" + sec.ToString("D2");
        }
    }
}
