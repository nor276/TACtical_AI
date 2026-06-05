using UnityEngine;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Control
{
    /// <summary>
    /// Per-tech Adam gradient ascent over "where I want to be in N seconds."
    /// Per CONTROL-CONTRACT §6. Pure C# 7.3 Adam implementation; numerical gradient
    /// at v0.1.0 (TODO v0.2: analytic gradient for performance).
    ///
    /// Variables optimized: TacticalGoal.Position (3D) + Heading (1D). Velocity is
    /// derived from position + lookahead at output time.
    /// </summary>
    public sealed class TacticalOptimizer
    {
        // Adam hyperparameters per CONTROL §6.3 (provisional).
        public const float Beta1 = 0.9f;
        public const float Beta2 = 0.999f;
        public const float Epsilon = 1e-8f;
        // Globally tuned by CMA-ES (Training §5).
        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.5: default lowered from 0.5 to 0.1 to match
        // CONTROL-CONTRACT §6.3 default. 0.5 was an Adam-style overshoot — with α=0.5,
        // a single 10-step pass moves the goal by ~5 meters per Operations tick (30 Hz),
        // 150 m/s of goal drift, so the MPC chases a moving target it can't catch.
        public static float LearningRate = 0.1f;
        public static int StepsPerTick = 10;
        public static float LookAheadSeconds = 1.0f;

        // Adam state per parameter (4 parameters: position xyz + heading).
        private const int Dim = 4;
        private readonly float[] _m = new float[Dim];
        private readonly float[] _v = new float[Dim];
        // Phase 6 (FIX-PLAN.md): same Adam overflow concern as OnlineTrainer.AdamState.T —
        // promoted to long so the bias-correction terms cannot freeze the optimizer
        // after ~1.3 years of 50 Hz operation.
        private long _t;

        // Current goal (4 params: pos.x, pos.y, pos.z, heading).
        private readonly float[] _g = new float[Dim];
        private bool _initialized;

        /// <summary>Reset Adam state and re-seed goal. Called on substantial belief change.</summary>
        public void Reset(Vector3 currentPosition, float currentHeading)
        {
            for (int i = 0; i < Dim; i++) { _m[i] = 0f; _v[i] = 0f; }
            _t = 0;
            _g[0] = currentPosition.x;
            _g[1] = currentPosition.y;
            _g[2] = currentPosition.z;
            _g[3] = currentHeading;
            _initialized = true;
        }

        /// <summary>
        /// One tactical-optimizer tick. Runs StepsPerTick Adam iterations.
        /// Returns the updated TacticalGoal.
        /// </summary>
        public TacticalGoal Step(BeliefState ownBelief, VehicleModelSnapshot vehicle, BeliefSnapshot beliefs)
        {
            if (!_initialized)
                Reset(ownBelief.PositionMean, ownBelief.HeadingMean);

            // Numerical gradient via central differences. Step size scales with parameter magnitude.
            const float Eps = 0.1f; // 0.1 m or 0.1 rad — small enough for finite-difference accuracy.

            for (int step = 0; step < StepsPerTick; step++)
            {
                _t++;
                var grad = new float[Dim];

                for (int i = 0; i < Dim; i++)
                {
                    float saved = _g[i];

                    _g[i] = saved + Eps;
                    float fPlus = Utility(ownBelief, vehicle, beliefs);

                    _g[i] = saved - Eps;
                    float fMinus = Utility(ownBelief, vehicle, beliefs);

                    _g[i] = saved;
                    grad[i] = (fPlus - fMinus) / (2f * Eps);
                }

                // Adam update (we're maximizing utility, so step in +gradient direction).
                for (int i = 0; i < Dim; i++)
                {
                    _m[i] = Beta1 * _m[i] + (1f - Beta1) * grad[i];
                    _v[i] = Beta2 * _v[i] + (1f - Beta2) * grad[i] * grad[i];
                    float mHat = _m[i] / (1f - Mathf.Pow(Beta1, _t));
                    float vHat = _v[i] / (1f - Mathf.Pow(Beta2, _t));
                    _g[i] += LearningRate * mHat / (Mathf.Sqrt(vHat) + Epsilon);
                }
            }

            // Derive velocity from displacement / lookahead.
            Vector3 pos = new Vector3(_g[0], _g[1], _g[2]);
            float heading = _g[3];
            Vector3 displacement = pos - ownBelief.PositionMean;
            Vector3 velocity = displacement / Mathf.Max(LookAheadSeconds, 0.1f);

            return new TacticalGoal(pos, heading, velocity, LookAheadSeconds);
        }

        /// <summary>
        /// Utility function. Per CONTROL §6.2. v0.1.0 implements engagement-range and
        /// kinematic-feasibility terms. Other terms (armor facing, cover, team role) are
        /// no-ops or stubs until their dependent subsystems are authored.
        /// </summary>
        private float Utility(BeliefState ownBelief, VehicleModelSnapshot vehicle, BeliefSnapshot beliefs)
        {
            Vector3 candidate = new Vector3(_g[0], _g[1], _g[2]);

            // Pass ownBelief.Team so EngagementUtility can filter friendlies out of the target
            // search. Without this, when the WorldModel finally contains both friendlies and
            // enemies (the recent enemy-registration fix), the nearest-belief lookup picks
            // friendlies as often as it picks enemies and the optimizer chases the wrong tech.
            float uEngagement = EngagementUtility(candidate, vehicle, beliefs, ownBelief.Team);
            float uFeasibility = KinematicFeasibility(candidate, ownBelief, vehicle);

            // P11 T6 Item 56 (post-decompile, no v0.3 defer): three utility terms previously
            // hard-coded to 0f with stale "TODO v0.2: activates when X lands" comments. X (the
            // dependent subsystems) all landed in v0.2 — this commit wires the consumers.
            float uArmorFacing = ArmorFacingUtility(candidate, ownBelief, vehicle, beliefs);
            float uCover = CoverUtility(candidate, ownBelief, beliefs);
            float uTeamRole = TeamRoleUtility(ownBelief, candidate);

            return uEngagement + uFeasibility + uArmorFacing + uCover + uTeamRole;
        }

        // ---- P11 T6 Item 56 utility-term implementations ----

        /// <summary>
        /// Penalize candidates that expose OUR weak face toward the nearest hostile. Reads
        /// own ArmorMap.QueryWeakFace per VEHICLE-CONTRACT §6.4. Positive when our strong
        /// face is toward the threat, negative otherwise. Weight 0.2 — secondary to engagement.
        /// </summary>
        private static float ArmorFacingUtility(Vector3 candidate, BeliefState ownBelief,
            VehicleModelSnapshot vehicle, BeliefSnapshot beliefs)
        {
            if (vehicle.Armor == null || beliefs == null || beliefs.ByTech.Count == 0) return 0f;
            // Find the nearest hostile to the candidate position.
            BeliefState nearest = null;
            float bestDistSq = float.MaxValue;
            foreach (var pair in beliefs.ByTech)
            {
                if (pair.Value.Team.Equals(ownBelief.Team)) continue;
                float distSq = (pair.Value.PositionMean - candidate).sqrMagnitude;
                if (distSq < bestDistSq) { bestDistSq = distSq; nearest = pair.Value; }
            }
            if (nearest == null) return 0f;

            // Attack direction in own tech-local frame. We approximate "local" via the heading
            // vector — own.HeadingMean rotates world→local around Y for ground vehicles. This
            // is the same XZ approximation the rest of the optimizer uses.
            Vector3 attackWorld = nearest.PositionMean - candidate;
            if (attackWorld.sqrMagnitude < 1e-4f) return 0f;
            attackWorld.y = 0f;
            attackWorld.Normalize();
            float cosH = Mathf.Cos(-ownBelief.HeadingMean);
            float sinH = Mathf.Sin(-ownBelief.HeadingMean);
            Vector3 attackLocal = new Vector3(
                attackWorld.x * cosH - attackWorld.z * sinH,
                0f,
                attackWorld.x * sinH + attackWorld.z * cosH);

            var weak = vehicle.Armor.QueryWeakFace(attackLocal);
            // QueryWeakFace returns the face being attacked. Lower TotalHP = weaker = worse
            // for us. Normalize against a baseline 1000 HP face to get [-0.2, +0.2] range.
            const float BaselineHpPerFace = 1000f;
            const float Weight = 0.2f;
            float facingScore = (weak.TotalHP - BaselineHpPerFace) / BaselineHpPerFace;
            return Mathf.Clamp(facingScore, -1f, 1f) * Weight;
        }

        /// <summary>
        /// Reward candidates with terrain occlusion to the nearest hostile. Uses Physics.Linecast
        /// against the default raycast layer mask — same approach the engine takes for
        /// line-of-fire checks. Weight 0.15. Cost: one raycast per candidate × StepsPerTick × 4
        /// gradient samples = ~40 raycasts per tactical-optimizer tick, well within budget.
        /// </summary>
        private static float CoverUtility(Vector3 candidate, BeliefState ownBelief, BeliefSnapshot beliefs)
        {
            if (beliefs == null || beliefs.ByTech.Count == 0) return 0f;
            BeliefState nearest = null;
            float bestDistSq = float.MaxValue;
            foreach (var pair in beliefs.ByTech)
            {
                if (pair.Value.Team.Equals(ownBelief.Team)) continue;
                float distSq = (pair.Value.PositionMean - candidate).sqrMagnitude;
                if (distSq < bestDistSq) { bestDistSq = distSq; nearest = pair.Value; }
            }
            if (nearest == null) return 0f;
            // Linecast from candidate (slightly elevated to clear ground-plane jitter) to
            // the hostile's position. If anything blocks, candidate has cover → +0.15.
            const float Weight = 0.15f;
            Vector3 from = candidate + Vector3.up * 1.5f;
            Vector3 to = nearest.PositionMean + Vector3.up * 1.5f;
            return Physics.Linecast(from, to) ? Weight : 0f;
        }

        /// <summary>
        /// Bias the optimizer's preferred position by the tech's currently-assigned coordination
        /// role. Reads via <c>SmartRuntime.TryGetTechRole</c> which snapshots the team's
        /// most-recent CoordinationState. Returns 0 when no role has been published yet
        /// (first frame after spawn, Coordinator daemon hasn't ticked).
        /// </summary>
        private static float TeamRoleUtility(BeliefState ownBelief, Vector3 candidate)
        {
            if (!SmartRuntime.TryGetTechRole(ownBelief.Id, out var role)) return 0f;
            switch (role)
            {
                case Coordination.Role.Scout:     return  0.10f;  // reward forward exploration
                case Coordination.Role.Flanker:   return  0.05f;
                case Coordination.Role.Support:   return -0.05f;  // bias toward holding station
                case Coordination.Role.Retreater: return -0.10f;
                default:                          return  0f;
            }
        }

        private float EngagementUtility(Vector3 candidate, VehicleModelSnapshot vehicle, BeliefSnapshot beliefs, TAC_AI.AI.Forms.Smart.World.TeamId ownTeam)
        {
            if (beliefs == null || beliefs.ByTech.Count == 0 || vehicle.Weapons.Count == 0) return 0f;

            // Find the most relevant target — for v0.1.0, the closest enemy. Skip same-team
            // techs (friendlies). Engine team semantics: tank.Team < 0 is typically an enemy/
            // hostile team grouping; Smart's TeamId wraps the raw int and Equals does value
            // equality, so "ownTeam.Equals(belief.Team)" is exactly the friendly-vs-hostile
            // distinction the engine uses everywhere else.
            BeliefState nearest = null;
            float bestDistSq = float.MaxValue;
            foreach (var pair in beliefs.ByTech)
            {
                if (pair.Value.Team.Equals(ownTeam)) continue; // skip friendlies
                float distSq = (pair.Value.PositionMean - candidate).sqrMagnitude;
                if (distSq < bestDistSq) { bestDistSq = distSq; nearest = pair.Value; }
            }
            if (nearest == null) return 0f;

            float dist = Mathf.Sqrt(bestDistSq);

            // Preferred range = mean of own weapon ranges.
            float preferredRange = 0f;
            for (int i = 0; i < vehicle.Weapons.Count; i++) preferredRange += vehicle.Weapons[i].Range;
            preferredRange /= vehicle.Weapons.Count;
            if (preferredRange < 1f) preferredRange = 50f;

            // Gaussian fitness around preferred range.
            float delta = dist - preferredRange * 0.75f; // sit at 75% of weapon range
            float sigma = preferredRange * 0.3f;
            return Mathf.Exp(-(delta * delta) / (2f * sigma * sigma));
        }

        private float KinematicFeasibility(Vector3 candidate, BeliefState ownBelief, VehicleModelSnapshot vehicle)
        {
            Vector3 displacement = candidate - ownBelief.PositionMean;
            float dist = displacement.magnitude;
            float maxReachableInLookahead = vehicle.Mobility.TopSpeedForward * LookAheadSeconds;
            if (maxReachableInLookahead < 1f) maxReachableInLookahead = 1f;

            // Soft penalty for unreachable goals — utility drops sharply as goal exceeds reach.
            float ratio = dist / maxReachableInLookahead;
            if (ratio < 1f) return 1f;
            return Mathf.Exp(-(ratio - 1f) * 3f);
        }
    }
}
