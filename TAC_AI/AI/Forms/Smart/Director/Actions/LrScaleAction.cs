using System;
using System.Globalization;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Actions
{
    /// <summary>
    /// lr_scale verb. Multiplicative scale of one model's Adam LR within
    /// [0.05x, 4.0x] of BaseLearningRate. Rate-limited 1/30s per model via
    /// Interlocked CAS on _lastLrScaleMono[modelId].
    ///
    /// Writes route through GetAdamSnapshot + LoadAdamState(new AdamSnapshot(M,V,T,newLR,baseLR));
    /// LoadAdamState takes the model SaveMutex so the swap is race-free against
    /// TrainOneMinibatch.
    /// </summary>
    public sealed class LrScaleAction : IDirectorAction
    {
        public const float MinFactor = 0.05f;
        public const float MaxFactor = 4.0f;
        public const int RateLimitSec = 30;

        // Per-model last-fire wall-clock (MonoClock ticks). 4 slots, one per ModelId.
        private static readonly long[] _lastLrScaleMono = new long[4];

        public string Verb => "lr_scale";

        public bool TryApply(DirectorActionContext ctx, out JournalEntry entry)
        {
            entry = default(JournalEntry);
            int slot = (int)ctx.Model;
            if ((uint)slot >= 4) return false;

            long now = MonoClock.Now();
            long prev = Interlocked.Read(ref _lastLrScaleMono[slot]);
            if (prev != 0L && MonoClock.Seconds(prev, now) < RateLimitSec)
            {
                JournalAppend("ACTION-RATE-LIMITED",
                    "verb=lr_scale model=" + ctx.Model + " sinceLastSec="
                    + MonoClock.Seconds(prev, now).ToString("F1", CultureInfo.InvariantCulture)
                    + " constraint=" + ctx.ConstraintName);
                return false;
            }

            float factor = ctx.Scalar;
            if (float.IsNaN(factor) || float.IsInfinity(factor) || factor <= 0f) return false;
            if (factor < MinFactor) factor = MinFactor;
            if (factor > MaxFactor) factor = MaxFactor;

            var model = ResolveModel(ctx.Model);
            if (model == null) return false;

            // CAS the rate-limit slot — first writer wins. The Apply work itself happens
            // outside the CAS because LoadAdamState is its own SaveMutex.
            if (Interlocked.CompareExchange(ref _lastLrScaleMono[slot], now, prev) != prev)
                return false;

            float priorLr;
            float newLr;
            try
            {
                var snap = model.GetAdamSnapshot();
                priorLr = snap.LR;
                float baseLr = snap.BaseLR > 0f ? snap.BaseLR : snap.LR;
                float lo = baseLr * MinFactor;
                float hi = baseLr * MaxFactor;
                newLr = baseLr * factor;
                if (newLr < lo) newLr = lo;
                if (newLr > hi) newLr = hi;
                model.LoadAdamState(new AdamSnapshot(snap.M, snap.V, snap.T, newLr, snap.BaseLR));
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-lr-scale-throw",
                    "[DIRECTOR-LR-SCALE] " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }

            string body = "model=" + ctx.Model
                + " factor=" + factor.ToString("F3", CultureInfo.InvariantCulture)
                + " priorLR=" + priorLr.ToString("E3", CultureInfo.InvariantCulture)
                + " newLR=" + newLr.ToString("E3", CultureInfo.InvariantCulture)
                + " constraint=" + ctx.ConstraintName;
            entry = new JournalEntry(now, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "ACTION-LR-SCALE", body);
            DirectorJournal.Append(entry);
            return true;
        }

        public void Undo(JournalEntry entry)
        {
            // Rollback machinery restores via ParamSnapshot.lr. Apply already journaled
            // priorLR for replayable diagnosis. Nothing to do here.
        }

        public string Describe(DirectorActionContext ctx)
        {
            return "lr_scale " + ctx.Model + " by " + ctx.Scalar.ToString("F2", CultureInfo.InvariantCulture);
        }

        private static void JournalAppend(string kind, string body)
        {
            DirectorJournal.Append(kind, body);
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
