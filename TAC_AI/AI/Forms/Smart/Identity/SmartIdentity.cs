using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Identity
{
    // Per-tech purpose classification. Stamped once at OnTechSpawn from authored RawTech intent
    // first, composition fallback second. Drives identity-specific goal sources in the
    // ContinuousController fallback path (Coordinator goals still win when fresh).
    // See Docs/SMART-IDENTITY-DESIGN.md for the full design.
    public enum SmartIdentity : byte
    {
        Generic = 0,        // unauthored + no composition match - controller falls through to TacticalOptimizer
        Hunter,             // mobile armed ground/sea - seek+kill, meander when no target
        Sniper,             // long-range armed (authored only at v0.1; composition rule deferred)
        Base,               // anchored / authored stationary defender - hold position, never roam
        Gatherer,           // resource collector - resource->base loop; flees when provoked
        AircraftSupport,    // air-class + has same-team ally - orbit team centroid, suppress threats
        AircraftHunter,     // air-class + alone - Hunter pattern in 3D
    }

    // Immutable identity stamp captured at spawn. SpawnAnchor is the tech's spawn-time
    // boundsCentre - used by Base hold-position, Gatherer solo hub, Sniper perch fallback,
    // and Hunter deterministic Lissajous seed.
    public readonly struct SmartIdentityStamp
    {
        public readonly SmartIdentity Identity;
        public readonly bool FromAuthored;       // true = authored hint hit; false = composition fallback
        public readonly long ClassifiedAtTick;   // diagnostic
        public readonly Vector3 SpawnAnchor;

        public SmartIdentityStamp(SmartIdentity identity, bool fromAuthored, long classifiedAtTick, Vector3 spawnAnchor)
        {
            Identity = identity;
            FromAuthored = fromAuthored;
            ClassifiedAtTick = classifiedAtTick;
            SpawnAnchor = spawnAnchor;
        }

        public static SmartIdentityStamp DefaultGeneric => new SmartIdentityStamp(SmartIdentity.Generic, false, 0L, Vector3.zero);
    }

    // Per-tick context passed to identity goal sources. TeamCentroid is undefined unless
    // HasAllies is true - consumers MUST gate on HasAllies first. World origin (0,0,0) is a
    // valid position, so Vector3.zero is NOT a sentinel for "no team".
    public readonly struct IdentityContext
    {
        public readonly TechId SelfTechId;
        public readonly SmartIdentityStamp Stamp;
        public readonly Vector3 TeamCentroid;
        public readonly bool HasAllies;
        public readonly TechId? NearestOwnBaseId;
        public readonly long TickCounter;

        public IdentityContext(TechId selfTechId, SmartIdentityStamp stamp, Vector3 teamCentroid,
            bool hasAllies, TechId? nearestOwnBaseId, long tickCounter)
        {
            SelfTechId = selfTechId;
            Stamp = stamp;
            TeamCentroid = teamCentroid;
            HasAllies = hasAllies;
            NearestOwnBaseId = nearestOwnBaseId;
            TickCounter = tickCounter;
        }
    }

    // Per-identity goal producer. Implementations are stateless singletons in
    // SmartIdentityRegistry. Per-tech mutable scratch lives on SmartPerTechState (not yet
    // wired - Phase 2+ adds IdentityScratch when the first concrete identity lands).
    // Called on the main thread from ContinuousController.OnOperationsTick.
    public interface ISmartGoalSource
    {
        SmartIdentity Identity { get; }
        TacticalGoal Produce(BeliefState ownBelief, VehicleModelSnapshot vehicle,
                             BeliefSnapshot beliefs, IdentityContext ctx);
    }

    // Singleton registry of identity -> goal source. Generic is NOT in the registry by
    // design (see Docs/SMART-IDENTITY-DESIGN.md sec 7.3) - the dispatcher bypasses identity
    // for Generic and calls TacticalOptimizer.Step inline.
    // Phase 1 registry is empty; Phase 2+ register concrete identities here.
    public static class SmartIdentityRegistry
    {
        private static readonly Dictionary<SmartIdentity, ISmartGoalSource> _sources =
            new Dictionary<SmartIdentity, ISmartGoalSource>();

        public static void Register(ISmartGoalSource source)
        {
            if (source == null) return;
            _sources[source.Identity] = source;
        }

        public static ISmartGoalSource For(SmartIdentity identity)
        {
            return _sources.TryGetValue(identity, out var src) ? src : null;
        }
    }
}
