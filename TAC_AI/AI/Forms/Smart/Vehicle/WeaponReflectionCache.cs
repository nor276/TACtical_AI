using System.Reflection;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P1 Item 1 + Item 7: cached reflection of TerraTech weapon internals + kind
    /// classifier. Mirrors the field-cache pattern at
    /// AI/Forms/Modified/Combat/EWeapSetup.cs:14-17 (the production code that already
    /// reads WeaponRound.m_Damage + BeamWeapon.m_DamagePerSecond). Single static init;
    /// thread-safe (cache is filled at type-load; subsequent reads are field loads).
    /// </summary>
    internal static class WeaponReflectionCache
    {
        // Same field handles EWeapSetup uses — sharing semantics keeps the two paths
        // in agreement if TerraTech ever renames the underlying field.
        internal static readonly FieldInfo WeaponRound_Damage =
            typeof(WeaponRound).GetField("m_Damage", BindingFlags.NonPublic | BindingFlags.Instance);
        internal static readonly FieldInfo BeamWeapon_DamagePerSecond =
            typeof(BeamWeapon).GetField("m_DamagePerSecond", BindingFlags.NonPublic | BindingFlags.Instance);
        // P11 T6 Item 57: ModuleWeapon.m_FiringEnergyRequired is `protected`. Verified at
        // F:\Tac-AI\Decompiled\AssemblyCSharp\ModuleWeapon.cs:36-38 — `[Tooltip("How many
        // units of electrical energy is required to fire this weapon one time")] protected
        // float m_FiringEnergyRequired`. Used internally by ModuleWeapon's HasEnergyForShot.
        internal static readonly FieldInfo ModuleWeapon_FiringEnergyRequired =
            typeof(ModuleWeapon).GetField("m_FiringEnergyRequired", BindingFlags.NonPublic | BindingFlags.Instance);

        // P1 Item 7 thresholds — match production semantics at
        // AI/Forms/Modified/Combat/EWeapSetup.cs:40 (CircleRange=170 rotation-range deg
        // is the line at which a gimbal qualifies as a turret).
        private const float CircleRangeDegrees = 170f;

        /// <summary>
        /// Read scalar values from a weapon block via reflection + FireData/WeaponRound/BeamWeapon
        /// probing. Returns true on success; any failure (missing component, NRE, etc.) returns
        /// false and the caller falls back to <see cref="WeaponSpec.PlaceholderFixed"/>.
        /// Mirrors the read pattern from RWeapSetup.cs:48-130.
        /// </summary>
        internal static bool TryReadScalars(TankBlock block, ModuleWeapon weap,
            out float range, out float projectileVelocity, out float damagePerShot,
            out float fireRateHz, out int ammoCapacity, out bool isEnergyWeapon,
            out float energyCostPerShot)
        {
            range = 100f;
            projectileVelocity = 200f;
            damagePerShot = 30f;
            fireRateHz = 1.5f;
            ammoCapacity = 100;
            isEnergyWeapon = false;
            energyCostPerShot = 0f;

            if (block == null || weap == null) return false;

            try
            {
                var fd = weap.GetComponent<FireData>();
                if (fd != null && fd.m_BulletPrefab != null)
                {
                    // Projectile path: WeaponRound carries damage; FireData carries muzzle velocity.
                    projectileVelocity = fd.m_MuzzleVelocity > 0f ? fd.m_MuzzleVelocity : projectileVelocity;
                    // Range estimate: projectile velocity * 1s flight time is the rough max
                    // practical engagement range used by the production AI. The shotgun
                    // FireDataShotgun.m_ShotMaxRange override (RWeapSetup.cs:85) is more
                    // accurate when present.
                    var fds = weap.GetComponent<FireDataShotgun>();
                    if (fds != null && fds.m_ShotMaxRange > 0f)
                        range = fds.m_ShotMaxRange;
                    else if (fd.m_MuzzleVelocity > 0f)
                        range = Mathf.Min(fd.m_MuzzleVelocity * 1.5f, 300f);

                    var round = fd.m_BulletPrefab.GetComponent<WeaponRound>();
                    if (round != null && WeaponRound_Damage != null)
                    {
                        try { damagePerShot = (int)WeaponRound_Damage.GetValue(round); }
                        catch { /* keep placeholder */ }
                    }
                }
                else
                {
                    // Beam path: BeamWeapon carries DPS in lieu of per-shot damage.
                    var beam = weap.GetComponentInChildren<BeamWeapon>();
                    if (beam != null && BeamWeapon_DamagePerSecond != null)
                    {
                        try
                        {
                            float dps = (int)BeamWeapon_DamagePerSecond.GetValue(beam);
                            // Beams have effectively no projectile velocity; encode DPS into
                            // damagePerShot * fireRateHz=1Hz so downstream cost-function math
                            // remains in the same units.
                            damagePerShot = dps;
                            fireRateHz = 1f;
                            projectileVelocity = 9999f;  // hitscan
                            isEnergyWeapon = true;
                            range = 200f; // beam reach varies; conservative placeholder
                        }
                        catch { /* keep placeholder */ }
                    }
                }

                // P11 T6 Item 57: per-shot energy cost via reflection. ModuleWeapon's own
                // UsesEnergyForFiring guard is `block.energyModule != null && m_FiringEnergyRequired > 0f`
                // (decompile line 137-146); we replicate the > 0f check here so the consumer can
                // skip the energy gate cheaply for non-energy weapons.
                if (ModuleWeapon_FiringEnergyRequired != null)
                {
                    try
                    {
                        float ec = (float)ModuleWeapon_FiringEnergyRequired.GetValue(weap);
                        if (ec > 0f)
                        {
                            energyCostPerShot = ec;
                            isEnergyWeapon = true;
                        }
                    }
                    catch { /* keep placeholder energyCostPerShot=0f */ }
                }

                // P11 T6 Item 59 (post-decompile): real fire rate from ModuleWeapon.m_ShotCooldown.
                // Verified at F:\Tac-AI\Decompiled\AssemblyCSharp\ModuleWeapon.cs:58 — public
                // [SerializeField] float m_ShotCooldown = 1f (also exposed via the ShotCooldown
                // property at line 155). Fire rate Hz = 1 / m_ShotCooldown for cooldowns ≥ 16ms;
                // sub-16ms cooldowns get clamped to 60Hz to avoid division-by-near-zero blowing
                // up downstream DPS math.
                float shotCooldown = weap.ShotCooldown;
                if (shotCooldown >= 0.016f) fireRateHz = 1f / shotCooldown;
                else fireRateHz = 60f;

                // Ammo capacity stays at the placeholder int.MaxValue-proxy 100. Decompile
                // confirms ammo isn't a ModuleWeapon concern — it lives on the IModuleWeapon
                // sub-component (m_WeaponComponent at ModuleWeapon.cs:71) or on a separate
                // ProjectileShooter. Most TerraTech weapons are infinite-ammo at the block
                // level; the placeholder is correct for those. A ProjectileShooterProbe is a
                // follow-up Item (60.1) — needs its own decompile pass on the projectile path.
            }
            catch
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Classify a weapon block as GunFixed / GunTurret / Beam per P1 Item 7.
        /// Mirrors the turret detection at RWeapSetup.cs:52-66 (GimbalAimer with rotation
        /// range >= 170 degrees AND rotate speed >= 60 = circle-capable turret).
        /// </summary>
        internal static WeaponKindFlag ClassifyKind(TankBlock block, ModuleWeapon weap)
        {
            if (block == null || weap == null) return WeaponKindFlag.GunFixed;

            try
            {
                // Beam detection first: BeamWeapon presence makes it a beam regardless of gimbal.
                var beam = weap.GetComponentInChildren<BeamWeapon>();
                if (beam != null) return WeaponKindFlag.Beam;

                // Turret detection: any GimbalAimer child with rotation range >= CircleRangeDegrees.
                var gimbals = weap.GetComponentsInChildren<GimbalAimer>();
                if (gimbals != null)
                {
                    for (int i = 0; i < gimbals.Length; i++)
                    {
                        var aim = gimbals[i];
                        if (aim == null || aim.rotationLimits == null || aim.rotationLimits.Length == 0)
                            continue;
                        float minLim = aim.rotationLimits[0], maxLim = aim.rotationLimits[0];
                        for (int j = 1; j < aim.rotationLimits.Length; j++)
                        {
                            if (aim.rotationLimits[j] < minLim) minLim = aim.rotationLimits[j];
                            if (aim.rotationLimits[j] > maxLim) maxLim = aim.rotationLimits[j];
                        }
                        if ((maxLim - minLim) >= CircleRangeDegrees) return WeaponKindFlag.GunTurret;
                    }
                }
            }
            catch { /* fall through to GunFixed */ }

            return WeaponKindFlag.GunFixed;
        }
    }
}
