using System;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// P2 Item 8: optional eager catalog prewarm. Default-OFF; the lazy GetOrProbe path
    /// is the v0.1 behavior. When enabled, walks <c>ManSpawn.inst.GetLoadedTankBlockNames()</c>
    /// at SmartRuntime.Init time and probes every known block prefab once, shifting the
    /// first-encounter cost out of the gameplay-critical window.
    ///
    /// Pattern matches the verified production iteration at AIWiki.cs:36-48 and
    /// AIGlobals.cs:47-52. Falls back silently if engine singletons aren't ready —
    /// the lazy GetOrProbe path still works.
    /// </summary>
    public static class CatalogPrewarm
    {
        /// <summary>Default OFF; flip to true to trigger prewarm on next SmartRuntime.Init.</summary>
        public static bool Enabled = false;

        private static int _lastPrewarmCount;
        public static int LastPrewarmCount => _lastPrewarmCount;

        /// <summary>
        /// Walk known block prefabs and prime the catalog. Idempotent — calling on a
        /// pre-populated catalog returns immediately (GetOrProbe is hash-keyed). Safe
        /// to call from SmartRuntime.Init.
        /// </summary>
        public static void Run(TypedBlockCatalog catalog)
        {
            if (!Enabled || catalog == null) return;
            int probed = 0;
            try
            {
                if (ManSpawn.inst == null) return;
                var names = ManSpawn.inst.GetLoadedTankBlockNames();
                if (names == null) return;
                for (int i = 0; i < names.Length; i++)
                {
                    try
                    {
                        var prefab = ManSpawn.inst.GetBlockPrefab(names[i]);
                        if (prefab == null) continue;
                        catalog.GetOrProbe(prefab);
                        probed++;
                    }
                    catch
                    {
                        // Per-prefab failure shouldn't abort the rest of the walk —
                        // each archetype probe has its own try/catch but a Unity null-deref
                        // could still bubble up here.
                    }
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly(
                    "catalog-prewarm-fatal",
                    "CatalogPrewarm.Run threw " + ex.GetType().Name + ": " + ex.Message
                    + " -- falling back to lazy GetOrProbe path.");
            }
            _lastPrewarmCount = probed;
            DebugTAC_AI.Log("[CHASSIS] CatalogPrewarm probed " + probed + " block types.");
        }
    }
}
