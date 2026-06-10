using System;
using System.Globalization;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Actions
{
    /// <summary>
    /// replay_bank verb. Re-enqueues up to N typed training events for one
    /// (SmartIdentity, ModelId) cell. Drain uses Monitor.TryEnter(model.SaveMutex, 100ms);
    /// miss journals SKIPPED. BankFullness gate journals SKIPPED-INSUFFICIENT when below
    /// minReplay. Per-envelope multiplicity cap = 2 (enforced inside IdentityReplayBank).
    /// Replayed envelopes carry Source=Replay so IdentityOutcomeConsumer ignores them.
    /// </summary>
    public sealed class ReplayBankAction : IDirectorAction
    {
        public const int MinCount = 16;
        public const int MaxCount = 512;
        public const int MinReplay = 16;
        public const int SaveMutexTryEnterMs = 100;

        public string Verb => "replay_bank";

        public bool TryApply(DirectorActionContext ctx, out JournalEntry entry)
        {
            entry = default(JournalEntry);
            int count = ctx.IntValue;
            if (count < MinCount) count = MinCount;
            if (count > MaxCount) count = MaxCount;

            // Bail immediately if shutting down so QuitSaveCoordinator's FlushPendingForPersist
            // is not blocked.
            if (TrainingDirector.IsKillSwitchSet)
            {
                DirectorJournal.Append("ACTION-SKIPPED",
                    "verb=replay_bank reason=shutdown identity=" + ctx.Identity + " model=" + ctx.Model);
                return false;
            }

            // BankFullness gate: SKIPPED-INSUFFICIENT when below minReplay.
            int fullness = IdentityReplayBank.BankFullness(ctx.Identity, ctx.Model);
            if (fullness < MinReplay)
            {
                DirectorJournal.Append("ACTION-SKIPPED-INSUFFICIENT",
                    "verb=replay_bank identity=" + ctx.Identity + " model=" + ctx.Model
                    + " bankFullness=" + fullness + " minReplay=" + MinReplay
                    + " constraint=" + ctx.ConstraintName);
                return false;
            }

            var model = ResolveModel(ctx.Model);
            if (model == null) return false;

            int drained = 0;
            bool gotLock = Monitor.TryEnter(model.SaveMutex, SaveMutexTryEnterMs);
            if (!gotLock)
            {
                DirectorJournal.Append("ACTION-SKIPPED",
                    "verb=replay_bank reason=savemutex-busy identity=" + ctx.Identity
                    + " model=" + ctx.Model + " constraint=" + ctx.ConstraintName);
                return false;
            }
            try
            {
                // Drain bails immediately if the kill switch flipped while we waited for the
                // lock. Replayed envelopes do not call any second model API that takes a
                // separate lock — only ConcurrentQueue.Enqueue on model.EventQueue.
                if (TrainingDirector.IsKillSwitchSet) return false;
                switch (ctx.Model)
                {
                    case ModelId.Intent:
                        drained = IdentityReplayBank.DrainIntent(ctx.Identity, (OpponentIntentClassifier)model, count);
                        break;
                    case ModelId.ActionValue:
                        drained = IdentityReplayBank.DrainActionValue(ctx.Identity, (ActionValueEstimator)model, count);
                        break;
                    case ModelId.TrajectoryResidual:
                        drained = IdentityReplayBank.DrainResidual(ctx.Identity, (TrajectoryResidualModel)model, count);
                        break;
                    case ModelId.ThreatAssessment:
                        drained = IdentityReplayBank.DrainThreat(ctx.Identity, (ThreatAssessmentModel)model, count);
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-replay-bank-throw",
                    "[DIRECTOR-REPLAY-BANK] " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            finally
            {
                Monitor.Exit(model.SaveMutex);
            }

            long now = MonoClock.Now();
            string body = "identity=" + ctx.Identity
                + " model=" + ctx.Model
                + " requested=" + count
                + " drained=" + drained
                + " constraint=" + ctx.ConstraintName;
            entry = new JournalEntry(now, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "ACTION-REPLAY-BANK", body);
            DirectorJournal.Append(entry);
            return drained > 0;
        }

        public void Undo(JournalEntry entry)
        {
            // Irreversible — events fold into Adam.
        }

        public string Describe(DirectorActionContext ctx)
        {
            return "replay_bank " + ctx.Identity + ":" + ctx.Model + " count=" + ctx.IntValue;
        }

        private static ILearnedModel ResolveModel(ModelId id)
        {
            switch (id)
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
