using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Threading;

namespace TAC_AI.AI.Forms.Smart.World
{
    /// <summary>
    /// Per-tech entry in the world model. Holds the latest belief and the per-tech belief
    /// double-buffer. Aether v0.1: dropped LatestObservation/HasFreshObservation flag pair
    /// (replaced by per-tech ObservationIntake slot, see <see cref="WorldModel.Intake"/>).
    /// </summary>
    public sealed class PerTechEntry
    {
        public TechId Id { get; }
        public BeliefState PriorBelief { get; internal set; }
        public DoubleBuffer<BeliefState> PerTechBuffer { get; }

        public PerTechEntry(TechId id, BeliefState initialBelief)
        {
            Id = id;
            PriorBelief = initialBelief;
            PerTechBuffer = new DoubleBuffer<BeliefState>(initialBelief);
        }
    }

    /// <summary>
    /// Smart's world-model state container. Owns per-tech belief entries, the team-level
    /// fused snapshot buffer, the per-tech observation intake registry, and the lifecycle
    /// hooks Smart's OnTechSpawn / OnTechRecycle call into. Per WORLD-CONTRACT §1.
    ///
    /// Thread safety: per-tech entries are tracked via a ConcurrentDictionary so the AetherFuser
    /// (worker thread) and main-thread lifecycle hooks race-safely. PerTechEntry.PriorBelief is
    /// written by AetherFuser on each tick (worker) and by <see cref="UpdateTeam"/> on the main
    /// thread; same-shape immutable-reference writes through Interlocked.Exchange semantics
    /// via the DoubleBuffer publish, with the field write itself benign as a reference swap
    /// (readers see either the old or new reference, both fully constructed).
    /// </summary>
    public sealed class WorldModel : ITechSidecar
    {
        // L-028 ITechSidecar wiring. Forget aliases the existing DeregisterTech.
        public string Name => "WorldModel";
        public void Forget(TechId id) => DeregisterTech(id);
        public System.Collections.Generic.IReadOnlyCollection<TechId> SnapshotKeys()
            => (System.Collections.Generic.IReadOnlyCollection<TechId>)_byTech.Keys;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<TechId, PerTechEntry> _byTech;
        private readonly DoubleBuffer<BeliefSnapshot> _fusedBuffer;
        private readonly ObservationIntake _intake;

        /// <summary>The team-wide fused snapshot consumers read.</summary>
        public DoubleBuffer<BeliefSnapshot> FusedBuffer => _fusedBuffer;

        /// <summary>
        /// Per-tech observation intake. Main-thread writers call <see cref="Observer.Submit"/>;
        /// AetherFuser reads via <c>TryGet</c> / slot <c>TryRead</c>.
        /// </summary>
        public ObservationIntake Intake => _intake;

        public WorldModel()
        {
            _byTech = new System.Collections.Concurrent.ConcurrentDictionary<TechId, PerTechEntry>();
            _fusedBuffer = new DoubleBuffer<BeliefSnapshot>(BeliefSnapshot.Empty);
            _intake = new ObservationIntake();
        }

        /// <summary>
        /// Register a newly-spawned tech with an initial belief. Called from Smart's
        /// OnTechSpawn handler (main thread). Aether v0.1: signature changed from
        /// <c>float heading</c> to <c>Vector3 forward</c> — preserves aircraft pitch/roll
        /// and matches the new BeliefState shape's 3D forward anchor.
        /// </summary>
        public PerTechEntry RegisterTech(TechId id, TeamId team, Vector3 position, Vector3 velocity, Vector3 forward, float maxAccelEstimate, long currentTick)
        {
            var initial = BeliefStateFactory.NewlyObserved(id, team, position, velocity, forward, maxAccelEstimate, currentTick);
            var entry = new PerTechEntry(id, initial);
            _byTech[id] = entry;
            _intake.GetOrCreate(id);
            WorldEventBus.Publish(new TechSpawned(id, team, position));
            return entry;
        }

        /// <summary>
        /// Drop a tech from the world model. Called from Smart's OnTechRecycle handler.
        /// </summary>
        public bool DeregisterTech(TechId id)
        {
            PerTechEntry _;
            if (!_byTech.TryRemove(id, out _)) return false;
            _intake.Remove(id);
            // L-083: the hard-coded per-sidecar fan-out lived here AND in SmartRuntime.Deregister
            // — duplicated cleanup that drifted apart subtly when new sidecars landed. Now
            // SmartRuntime.Deregister owns the TechLifecycleRegistry.ForgetAll fan-out
            // (L-055); WorldModel just publishes TechDespawned and lets the registry handle
            // the rest. SmartRuntime.Deregister callers that pass through WorldModel still
            // get the registry fan-out via their own SmartRuntime.Deregister call.
            WorldEventBus.Publish(new TechDespawned(id));
            return true;
        }

        /// <summary>
        /// Phase 3.3 (FIX-PLAN.md): update a per-tech belief to reflect a team change.
        /// Constructs a fresh BeliefState via <see cref="BeliefStateFactory.WithTeam"/> with
        /// the same anchors but the new team, and publishes it to the per-tech buffer so
        /// consumers (ThreatField, planners) immediately see the new allegiance. Called
        /// from <see cref="SmartEventBridge.OnTankTeamChanged"/>.
        /// </summary>
        public bool UpdateTeam(TechId id, TeamId newTeam)
        {
            PerTechEntry entry;
            if (!_byTech.TryGetValue(id, out entry)) return false;
            var prior = entry.PriorBelief;
            if (prior == null) return false;
            var rebound = BeliefStateFactory.WithTeam(prior, newTeam);
            entry.PriorBelief = rebound;
            entry.PerTechBuffer.Write(rebound);
            return true;
        }

        /// <summary>
        /// Predicate for callers that need idempotent registration. Used by SmartEventBridge
        /// to avoid clobbering a Smart-driven tech's entry (which already carries its
        /// vehicle-derived maxAccel) when the engine's TankPostSpawnEvent fires for the same
        /// tank — both event paths race on every spawn, so the second one to fire would
        /// overwrite the first's entry and republish a duplicate TechSpawned without this gate.
        /// </summary>
        public bool IsRegistered(TechId id) => _byTech.ContainsKey(id);

        /// <summary>
        /// Update a per-tech belief and publish to that tech's buffer. Called by AetherFuser.
        /// </summary>
        public void PublishPerTechBelief(TechId id, BeliefState belief)
        {
            PerTechEntry entry;
            if (!_byTech.TryGetValue(id, out entry)) return;
            entry.PriorBelief = belief;
            entry.PerTechBuffer.Write(belief);
        }

        /// <summary>
        /// Read snapshot of current per-tech entries for the worker to iterate without
        /// holding the dictionary lock.
        /// </summary>
        public Dictionary<TechId, PerTechEntry> SnapshotPerTechState()
        {
            var copy = new Dictionary<TechId, PerTechEntry>(_byTech.Count);
            foreach (var kv in _byTech) copy[kv.Key] = kv.Value;
            return copy;
        }

        /// <summary>Per-tech buffer accessor for downstream readers.</summary>
        public DoubleBuffer<BeliefState> GetPerTechBuffer(TechId id)
        {
            PerTechEntry entry;
            return _byTech.TryGetValue(id, out entry) ? entry.PerTechBuffer : null;
        }

        /// <summary>Clear all state. Called on world reset.</summary>
        public void Clear()
        {
            _byTech.Clear();
            _intake.Clear();
            _fusedBuffer.Write(BeliefSnapshot.Empty);
        }
    }
}
