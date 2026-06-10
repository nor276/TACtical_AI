using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>
    /// Uniform cubic B-spline trajectory per PATHING-CONTRACT §5.1. Position, Velocity,
    /// and Acceleration are evaluated analytically from the control points + duration.
    ///
    /// With N control points there are N-3 cubic segments; the parameter t ∈ [0,1]
    /// maps onto them uniformly. First and last (2) control points are pinned per §5.3
    /// to start/end positions + initial/terminal velocity; intermediate control points
    /// are the optimization variables.
    /// </summary>
    public sealed class Trajectory
    {
        public IReadOnlyList<Vector3> ControlPoints { get; }
        public float Duration { get; }
        public int N => ControlPoints.Count;
        public int SegmentCount => Mathf.Max(1, N - 3);

        /// <summary>
        /// L-042: monotonic timestamp (MonoClock.Now) when the optimizer published this
        /// trajectory. PathingService.GetLastPathFresh uses (Now - SolvedAtMono) to drop
        /// stale cached paths. 0 = never solved (default ctor / test fixture).
        /// </summary>
        public long SolvedAtMono { get; set; }

        // Backing array; same reference as ControlPoints. Allows optimizer to mutate in place.
        internal readonly Vector3[] _backing;

        public Trajectory(Vector3[] controlPoints, float duration)
        {
            _backing = controlPoints ?? throw new ArgumentNullException(nameof(controlPoints));
            ControlPoints = controlPoints;
            Duration = Mathf.Max(0.01f, duration);
            SolvedAtMono = 0L;   // optimizer overwrites at publish
        }

        public Vector3 Position(float t)
        {
            ResolveSegment(t, out int i, out float u);
            Vector3 p0 = _backing[i], p1 = _backing[i + 1], p2 = _backing[i + 2], p3 = _backing[i + 3];
            float omu = 1f - u;
            float b0 = omu * omu * omu / 6f;
            float b1 = (3f * u * u * u - 6f * u * u + 4f) / 6f;
            float b2 = (-3f * u * u * u + 3f * u * u + 3f * u + 1f) / 6f;
            float b3 = u * u * u / 6f;
            return b0 * p0 + b1 * p1 + b2 * p2 + b3 * p3;
        }

        public Vector3 Velocity(float t)
        {
            ResolveSegment(t, out int i, out float u);
            Vector3 p0 = _backing[i], p1 = _backing[i + 1], p2 = _backing[i + 2], p3 = _backing[i + 3];
            float omu = 1f - u;
            float d0 = -3f * omu * omu / 6f;
            float d1 = (9f * u * u - 12f * u) / 6f;
            float d2 = (-9f * u * u + 6f * u + 3f) / 6f;
            float d3 = 3f * u * u / 6f;
            Vector3 dPdU = d0 * p0 + d1 * p1 + d2 * p2 + d3 * p3;
            // dP/dt where t ∈ [0,1] is dP/dU * SegmentCount; per-second velocity divides by Duration.
            return dPdU * SegmentCount / Duration;
        }

        public Vector3 Acceleration(float t)
        {
            ResolveSegment(t, out int i, out float u);
            Vector3 p0 = _backing[i], p1 = _backing[i + 1], p2 = _backing[i + 2], p3 = _backing[i + 3];
            float a0 = (1f - u);
            float a1 = (3f * u - 2f);
            float a2 = (-3f * u + 1f);
            float a3 = u;
            // From d²B/du²; the factor (1/6)*6 = 1 cancels.
            Vector3 d2PdU2 = a0 * p0 + a1 * p1 + a2 * p2 + a3 * p3;
            float segDur = Duration / SegmentCount;
            return d2PdU2 / (segDur * segDur);
        }

        private void ResolveSegment(float t, out int i, out float u)
        {
            int segs = SegmentCount;
            float clamped = Mathf.Clamp01(t);
            float scaled = clamped * segs;
            i = Mathf.Clamp((int)scaled, 0, segs - 1);
            u = scaled - i;
        }
    }

    /// <summary>
    /// CHOMP-inspired trajectory optimizer per PATHING-CONTRACT §6. Numerical-gradient
    /// implementation at v0.1.0; analytic gradients (§6.2) and the inverse-smoothness-metric
    /// preconditioner (§6.3) are flagged TODO v0.2.
    ///
    /// Per-solve compute is bounded: K=20 gradient steps × N_free × M_samples × O(N_threats).
    /// At default settings (~12 free CP × 16 samples × ~10 threats) ≈ 60k evaluations.
    /// </summary>
    public sealed class TrajectoryOptimizer
    {
        public const int DefaultControlPoints = 16;
        public const int PinnedPerEnd = 2;
        public const int DefaultGradientSteps = 20;
        public const int DefaultSamplePoints = 16;

        // §6.1 cost weights — provisional, OPEN per §10. Globally tuned by CMA-ES (Training §5).
        // Public assign API preserved for the CMA-ES tuner (TrainingMatch.cs writes directly).
        // Worker-side reads inside Solve/Cost/TerrainPenalty go through Volatile.Read so a
        // mid-sweep main-thread write is observed coherently per-field on the next pass. A
        // sweep that lands across multiple fields can still be observed half-applied; tuner
        // callers that need atomicity across the bundle issue Thread.MemoryBarrier after the
        // full set (see TrainingMatch CMA-ES write path).
        public static float WThreat = 1.0f;
        public static float WTerrain = 0.3f;
        public static float WSmooth = 0.1f;
        public static float WLength = 0.05f;
        public static float WReach = 5.0f;
        public static float WVelocity = 1.0f;

        public static float LearningRate = 0.5f;
        public static int GradientSteps = DefaultGradientSteps;
        public static int SamplePoints = DefaultSamplePoints;
        public static float ConvergenceGradientNorm = 0.05f;
        public static float NumericalEps = 0.1f; // metres per axis perturbation

        public sealed class Result
        {
            public Trajectory Trajectory;
            public int GradientStepsRun;
            public float FinalCost;
            public bool Cancelled;
        }

        public Result Solve(
            Vector3 start, Vector3 startVel,
            Vector3 goal, Vector3 goalVel,
            float duration,
            IThreatField threatField,
            ITerrainMap terrain,
            VehicleCapability capability,
            Trajectory warmStart,
            CancellationToken token)
        {
            int N = DefaultControlPoints;
            Vector3[] cps = (warmStart != null && warmStart.N == N)
                ? CloneArray(warmStart._backing)
                : InitializeStraightLine(start, goal, N);

            ApplyPinnedEnds(cps, start, startVel, goal, goalVel, duration, N);
            var traj = new Trajectory(cps, duration);

            int firstFree = PinnedPerEnd;
            int lastFreeExclusive = N - PinnedPerEnd;
            int freeCount = lastFreeExclusive - firstFree;
            var grads = new Vector3[freeCount];

            int stepsRun = 0;
            bool cancelled = false;
            float finalCost = float.NaN;

            // Snapshot the tuning fields once per Solve via Volatile.Read so the loop sees
            // a coherent set even if the CMA-ES tuner writes mid-solve. Subsequent Cost()
            // calls inside this Solve still re-read weights per evaluation (matching the
            // documented per-tick observability), but loop-bound parameters are stable.
            int gradientSteps = System.Threading.Volatile.Read(ref GradientSteps);
            float numericalEps = System.Threading.Volatile.Read(ref NumericalEps);
            float learningRate = System.Threading.Volatile.Read(ref LearningRate);
            float convergenceGradientNorm = System.Threading.Volatile.Read(ref ConvergenceGradientNorm);

            for (int step = 0; step < gradientSteps; step++)
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }

                // Numerical gradient per free CP, per axis (central differences).
                for (int i = 0; i < freeCount; i++) grads[i] = Vector3.zero;
                for (int i = 0; i < freeCount; i++)
                {
                    int cpIdx = firstFree + i;
                    Vector3 grad = Vector3.zero;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        Vector3 saved = cps[cpIdx];
                        Vector3 perturb = AxisVec(axis, numericalEps);

                        cps[cpIdx] = saved + perturb;
                        float costPlus = Cost(traj, threatField, terrain, capability, goal, goalVel);
                        cps[cpIdx] = saved - perturb;
                        float costMinus = Cost(traj, threatField, terrain, capability, goal, goalVel);
                        cps[cpIdx] = saved;

                        grad[axis] = (costPlus - costMinus) / (2f * numericalEps);
                    }
                    grads[i] = grad;
                }

                // Apply update: M⁻¹ ≈ I at v0.1.0 (TODO v0.2 actual smoothness preconditioner per §6.3).
                float gradNormSq = 0f;
                for (int i = 0; i < freeCount; i++)
                {
                    var g = grads[i];
                    gradNormSq += g.sqrMagnitude;
                    cps[firstFree + i] -= learningRate * g;
                }
                stepsRun++;
                if (Mathf.Sqrt(gradNormSq) < convergenceGradientNorm) break;
            }

            finalCost = Cost(traj, threatField, terrain, capability, goal, goalVel);

            return new Result
            {
                Trajectory = traj,
                GradientStepsRun = stepsRun,
                FinalCost = finalCost,
                Cancelled = cancelled,
            };
        }

        // ---- Cost computation (PATHING §6.1) ----
        private float Cost(
            Trajectory traj,
            IThreatField threatField,
            ITerrainMap terrain,
            VehicleCapability capability,
            Vector3 goal, Vector3 goalVel)
        {
            int M = System.Threading.Volatile.Read(ref SamplePoints);
            if (M < 2) M = 2;
            float threatInt = 0f, terrainInt = 0f, smoothInt = 0f, lengthInt = 0f;
            float dt = 1f / (M - 1);

            for (int s = 0; s < M; s++)
            {
                float t = s * dt;
                Vector3 p = traj.Position(t);
                Vector3 v = traj.Velocity(t);
                Vector3 a = traj.Acceleration(t);

                if (threatField != null) threatInt += threatField.Evaluate(p, capability);
                if (terrain != null) terrainInt += TerrainPenalty(terrain, p, capability);
                smoothInt += a.sqrMagnitude;
                lengthInt += v.magnitude;
            }
            // Trapezoidal weighting via simple uniform sums (close enough at M=16).
            threatInt *= dt;
            terrainInt *= dt;
            smoothInt *= dt;
            lengthInt *= dt;

            Vector3 endP = traj.Position(1f);
            Vector3 endV = traj.Velocity(1f);
            float reachSq = (endP - goal).sqrMagnitude;
            float velSq = (endV - goalVel).sqrMagnitude;

            // Snapshot weights via Volatile.Read so a mid-evaluation main-thread write to
            // any weight is observed coherently per-field (no torn float on x86; barrier
            // discipline for cross-arch correctness on Mono/IL2CPP backends).
            float wThreat = System.Threading.Volatile.Read(ref WThreat);
            float wTerrain = System.Threading.Volatile.Read(ref WTerrain);
            float wSmooth = System.Threading.Volatile.Read(ref WSmooth);
            float wLength = System.Threading.Volatile.Read(ref WLength);
            float wReach = System.Threading.Volatile.Read(ref WReach);
            float wVelocity = System.Threading.Volatile.Read(ref WVelocity);
            return wThreat * threatInt
                 + wTerrain * terrainInt
                 + wSmooth * smoothInt
                 + wLength * lengthInt
                 + wReach * reachSq
                 + wVelocity * velSq;
        }

        private static float TerrainPenalty(ITerrainMap terrain, Vector3 p, VehicleCapability cap)
        {
            // Penalize traversability gating + height divergence (forces path to follow ground).
            float penalty = 0f;
            Vector2 xz = new Vector2(p.x, p.z);
            if (!terrain.IsTraversable(xz, cap)) penalty += 50f;
            float h = terrain.HeightAt(xz);
            float clearance = p.y - h;
            // Punish going underground (large) and unnecessarily-high-air (small).
            if (clearance < -0.5f) penalty += 100f * (-clearance);
            else if (clearance > 10f && cap.Class != VehicleClass.Airplane) penalty += 0.1f * (clearance - 10f);
            return penalty;
        }

        // ---- Initialization helpers ----

        private static Vector3[] InitializeStraightLine(Vector3 start, Vector3 goal, int N)
        {
            var cps = new Vector3[N];
            for (int i = 0; i < N; i++)
            {
                float t = (float)i / (N - 1);
                cps[i] = Vector3.Lerp(start, goal, t);
            }
            return cps;
        }

        private static void ApplyPinnedEnds(Vector3[] cps, Vector3 start, Vector3 startVel,
            Vector3 goal, Vector3 goalVel, float duration, int N)
        {
            // First two CPs control initial position + velocity. For a uniform cubic
            // B-spline, P(0) = (P0 + 4*P1 + P2)/6 (clamped) — but with N≥4 the first
            // CP isn't directly on-curve; we approximate by pinning P0 at start and
            // P1 at start + startVel*Δt where Δt is one B-spline segment in seconds.
            // Same shape at the end.
            float segDur = duration / Mathf.Max(1, N - 3);
            cps[0] = start;
            cps[1] = start + startVel * segDur * 0.5f;
            cps[N - 2] = goal - goalVel * segDur * 0.5f;
            cps[N - 1] = goal;
        }

        private static Vector3[] CloneArray(Vector3[] source)
        {
            var copy = new Vector3[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static Vector3 AxisVec(int axis, float magnitude)
        {
            switch (axis)
            {
                case 0: return new Vector3(magnitude, 0f, 0f);
                case 1: return new Vector3(0f, magnitude, 0f);
                default: return new Vector3(0f, 0f, magnitude);
            }
        }
    }
}
