using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>Fire-mode classification per CONTROL-CONTRACT §10.5.</summary>
    public enum WeaponFireMode { Continuous, Salvo }

    /// <summary>
    /// Per-weapon static + dynamic profile. Per VEHICLE-CONTRACT §5.
    /// Static fields (Mount, Range, FireRate, Damage, ProjectileVelocity) come from the
    /// weapon block's profile at rebuild time. Dynamic fields (Cooldown, Ammo) come from
    /// the per-tick observation of the weapon's instantaneous state.
    /// </summary>
    public readonly struct WeaponProfile
    {
        public readonly WeaponId Id;
        public readonly Vector3 MountPositionLocal;
        public readonly Vector3 ForwardDirectionLocal;
        public readonly float YawArcRadians;
        public readonly float PitchArcRadians;
        public readonly float Range;
        public readonly float ProjectileVelocity;
        public readonly float DamagePerProjectile;
        public readonly float FireRateHz;
        public readonly int AmmoCurrent;
        public readonly int AmmoCapacity;
        public readonly float CooldownRemaining;
        public readonly WeaponFireMode FireMode;
        public readonly bool IsEnergyWeapon;
        // P11 T6 Item 57: reflected per-shot energy cost. 0f for non-energy weapons.
        public readonly float EnergyCostPerShot;

        public WeaponProfile(
            WeaponId id,
            Vector3 mount, Vector3 forward,
            float yawArc, float pitchArc,
            float range, float projVel, float damage, float fireRateHz,
            int ammoCurrent, int ammoCapacity, float cooldown,
            WeaponFireMode fireMode, bool isEnergy, float energyCostPerShot = 0f)
        {
            Id = id;
            MountPositionLocal = mount;
            ForwardDirectionLocal = forward;
            YawArcRadians = yawArc;
            PitchArcRadians = pitchArc;
            Range = range;
            ProjectileVelocity = projVel;
            DamagePerProjectile = damage;
            FireRateHz = fireRateHz;
            AmmoCurrent = ammoCurrent;
            AmmoCapacity = ammoCapacity;
            CooldownRemaining = cooldown;
            FireMode = fireMode;
            IsEnergyWeapon = isEnergy;
            EnergyCostPerShot = energyCostPerShot;
        }
    }

    /// <summary>
    /// Builds the weapon profile table from a tech's block list. Per VEHICLE §5.
    ///
    /// TODO v0.2: verify TerraTech weapon-block APIs:
    ///   - which ModuleXxx component holds projectile velocity / damage / fire rate
    ///     (likely ModuleWeapon and ModuleWeaponGun, or properties on the block visible directly)
    ///   - arc cone (turret limits in ModuleWeapon)
    ///   - cooldown countdown timer
    ///   - ammo state (ammo capacity / current).
    /// Until verified, the builder emits an empty list. Architecture is in place.
    /// </summary>
    public static class WeaponProfileBuilder
    {
        private const float SalvoDamageThreshold = 50.0f; // CONTROL §10.5 provisional

        public static WeaponProfile[] Build(BlockInstancePose[] poses, TypedBlockCatalog catalog)
        {
            if (poses == null || poses.Length == 0 || catalog == null) return new WeaponProfile[0];

            var list = new List<WeaponProfile>();
            int nextWeaponIdValue = 0;
            for (int i = 0; i < poses.Length; i++)
            {
                var pose = poses[i];
                var arch = catalog.TryGet(pose.TypeKey);
                if (arch == null || !arch.HasWeapon) continue;

                // Chassis Step 8 (REV 3.1 Decision #8): scalar weapon values come from the
                // archetype's WeaponSpec placeholder factory (matches today's hardcoded values
                // verbatim per WeaponProfileBuilder.cs:84-103 as it was). Per Decision #2,
                // ForwardDirectionLocal is computed via the OLD lossy formula in v0.1 (frozen)
                // for combat-feel preservation; v0.2 swaps to honest geometry alongside
                // WeaponSpec real reflected scalars.
                var spec = arch.Weapon;

                // ForwardDirectionLocal: P1 Item 2 gate. WeaponSpecPolicy.UseHonestBarrelGeometry
                // selects between v0.1 lossy formula (block-+Z assumption) and the reflected
                // barrel-direction stored on spec.BarrelDirBlockLocal (probed at
                // ArchetypeProbe.cs:139 via InverseTransformDirection). Default OFF preserves
                // v0.1 combat feel until flipped + P1.1 spawn-test gate.
                //
                // Geometrically: for forward-facing barrels both formulas produce identical
                // chassis-frame vectors. They diverge for side-mounted / canted turrets where
                // the weapon.transform.forward != block.transform.forward — IsAimedWithinArc
                // and CostFunction make different decisions on those weapons under the gate.
                Vector3 forwardDirLocal = WeaponSpecPolicy.UseHonestBarrelGeometry
                    ? pose.LocalRotation * spec.BarrelDirBlockLocal
                    : pose.LocalRotation * Vector3.forward;

                bool isTurret = spec.Kind == WeaponKindFlag.GunTurret;

                WeaponFireMode mode = (spec.FireRateHz > 2f && spec.DamagePerShot < SalvoDamageThreshold)
                    ? WeaponFireMode.Continuous
                    : WeaponFireMode.Salvo;

                list.Add(new WeaponProfile(
                    new WeaponId(nextWeaponIdValue++),
                    pose.LocalPosition, forwardDirLocal,
                    spec.YawArcRad, spec.PitchArcRad,
                    spec.Range, spec.ProjectileVelocity, spec.DamagePerShot, spec.FireRateHz,
                    ammoCurrent: spec.AmmoCapacity, ammoCapacity: spec.AmmoCapacity,
                    cooldown: 0f,
                    fireMode: mode, isEnergy: spec.IsEnergyWeapon,
                    energyCostPerShot: spec.EnergyCostPerShot));
            }
            return list.ToArray();
        }
    }
}
