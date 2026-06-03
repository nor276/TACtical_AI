using System;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: multi-role block taxonomy. [Flags] uint per CHASSIS-DESIGN.md core design.
    ///
    /// Replaces the single-value <see cref="BlockRole"/> enum's information loss: a block
    /// with both ModuleAnchor and ModuleWeapon can carry both bits, where the old enum had
    /// to pick one. <see cref="BlockArchetype.Kinds"/> is the multi-bit field; BlockRole
    /// stays as a backward-compat shim via PrimaryKind().
    ///
    /// Walker / Drill / Repair are reserved-unreachable in v0.1 per Decisions #7 + #12.
    /// ModuleWalker is absent in this TerraTech version; ModuleHammer (REV 1's claimed
    /// Drill+Repair source) has zero codebase references, so no v0.1 probe sets those bits.
    /// They remain in the bit layout so v0.2 can wire probes without an ABI break.
    /// </summary>
    [Flags]
    public enum BlockKindFlags : uint
    {
        None           = 0,
        Structural     = 1u << 0,
        Wheel          = 1u << 1,
        Hover          = 1u << 2,
        Booster        = 1u << 3,
        Wing           = 1u << 4,
        Walker         = 1u << 5,   // reserved-unreachable v0.1
        WeaponFixed    = 1u << 6,
        WeaponTurret   = 1u << 7,
        Anchor         = 1u << 8,
        Conveyor       = 1u << 9,
        Holder         = 1u << 10,  // ModuleItemHolder w/ Collector flag
        Producer       = 1u << 11,
        Repair         = 1u << 12,  // reserved-unreachable v0.1
        Shield         = 1u << 13,
        EnergyStore    = 1u << 14,
        Generator      = 1u << 15,
        Drill          = 1u << 16,  // reserved-unreachable v0.1
        GyroStabilizer = 1u << 17,
    }
}
