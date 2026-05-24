using System;

namespace TAC_AI
{
    [Flags]
    public enum AIEnabledModes : byte
    {
        None = 0,
        Assassin = 1,
        Aegis = 2,
        Prospector = 4,
        Scrapper = 8,
        Energizer = 16,
        Aviator = 32,
        Astrotech = 64,
        Buccaneer = 128,
        All = 255,
    }
}
