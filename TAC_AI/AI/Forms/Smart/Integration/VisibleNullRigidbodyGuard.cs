using System;
using System.Reflection;
using HarmonyLib;

namespace TAC_AI.AI.Forms.Smart.Integration
{
    /// <summary>
    /// Harmony prefix on the engine's <c>Visible.IsRigidbodySleeping</c> that returns
    /// <c>true</c> (= "yes, it's sleeping; skip the update") when the underlying
    /// Rigidbody reference is null. The pre-patched engine code unconditionally calls
    /// <c>m_Rigidbody.IsSleeping()</c>, which NREs when the Rigidbody has been destroyed
    /// but the Visible component is still in <c>SwitchableUpdater</c>'s iteration list.
    /// During an 8-hour overnight session we logged 1.38 MILLION of these NREs (≈50/s
    /// every frame), filling the log file to 844 MB and likely contributing memory/GC
    /// pressure that surfaced the quit-time <c>ManWorld.OnDestroy</c> crash.
    ///
    /// This is a TerraTech engine bug, not Smart's; the patch is purely defensive log
    /// hygiene. Returning <c>true</c> from the prefix tells <c>OnUpdateNotAsleep</c>
    /// that the Visible IS sleeping, which short-circuits the post-update path — exactly
    /// the behavior the engine wants when the Rigidbody is gone (no physics → no
    /// not-asleep work).
    ///
    /// Field name <c>m_Rigidbody</c> resolved at static-init via AccessTools; failure to
    /// resolve disables the patch (logs once) so a future engine refactor of the field
    /// name doesn't break the build.
    /// </summary>
    [HarmonyPatch]
    internal static class VisibleNullRigidbodyGuard
    {
        private static readonly FieldInfo _rigidbodyField;
        private static readonly bool _resolved;

        static VisibleNullRigidbodyGuard()
        {
            try
            {
                var visibleType = AccessTools.TypeByName("Visible");
                if (visibleType != null)
                {
                    _rigidbodyField = AccessTools.Field(visibleType, "m_Rigidbody");
                    _resolved = _rigidbodyField != null;
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("VisibleNullRigidbodyGuard.cctor: " + ex.Message);
            }
            if (!_resolved)
            {
                DebugTAC_AI.LogWarning("VisibleNullRigidbodyGuard: failed to resolve Visible.m_Rigidbody — engine NRE spam will NOT be suppressed");
            }
        }

        static MethodBase TargetMethod()
        {
            var visibleType = AccessTools.TypeByName("Visible");
            if (visibleType == null) return null;
            return AccessTools.Method(visibleType, "IsRigidbodySleeping");
        }

        static bool Prefix(object __instance, ref bool __result)
        {
            if (!_resolved || _rigidbodyField == null) return true;   // fall through to original
            try
            {
                var rb = _rigidbodyField.GetValue(__instance) as UnityEngine.Rigidbody;
                if (rb == null)
                {
                    __result = true;   // pretend "sleeping" — short-circuits not-asleep update
                    return false;      // skip original method body (the NRE source)
                }
            }
            catch
            {
                // On reflection failure, fall through to original; worst case we keep
                // logging the NREs we were already getting.
            }
            return true;
        }
    }
}
