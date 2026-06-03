using System;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: which propulsion mechanism an emitter implements. Determines aggregation
    /// bucket (LiftBudget vs JetBudget vs GroundTractionBudget vs AeroLiftBudget) and
    /// the wheel-substitution branch in ThrustField.Compute.
    ///
    /// Per Decision #12: REV 3 dropped REV 2's JetExhaust; v0.1 enumerates only the kinds
    /// with verified probe paths. FanLift renamed to FanJet to match the production module
    /// child name verified at AIControllerAir.cs:151-198.
    /// </summary>
    public enum EmitterKind : byte
    {
        WheelRoll      = 0,  // ModuleWheels -- chassis +Z substitution (per Decision #3)
        FanJet         = 1,  // ModuleBooster FanJet child
        BoosterImpulse = 2,  // ModuleBooster BoosterJet child
        HoverPad       = 3,  // ModuleHover -- v0.1 2g placeholder per S1/S7
        WingLift       = 4,  // ModuleWing -- v0.1 2g placeholder per S1/S7
    }

    /// <summary>
    /// Per-emitter modifier bits orthogonal to <see cref="EmitterKind"/>.
    /// </summary>
    [Flags]
    public enum EmitterFlags : byte
    {
        None              = 0,
        Bidirectional     = 1 << 0,  // wheels brake/reverse with ReverseForceN > 0
        AerodynamicScaling = 1 << 1, // contributes to AeroLiftBudget (Decision #13)
        ConsumesFuel      = 1 << 2,  // boosters / fan-jets (placeholder, no v0.1 consumer)
    }

    /// <summary>
    /// Weapon kind classification at probe time. v0.1 defaults all weapons to GunFixed
    /// per S6 (matches VehicleModel.cs:134 fall-through behavior today).
    /// </summary>
    [Flags]
    public enum WeaponKindFlag : byte
    {
        None      = 0,
        GunFixed  = 1 << 0,
        GunTurret = 1 << 1,
        Beam      = 1 << 2,
    }
}
