using System;
using TAC_AI.AI.Engine;

namespace TAC_AI.AI.Behaviors
{
    /// <summary>
    /// The functional family a behavior belongs to. Mirrors the divisions the AI already had
    /// (combat attack-mode, idle/patrol, economy roles, support roles) - NOT a new taxonomy.
    /// A profile picks at most one behavior per category.
    /// </summary>
    public enum AIBehaviorCategory
    {
        Attack,     // Engage: the TryAdjustForCombat bodies + the enemy attack-mode buckets
        Retreat,    // Safety: GetRetreatLocation / CanRetreat / Scurry overlay
        Idle,       // Idle/Patrol: DispatchNoTargetIdle / DefaultIdle / HomingIdle / LollyGag
        Economy,    // Mine / Scavenge (RMiner == BProspector, RScavenger == BScrapper)
        Support,    // Escort / Protect / Charge / MultiTech (BAegis == RGuardian, the escort family)
    }

    /// <summary>
    /// The locomotion class a behavior is built for. Discovery keys off this (folder / attribute).
    /// Shared = loco-agnostic (the RGeneral/BGeneral helpers); the rest carry a per-loco implementation.
    /// </summary>
    public enum VehicleClass
    {
        Shared,
        Land,
        Sea,
        Air,
        Space,
        Static,
    }

    /// <summary>
    /// Tags a behavior/profile type for folder-independent discovery. The loader keys a type by
    /// (VehicleClass, Category) from this attribute first, falling back to the namespace if absent.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class AIBehaviorAttribute : Attribute
    {
        public AIBehaviorCategory Category { get; }
        public VehicleClass VehicleClass { get; }
        /// <summary>Stable id used for selection + save. If null the loader uses the type name.</summary>
        public string Id { get; }

        public AIBehaviorAttribute(AIBehaviorCategory category, VehicleClass vehicleClass, string id = null)
        {
            Category = category;
            VehicleClass = vehicleClass;
            Id = id;
        }
    }

    /// <summary>
    /// One swappable unit of AI behavior - the extracted body of a single handler-case /
    /// TryAdjustForCombat block. Alignment-agnostic: the ally and enemy version of a behavior are
    /// the SAME module; where they genuinely differed it becomes a parameter read from IAIContext.
    /// A behavior touches only IAIContext and owns its tunables. Ticked in the Operations cadence.
    /// </summary>
    public interface IBehavior
    {
        AIBehaviorCategory Category { get; }
        VehicleClass VehicleClass { get; }
        /// <summary>Stable name, for selection + save.</summary>
        string Id { get; }

        /// <summary>Produce drive intent for this pass. Alignment is read from ctx, not a separate class.</summary>
        void Tick(IAIContext ctx);
        /// <summary>Idempotent install hook (cache refs, reset latches owned by this behavior).</summary>
        void OnInstall(IAIContext ctx);
        /// <summary>Teardown when swapped out - see IAIContext.BehaviorTeardownState.</summary>
        void OnRemove(IAIContext ctx);
    }
}
