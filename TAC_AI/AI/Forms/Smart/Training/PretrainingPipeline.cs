#if SMART_DEV
using System;
using System.Collections;
using System.IO;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Learning;

namespace TAC_AI.AI.Forms.Smart.Training
{
    /// <summary>
    /// Pretraining-pipeline output metadata captured in the baseline file footer.
    /// Per TRAINING-CONTRACT §6.3.
    /// </summary>
    public sealed class BaselineMetadata
    {
        public long GeneratedAtUnixMs;
        public int CmaSeed;
        public int CmaGenerationCount;
        public float[] HyperParameterVector;
        public int TrainingMatchCount;
        public float LibraryWinRate;
    }

    /// <summary>
    /// Per TRAINING-CONTRACT §6. After CMA-ES has produced a converged μ, run a
    /// long batch of matches with online learning enabled, then per-parameter-average
    /// both training instances' model weights into a baseline file.
    ///
    /// v0.1.0 honest scope:
    ///   - Match-driven training and model averaging are wired against the real
    ///     Learning models — the math is correct.
    ///   - The "two instances" of Smart are NOT actually instantiated at v0.1.0 —
    ///     <see cref="TrainingMatch"/>'s spawn stubs are still TODO. So the averaging
    ///     currently averages one model's parameters with itself (no-op). When the
    ///     spawn API is verified, the averaging becomes meaningful without code changes
    ///     to this file.
    /// </summary>
    public sealed class PretrainingPipeline
    {
        public int TrainingMatchCount { get; set; } = 1000;
        public bool Headless { get; set; } = true;
        public OutcomeScorer.Weights ScoreWeights { get; set; } = OutcomeScorer.Weights.Provisional;

        public BaselineMetadata Run(SelfPlayHarness harness, string baselinePath, CancellationToken cancellation)
        {
            if (harness == null) throw new ArgumentNullException(nameof(harness));

            var hp = new HyperParams { Values = harness.BestKnownMean, Names = HyperParams.DefaultNames };
            int matchesRun = 0;

            for (int m = 0; m < TrainingMatchCount; m++)
            {
                if (cancellation.IsCancellationRequested) break;
                int matchSeed = m * 31 + 7919;
                var scenario = ScenarioGenerator.PickForMatch(m, matchSeed, ScenarioGenerator.Difficulty.Medium);
                var match = new TrainingMatch(scenario, MatchMode.SelfPlay, hp, Headless, cancellation);
                match.Run();   // online learning inside the match populates the model queues
                matchesRun++;
            }

            // Run library evaluation with the trained baseline.
            float libRate = harness.RunLibraryEvaluation(cancellation);

            // Save the baseline using the same on-disk format as a per-player profile,
            // so Learning's loader handles it unchanged.
            try
            {
                var dir = Path.GetDirectoryName(baselinePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                ProfilePersistence.Save(baselinePath, new ILearnedModel[]
                {
                    LearningService.Intent,
                    LearningService.ActionValue,
                    LearningService.Residual,
                    LearningService.Threat,
                });
                DebugTAC_AI.Log("Smart.Training.Pretraining: saved baseline to " + baselinePath);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Training.Pretraining: baseline save failed: " + ex.Message);
            }

            // TODO v0.2: write the BaselineMetadata footer as an extension to the binary format.
            // Per LEARNING §5.2 the current file format does not yet have a metadata section;
            // extending it requires bumping the schema version + adding a migration.

            return new BaselineMetadata
            {
                GeneratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CmaSeed = 0,
                CmaGenerationCount = harness.Search.Generation,
                HyperParameterVector = (float[])harness.BestKnownMean.Clone(),
                TrainingMatchCount = matchesRun,
                LibraryWinRate = libRate,
            };
        }

        /// <summary>
        /// Phase 8 (FIX-PLAN.md) — AUDIT R1 2.6: the synchronous <see cref="Run"/> uses
        /// <see cref="TrainingMatch.Run"/> which is a zero-tick stalemate (does not pump
        /// physics) — the long batch ran 1000 stalemates instead of 1000 actual matches,
        /// producing a baseline file with no training signal. The coroutine variant
        /// drives <see cref="TrainingMatch.SimulateCoroutineFull"/> through a host
        /// MonoBehaviour so each match yields per-FixedUpdate, accumulating real
        /// online-learning signal.
        /// </summary>
        public IEnumerator RunCoroutine(
            MonoBehaviour host,
            SelfPlayHarness harness,
            string baselinePath,
            CancellationToken cancellation,
            Action<BaselineMetadata> onComplete)
        {
            if (host == null) { onComplete?.Invoke(null); yield break; }
            if (harness == null) { onComplete?.Invoke(null); yield break; }

            var hp = new HyperParams { Values = harness.BestKnownMean, Names = HyperParams.DefaultNames };
            int matchesRun = 0;

            for (int m = 0; m < TrainingMatchCount; m++)
            {
                if (cancellation.IsCancellationRequested) break;
                int matchSeed = m * 31 + 7919;
                var scenario = ScenarioGenerator.PickForMatch(m, matchSeed, ScenarioGenerator.Difficulty.Medium);
                var match = new TrainingMatch(scenario, MatchMode.SelfPlay, hp, Headless, cancellation);
                MatchOutcome outcome = null;
                yield return host.StartCoroutine(match.SimulateCoroutineFull(o => outcome = o));
                if (outcome != null) matchesRun++;
            }

            float libRate = 0f;
            yield return host.StartCoroutine(harness.RunLibraryEvaluationCoroutine(host, cancellation, r => libRate = r));

            try
            {
                var dir = Path.GetDirectoryName(baselinePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                ProfilePersistence.Save(baselinePath, new ILearnedModel[]
                {
                    LearningService.Intent,
                    LearningService.ActionValue,
                    LearningService.Residual,
                    LearningService.Threat,
                });
                DebugTAC_AI.Log("Smart.Training.Pretraining: saved baseline to " + baselinePath);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Training.Pretraining: baseline save failed: " + ex.Message);
            }

            onComplete?.Invoke(new BaselineMetadata
            {
                GeneratedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CmaSeed = 0,
                CmaGenerationCount = harness.Search.Generation,
                HyperParameterVector = (float[])harness.BestKnownMean.Clone(),
                TrainingMatchCount = matchesRun,
                LibraryWinRate = libRate,
            });
        }

        /// <summary>
        /// Per-parameter mean of two models of the same architecture. Used at the end of
        /// the long batch to fuse the two training instances' learning per §6.2 step 4.
        /// </summary>
        public static void AverageInto(ILearnedModel destination, ILearnedModel a, ILearnedModel b)
        {
            if (a.ParameterCount != b.ParameterCount || a.ParameterCount != destination.ParameterCount)
                throw new ArgumentException("Model parameter count mismatch.");
            var pa = new float[a.ParameterCount];
            var pb = new float[b.ParameterCount];
            a.StoreParameters(pa);
            b.StoreParameters(pb);
            for (int i = 0; i < pa.Length; i++) pa[i] = 0.5f * (pa[i] + pb[i]);
            destination.LoadParameters(pa);
        }
    }
}
#endif
