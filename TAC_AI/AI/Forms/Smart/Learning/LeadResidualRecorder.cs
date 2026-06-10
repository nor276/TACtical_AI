using System.Collections.Concurrent;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P7 Item 18: per-target lead-residual recorder. Captures the predicted impact point
    /// at fire-commit time; when a later observation of the same target arrives near the
    /// projected arrival time, computes the residual (actual - predicted) and emits a
    /// <see cref="ResidualEvent"/> into the <see cref="TrajectoryResidualModel"/> queue.
    ///
    /// Singleton: <see cref="Instance"/> exposed for the WeaponFireController call site
    /// (ContinuousController dispatch). Lifecycle owned by LearningService — reset on
    /// Init/Shutdown.
    ///
    /// v0.2 matching shape: at most one pending fire-commit per target. New OnFireCommit
    /// overwrites any pending entry. OnObservation matches and emits if the elapsed time
    /// between commit and observation is within tolerance of the projected projectile time
    /// (with a ±0.25s slop). Coarser than per-projectile tracking but adequate to start
    /// populating the residual training queue.
    /// </summary>
    public sealed class LeadResidualRecorder : TAC_AI.AI.Forms.Smart.World.ITechSidecar
    {
        public static LeadResidualRecorder Instance { get; private set; }
        public static void Install(LeadResidualRecorder inst) { Instance = inst; }
        public static void Uninstall() { Instance = null; }

        // L-028 ITechSidecar wiring.
        public string Name => "LeadResidualRecorder";
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotKeys()
            => (System.Collections.Generic.IReadOnlyCollection<TechId>)_pending.Keys;

        private struct Pending
        {
            internal Vector3 PredictedPos;
            internal Vector3 OwnPos;
            internal float ProjTimeSec;
            internal long FireTickMono;
            // FEATURE-EXPANSION-PLAN §3.4 R2-04: fire-time feature slice (Residual block,
            // 48 floats). Captured at fire instant — NOT at arrival. The model treats
            // [32..34] (LinearExtrapXYZ) as input and the post-arrival observed residual
            // as the training label, NEVER mixed into the features. Null when the
            // ContinuousController didn't have a StrategicStateVector slice handy.
            internal float[] FireTimeFeatures;
        }

        private readonly ConcurrentDictionary<TechId, Pending> _pending =
            new ConcurrentDictionary<TechId, Pending>();

        // ±0.25s slop window around the projected arrival.
        private const float ArrivalSlopSec = 0.25f;

        public void OnFireCommit(TechId target, Vector3 predictedPos, Vector3 ownPos,
                                 float projTimeSec, long fireTickMono)
            => OnFireCommit(target, predictedPos, ownPos, projTimeSec, fireTickMono, fireTimeFeatures: null);

        // FEATURE-EXPANSION-PLAN §3.4 R2-04: widened overload that takes the
        // ContinuousController's fire-time slice of the StrategicStateVector Residual
        // block. Caller passes a length-48 float[] (or null when not yet wired).
        public void OnFireCommit(TechId target, Vector3 predictedPos, Vector3 ownPos,
                                 float projTimeSec, long fireTickMono, float[] fireTimeFeatures)
        {
            if (projTimeSec <= 0f) return;
            _pending[target] = new Pending
            {
                PredictedPos = predictedPos,
                OwnPos = ownPos,
                ProjTimeSec = projTimeSec,
                FireTickMono = fireTickMono,
                FireTimeFeatures = fireTimeFeatures,
            };
        }

        public void OnObservation(TechId target, Vector3 actualPos, long obsTickMono)
        {
            Pending p;
            if (!_pending.TryGetValue(target, out p)) return;
            float elapsedSec = MonoClock.Seconds(p.FireTickMono, obsTickMono);
            // Only emit when the observation arrives near the projected impact window.
            if (elapsedSec < p.ProjTimeSec - ArrivalSlopSec) return;          // too early
            if (elapsedSec > p.ProjTimeSec + ArrivalSlopSec)
            {
                // Window passed without a matching observation — drop the pending entry.
                Pending _; _pending.TryRemove(target, out _);
                return;
            }
            Vector3 residual = actualPos - p.PredictedPos;
            // FEATURE-EXPANSION-PLAN §3.4 / R2-04: when the ContinuousController passed a
            // 48-float fire-time slice, use it verbatim. Otherwise fall back to the v0.2
            // skeleton (still 48-wide post-Phase-3) populating projection-geometry slots
            // [0..7] only. The ObservedResidual XYZ stays the LABEL — never written into
            // the feature vector (R2 F-09 anti-leakage; ResidualEvent.ObservedResidual is
            // the canonical label channel).
            float[] features;
            if (p.FireTimeFeatures != null && p.FireTimeFeatures.Length == TrajectoryResidualModel.FeatureDim)
            {
                features = p.FireTimeFeatures;
            }
            else
            {
                features = new float[TrajectoryResidualModel.FeatureDim];
                Vector3 toTarget = p.PredictedPos - p.OwnPos;
                float distance = toTarget.magnitude;
                features[0] = distance;
                features[1] = p.ProjTimeSec;
                features[2] = elapsedSec;
                if (distance > 1e-3f)
                {
                    Vector3 dir = toTarget / distance;
                    features[3] = dir.x;
                    features[4] = dir.y;
                    features[5] = dir.z;
                }
                // [6..47] left zero — extractor-wired Residual slice is the path to
                // a fully-populated input vector.
            }

            var residualModel = LearningService.Residual;
            if (residualModel != null)
            {
                var residualEvent = new ResidualEvent(target, features, residual);
                residualModel.EventQueue.Enqueue(residualEvent);
                // Tee into IdentityReplayBank. Identity is the target's (the residual is
                // about predicting THIS target's motion).
                var identity = TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.ResolveIdentity(target);
                TAC_AI.AI.Forms.Smart.Director.IdentityReplayBank.TeeResidual(identity, residualEvent);
            }

            // Pending consumed.
            { Pending _; _pending.TryRemove(target, out _); }
        }

        public void Forget(TechId target) { Pending _; _pending.TryRemove(target, out _); }
        public void Clear() => _pending.Clear();
        public int PendingCount => _pending.Count;
    }
}
