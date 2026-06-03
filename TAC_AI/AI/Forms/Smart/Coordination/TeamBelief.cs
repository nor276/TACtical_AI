using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// Aggregates per-friendly LOS into shared belief: an enemy is "in sight" by the team
    /// iff at least one friendly currently has LOS. Per COORDINATION-CONTRACT §3.
    ///
    /// Aether v0.1: InjectObservations is stubbed — has zero callers in the repo today.
    /// v0.2: route ally-LOS observations through <see cref="Observer.Submit"/> and the
    /// per-tech <see cref="WorldModel.Intake"/> slot.
    /// </summary>
    public sealed class TeamBelief
    {
        private readonly WorldModel _world;
        private readonly Dictionary<TechId, List<TechId>> _losCoverage = new Dictionary<TechId, List<TechId>>();

        public IReadOnlyDictionary<TechId, List<TechId>> LOSCoverage => _losCoverage;

        public TeamBelief(WorldModel world)
        {
            _world = world;
        }

        public void Clear() => _losCoverage.Clear();

        /// <summary>
        /// Record one friendly's LOS list for this tick. Called per friendly per tick.
        /// </summary>
        public void RecordLOS(TechId observer, List<TechId> seen)
        {
            _losCoverage[observer] = seen;
        }

        /// <summary>
        /// Returns whether <paramref name="enemy"/> is observed by anyone on the team.
        /// </summary>
        public bool TeamHasLOS(TechId enemy)
        {
            foreach (var kv in _losCoverage)
            {
                var list = kv.Value;
                for (int i = 0; i < list.Count; i++) if (list[i] == enemy) return true;
            }
            return false;
        }

        /// <summary>
        /// Inject ground-truth observations for every enemy currently observed by any friendly.
        ///
        /// Aether v0.1: STUBBED. Has zero callers in the repo today (verified pre-migration).
        /// When a real producer is added in v0.2, route through <c>Observer.Submit</c> with
        /// the per-tech intake slot from <see cref="WorldModel.Intake"/>, not through the
        /// removed <c>RecordObservation</c> path.
        /// </summary>
        public void InjectObservations(
            IReadOnlyDictionary<TechId, Tank> enemyTanks)
        {
            throw new System.NotImplementedException(
                "Aether v0.2: route ally-LOS observations through Observer.Submit and WorldModel.Intake.");
        }
    }
}
