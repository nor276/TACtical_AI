using System;
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
        Generic = 0,        // P6 Item 27: NOW registered (was bypass-only). Dispatches via IdentityContext.TacticalGoalHandle -> per-controller _tactical.Step. Behavior bit-identical to v0.1 inline path.
        Hunter,             // mobile armed ground/sea - seek+kill, meander when no target
        Sniper,             // long-range armed (authored only at v0.1; composition rule deferred)
        Base,               // anchored / authored stationary defender - hold position, never roam
        Gatherer,           // resource collector - resource->base loop; flees when provoked
        AircraftSupport,    // air-class + has same-team ally - orbit team centroid, suppress threats
        AircraftHunter,     // air-class + alone - Hunter pattern in 3D
        RepairSupport = 7,  // P6 Item 26: ground + armed + Shield blocks; orbits damaged friendly (gated SmartIdentityTuning.EnableRepairSupport)
        Patrol = 8,         // P6 Item 28: solo ground armed; meander spawn anchor + react to nearby hostiles (gated SmartIdentityTuning.EnablePatrol)
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

        // P6 Item 27: per-controller delegate that wraps _tactical.Step(belief, vehicle, beliefs).
        // Signature MATCHES TacticalOptimizer.Step at TacticalOptimizer.cs:59 — middle arg is
        // VehicleModelSnapshot, not BeliefSnapshot. Null when no controller has wired one
        // (default-constructed IdentityContext or pre-P6 callers).
        public readonly Func<BeliefState, VehicleModelSnapshot, BeliefSnapshot, TacticalGoal> TacticalGoalHandle;

        public IdentityContext(TechId selfTechId, SmartIdentityStamp stamp, Vector3 teamCentroid,
            bool hasAllies, TechId? nearestOwnBaseId, long tickCounter,
            Func<BeliefState, VehicleModelSnapshot, BeliefSnapshot, TacticalGoal> tacticalGoalHandle = null)
        {
            SelfTechId = selfTechId;
            Stamp = stamp;
            TeamCentroid = teamCentroid;
            HasAllies = hasAllies;
            NearestOwnBaseId = nearestOwnBaseId;
            TickCounter = tickCounter;
            TacticalGoalHandle = tacticalGoalHandle;
        }

        /// <summary>Returns a copy of this context with the supplied tactical handle attached.</summary>
        public IdentityContext WithTacticalGoalHandle(
            Func<BeliefState, VehicleModelSnapshot, BeliefSnapshot, TacticalGoal> handle)
            => new IdentityContext(SelfTechId, Stamp, TeamCentroid, HasAllies, NearestOwnBaseId, TickCounter, handle);
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

    // Singleton registry of identity -> goal source.
    // P6 Item 27 (REV 7): Generic IS registered as of v0.2 — its Produce delegates to the
    // per-controller TacticalOptimizer via IdentityContext.TacticalGoalHandle. The dispatcher
    // in ContinuousController wraps every ctx with the per-controller handle before calling
    // Produce, so per-instance Adam state lives where it always did (on _tactical) — no
    // singleton-state contamination. Behavior at the dispatch level is bit-identical to the
    // pre-v0.2 inline path (call-equivalence proof in V0.2-PLAN-REV7.md Item 27).
    // See Docs/SMART-IDENTITY-DESIGN.md sec 7.3 for the design.
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
