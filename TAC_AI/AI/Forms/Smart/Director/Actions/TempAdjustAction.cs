using System;
using System.Globalization;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Actions
{
    /// <summary>
    /// temp_adjust verb. Intent-only inference-side stochasticity. Writes to
    /// LearningTuning.IntentTemperature (volatile float; ECMA-335 permits volatile float).
    /// Intent-only rejection: SamplingMPC.Sigma* perturbs control vectors only and has no
    /// causal link to Q-net loss-variance. ctx.Scalar carries a log-tau delta in
    /// [-0.5, +0.5]; newTau = priorTau * exp(delta) clamped to [0.25, 4.0]. Trainer
    /// captures tau once per batch into a local so writes during a batch take effect on
    /// the NEXT batch.
    /// </summary>
    public sealed class TempAdjustAction : IDirectorAction
    {
        public const float MinTau = 0.25f;
        public const float MaxTau = 4.0f;
        public const float MinDelta = -0.5f;
        public const float MaxDelta = 0.5f;

        public string Verb => "temp_adjust";

        public bool TryApply(DirectorActionContext ctx, out JournalEntry entry)
        {
            entry = default(JournalEntry);
            if (ctx.Model != ModelId.Intent)
            {
                DirectorJournal.Append("ACTION-REJECTED",
                    "verb=temp_adjust reason=intent-only model=" + ctx.Model
                    + " constraint=" + ctx.ConstraintName);
                return false;
            }

            float delta = ctx.Scalar;
            if (float.IsNaN(delta) || float.IsInfinity(delta)) return false;
            if (delta < MinDelta) delta = MinDelta;
            if (delta > MaxDelta) delta = MaxDelta;

            float priorTau = LearningTuning.IntentTemperature;
            if (priorTau <= 0f || float.IsNaN(priorTau)) priorTau = 1f;
            float newTau = priorTau * (float)Math.Exp(delta);
            if (newTau < MinTau) newTau = MinTau;
            if (newTau > MaxTau) newTau = MaxTau;

            LearningTuning.IntentTemperature = newTau;

            long now = MonoClock.Now();
            string body = "model=Intent delta=" + delta.ToString("F3", CultureInfo.InvariantCulture)
                + " priorTau=" + priorTau.ToString("F3", CultureInfo.InvariantCulture)
                + " newTau=" + newTau.ToString("F3", CultureInfo.InvariantCulture)
                + " constraint=" + ctx.ConstraintName;
            entry = new JournalEntry(now, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "ACTION-TEMP-ADJUST", body);
            DirectorJournal.Append(entry);
            return true;
        }

        public void Undo(JournalEntry entry)
        {
            // Rollback machinery restores via ParamSnapshot.intentTemp; nothing here.
        }

        public string Describe(DirectorActionContext ctx)
        {
            return "temp_adjust Intent log-tau " + ctx.Scalar.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
