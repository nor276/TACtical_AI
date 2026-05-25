using TerraTechETCUtil;
using TAC_AI.AI.Engine.Registry;
using TAC_AI.AI.Engine.Profiles;
using TAC_AI.AI.Forms;
using TAC_AI.AI.Tunables;
using TAC_AI.AI.Tunables.Catalog;

namespace TAC_AI.AI.Engine
{
    /// <summary>
    /// The single boot hook for the modular AI: builds the tunable registry, scans/registers behavior
    /// modules, and publishes default profiles. Called from all three init sequences (MainOfficialInit,
    /// MainOfficialInitIterate, TTMM Main) after RawTechExporter.Initiate and before/within the options
    /// publish - wired in Step 7. Idempotent across DeInit -> reInit; torn down in DeInitALL.
    /// </summary>
    public static class AIModuleBootstrap
    {
        private static bool initialized;

        public static void InitAIModules()
        {
            if (initialized) return;

            // 1) declare tunables (catalog files). More catalogs register here as they are added (Step 6).
            CombatTunables.Register();
            RangeTimingTunables.Register();
            MovementFlightTunables.Register();

            // 2) discover behavior modules by being in the assembly with the right attribute/namespace.
            AIModuleLoader.ScanAndRegister();

            // 2b) discover the top-level AI forms (Vanilla / Basic / Modified / future) the same way - by existing.
            AIFormRegistry.ScanAndRegister();

            // 3) publish profiles. Default selectable allied profiles (mirroring the DediAI roles) - the picker
            //    catalog (Step 7). Selecting one overrides the runner's default auto-assignment.
            DefaultProfiles.Register();

            initialized = true;
            DebugTAC_AI.Log("AIModuleBootstrap: InitAIModules complete. profiles=" + ProfileStore.Count);
        }

        public static void DeInitAIModules()
        {
            if (!initialized) return;
            TunableRegistry.DeInitRestores();   // run host-only world-state restores
            AIModuleRegistry.Clear();
            AIFormRegistry.Clear();
            ProfileStore.Clear();
            initialized = false;
        }
    }
}
