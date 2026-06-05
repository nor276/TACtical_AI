using System.Collections.Generic;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P1 parity-catcher per V0.2-PLAN-REV7 P10 [WEAPON-PARITY]. One-shot file-only
    /// log per archetype: when WeaponSpecPolicy.UseReflectedScalars=true and the
    /// reflected spec diverges from PlaceholderFixed beyond tolerance, emit one
    /// [WEAPON-PARITY] line so the spawn-test driver can spot regressions.
    /// </summary>
    internal static class WeaponSpecParityGate
    {
        private static readonly HashSet<int> _logged = new HashSet<int>();
        private static readonly object _gate = new object();

        // Tolerance per scalar — log when the relative delta exceeds these (rough heuristic).
        private const float RangeTolerance = 0.25f;        // 25%
        private const float VelocityTolerance = 0.25f;
        private const float DamageTolerance = 0.50f;       // damage varies wider
        private const float FireRateTolerance = 0.50f;

        internal static void TryEmitOnce(int typeKey, WeaponSpec real, WeaponSpec placeholder)
        {
            lock (_gate)
            {
                if (_logged.Contains(typeKey)) return;
                _logged.Add(typeKey);
            }

            bool divergent =
                RelDiff(real.Range, placeholder.Range) > RangeTolerance ||
                RelDiff(real.ProjectileVelocity, placeholder.ProjectileVelocity) > VelocityTolerance ||
                RelDiff(real.DamagePerShot, placeholder.DamagePerShot) > DamageTolerance ||
                RelDiff(real.FireRateHz, placeholder.FireRateHz) > FireRateTolerance ||
                real.Kind != placeholder.Kind ||
                real.IsEnergyWeapon != placeholder.IsEnergyWeapon;

            if (divergent)
            {
                DebugTAC_AI.LogWarnFileOnly(
                    key: "weapon-parity-" + typeKey,
                    message: "[WEAPON-PARITY] typeKey=" + typeKey
                        + " kind=" + real.Kind + " (was " + placeholder.Kind + ")"
                        + " range=" + real.Range.ToString("F1") + " (was " + placeholder.Range.ToString("F1") + ")"
                        + " velocity=" + real.ProjectileVelocity.ToString("F1") + " (was " + placeholder.ProjectileVelocity.ToString("F1") + ")"
                        + " damage=" + real.DamagePerShot.ToString("F1") + " (was " + placeholder.DamagePerShot.ToString("F1") + ")"
                        + " fireRate=" + real.FireRateHz.ToString("F2") + " (was " + placeholder.FireRateHz.ToString("F2") + ")"
                        + " energy=" + real.IsEnergyWeapon);
            }
        }

        private static float RelDiff(float real, float placeholder)
        {
            float denom = UnityEngine.Mathf.Max(UnityEngine.Mathf.Abs(placeholder), 0.001f);
            return UnityEngine.Mathf.Abs(real - placeholder) / denom;
        }

        /// <summary>P11 T1: clear per-archetype dedup so a policy flip mid-session re-emits.</summary>
        internal static void Reset()
        {
            lock (_gate) _logged.Clear();
        }
    }
}
