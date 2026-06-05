using System.Collections.Concurrent;
using UnityEngine;
using TerraTechETCUtil;  // BlockDetails

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P2 Item 4 + REV 3 semantic rename: probes for Drill / Heal / Protection / Gyro
    /// detection. The original Item 4 spec referenced phantom types (ModuleDrill,
    /// ModuleHealBeam, ModuleGyro) — all 10/10 reviewer-confirmed as non-existent in
    /// TerraTech's assemblies. REV 3 swapped to verified live APIs:
    ///   IsDrill          -> BlockDetails(BlockType).IsMelee + component-name allowlist
    ///   HasProtection    -> ModuleShieldGenerator presence (shield-as-protection only)
    ///   IsHealer         -> component-name match for "Heal"/"Repair" (no shield false-positive)
    ///   HasGyroStabilizer-> BlockDetails(BlockType).IsGyro
    ///
    /// Per the REV 3 plan note (P2 line 333): no v0.2 consumer reads BlockKindFlags.Repair
    /// (RepairSupport uses BlockKindFlags.Shield via HasProtection). IsHealer is shipped
    /// for v0.3 forward-compatibility when a real heal-source consumer lands.
    ///
    /// Each probe is wrapped in try/catch; misses return false. Patterns match the
    /// production reflection-discovery comments at the original ArchetypeProbe site.
    /// </summary>
    internal static class DrillRepairProbe
    {
        // P11 T6 Item 61: discovery logging. Per the P2 acceptance criterion, log once per
        // (probe-kind, block-type) pair when a substring scan triggers — lets the operator
        // verify the scan isn't false-positiving on UI elements or unrelated components that
        // happen to contain "Drill"/"Hammer"/"Heal"/"Repair" in their type name.
        private static readonly ConcurrentDictionary<string, byte> _discoveryLog =
            new ConcurrentDictionary<string, byte>();

        private static void LogDiscovery(string probeKind, string blockTypeName, string matchedComponentName)
        {
            string key = probeKind + ":" + blockTypeName + ":" + matchedComponentName;
            if (!_discoveryLog.TryAdd(key, 1)) return;
            DebugTAC_AI.LogWarnFileOnly("probe-discovery",
                "[PROBE-DISCOVERY] " + probeKind + " matched on block '" + (blockTypeName ?? "<null>")
                + "' via component type '" + (matchedComponentName ?? "<null>") + "'");
        }

        internal static bool IsDrill(TankBlock b)
        {
            if (b == null) return false;
            try
            {
                string blockName = b.BlockType.ToString();
                var bd = new BlockDetails(b.BlockType);
                if (bd.IsMelee)
                {
                    LogDiscovery("Drill", blockName, "BlockDetails.IsMelee");
                    return true;
                }
                var comps = b.GetComponentsInChildren<Component>(true);
                if (comps != null)
                {
                    for (int i = 0; i < comps.Length; i++)
                    {
                        var c = comps[i];
                        if (c == null) continue;
                        var name = c.GetType().Name;
                        if (name.Contains("Drill") || name.Contains("Hammer"))
                        {
                            LogDiscovery("Drill", blockName, name);
                            return true;
                        }
                    }
                }
            }
            catch { /* fall through */ }
            return false;
        }

        internal static bool HasProtection(TankBlock b)
        {
            if (b == null) return false;
            try
            {
                var shield = b.GetComponent<ModuleShieldGenerator>();
                if (shield != null)
                {
                    LogDiscovery("Protection", b.BlockType.ToString(), "ModuleShieldGenerator");
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        internal static bool IsHealer(TankBlock b)
        {
            if (b == null) return false;
            try
            {
                string blockName = b.BlockType.ToString();
                var comps = b.GetComponentsInChildren<Component>(true);
                if (comps != null)
                {
                    for (int i = 0; i < comps.Length; i++)
                    {
                        var c = comps[i];
                        if (c == null) continue;
                        var name = c.GetType().Name;
                        if (name.Contains("Heal") || name.Contains("Repair"))
                        {
                            LogDiscovery("Healer", blockName, name);
                            return true;
                        }
                    }
                }
            }
            catch { /* fall through */ }
            return false;
        }

        internal static bool HasGyroStabilizer(TankBlock b)
        {
            if (b == null) return false;
            try
            {
                if (new BlockDetails(b.BlockType).IsGyro)
                {
                    LogDiscovery("Gyro", b.BlockType.ToString(), "BlockDetails.IsGyro");
                    return true;
                }
                return false;
            }
            catch { return false; }
        }
    }
}
