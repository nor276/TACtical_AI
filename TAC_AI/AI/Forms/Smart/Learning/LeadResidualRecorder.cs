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
        }

        private readonly ConcurrentDictionary<TechId, Pending> _pending =
            new ConcurrentDictionary<TechId, Pending>();

        // ±0.25s slop window around the projected arrival.
        private const float ArrivalSlopSec = 0.25f;

        public void OnFireCommit(TechId target, Vector3 predictedPos, Vector3 ownPos,
                                 float projTimeSec, long fireTickMono)
        {
            if (projTimeSec <= 0f) return;
            _pending[target] = new Pending
            {
                PredictedPos = predictedPos,
                OwnPos = ownPos,
                ProjTimeSec = projTimeSec,
                FireTickMono = fireTickMono,
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
            // Build feature vector matching TrajectoryResidualModel.FeatureDim (15). v0.2
            // placeholder: features are the lead-relevant kinematic state (own→target
            // bearing + distance + projected time). Real feature wiring at v0.3 when the
            // VehicleModelSnapshot for the target is plumbed.
            var features = new float[TrajectoryResidualModel.FeatureDim];
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
            features[6] = residual.x;
            features[7] = residual.y;
            features[8] = residual.z;
            // features[9..14] reserved for v0.3 VehicleModelSnapshot features.

            var residualModel = LearningService.Residual;
            if (residualModel != null)
                residualModel.EventQueue.Enqueue(new ResidualEvent(target, features, residual));

            // Pending consumed.
            { Pending _; _pending.TryRemove(target, out _); }
        }

        public void Forget(TechId target) { Pending _; _pending.TryRemove(target, out _); }
        public void Clear() => _pending.Clear();
        public int PendingCount => _pending.Count;
    }
}
