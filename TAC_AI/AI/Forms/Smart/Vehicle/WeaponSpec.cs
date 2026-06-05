using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis: per-archetype weapon-block spec. Immutable; lives on
    /// <see cref="BlockArchetype.Weapon"/> when <see cref="BlockArchetype.HasWeapon"/>
    /// is true.
    ///
    /// v0.1 (per Decision #8): scalar fields (Range/ProjectileVelocity/DamagePerShot/
    /// FireRateHz/YawArcRad/PitchArcRad/AmmoCapacity/IsEnergyWeapon) are populated with
    /// today's placeholder values from WeaponProfileBuilder.cs:84-103 to preserve combat
    /// feel. Kind defaults to GunFixed per S6 (matches VehicleModel.cs:134 fall-through).
    /// BarrelDirBlockLocal IS populated by ArchetypeProbe via
    /// `block.transform.InverseTransformDirection(weapon.transform.forward)` but is
    /// UNREAD by any v0.1 consumer (Decision #2 defers ForwardDirectionLocal honest-
    /// geometry to v0.2 alongside the rest of WeaponSpec real-values migration).
    ///
    /// v0.2 swaps every scalar to reflected values from FireData + WeaponRound and wires
    /// BarrelDirBlockLocal through WeaponProfileBuilder; runs its own parallel-run gate.
    /// </summary>
    public readonly struct WeaponSpec
    {
        public readonly WeaponKindFlag Kind;
        public readonly Vector3 BarrelDirBlockLocal;
        public readonly float Range;
        public readonly float ProjectileVelocity;
        public readonly float DamagePerShot;
        public readonly float FireRateHz;
        public readonly float YawArcRad;
        public readonly float PitchArcRad;
        public readonly int AmmoCapacity;
        public readonly bool IsEnergyWeapon;
        // P11 T6 Item 57: per-shot electrical-energy cost. Reflected from
        // ModuleWeapon.m_FiringEnergyRequired (verified at
        // F:\Tac-AI\Decompiled\AssemblyCSharp\ModuleWeapon.cs:36-38).
        // 0f for non-energy weapons or when reflection fails — caller treats 0 as "no gate".
        public readonly float EnergyCostPerShot;

        public WeaponSpec(
            WeaponKindFlag kind,
            Vector3 barrelDirBlockLocal,
            float range,
            float projectileVelocity,
            float damagePerShot,
            float fireRateHz,
            float yawArcRad,
            float pitchArcRad,
            int ammoCapacity,
            bool isEnergyWeapon,
            float energyCostPerShot = 0f)
        {
            Kind = kind;
            BarrelDirBlockLocal = barrelDirBlockLocal;
            Range = range;
            ProjectileVelocity = projectileVelocity;
            DamagePerShot = damagePerShot;
            FireRateHz = fireRateHz;
            YawArcRad = yawArcRad;
            PitchArcRad = pitchArcRad;
            AmmoCapacity = ammoCapacity;
            IsEnergyWeapon = isEnergyWeapon;
            EnergyCostPerShot = energyCostPerShot;
        }

        // v0.1 placeholder factory: produces the same scalar values today's
        // WeaponProfileBuilder.Build hardcodes (lines 84-103). Used by ArchetypeProbe
        // for every weapon archetype until v0.2 wires real reflected values.
        public static WeaponSpec PlaceholderFixed(Vector3 barrelDirBlockLocal)
        {
            return new WeaponSpec(
                kind: WeaponKindFlag.GunFixed,
                barrelDirBlockLocal: barrelDirBlockLocal,
                range: 100f,
                projectileVelocity: 200f,
                damagePerShot: 30f,
                fireRateHz: 1.5f,
                yawArcRad: Mathf.PI / 4f,
                pitchArcRad: Mathf.PI / 6f,
                ammoCapacity: 100,
                isEnergyWeapon: false);
        }

        // P1 Item 1: reflected-values factory. Wraps every read in try/catch via
        // WeaponReflectionCache.TryReadScalars and WeaponReflectionCache.ClassifyKind;
        // any reflection failure falls back to PlaceholderFixed so the build is
        // never weaker than v0.1.
        public static WeaponSpec FromReflection(Vector3 barrelDirBlockLocal,
                                                TankBlock block, ModuleWeapon weap)
        {
            float range, projVel, dmg, fireRate, energyCost;
            int ammoCap;
            bool isEnergy;
            if (!WeaponReflectionCache.TryReadScalars(
                    block, weap, out range, out projVel, out dmg, out fireRate,
                    out ammoCap, out isEnergy, out energyCost))
            {
                return PlaceholderFixed(barrelDirBlockLocal);
            }
            WeaponKindFlag kind = WeaponReflectionCache.ClassifyKind(block, weap);
            // Arc radians: GunTurret gets full 2pi yaw; GunFixed/Beam keep placeholder arcs.
            float yawArc = (kind == WeaponKindFlag.GunTurret) ? Mathf.PI : Mathf.PI / 4f;
            float pitchArc = Mathf.PI / 6f;
            return new WeaponSpec(
                kind: kind,
                barrelDirBlockLocal: barrelDirBlockLocal,
                range: range,
                projectileVelocity: projVel,
                damagePerShot: dmg,
                fireRateHz: fireRate,
                yawArcRad: yawArc,
                pitchArcRad: pitchArc,
                ammoCapacity: ammoCap,
                isEnergyWeapon: isEnergy,
                energyCostPerShot: energyCost);
        }
    }
}
