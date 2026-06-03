using System;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Control
{
    /// <summary>
    /// MPPI-variant sampling MPC. Per CONTROL-CONTRACT §3.
    /// Generate N candidate control sequences as perturbations of a previous-mean, evaluate
    /// each by rollout + cost, and return an importance-weighted average.
    ///
    /// Hyperparameters per CONTROL §3.2 (provisional):
    ///   N = 128 samples
    ///   H = 20 steps (horizon = H × dt)
    ///   λ = 1.0 (MPPI temperature)
    ///   σ per control dim: throttle 0.3, steer 0.4, brake 0.2 (normalized)
    ///
    /// Anytime: on cancellation mid-loop, the best-completed-samples result is returned.
    /// </summary>
    public sealed class SamplingMPC
    {
        // Globally tuned by CMA-ES (Training §5); shared across all per-tech MPCs in the
        // current Smart session. Static rather than per-instance so the harness can write
        // them once between matches without reaching into every controller.
        public static int SampleCount = 128;
        public static int HorizonSteps = 20;
        public static float StepDt = 0.05f;
        public static float Lambda = 1.0f;
        public static float SigmaThrottle = 0.3f;
        public static float SigmaSteer = 0.4f;
        public static float SigmaBrake = 0.2f;

        // System.Random (BCL seedable PRNG) — explicitly qualified so the using
        // UnityEngine import doesn't pull UnityEngine.Random into the resolution.
        // UnityEngine.Random is a static façade, not instantiable, and wouldn't fit
        // this seed-per-tech pattern anyway.
        private readonly System.Random _rng;

        // Phase 9 (FIX-PLAN.md) — AUDIT R1 §3.6: per-tick allocations from Solve()
        // were dominating Smart's GC budget at 32 techs × 30 Hz × (N=128 samples ×
        // (H+1) RolloutStates + N ControlVector arrays + cost array + weights). The
        // pool is a single-instance reuse — Solve is called from one worker, sequenced
        // by the Single-pending-slot pattern in ContinuousController, so concurrent
        // access never happens for a given SamplingMPC instance.
        private ControlVector[][] _samplesScratch;
        private float[] _costsScratch;
        private float[] _weightsScratch;
        // Single trajectory scratch — reused across all N samples per Solve call. Each
        // sample's evaluation is a strict read-then-evaluate sequence; nothing holds a
        // reference to the trajectory past the cost call.
        private RolloutState[] _trajScratch;

        public SamplingMPC(int seed = 0)
        {
            _rng = seed == 0 ? new System.Random() : new System.Random(seed);
        }

        /// <summary>
        /// Solve for the optimal control sequence given the current request.
        /// Returns null if no samples completed (e.g. immediate cancel).
        /// </summary>
        public ControlVector[] Solve(
            VehicleModelSnapshot vehicle,
            RolloutState x0,
            TacticalGoal goal,
            BeliefSnapshot beliefs,
            IThreatField threatField,
            VehicleCapability capability,
            ControlVector[] previousMean,
            CancellationToken cancellation)
        {
            int N = SampleCount;
            int H = HorizonSteps;

            // Reuse scratch arrays across Solve calls. Grow if N changed (CMA-ES retune).
            if (_samplesScratch == null || _samplesScratch.Length < N) _samplesScratch = new ControlVector[N][];
            if (_costsScratch == null || _costsScratch.Length < N) _costsScratch = new float[N];
            var samples = _samplesScratch;
            var costs = _costsScratch;
            int completed = 0;

            for (int i = 0; i < N; i++)
            {
                if (cancellation.IsCancellationRequested) break;

                // Phase 9 (FIX-PLAN.md): reuse the existing ControlVector[] when its
                // length matches H; otherwise allocate a fresh one and stash it. Allows
                // amortized zero-alloc per Solve once H stabilizes (typical case —
                // CMA-ES retunes the count slowly).
                if (samples[i] == null || samples[i].Length != H)
                    samples[i] = GenerateSample(previousMean, H);
                else
                    FillSampleInPlace(samples[i], previousMean);
                // Trajectory length must exactly equal H+1: CostFunction.Compute uses
                // trajectory.Length (not an explicit length parameter), so an oversized
                // scratch would include stale entries past the actual rollout. We reuse
                // when H is unchanged across calls (the common case) and reallocate
                // only when CMA-ES retunes HorizonSteps.
                if (_trajScratch == null || _trajScratch.Length != H + 1)
                    _trajScratch = new RolloutState[H + 1];
                PhysicsRollout.SimulateInto(vehicle, x0, samples[i], StepDt, _trajScratch);
                costs[i] = CostFunction.Compute(_trajScratch, samples[i], goal, vehicle, beliefs, threatField, capability);
                completed++;
            }

            if (completed == 0) return null;

            return CombineSamples(samples, costs, completed, H);
        }

        private ControlVector[] GenerateSample(ControlVector[] previousMean, int H)
        {
            var seq = new ControlVector[H];
            FillSampleInPlace(seq, previousMean);
            return seq;
        }

        private void FillSampleInPlace(ControlVector[] seq, ControlVector[] previousMean)
        {
            int H = seq.Length;
            for (int h = 0; h < H; h++)
            {
                var baseControl = previousMean != null && h < previousMean.Length
                    ? previousMean[h]
                    : ControlVector.Zero;

                float t = Clamp01(baseControl.Throttle + (float)NextGaussian() * SigmaThrottle, -1f, 1f);
                float s = Clamp01(baseControl.Steer + (float)NextGaussian() * SigmaSteer, -1f, 1f);
                float b = Clamp01(baseControl.Brake + (float)NextGaussian() * SigmaBrake, 0f, 1f);

                seq[h] = new ControlVector(t, s, b);
            }
        }

        private ControlVector[] CombineSamples(ControlVector[][] samples, float[] costs, int completed, int H)
        {
            // Min-cost subtraction for numerical stability of the exp.
            float minCost = costs[0];
            for (int i = 1; i < completed; i++) if (costs[i] < minCost) minCost = costs[i];

            // Phase 6 (FIX-PLAN.md) — AUDIT-R2 §1.R2-J: floor Lambda so a tuning write of
            // Lambda=0 cannot produce 0/0 = NaN that poisons every output ControlVector
            // (and propagates into previousMean for every future Solve, breaking the
            // tech permanently). Plan 5 #1 + Plan 6 #5: critical to apply at consumer.
            float safeLambda = Mathf.Max(Lambda, 1e-6f);

            // Phase 9 (FIX-PLAN.md): reuse weights scratch buffer.
            if (_weightsScratch == null || _weightsScratch.Length < completed)
                _weightsScratch = new float[completed];
            var weights = _weightsScratch;
            float sumW = 0f;
            for (int i = 0; i < completed; i++)
            {
                weights[i] = Mathf.Exp(-(costs[i] - minCost) / safeLambda);
                sumW += weights[i];
            }
            // NaN-safe guard: if every cost was NaN or otherwise produced NaN weights,
            // fall back to uniform weighting so the optimizer still emits a sensible output.
            if (!(sumW > 1e-9f) || float.IsNaN(sumW))
            {
                float uniform = 1f / Mathf.Max(1, completed);
                for (int i = 0; i < completed; i++) weights[i] = uniform;
            }
            else
            {
                for (int i = 0; i < completed; i++) weights[i] /= sumW;
            }

            var optimal = new ControlVector[H];
            for (int h = 0; h < H; h++)
            {
                float t = 0f, s = 0f, b = 0f;
                for (int i = 0; i < completed; i++)
                {
                    t += weights[i] * samples[i][h].Throttle;
                    s += weights[i] * samples[i][h].Steer;
                    b += weights[i] * samples[i][h].Brake;
                }
                optimal[h] = new ControlVector(t, s, b);
            }
            return optimal;
        }

        /// <summary>Box-Muller Gaussian sample, mean 0, std 1.</summary>
        private double NextGaussian()
        {
            // Two uniforms → one standard normal (we discard the second for simplicity at v0.1.0).
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static float Clamp01(float v, float min, float max) => Mathf.Clamp(v, min, max);
    }
}
