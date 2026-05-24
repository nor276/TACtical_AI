using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TerraTechETCUtil;

namespace TAC_AI.Templates
{
    internal class PlayerSpawnAid
    {
        public static void TryBotePlayerSpawn()
        {
            if (Singleton.playerTank.IsNull())
            {
                DebugTAC_AI.Log(KickStart.ModID + ": Retrofit failed - Player Tech is null");
                return;
            }
            Tank tech = Singleton.playerTank;

            if (tech.name == "My Tech")
            {
                if (RawTechTemplate.GetBBCost(tech) < 2272)
                    RawTechLoader.ReconstructPlayerTech(SpawnBaseTypes.FTUEGSOGrade1Bote, SpawnBaseTypes.FTUEGSOGrade1BoteFallBack);
            }
            else if (tech.name == "FTUE GSO grade 2")
            {
                RawTechLoader.ReconstructPlayerTech(SpawnBaseTypes.FTUEGSOGrade2Bote, SpawnBaseTypes.FTUEGSOGrade2BoteFallBack);
            }
            else if (tech.name == "FTUE GSO grade 3")
            {
                RawTechLoader.ReconstructPlayerTech(SpawnBaseTypes.FTUEGSOGrade3Bote, SpawnBaseTypes.FTUEGSOGrade3BoteFallBack);
            }
            else if (tech.name == "FTUE GSO grade 4")
            {
                RawTechLoader.ReconstructPlayerTech(SpawnBaseTypes.FTUEGSOGrade4Bote, SpawnBaseTypes.FTUEGSOGrade4BoteFallBack);
            }
            else if (tech.name == "FTUE GSO grade 5")
            {
                RawTechLoader.ReconstructPlayerTech(SpawnBaseTypes.FTUEGSOGrade5Bote, SpawnBaseTypes.FTUEGSOGrade5BoteFallBack);
            }
        }
    }
}
