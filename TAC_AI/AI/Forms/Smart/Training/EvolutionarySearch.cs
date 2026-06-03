#if SMART_DEV
using System;
using System.IO;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Training
{
    /// <summary>
    /// Tunable hyperparameter vector. Per TRAINING-CONTRACT §5.1.
    /// v0.1.0 ships a compact subset (~16 dims) — the full list per §5.1 lands
    /// when each consumer subsystem exposes its own writable hyperparameter
    /// surface (TODO v0.2 per Control, Planning, Pathing).
    /// </summary>
    public sealed class HyperParams
    {
        public float[] Values;
        public string[] Names;

        // Phase 8 (FIX-PLAN.md) — AUDIT R1 §3.6: extended to cover every static-mutable
        // hyperparameter discoverable by CMA-ES. Previously CMA-ES could not tune
        // EnergyRefillPerSecond, SigmaThrottle/Steer/Brake, or the strategic-value
        // weights for position/speed/coverage — the search had a partial surface.
        // Names use the existing _underscore convention; new entries are appended so
        // index-based callers keep working with old means/bounds arrays.
        public static readonly string[] DefaultNames =
        {
            "MPC_sample_count", "MPC_horizon_seconds", "MPC_temperature",
            "Tactical_learning_rate", "Salvo_threshold_damage", "Sticky_target_ticks",
            "PUCT_exploration_constant", "MCTS_node_budget", "Rollout_depth_seconds",
            "w_health_value", "w_threat_value",
            "w_reach_cost", "w_effort_cost", "w_threat_cost", "w_weapon_cost",
            "CHOMP_step_count",
            // --- Phase 8 additions ---
            "Energy_refill_per_second",
            "MPC_sigma_throttle", "MPC_sigma_steer", "MPC_sigma_brake",
            "w_position_value", "w_speed_value", "w_coverage_value",
        };

        public static readonly float[] DefaultMeans =
        {
            128f, 1.0f, 1.0f,
            0.1f, 50f, 30f,
            1.4f, 5000f, 2.5f,
            2.0f, 1.5f,
            1.0f, 0.05f, 2.0f, 0.5f,
            20f,
            0.2f,
            0.3f, 0.4f, 0.2f,
            1.0f, 0.5f, 1.0f,
        };

        public static readonly float[] DefaultLowerBounds =
        {
            64f, 0.5f, 0.1f, 0.01f, 20f, 10f,
            0.5f, 1000f, 1.0f,
            0.5f, 0.5f, 0.3f, 0.01f, 0.5f, 0.1f, 5f,
            0.05f,
            0.05f, 0.05f, 0.05f,
            0.1f, 0.05f, 0.1f,
        };

        public static readonly float[] DefaultUpperBounds =
        {
            256f, 2.0f, 10f, 1.0f, 100f, 90f,
            3.0f, 20000f, 5.0f,
            5f, 5f, 3f, 0.5f, 6f, 2f, 50f,
            1.0f,
            0.8f, 0.8f, 0.8f,
            3.0f, 2.0f, 3.0f,
        };

        public static HyperParams DefaultProvisional()
        {
            return new HyperParams { Values = (float[])DefaultMeans.Clone(), Names = DefaultNames };
        }

        public HyperParams Clone()
        {
            return new HyperParams { Values = (float[])Values.Clone(), Names = Names };
        }
    }

    /// <summary>
    /// Separable CMA-ES (sep-CMA-ES): treats the covariance matrix as diagonal,
    /// drastically simplifying the math vs. full CMA-ES (no eigendecomposition).
    /// Per TRAINING-CONTRACT §5.3 — Hansen's CMA-ES tutorial is the reference;
    /// the sep- variant is from Ros & Hansen 2008. Effective for separable
    /// hyperparameter spaces where most dimensions evolve independently.
    ///
    /// v0.1.0 chooses sep-CMA-ES over full CMA-ES as a pragmatic v0.1.0 choice.
    /// TODO v0.2: full CMA-ES with Jacobi eigendecomposition once the dimension
    /// stabilizes and covariance learning becomes the limiting factor.
    ///
    /// Population sampling is bounded — values are clamped to per-dimension lower/upper
    /// bounds at sample time. Internal state tracks the unbounded variables.
    /// </summary>
    public sealed class EvolutionarySearch
    {
        public int Dimension { get; }
        public int PopulationSize { get; }
        public int Mu { get; }                   // half-population for recombination

        public float[] Mean { get; private set; }     // μ — current distribution mean
        public float[] Sigma { get; private set; }    // σ — per-dimension step size (diagonal stdev)
        public float[] EvolutionPath { get; private set; }   // p_σ

        public int Generation { get; private set; }
        public float[] LowerBounds;
        public float[] UpperBounds;

        // sep-CMA-ES constants (Ros & Hansen).
        private readonly float[] _weights;
        private readonly float _muEff;
        private readonly float _cSigma;
        private readonly float _dSigma;
        private readonly float _cC;
        private readonly float _c1Sep;
        private readonly float _cMuSep;

        private readonly Mulberry32 _rng;

        public EvolutionarySearch(int dimension, int populationSize, int seed,
            float[] initialMean, float[] initialSigma, float[] lowerBounds, float[] upperBounds)
        {
            Dimension = dimension;
            PopulationSize = populationSize;
            Mu = populationSize / 2;
            Mean = (float[])initialMean.Clone();
            Sigma = (float[])initialSigma.Clone();
            EvolutionPath = new float[dimension];
            LowerBounds = lowerBounds;
            UpperBounds = upperBounds;
            _rng = new Mulberry32((uint)seed);

            // Recombination weights w_i = log(μ+1) - log(i)  normalized.
            _weights = new float[Mu];
            float wSum = 0f;
            for (int i = 0; i < Mu; i++)
            {
                _weights[i] = Mathf.Log(Mu + 1f) - Mathf.Log(i + 1f);
                wSum += _weights[i];
            }
            for (int i = 0; i < Mu; i++) _weights[i] /= wSum;

            float sumWSq = 0f;
            for (int i = 0; i < Mu; i++) sumWSq += _weights[i] * _weights[i];
            _muEff = 1f / sumWSq;

            float N = dimension;
            _cSigma = (_muEff + 2f) / (N + _muEff + 5f);
            _dSigma = 1f + 2f * Mathf.Max(0f, Mathf.Sqrt((_muEff - 1f) / (N + 1f)) - 1f) + _cSigma;
            _cC = (4f + _muEff / N) / (N + 4f + 2f * _muEff / N);

            // sep-CMA-ES rank-one / rank-μ learning rates (Ros-Hansen 2008).
            // Factor (N + 2)/3 vs. full CMA-ES dampens rank-μ in low dims.
            float factor = (N + 2f) / 3f;
            _c1Sep = 2f / ((N + 1.3f) * (N + 1.3f) + _muEff) * factor;
            _cMuSep = Mathf.Min(1f - _c1Sep, 2f * (_muEff - 2f + 1f / _muEff) / ((N + 2f) * (N + 2f) + _muEff)) * factor;
        }

        public static EvolutionarySearch FromDefaults(int seed)
        {
            int N = HyperParams.DefaultMeans.Length;
            // Initial σ: 25% of each dim's range.
            var sigma = new float[N];
            for (int i = 0; i < N; i++) sigma[i] = (HyperParams.DefaultUpperBounds[i] - HyperParams.DefaultLowerBounds[i]) * 0.25f;
            int pop = 4 + (int)(3f * Mathf.Log(N));
            return new EvolutionarySearch(N, pop, seed,
                HyperParams.DefaultMeans, sigma,
                HyperParams.DefaultLowerBounds, HyperParams.DefaultUpperBounds);
        }

        /// <summary>Sample <paramref name="PopulationSize"/> candidates from the current distribution.</summary>
        public float[][] SamplePopulation()
        {
            var pop = new float[PopulationSize][];
            for (int i = 0; i < PopulationSize; i++)
            {
                pop[i] = new float[Dimension];
                for (int j = 0; j < Dimension; j++)
                {
                    float z = _rng.NextGaussian();
                    float v = Mean[j] + Sigma[j] * z;
                    if (LowerBounds != null) v = Mathf.Max(v, LowerBounds[j]);
                    if (UpperBounds != null) v = Mathf.Min(v, UpperBounds[j]);
                    pop[i][j] = v;
                }
            }
            return pop;
        }

        /// <summary>
        /// Update distribution from per-sample fitness. <paramref name="population"/> and
        /// <paramref name="fitness"/> must have the same length; higher fitness = better.
        /// </summary>
        public void Update(float[][] population, float[] fitness)
        {
            if (population.Length != fitness.Length) throw new ArgumentException("population/fitness size mismatch");

            // Sort indices by fitness descending.
            var order = new int[population.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => fitness[b].CompareTo(fitness[a]));

            // Recombination — weighted mean of top μ.
            var newMean = new float[Dimension];
            for (int j = 0; j < Dimension; j++)
            {
                float sum = 0f;
                for (int k = 0; k < Mu; k++) sum += _weights[k] * population[order[k]][j];
                newMean[j] = sum;
            }

            // Δμ / σ (per-dimension).
            var deltaOverSigma = new float[Dimension];
            for (int j = 0; j < Dimension; j++)
            {
                deltaOverSigma[j] = (newMean[j] - Mean[j]) / Mathf.Max(Sigma[j], 1e-12f);
            }

            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6 / R2 1.R2-N: snapshot the pre-step
            // Sigma so rank-μ normalization (y) and the additive C update both use the σ
            // that the population was actually SAMPLED with. The previous code applied
            // sigmaMultiplier to Sigma BEFORE rank-μ — so y was normalized by post-step
            // σ (defeating step-size adaptation), and the rank-μ additive term was
            // multiplied by the post-step σ² (double-applying the multiplier into C).
            var preSigma = new float[Dimension];
            for (int j = 0; j < Dimension; j++) preSigma[j] = Sigma[j];

            // Evolution path for σ (sep-CMA-ES treats it diagonally too).
            for (int j = 0; j < Dimension; j++)
            {
                EvolutionPath[j] = (1f - _cSigma) * EvolutionPath[j]
                                 + Mathf.Sqrt(_cSigma * (2f - _cSigma) * _muEff) * deltaOverSigma[j];
            }

            // σ update via the ||p_σ|| heuristic, per-dimension form for sep variant.
            float E = Mathf.Sqrt(Dimension) * (1f - 1f / (4f * Dimension) + 1f / (21f * Dimension * Dimension));
            float pSigNorm = 0f;
            for (int j = 0; j < Dimension; j++) pSigNorm += EvolutionPath[j] * EvolutionPath[j];
            pSigNorm = Mathf.Sqrt(pSigNorm);
            float sigmaMultiplier = Mathf.Exp((_cSigma / _dSigma) * (pSigNorm / E - 1f));

            // Rank-one + rank-μ update — Ros-Hansen 2008 sep-CMA-ES form. The previous
            // code multiplied the additive terms by oldVar, collapsing the update to a
            // pure scaling of prior variance (rank-μ could never inject variance independent
            // of current scale → σ effectively just decayed). Correct: additive terms are
            // NOT multiplied by oldVar; y is normalized by the SAMPLING σ (preSigma).
            for (int j = 0; j < Dimension; j++)
            {
                float rankMu = 0f;
                float sigmaSample = Mathf.Max(preSigma[j], 1e-12f);
                for (int k = 0; k < Mu; k++)
                {
                    float y = (population[order[k]][j] - Mean[j]) / sigmaSample;
                    rankMu += _weights[k] * y * y;
                }
                float oldVar = preSigma[j] * preSigma[j];
                // Pre-multiplier C update (Ros-Hansen): C_j ← (1-c1-cμ) C_j + c1 p_σ_j² + cμ Σ w_k y_kj²
                float newVar = (1f - _c1Sep - _cMuSep) * oldVar
                             + _c1Sep * EvolutionPath[j] * EvolutionPath[j]
                             + _cMuSep * rankMu;
                // THEN apply the global step-size multiplier so σ adaptation and C
                // adaptation compose without double-multiplication.
                Sigma[j] = Mathf.Sqrt(Mathf.Max(newVar, 1e-20f)) * sigmaMultiplier;
            }

            Mean = newMean;
            Generation++;
        }

        /// <summary>True per §5.4: σ shrank below a tiny epsilon (converged).</summary>
        public bool Converged(float epsilon = 1e-4f)
        {
            for (int j = 0; j < Dimension; j++) if (Sigma[j] > epsilon) return false;
            return true;
        }

        // ---- Checkpoint persistence ----
        public void SaveCheckpoint(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(filePath, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((uint)0x434D4145u); // 'CMAE'
                bw.Write((uint)1);            // schema version
                bw.Write(Dimension);
                bw.Write(PopulationSize);
                bw.Write(Generation);
                for (int j = 0; j < Dimension; j++) bw.Write(Mean[j]);
                for (int j = 0; j < Dimension; j++) bw.Write(Sigma[j]);
                for (int j = 0; j < Dimension; j++) bw.Write(EvolutionPath[j]);
            }
        }

        public static EvolutionarySearch LoadCheckpoint(string filePath, float[] lowerBounds, float[] upperBounds, int seed)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                uint magic = br.ReadUInt32();
                if (magic != 0x434D4145u) throw new InvalidDataException("Bad CMA-ES checkpoint magic.");
                uint ver = br.ReadUInt32();
                if (ver != 1) throw new InvalidDataException("Unknown CMA-ES checkpoint version " + ver);
                int dim = br.ReadInt32();
                int pop = br.ReadInt32();
                int gen = br.ReadInt32();
                var mean = new float[dim];
                var sigma = new float[dim];
                var ps = new float[dim];
                for (int j = 0; j < dim; j++) mean[j] = br.ReadSingle();
                for (int j = 0; j < dim; j++) sigma[j] = br.ReadSingle();
                for (int j = 0; j < dim; j++) ps[j] = br.ReadSingle();
                var es = new EvolutionarySearch(dim, pop, seed, mean, sigma, lowerBounds, upperBounds);
                es.Generation = gen;
                Array.Copy(ps, es.EvolutionPath, dim);
                return es;
            }
        }
    }
}
#endif
