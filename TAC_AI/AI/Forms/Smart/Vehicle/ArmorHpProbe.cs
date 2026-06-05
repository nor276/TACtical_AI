using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P3 Item 5: per-block HP probes using verified live TerraTech APIs.
    ///
    /// Spec-HP source: <c>ModuleDamage.maxHealth</c> on the prefab. Confirmed live at
    /// ManEnemyWorld.cs:2318 (<c>block.GetComponent&lt;ModuleDamage&gt;().maxHealth</c>),
    /// ManEnemyWorld.cs:2487, TankAIHelper.cs:3259 (cached as <c>root.damage.maxHealth</c>).
    ///
    /// Current-HP source: <c>Damageable.Health</c> on the block's Visible. Confirmed live at
    /// TankAIHelper.cs:3259 (<c>root.visible.damageable.Health / (float)root.damage.maxHealth</c>) —
    /// the exact pattern this probe mirrors. Health does NOT live on ModuleDamage
    /// (verified by the REV 6 review when P2's HealthSidecar attempted the wrong accessor).
    ///
    /// Reflection-free; all reads via public properties. Each probe is null-guarded so
    /// freshly-spawned or partial-init blocks fall back to safe defaults
    /// (SpecHP = mass fallback, HpFraction = 1.0).
    /// </summary>
    internal static class ArmorHpProbe
    {
        /// <summary>
        /// Per-archetype max HP via ModuleDamage. Probed once per BlockType by ArchetypeProbe.
        /// Falls back to <paramref name="massFallback"/> when the component is missing or zero.
        /// </summary>
        internal static float ReadSpecHp(TankBlock prefab, float massFallback)
        {
            if (prefab == null) return massFallback;
            try
            {
                var dmg = prefab.GetComponent<ModuleDamage>();
                if (dmg != null && dmg.maxHealth > 0) return dmg.maxHealth;
            }
            catch { /* fall through to mass fallback */ }
            return massFallback;
        }

        /// <summary>
        /// Per-instance HP fraction in [0, 1]. Reads current via the Damageable on the
        /// block's Visible (matches TankAIHelper.cs:3259 pattern); reads max from the
        /// block's ModuleDamage. Returns 1.0 (fully healthy) when any component is
        /// missing — newly-spawned blocks haven't taken damage yet.
        /// </summary>
        internal static float ReadHpFraction(TankBlock instance)
        {
            if (instance == null) return 1f;
            try
            {
                var dmg = instance.GetComponent<ModuleDamage>();
                if (dmg == null || dmg.maxHealth <= 0) return 1f;
                var vis = instance.visible;
                if (vis == null || vis.damageable == null) return 1f;
                return Mathf.Clamp01(vis.damageable.Health / (float)dmg.maxHealth);
            }
            catch
            {
                return 1f;
            }
        }
    }
}
