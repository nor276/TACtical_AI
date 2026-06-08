using System;
using System.Reflection;
using HarmonyLib;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Reflection wrapper around <c>ModuleWeapon.m_WeaponComponent</c> (private
    /// <c>IModuleWeapon</c> backing field) so the AI can read <c>ReadyToFire()</c> and
    /// <c>FiringObstructed()</c> without taking a direct reference to the private interface
    /// (<c>IModuleWeapon</c> is internal-only by convention — exposing it via public API
    /// is what forced the wrapper).
    ///
    /// Field handle resolved once at type-load via AccessTools, mirroring
    /// <c>VisibleNullRigidbodyGuard.cs:30-52</c>. Resolution failure disables the helper
    /// (one logged warning per session) and the methods return their conservative defaults
    /// (ready=false, obstructed=true) so the consumer skips the weapon rather than firing
    /// blind.
    /// </summary>
    internal static class WeaponReadinessHelper
    {
        private static readonly FieldInfo _weaponComponentField;
        private static readonly bool _resolved;

        // Once-per-session diagnostic; reflection errors at call-time are otherwise
        // silently swallowed (per-weapon NREs would spam the log).
        private static bool _loggedCallFailure;

        static WeaponReadinessHelper()
        {
            try
            {
                _weaponComponentField = AccessTools.Field(typeof(ModuleWeapon), "m_WeaponComponent");
                _resolved = _weaponComponentField != null;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("WeaponReadinessHelper.cctor: " + ex.Message);
            }
            if (!_resolved)
            {
                DebugTAC_AI.LogWarning("WeaponReadinessHelper: failed to resolve ModuleWeapon.m_WeaponComponent — readiness reads disabled (assume not-ready)");
            }
        }

        /// <summary>
        /// True iff the weapon's underlying <see cref="IModuleWeapon"/> reports it can fire
        /// right now. Conservative default on any failure path: <c>false</c> (skip the
        /// weapon rather than try to shoot through a hung component).
        /// </summary>
        internal static bool TryReadyToFire(ModuleWeapon weapon)
        {
            if (!_resolved || weapon == null) return false;
            try
            {
                var comp = _weaponComponentField.GetValue(weapon) as IModuleWeapon;
                if (comp == null) return false;
                return comp.ReadyToFire();
            }
            catch (Exception ex)
            {
                LogCallFailureOnce("TryReadyToFire", ex);
                return false;
            }
        }

        /// <summary>
        /// True iff the weapon's underlying <see cref="IModuleWeapon"/> reports its line
        /// of fire is obstructed (e.g. friendly block in the way). Conservative default
        /// on any failure path: <c>true</c> (assume obstructed so the consumer doesn't
        /// count a hung weapon as ready+clear).
        /// </summary>
        internal static bool TryFiringObstructed(ModuleWeapon weapon)
        {
            if (!_resolved || weapon == null) return true;
            try
            {
                var comp = _weaponComponentField.GetValue(weapon) as IModuleWeapon;
                if (comp == null) return true;
                return comp.FiringObstructed();
            }
            catch (Exception ex)
            {
                LogCallFailureOnce("TryFiringObstructed", ex);
                return true;
            }
        }

        private static void LogCallFailureOnce(string where, Exception ex)
        {
            if (_loggedCallFailure) return;
            _loggedCallFailure = true;
            DebugTAC_AI.LogWarning("WeaponReadinessHelper." + where + ": " + ex.Message + " (further failures suppressed)");
        }
    }
}
