using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    /// <summary>
    /// P6 Item 28 (REV 2/6/7): Patrol goal source. Solo armed mobile ground techs.
    /// Behavior:
    ///   - If a hostile is within <see cref="ProvocationRadius"/>: pursue (HunterGoalSource-style).
    ///   - Else: meander deterministically around <see cref="IdentityContext.Stamp"/>.SpawnAnchor
    ///     via a Lissajous pattern seeded by TechId (matches GathererGoalSource.cs:80-95 template).
    ///
    /// REV 2 reshape: classifier-time threat check is structurally impossible
    /// (<see cref="SmartIdentityClassifier.Classify"/> has no BeliefSnapshot param). The threat
    /// decision lives here, in Produce, where BeliefSnapshot is available per the
    /// <see cref="ISmartGoalSource.Produce"/> contract.
    ///
    /// Defaults preserving v0.1: gated by <see cref="SmartIdentityTuning.EnablePatrol"/>
    /// (default false). With OFF, no tech classifies as Patrol so this goal source never runs.
    /// </summary>
    public sealed class PatrolGoalSource : ISmartGoalSource
    {
        public SmartIdentity Identity => SmartIdentity.Patrol;

        public const float ProvocationRadius = 80f;
        public const float MeanderRadius = 60f;
        public const float MeanderTimeScale = 0.02f;

        public TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                                    BeliefSnapshot beliefs, IdentityContext ctx)
        {
            // Inline hostile scan over BeliefSnapshot.ByTech. Verified field names: b.Team, b.Id.
            // Friendliness check mirrors TacticalOptimizer.cs:141 pattern.
            BeliefState nearest = null;
            float bestSqr = ProvocationRadius * ProvocationRadius;
            foreach (var kv in beliefs.ByTech)
            {
                var b = kv.Value;
                if (b.Team.Equals(ownBelief.Team)) continue;     // skip friendlies
                if (b.Id.Equals(ctx.SelfTechId)) continue;        // skip self
                float d = (b.PositionMean - ownBelief.PositionMean).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; nearest = b; }
            }
            if (nearest != null)
            {
                // Pursue: drive-to with engagement velocity vector.
                // Real TacticalGoal ctor per CostFunction.cs:20 — (pos, heading, vel, lookAheadSec).
                // Engagement intent encoded by velocity vector (no Kind enum in v0.2).
                Vector3 toHostile = nearest.PositionMean - ownBelief.PositionMean;
                Vector3 headingVec = toHostile.sqrMagnitude > 1e-4f ? toHostile.normalized : Vector3.forward;
                float heading = Mathf.Atan2(headingVec.x, headingVec.z);
                const float PursuitSpeed = 1f;
                const float LookAheadSec = 1.0f;
                Vector3 velocity = headingVec * PursuitSpeed * (1f / LookAheadSec) * 10f; // pursuit pace
                return new TacticalGoal(nearest.PositionMean, heading, velocity, LookAheadSec);
            }
            // Idle meander: deterministic Lissajous around SpawnAnchor seeded by TechId.
            // Real MonoClock surface per MonoClock.cs:22-50: Now()/Seconds()/TickFreq.
            // Stopwatch.GetTimestamp on Windows = QPC (system-boot relative, not process-start) —
            // irrelevant here because Lissajous uses deltas across ticks (seed is per-tech-stable;
            // t advances at a fixed rate per second). Absolute origin doesn't matter.
            float seed = (ctx.SelfTechId.Value & 0xFFFF) * 0.001f;
            float t = (float)(MonoClock.Now() * MonoClock.TickFreq) * MeanderTimeScale + seed;
            var offset = new Vector3(Mathf.Sin(t * 1.7f), 0f, Mathf.Cos(t * 1.1f)) * MeanderRadius;
            Vector3 meanderTarget = ctx.Stamp.SpawnAnchor + offset;
            Vector3 meanderHeadingVec = offset.sqrMagnitude > 1e-4f ? offset.normalized : Vector3.forward;
            float meanderHeading = Mathf.Atan2(meanderHeadingVec.x, meanderHeadingVec.z);
            const float MeanderSpeed = 0.5f;
            const float MeanderLookAheadSec = 1.0f;
            Vector3 meanderVelocity = (meanderTarget - ownBelief.PositionMean) / MeanderLookAheadSec * MeanderSpeed;
            return new TacticalGoal(meanderTarget, meanderHeading, meanderVelocity, MeanderLookAheadSec);
        }
    }
}
