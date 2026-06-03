#if SMART_DEV
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Training
{
    /// <summary>
    /// Top-level self-play orchestrator. Per TRAINING-CONTRACT §2.
    ///
    /// Drives the CMA-ES outer loop:
    ///   for each generation:
    ///     sample population
    ///     for each candidate:
    ///       evaluate via N matches (mostly procedural self-play, occasional library benchmark)
    ///       aggregate score
    ///     update CMA-ES distribution
    ///     periodically run library evaluation against current μ
    ///     checkpoint
    ///
    /// Lives in dev builds only (`#if SMART_DEV`). Driven from a developer-tool entry
    /// point (not yet authored — TODO v0.2: tool menu integration).
    /// </summary>
    public sealed class SelfPlayHarness
    {
        public int EvalMatchesPerCandidate { get; set; } = 10;
        public int LibraryEvaluationEveryNGenerations { get; set; } = 5;
        public int BaselineBenchmarkEveryNMatches { get; set; } = 20;
        public bool Headless { get; set; } = true;
        public OutcomeScorer.Weights ScoreWeights { get; set; } = OutcomeScorer.Weights.Provisional;

        public EvolutionarySearch Search { get; }
        public int MatchCounter { get; private set; }
        public int BestKnownGeneration { get; private set; }
        public float BestKnownMeanScore { get; private set; } = float.MinValue;
        public float[] BestKnownMean { get; private set; }

        // Plateau detection per §5.4.
        private readonly Queue<float> _libraryHistory = new Queue<float>();
        public int PlateauWindow { get; set; } = 10;

        public SelfPlayHarness(EvolutionarySearch search)
        {
            Search = search;
            BestKnownMean = (float[])search.Mean.Clone();
        }

        public static SelfPlayHarness FromDefaults(int seed = 12345)
        {
            return new SelfPlayHarness(EvolutionarySearch.FromDefaults(seed));
        }

        /// <summary>
        /// Coroutine-driven entry point. Starts the harness loop on
        /// <paramref name="host"/> and returns the <see cref="Coroutine"/> handle so the
        /// caller can monitor or stop it. Per TRAINING-CONTRACT §2.2 + §2.4 — the
        /// MonoBehaviour host pumps Unity FixedUpdate ticks between match steps.
        /// </summary>
        public Coroutine BeginAsync(MonoBehaviour host, int maxGenerations, CancellationToken cancellation)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            return host.StartCoroutine(RunCoroutine(host, maxGenerations, cancellation));
        }

        /// <summary>
        /// Per FIX-PLAN.md Phase 2.4: parameterless entry that lazily creates a
        /// <see cref="SmartTrainingDriver"/> MonoBehaviour host on a hidden GameObject,
        /// then forwards to <see cref="BeginAsync(MonoBehaviour,int,CancellationToken)"/>.
        /// This is the canonical "developer presses Start" entry — no caller-side
        /// MonoBehaviour authoring required.
        /// </summary>
        public Coroutine BeginAsync(int maxGenerations, CancellationToken cancellation)
        {
            var driver = SmartTrainingDriver.GetOrCreate();
            return BeginAsync(driver, maxGenerations, cancellation);
        }

        /// <summary>
        /// Coroutine body. Mirrors <see cref="Run"/>'s outer loop but yields each match
        /// through to Unity so physics actually ticks between fitness samples.
        /// </summary>
        public IEnumerator RunCoroutine(MonoBehaviour host, int maxGenerations, CancellationToken cancellation)
        {
            for (int gen = 0; gen < maxGenerations; gen++)
            {
                if (cancellation.IsCancellationRequested) yield break;
                if (Search.Converged()) { DebugTAC_AI.Log("Smart.Training: CMA-ES converged at gen " + Search.Generation); yield break; }

                var population = Search.SamplePopulation();
                var fitness = new float[population.Length];
                for (int p = 0; p < population.Length; p++)
                {
                    if (cancellation.IsCancellationRequested) yield break;
                    float candidateScore = 0f;
                    int matchesEvaluated = 0;

                    var hp = new HyperParams { Values = population[p], Names = HyperParams.DefaultNames };
                    for (int m = 0; m < EvalMatchesPerCandidate; m++)
                    {
                        if (cancellation.IsCancellationRequested) yield break;
                        MatchMode mode = (MatchCounter % BaselineBenchmarkEveryNMatches == BaselineBenchmarkEveryNMatches - 1)
                            ? MatchMode.BaselineBenchmark
                            : MatchMode.SelfPlay;
                        int matchSeed = MatchCounter * 31 + Search.Generation;
                        var scenario = ScenarioGenerator.PickForMatch(MatchCounter, matchSeed, ScenarioGenerator.Difficulty.Medium);
                        var match = new TrainingMatch(scenario, mode, hp, Headless, cancellation);

                        MatchOutcome outcome = null;
                        yield return host.StartCoroutine(match.SimulateCoroutineFull(o => outcome = o));
                        if (outcome != null)
                        {
                            candidateScore += OutcomeScorer.Score(outcome, ScoreWeights);
                            matchesEvaluated++;
                        }
                        MatchCounter++;
                    }
                    fitness[p] = matchesEvaluated > 0 ? candidateScore / matchesEvaluated : 0f;
                }

                Search.Update(population, fitness);

                float meanFit = 0f, bestFit = float.MinValue;
                for (int p = 0; p < fitness.Length; p++)
                {
                    meanFit += fitness[p];
                    if (fitness[p] > bestFit) bestFit = fitness[p];
                }
                meanFit /= fitness.Length;

                DebugTAC_AI.Log("Smart.Training: gen=" + Search.Generation
                                + " meanScore=" + meanFit.ToString("F3")
                                + " bestScore=" + bestFit.ToString("F3"));

                if (meanFit > BestKnownMeanScore)
                {
                    BestKnownMeanScore = meanFit;
                    BestKnownGeneration = Search.Generation;
                    BestKnownMean = (float[])Search.Mean.Clone();
                }

                if ((Search.Generation % LibraryEvaluationEveryNGenerations) == 0)
                {
                    float libRate = 0f;
                    yield return host.StartCoroutine(RunLibraryEvaluationCoroutine(host, cancellation, r => libRate = r));
                    DebugTAC_AI.Log("Smart.Training: library_winrate=" + libRate.ToString("F3"));
                    if (IsLibraryPlateaued(libRate))
                    {
                        DebugTAC_AI.Log("Smart.Training: library plateau detected; halting.");
                        yield break;
                    }
                }
            }
        }

        /// <summary>Coroutine variant of <see cref="RunLibraryEvaluation"/>.</summary>
        public IEnumerator RunLibraryEvaluationCoroutine(MonoBehaviour host, CancellationToken cancellation, Action<float> onComplete)
        {
            var hp = new HyperParams { Values = Search.Mean, Names = HyperParams.DefaultNames };
            int wins = 0, total = 0;
            for (int i = 0; i < ScenarioGenerator.LibraryNames.Length; i++)
            {
                if (cancellation.IsCancellationRequested) break;
                var scen = ScenarioGenerator.LoadLibrary(ScenarioGenerator.LibraryNames[i]);
                if (scen == null) continue; // v0.1.0 library not present

                var match = new TrainingMatch(scen, MatchMode.BaselineBenchmark, hp, Headless, cancellation);
                MatchOutcome outcome = null;
                yield return host.StartCoroutine(match.SimulateCoroutineFull(o => outcome = o));
                if (outcome != null)
                {
                    int winner = OutcomeScorer.DeriveWinner(outcome, ScoreWeights);
                    if (winner == (int)TeamSide.Us) wins++;
                    total++;
                }
            }
            float rate = total > 0 ? (float)wins / total : 0f;
            _libraryHistory.Enqueue(rate);
            while (_libraryHistory.Count > PlateauWindow) _libraryHistory.Dequeue();
            onComplete?.Invoke(rate);
        }

        /// <summary>
        /// Run for up to <paramref name="maxGenerations"/> or until cancellation /
        /// convergence / plateau per §5.4. **Synchronous — does not pump Unity ticks.**
        /// Retained for CMA-ES math testing without a host MonoBehaviour; each match
        /// evaluates to a zero-duration stalemate, so fitness is constant across candidates
        /// and the search produces no meaningful pressure. Use <see cref="BeginAsync"/>
        /// for actual training.
        /// </summary>
        public void Run(int maxGenerations, CancellationToken cancellation)
        {
            for (int gen = 0; gen < maxGenerations; gen++)
            {
                if (cancellation.IsCancellationRequested) break;
                if (Search.Converged()) { DebugTAC_AI.Log("Smart.Training: CMA-ES converged at gen " + Search.Generation); break; }

                var population = Search.SamplePopulation();
                var fitness = new float[population.Length];
                for (int p = 0; p < population.Length; p++)
                {
                    if (cancellation.IsCancellationRequested) break;
                    fitness[p] = EvaluateCandidate(population[p], cancellation);
                }

                Search.Update(population, fitness);

                float meanFit = 0f, bestFit = float.MinValue;
                for (int p = 0; p < fitness.Length; p++)
                {
                    meanFit += fitness[p];
                    if (fitness[p] > bestFit) bestFit = fitness[p];
                }
                meanFit /= fitness.Length;

                DebugTAC_AI.Log("Smart.Training: gen=" + Search.Generation
                                + " meanScore=" + meanFit.ToString("F3")
                                + " bestScore=" + bestFit.ToString("F3"));

                if (meanFit > BestKnownMeanScore)
                {
                    BestKnownMeanScore = meanFit;
                    BestKnownGeneration = Search.Generation;
                    BestKnownMean = (float[])Search.Mean.Clone();
                }

                if ((Search.Generation % LibraryEvaluationEveryNGenerations) == 0)
                {
                    float libRate = RunLibraryEvaluation(cancellation);
                    DebugTAC_AI.Log("Smart.Training: library_winrate=" + libRate.ToString("F3"));
                    if (IsLibraryPlateaued(libRate))
                    {
                        DebugTAC_AI.Log("Smart.Training: library plateau detected; halting.");
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Run <see cref="EvalMatchesPerCandidate"/> matches with this candidate; return the mean composite score.
        /// </summary>
        private float EvaluateCandidate(float[] candidateValues, CancellationToken cancellation)
        {
            var hp = new HyperParams { Values = candidateValues, Names = HyperParams.DefaultNames };
            float scoreSum = 0f;
            int n = 0;
            for (int m = 0; m < EvalMatchesPerCandidate; m++)
            {
                if (cancellation.IsCancellationRequested) break;
                MatchMode mode = (MatchCounter % BaselineBenchmarkEveryNMatches == BaselineBenchmarkEveryNMatches - 1)
                    ? MatchMode.BaselineBenchmark
                    : MatchMode.SelfPlay;

                int matchSeed = MatchCounter * 31 + Search.Generation;
                var scenario = ScenarioGenerator.PickForMatch(MatchCounter, matchSeed, ScenarioGenerator.Difficulty.Medium);
                var match = new TrainingMatch(scenario, mode, hp, Headless, cancellation);
                var outcome = match.Run();
                scoreSum += OutcomeScorer.Score(outcome, ScoreWeights);
                MatchCounter++;
                n++;
            }
            return n > 0 ? scoreSum / n : 0f;
        }

        /// <summary>Run all library scenarios with the current distribution mean; return win rate.</summary>
        public float RunLibraryEvaluation(CancellationToken cancellation)
        {
            var hp = new HyperParams { Values = Search.Mean, Names = HyperParams.DefaultNames };
            int wins = 0, total = 0;
            for (int i = 0; i < ScenarioGenerator.LibraryNames.Length; i++)
            {
                if (cancellation.IsCancellationRequested) break;
                var scen = ScenarioGenerator.LoadLibrary(ScenarioGenerator.LibraryNames[i]);
                if (scen == null) continue; // v0.1.0: library not present, skipped

                var match = new TrainingMatch(scen, MatchMode.BaselineBenchmark, hp, Headless, cancellation);
                var outcome = match.Run();
                int winner = OutcomeScorer.DeriveWinner(outcome, ScoreWeights);
                if (winner == (int)TeamSide.Us) wins++;
                total++;
            }
            float rate = total > 0 ? (float)wins / total : 0f;
            _libraryHistory.Enqueue(rate);
            while (_libraryHistory.Count > PlateauWindow) _libraryHistory.Dequeue();
            return rate;
        }

        // Phase 8 (FIX-PLAN.md) — AUDIT R1 2.6: previous detector was upside-down. It
        // fired plateau when (best − current) < 0.01 — meaning "we have *not* fallen
        // off" was misread as "we have stopped improving." A real plateau is the OLDER
        // entries' max ≈ NEWER entries' max — improvement has stopped. The fix
        // compares the older half's best with the newer half's best and triggers
        // when the newer half hasn't beaten the older half by > 1% across the window.
        // Also gates on having BOTH halves populated (the prior < PlateauWindow guard
        // was correct but allowed false positives when the queue fills with similar
        // early-training noise).
        private bool IsLibraryPlateaued(float currentRate)
        {
            if (_libraryHistory.Count < PlateauWindow) return false;
            int half = PlateauWindow / 2;
            if (half < 1) return false;
            float olderBest = float.MinValue;
            float newerBest = float.MinValue;
            int i = 0;
            foreach (var r in _libraryHistory)
            {
                if (i < half) { if (r > olderBest) olderBest = r; }
                else          { if (r > newerBest) newerBest = r; }
                i++;
            }
            // Real plateau: newer-half max barely surpasses older-half max.
            return newerBest - olderBest < 0.01f;
        }
    }
}
#endif
