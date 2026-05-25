namespace TAC_AI.AI.Engine.Profiles
{
    /// <summary>
    /// Registers the default selectable allied profiles (mirroring today's DediAI roles), so the Step-7 picker has
    /// a catalog out of the box. Selecting one sets helper.SelectedAIProfileId, which ProfileRunner honors over the
    /// default auto-assignment. These defaults reproduce existing behavior; players (and future custom profiles)
    /// can compose the slots differently. Called from AIModuleBootstrap.InitAIModules.
    /// </summary>
    public static class DefaultProfiles
    {
        public static void Register()
        {
            ProfileStore.Register(new AIProfile("Escort",     "Escort",              "Allied.Escort"));
            ProfileStore.Register(new AIProfile("Assault",    "Assault",             "Allied.Assault"));
            ProfileStore.Register(new AIProfile("Aegis",      "Aegis (Protect)",     "Support.Protect"));
            ProfileStore.Register(new AIProfile("Prospector", "Prospector (Mine)",   "Economy.Mine"));
            ProfileStore.Register(new AIProfile("Scrapper",   "Scrapper (Scavenge)", "Economy.Scavenge"));
            ProfileStore.Register(new AIProfile("Energizer",  "Energizer (Charge)",  "Allied.Energizer"));
            ProfileStore.Register(new AIProfile("MultiTech",  "MultiTech (Static)",  "Allied.MultiTech.Static"));
        }
    }
}
