using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Coordination;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Vehicle;

namespace TAC_AI.AI.Tunables.Catalog
{
    /// <summary>
    /// P11 T9 Item 65: populates the previously-empty <c>smart.</c> / <c>chassis.</c> /
    /// <c>weaponspec.</c> / <c>armormap.</c> / <c>los.</c> prefix lanes of
    /// <see cref="TAC_AI.AI.Forms.Smart.Tooling.TunableMenuBridge"/>.SmartPrefixes.
    /// Promotes parity-gate tolerances + policy flags + ChassisFixtureGate.AssertOnFailure
    /// to live tunables so operators can flip them via the in-game menu, the F8 live tweaker
    /// panel, or <c>smart.preset.save / load</c> instead of needing a debugger.
    ///
    /// Same registration pattern as <see cref="AetherTunables"/> — bind sets the static
    /// field, defaults equal the field's value so registration is a no-op at boot.
    /// </summary>
    public static class SmartParityTunables
    {
        public static void Register()
        {
            // P11 post-flip defaults: every spawn-test-gated policy ships ON. Operators can flip
            // any individual flag off via the in-game menu / live tweaker / preset commands.
            // defaultValues here match the field initializers so bind(default) at register-time
            // is a no-op (= preserves the new ON behavior on first launch).

            // ---- ArmorMap policy + gate ----
            TunableRegistry.RegisterBool(
                key: "armormap.useRealSpecHP",
                category: "ArmorMap",
                defaultValue: true,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "ArmorMap — use real spec HP (P3)",
                bind: v => { ArmorMapPolicy.UseRealSpecHP = v; TAC_AI.AI.Forms.Smart.Tests.VehicleParityCatcher.ResetAllGates(); });

            // ---- WeaponSpec policy + gate ----
            TunableRegistry.RegisterBool(
                key: "weaponspec.useReflectedScalars",
                category: "WeaponSpec",
                defaultValue: true,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "WeaponSpec — reflected scalars (P1)",
                bind: v => { WeaponSpecPolicy.UseReflectedScalars = v; TAC_AI.AI.Forms.Smart.Tests.VehicleParityCatcher.ResetAllGates(); });

            TunableRegistry.RegisterBool(
                key: "weaponspec.useHonestBarrelGeometry",
                category: "WeaponSpec",
                defaultValue: true,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "WeaponSpec — honest barrel geometry (P1)",
                bind: v => { WeaponSpecPolicy.UseHonestBarrelGeometry = v; TAC_AI.AI.Forms.Smart.Tests.VehicleParityCatcher.ResetAllGates(); });

            // ---- LOS producer + gate ----
            TunableRegistry.RegisterBool(
                key: "los.producerEnabled",
                category: "LOS",
                defaultValue: true,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "LOS producer — enable per-team coverage (P5)",
                bind: v => { LineOfSightProducer.Enabled = v; TAC_AI.AI.Forms.Smart.Tests.VehicleParityCatcher.ResetAllGates(); });

            // ---- SmartIdentity tuning flags ----
            TunableRegistry.RegisterBool(
                key: "smart.identity.enableRepairSupport",
                category: "Smart.Identity",
                defaultValue: true,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Identity — enable RepairSupport (P6)",
                bind: v => { SmartIdentityTuning.EnableRepairSupport = v; TAC_AI.AI.Forms.Smart.Tests.VehicleParityCatcher.ResetAllGates(); });

            TunableRegistry.RegisterBool(
                key: "smart.identity.enablePatrol",
                category: "Smart.Identity",
                defaultValue: true,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Identity — enable Patrol (P6)",
                bind: v => { SmartIdentityTuning.EnablePatrol = v; TAC_AI.AI.Forms.Smart.Tests.VehicleParityCatcher.ResetAllGates(); });

            TunableRegistry.RegisterBool(
                key: "smart.identity.useHarvesterModulesForGatherer",
                category: "Smart.Identity",
                defaultValue: true,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Identity — harvester modules → Gatherer (P6)",
                bind: v => { SmartIdentityTuning.UseHarvesterModulesForGatherer = v; TAC_AI.AI.Forms.Smart.Tests.VehicleParityCatcher.ResetAllGates(); });

            // ---- Chassis fixture gate ----
            TunableRegistry.RegisterBool(
                key: "chassis.fixture.assertOnFailure",
                category: "Chassis",
                defaultValue: false,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Chassis fixture — assert on failure (P10/P11)",
                bind: v => ChassisFixtureGate.AssertOnFailure = v);

            // ---- Vehicle parity catcher master ----
            TunableRegistry.RegisterBool(
                key: "chassis.parityCatcher.enabled",
                category: "Chassis",
                defaultValue: true,
                applyMode: TunableApplyMode.LiveSafe,
                menuVisible: true,
                menuLabel: "Parity catcher — enable [X-PARITY] emission",
                bind: v => TAC_AI.AI.Forms.Smart.Tests.VehicleParityCatcher.Enabled = v);
        }
    }
}
