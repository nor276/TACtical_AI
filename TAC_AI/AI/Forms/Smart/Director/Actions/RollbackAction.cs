using System;
using System.Globalization;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Coordination;
using TAC_AI.AI.Forms.Smart.Director.Guards;
using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Actions
{
    /// <summary>
    /// rollback verb (§7.3). 3-tier revert (memory / disk / named). Single-flight via the
    /// shared TrainingDirector._rollbackInProgress CAS. Pauses trainers via the Director
    /// pause channel, waits for them to quiesce, restores per-model under arch + finite
    /// validation, reverts the active scenario from the journal cursor, resets guard
    /// observation state, then force-journals the post-rollback markers. The DirectiveTable
    /// is intentionally NOT rolled back — STANDING-DIRECTIVES-REAPPLIED warns the operator.
    /// </summary>
    public sealed class RollbackAction : IDirectorAction
    {
        public const byte TierMemory = 0;
        public const byte TierDisk = 1;
        public const byte TierNamed = 2;

        public const int RateLimitSec = 300;        // 1 per 5 min, global
        public const int TrainerDrainTimeoutMs = 5000;
        private const string Owner = "Director";

        private static long _lastRollbackMono;

        public string Verb => "rollback";

        public bool TryApply(DirectorActionContext ctx, out JournalEntry entry)
        {
            entry = default(JournalEntry);

            // Global rate-limit: 1 per 5 min.
            long now = MonoClock.Now();
            long prev = Interlocked.Read(ref _lastRollbackMono);
            if (prev != 0L && MonoClock.Seconds(prev, now) < RateLimitSec)
            {
                DirectorJournal.Append("ROLLBACK-RATE-LIMITED",
                    "sinceLastSec=" + MonoClock.Seconds(prev, now).ToString("F1", CultureInfo.InvariantCulture)
                    + " constraint=" + ctx.ConstraintName);
                return false;
            }

            // Single-flight gate — one rollback at a time across the whole Director.
            if (!TrainingDirector.TryEnterRollbackSingleFlight())
            {
                DirectorJournal.Append("ROLLBACK-REJECTED-INFLIGHT", "constraint=" + ctx.ConstraintName);
                return false;
            }

            string tier = TierName(ctx.Mode);
            bool didWork = false;
            try
            {
                DirectorJournal.Append("ROLLBACK", "started tier=" + tier + " constraint=" + ctx.ConstraintName);

                // Implicit pre-rollback checkpoint so rollback-of-rollback works (disk/named only).
                if (ctx.Mode != TierMemory)
                {
                    try { DirectorParamRing.CapturePreAction("pre-rollback"); }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarnFileOnly("rollback-pre-checkpoint",
                            "[DIRECTOR-ROLLBACK] pre-rollback capture threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                SmartRuntime.AcquirePauseToken(Owner);
                try
                {
                    SmartRuntime.RequestDirectorPause();

                    if (!TrainerBarrier.WaitAll(TrainerDrainTimeoutMs))
                    {
                        DirectorJournal.Append("ROLLBACK-ABORTED-WAIT-TIMEOUT",
                            "trainerDrainTimeoutMs=" + TrainerDrainTimeoutMs + " constraint=" + ctx.ConstraintName);
                        // Force-journal the abort digest trigger; finally releases pause/token.
                        DirectorJournal.Append("DIGEST-TRIGGER", "trig=TRIG_ROLLBACK_ABORT reason=trainer-drain-timeout");
                        return false;
                    }

                    SmartRuntime.AcceptingTrainingEvents_Dec();
                    try
                    {
                        didWork = RestoreModels(ctx.Mode, tier);

                        // Active scenario revert + invalidate in-flight spawn envelopes.
                        Interlocked.Increment(ref DirectorState.ScenarioGeneration);
                        RestoreActiveScenarioFromJournal(now);

                        // Guard observation state reset (step 6.5).
                        ResetGuardObservationState();
                    }
                    finally
                    {
                        SmartRuntime.AcceptingTrainingEvents_Inc();
                    }
                }
                finally
                {
                    SmartRuntime.ReleaseDirectorPause();
                    SmartRuntime.ReleasePauseToken(Owner);
                    // Re-arm the barriers so the NEXT pause cycle (rollback or host-loss)
                    // actually waits instead of returning on stale already-signaled events.
                    try { TrainerBarrier.Reset(); }
                    catch (Exception ex)
                    {
                        DebugTAC_AI.LogWarnFileOnly("rollback-barrier-reset",
                            "[DIRECTOR-ROLLBACK] TrainerBarrier.Reset threw " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                Interlocked.Exchange(ref _lastRollbackMono, now);

                string body = "tier=" + tier + " restored=" + didWork + " constraint=" + ctx.ConstraintName;
                entry = new JournalEntry(now, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "ROLLBACK-COMPLETED", body);
                DirectorJournal.Append(entry);
                // Force-emit post-rollback digest markers (bypasses IsPaused gate — they are
                // plain journal writes until DigestBuilder ships in a later phase).
                DirectorJournal.Append("DIGEST-TRIGGER", "trig=TRIG_ROLLBACK");
                DirectorJournal.Append("STANDING-DIRECTIVES-REAPPLIED",
                    "directiveCount=" + DirectorState.DirectiveTable.Count
                    + " (DirectiveTable is not rolled back; standing directives re-clobber restored state)");
                DirectorJournal.Append("GUARD-WINDOWS-RESET", "post-rollback");
                return didWork;
            }
            catch (ThreadAbortException)
            {
                AbortGuard.Absorb("Director-Rollback");
                DirectorJournal.Append("ROLLBACK-ABORTED-TAE", "constraint=" + ctx.ConstraintName);
                return false;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-rollback-throw",
                    "[DIRECTOR-ROLLBACK] " + ex.GetType().Name + ": " + ex.Message);
                DirectorJournal.Append("ROLLBACK-FAILED", "err=" + ex.Message + " constraint=" + ctx.ConstraintName);
                return false;
            }
            finally
            {
                TrainingDirector.ExitRollbackSingleFlight();
            }
        }

        // Per-model restore with INDIVIDUAL try/catch — a model-2 fault does not abort the
        // loop after model-1 already restored. memory tier reads the T1 ring; disk/named
        // tiers route through ProfilePersistence.
        private static bool RestoreModels(byte tierMode, string tier)
        {
            bool any = false;
            for (int m = 0; m < DirectorParamRing.ModelCount; m++)
            {
                ILearnedModel model = ResolveModel(m);
                if (model == null) continue;
                try
                {
                    if (tierMode == TierMemory)
                    {
                        ParamSnapshot snap = DirectorParamRing.LatestHealthy(m);
                        if (snap == null || !snap.Populated)
                        {
                            DirectorJournal.Append("ROLLBACK-SKIPPED-NO-SNAPSHOT", "model=" + model.Id);
                            continue;
                        }
                        // Arch validate BEFORE LoadParameters (which only length-checks).
                        if (snap.ArchitectureVersion != model.ArchitectureVersion)
                        {
                            DirectorJournal.Append("ROLLBACK-SKIPPED-ARCH",
                                "model=" + model.Id + " snap=" + snap.ArchitectureVersion + " live=" + model.ArchitectureVersion);
                            continue;
                        }
                        // Finite/range validation on the tuning snapshot — guards persisted NaN/zero.
                        if (!IsFinite(snap.intentTemp) || snap.intentTemp <= 0f || snap.adamT < 0 || snap.lr <= 0f)
                        {
                            DirectorJournal.Append("ROLLBACK-SKIPPED-INVALID-TUNING", "model=" + model.Id);
                            continue;
                        }
                        model.LoadParameters(snap.paramsCopy);
                        model.LoadAdamState(new AdamSnapshot(snap.adamM, snap.adamV, snap.adamT, snap.lr, snap.baseLr));
                        // Restore per-model LR scale + frozen ticket from the snapshot.
                        DirectorTuning.SetLrScale(m, snap.lrScales != null && m < snap.lrScales.Length ? snap.lrScales[m] : 1f);
                        model.FrozenUntilTickMono = snap.frozenUntilMono;
                        DirectorJournal.Append("ROLLBACK-MODEL-RESTORED", "model=" + model.Id + " tier=memory");
                        any = true;
                    }
                    else
                    {
                        if (RestoreModelFromDisk(model))
                        {
                            DirectorJournal.Append("ROLLBACK-MODEL-RESTORED", "model=" + model.Id + " tier=" + tier);
                            any = true;
                        }
                        else
                        {
                            DirectorJournal.Append("ROLLBACK-SKIPPED-NO-DISK", "model=" + model.Id + " tier=" + tier);
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    AbortGuard.Absorb("Director-Rollback");
                    DirectorJournal.Append("ROLLBACK-ABORTED-TAE", "model=" + model.Id);
                    break;
                }
                catch (Exception ex)
                {
                    DirectorJournal.Append("ROLLBACK-FAILED-PARAM-LOAD", "model=" + model.Id + " err=" + ex.Message);
                }
            }

            // memory-tier: also restore the global tuning snapshot from the freshest model
            // snapshot (intentTemp + outcomeWeights are global, captured per-trio).
            if (tierMode == TierMemory)
            {
                var lead = DirectorParamRing.LatestHealthy(0);
                if (lead != null && lead.Populated && IsFinite(lead.intentTemp) && lead.intentTemp > 0f)
                {
                    lock (DirectorTuning.TuningSnapshotLock)
                    {
                        LearningTuning.IntentTemperature = lead.intentTemp;
                        DirectorTuning.IntentTemperature = lead.intentTemp;
                        if (lead.outcomeWeights != null)
                        {
                            int n = Math.Min(lead.outcomeWeights.Length, DirectorTuning.OutcomeWeights.Length);
                            for (int i = 0; i < n; i++) DirectorTuning.SetOutcomeWeight(i, lead.outcomeWeights[i]);
                        }
                    }
                }
            }
            return any;
        }

        // Disk-tier restore: profile primary, then .previous, then .penultimate.
        private static bool RestoreModelFromDisk(ILearnedModel model)
        {
            string path = LearningService.RollbackProfilePath();
            if (string.IsNullOrEmpty(path)) return false;
            if (ProfilePersistence.RestoreFromCheckpoint(path, "", model)) return true;
            if (ProfilePersistence.RestoreFromCheckpoint(path, ".previous", model)) return true;
            if (ProfilePersistence.RestoreFromCheckpoint(path, ".penultimate", model)) return true;
            return false;
        }

        // Step 6.5. Clears guard observation windows + releases every hard-correct mailbox
        // slot so a rollback does not re-teach guard-flagged lessons (V2 fix). The actuator's
        // own per-tech cooldowns re-arm naturally if conditions persist post-rollback.
        private static void ResetGuardObservationState()
        {
            try { GuardWindowPool.Clear(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("rollback-guard-clear",
                    "[DIRECTOR-ROLLBACK] GuardWindowPool.Clear threw " + ex.GetType().Name + ": " + ex.Message);
            }
            try { GuardCorrectiveActuator.ForceReleaseAll(); }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("rollback-guard-actuator",
                    "[DIRECTOR-ROLLBACK] ForceReleaseAll threw " + ex.GetType().Name + ": " + ex.Message);
            }
            DirectorJournal.Append("GUARD-WINDOWS-RESET",
                "windows=cleared actuator=released counters=n/a cooldowns=re-arm-on-persist");
        }

        // Step 7. Revert the active scenario to the most recent ACTION-SCENARIO-SET in the
        // in-mem journal at-or-before the rollback instant. monoTick within the same process.
        private static void RestoreActiveScenarioFromJournal(long atMono)
        {
            int restoreId = 0;
            float restoreIntensity = 1f;
            long bestMono = 0L;
            foreach (var je in DirectorState.JournalRingInMem)
            {
                if (je.Kind != "ACTION-SCENARIO-SET") continue;
                if (je.MonoTick > atMono) continue;
                if (je.MonoTick < bestMono) continue;
                int id;
                float intensity;
                if (TryParseScenarioBody(je.Body, out id, out intensity))
                {
                    bestMono = je.MonoTick;
                    restoreId = id;
                    restoreIntensity = intensity;
                }
            }
            if (restoreId <= 0)
            {
                DirectorJournal.Append("ROLLBACK-SCENARIO-NOCURSOR",
                    "no prior scenario_set in journal — active scenario unchanged");
                return;
            }
            bool ok = ScenarioWorker.RequestScenarioSet(restoreId, restoreIntensity, UnityEngine.Vector3.zero);
            DirectorJournal.Append("ROLLBACK-SCENARIO-REVERTED",
                "id=" + restoreId + " intensity=" + restoreIntensity.ToString("F2", CultureInfo.InvariantCulture)
                + " ok=" + ok);
        }

        private static bool TryParseScenarioBody(string body, out int id, out float intensity)
        {
            id = 0;
            intensity = 1f;
            if (string.IsNullOrEmpty(body)) return false;
            id = ParseIntField(body, "id=");
            float parsed;
            if (TryParseFloatField(body, "intensity=", out parsed)) intensity = parsed;
            return id > 0;
        }

        private static int ParseIntField(string body, string key)
        {
            int k = body.IndexOf(key, StringComparison.Ordinal);
            if (k < 0) return 0;
            int start = k + key.Length;
            int end = start;
            while (end < body.Length && body[end] != ' ') end++;
            int val;
            return int.TryParse(body.Substring(start, end - start), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out val) ? val : 0;
        }

        private static bool TryParseFloatField(string body, string key, out float value)
        {
            value = 0f;
            int k = body.IndexOf(key, StringComparison.Ordinal);
            if (k < 0) return false;
            int start = k + key.Length;
            int end = start;
            while (end < body.Length && body[end] != ' ') end++;
            return float.TryParse(body.Substring(start, end - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        private static bool IsFinite(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v);
        }

        private static string TierName(byte mode)
        {
            switch (mode)
            {
                case TierMemory: return "memory";
                case TierDisk: return "disk";
                case TierNamed: return "named";
            }
            return "memory";
        }

        public void Undo(JournalEntry entry)
        {
            // Rollback is itself the undo path. The implicit pre-rollback pre-action
            // snapshot (disk/named) lets a subsequent rollback revert this one.
        }

        public string Describe(DirectorActionContext ctx)
        {
            return "rollback tier=" + TierName(ctx.Mode);
        }

        private static ILearnedModel ResolveModel(int modelId)
        {
            switch ((ModelId)modelId)
            {
                case ModelId.Intent: return LearningService.Intent;
                case ModelId.ActionValue: return LearningService.ActionValue;
                case ModelId.TrajectoryResidual: return LearningService.Residual;
                case ModelId.ThreatAssessment: return LearningService.Threat;
            }
            return null;
        }
    }
}
