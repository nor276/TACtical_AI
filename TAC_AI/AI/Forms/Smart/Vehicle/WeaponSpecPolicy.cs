namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P1 default-OFF feature gates per V0.2-PLAN-REV7 P1.
    /// Flips require the P1.1 spawn-test gate per REV 3 C14 + C13.
    /// </summary>
    public static class WeaponSpecPolicy
    {
        /// <summary>
        /// When true (P11 post-flip default), ArchetypeProbe populates WeaponSpec via
        /// WeaponSpec.FromReflection (real Range / Velocity / Damage / FireRate / EnergyCost /
        /// IsEnergyWeapon via P11 T6's m_ShotCooldown + m_FiringEnergyRequired reflection).
        /// When false, uses WeaponSpec.PlaceholderFixed for v0.1 bit-identical combat feel.
        /// Tunable: <c>weaponspec.useReflectedScalars</c>.
        /// </summary>
        public static bool UseReflectedScalars = true;

        /// <summary>
        /// When true (P11 post-flip default), WeaponProfileBuilder.Build computes
        /// ForwardDirectionLocal as <c>pose.LocalRotation * spec.BarrelDirBlockLocal</c>
        /// (honest reflected barrel geometry). When false, uses
        /// <c>pose.LocalRotation * Vector3.forward</c> — the v0.1 lossy formula that assumes
        /// every weapon points block-+Z. Affects side-mounted / canted turrets' arc + cost
        /// math. Tunable: <c>weaponspec.useHonestBarrelGeometry</c>.
        /// </summary>
        public static bool UseHonestBarrelGeometry = true;
    }
}
