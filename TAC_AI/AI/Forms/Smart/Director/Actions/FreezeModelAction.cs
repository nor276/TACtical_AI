using System;
using System.Diagnostics;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Actions
{
    /// <summary>
    /// freeze_model verb. Two modes:
    ///   diagnostic (Mode=0, default): leaves typed event queue untouched, lets it
    ///     backpressure-drop at capacity. TrainerWorker checks FrozenUntilTickMono and
    ///     skips Adam.Step + DoubleBuffer.Write.
    ///   soft (Mode=1): drain-to-discard via ILearnedModel.DrainOneMinibatchDiscard so
    ///     queue does not grow unbounded.
    ///
    /// durationSec ∈ [60, 1800] wall-clock seconds; MonoClock=Stopwatch.GetTimestamp() does
    /// NOT stop during game pause so a freeze active during pause expires by wall-clock.
    /// AdamState.T is preserved across freeze (not reset) for bias-correction continuity.
    /// </summary>
    public sealed class FreezeModelAction : IDirectorAction
    {
        public const int MinDurationSec = 60;
        public const int MaxDurationSec = 1800;
        public const byte ModeDiagnostic = 0;
        public const byte ModeSoft = 1;

        public string Verb => "freeze_model";

        public bool TryApply(DirectorActionContext ctx, out JournalEntry entry)
        {
            entry = default(JournalEntry);
            int slot = (int)ctx.Model;
            if ((uint)slot >= 4) return false;

            int durationSec = ctx.IntValue;
            if (durationSec < MinDurationSec) durationSec = MinDurationSec;
            if (durationSec > MaxDurationSec) durationSec = MaxDurationSec;

            var model = ResolveModel(ctx.Model);
            if (model == null) return false;

            long now = MonoClock.Now();
            long deadline = now + (long)(durationSec * Stopwatch.Frequency);
            // Store mode + deadline separately. ModeTable carries mode; the per-model
            // FrozenUntilTickMono setter carries the deadline (already Interlocked-wrapped).
            Interlocked.Exchange(ref ModeTable[slot], (long)ctx.Mode);
            model.FrozenUntilTickMono = deadline;

            string modeName = ctx.Mode == ModeSoft ? "soft" : "diagnostic";
            string body = "model=" + ctx.Model
                + " mode=" + modeName
                + " durationSec=" + durationSec
                + " deadlineMono=" + deadline
                + " constraint=" + ctx.ConstraintName;
            entry = new JournalEntry(now, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "ACTION-FREEZE-MODEL", body);
            DirectorJournal.Append(entry);
            return true;
        }

        public void Undo(JournalEntry entry)
        {
            // Rollback machinery restores via ParamSnapshot.frozenUntilMono.
        }

        public string Describe(DirectorActionContext ctx)
        {
            string m = ctx.Mode == ModeSoft ? "soft" : "diagnostic";
            return "freeze_model " + ctx.Model + " mode=" + m + " for " + ctx.IntValue + "s";
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

        // ---- Mode lookup for OnlineTrainer freeze check ----
        // ModeTable[modelId] = current freeze mode (0=diagnostic, 1=soft). Trainer reads via
        // GetMode below. Reset to diagnostic when no freeze is active.
        private static readonly long[] ModeTable = new long[4];

        /// <summary>
        /// Trainer-side: is this model currently frozen, and in which mode?
        /// Returns true when MonoClock.Now() < FrozenUntilTickMono. mode receives 0=diagnostic / 1=soft.
        /// FrozenUntilTickMono property already wraps Interlocked.Read internally
        /// (OpponentIntentClassifier.cs:81 pattern).
        /// </summary>
        public static bool IsFrozen(ILearnedModel model, out byte mode)
        {
            mode = ModeDiagnostic;
            if (model == null) return false;
            long until = model.FrozenUntilTickMono;
            if (until <= 0L) return false;
            long now = MonoClock.Now();
            if (now >= until) return false;
            int slot = (int)model.Id;
            if ((uint)slot < 4) mode = (byte)Interlocked.Read(ref ModeTable[slot]);
            return true;
        }
    }
}
