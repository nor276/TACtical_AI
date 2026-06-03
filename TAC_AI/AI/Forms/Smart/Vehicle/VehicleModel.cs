using System;
using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Coarse single-role classification for a block.
    /// Aether/Chassis (REV 3.1): kept as a backward-compat shim. The Chassis pipeline uses
    /// the multi-bit <see cref="BlockKindFlags"/> on <see cref="BlockArchetype.Kinds"/>;
    /// BlockRole stays here so PropulsionBlock (legacy struct shared with diagnostics) and
    /// any older Vehicle/Identity reader continue to compile.
    /// </summary>
    public enum BlockRole
    {
        Structural,
        Wheel,
        Jet,
        Hover,
        Prop,
        Walker,
        WeaponFixed,
        WeaponTurret,
        EnergyShield,
        Battery,
        Repair,
        Other,
    }

    /// <summary>
    /// Per-tech vehicle model snapshot. Immutable. Published via DoubleBuffer.
    /// Per VEHICLE-CONTRACT §2.
    ///
    /// Chassis Step 8a: Thrust re-typed from <c>ThrustMap</c> (deleted) to <see cref="ThrustField"/>;
    /// new <see cref="KindCounts"/> field carries per-bit tallies from the Chassis pipeline.
    /// </summary>
    public sealed class VehicleModelSnapshot
    {
        public int TechIdValue { get; }
        public long ConstructedAtTick { get; }
        public MassDistribution Mass { get; }
        public ThrustField Thrust { get; }
        public IReadOnlyList<WeaponProfile> Weapons { get; }
        public ArmorMap Armor { get; }
        public KinematicState Kinematics { get; }
        public MobilityProfile Mobility { get; }
        public BlockKindCounts KindCounts { get; }

        public VehicleModelSnapshot(
            int techIdValue,
            long constructedAtTick,
            MassDistribution mass,
            ThrustField thrust,
            IReadOnlyList<WeaponProfile> weapons,
            ArmorMap armor,
            KinematicState kinematics,
            MobilityProfile mobility,
            BlockKindCounts kindCounts)
        {
            TechIdValue = techIdValue;
            ConstructedAtTick = constructedAtTick;
            Mass = mass;
            Thrust = thrust;
            Weapons = weapons ?? Array.Empty<WeaponProfile>();
            Armor = armor;
            Kinematics = kinematics;
            Mobility = mobility;
            KindCounts = kindCounts;
        }

        /// <summary>Empty placeholder snapshot. Used as the initial state until first rebuild.</summary>
        public static VehicleModelSnapshot Empty(int techIdValue) => new VehicleModelSnapshot(
            techIdValue,
            constructedAtTick: 0,
            mass: MassDistribution.Empty,
            thrust: ThrustField.Empty,
            weapons: Array.Empty<WeaponProfile>(),
            armor: ArmorMap.Empty,
            kinematics: KinematicState.Zero,
            mobility: MobilityProfile.Default,
            kindCounts: BlockKindCounts.Empty);
    }
}
