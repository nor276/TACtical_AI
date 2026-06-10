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
    /// Per TRAINING-CONTRACT §6. After CMA-ES has produced a converged μ, run a long
    /// batch of matches with online learning enabled, then persist the resulting model
    /// weights as a baseline file via the same on-disk format as a per-player profile.
    ///
    /// HONEST SCOPE:
    ///   - Drives <see cref="TrainingMatch.SimulateCoroutineFull"/> against the live
    ///     LearningService singletons; the model deltas accumulate in those singletons'
    ///     trainer-worker queues exactly as during normal play, then <see cref="ProfilePersistence.Save"/>
    ///     writes the current parameter vectors to <c>baselinePath</c>.
    ///   - NO multi-instance model averaging. The "two-instance per-parameter average"
    ///     described in earlier docs was never wired (TrainingMatch never instantiates a
    ///     second Smart side); the older AverageInto helper has been removed.
    ///   - No baseline metadata footer ships in the file; provenance (CMA seed, generation
    ///     count, score weights) lives only in the calling dev's log line.
    /// </summary>
    public sealed class PretrainingPipeline
    {
        public int TrainingMatchCount { get; set; } = 1000;
        public bool Headless { get; set; } = true;
        public OutcomeScorer.Weights ScoreWeights { get; set; } = OutcomeScorer.Weights.Provisional;

        /// <summary>
        /// Coroutine-driven long batch. Yields one TrainingMatch per FixedUpdate-driven
        /// simulation through the supplied host MonoBehaviour, then persists the model
        /// weights to <paramref name="baselinePath"/>. Returns the number of matches
        /// that produced a non-null outcome via <paramref name="onComplete"/>.
        /// </summary>
        public IEnumerator RunCoroutine(
            MonoBehaviour host,
            SelfPlayHarness harness,
            string baselinePath,
            CancellationToken cancellation,
            Action<int> onComplete)
        {
            if (host == null) { onComplete?.Invoke(0); yield break; }
            if (harness == null) { onComplete?.Invoke(0); yield break; }

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
                DebugTAC_AI.Log("Smart.Training.Pretraining: saved baseline to " + baselinePath
                                + " (matches=" + matchesRun + " libRate=" + libRate.ToString("F3") + ")");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Training.Pretraining: baseline save failed: " + ex.Message);
            }

            onComplete?.Invoke(matchesRun);
        }
    }
}
#endif
