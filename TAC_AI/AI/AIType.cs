
namespace TAC_AI.AI {
    public enum AIType
    {   //like the old plans, we make the AI do stuff
        Null = -1,

        Escort,     // Good ol' player defender                     (Classic player defense numbnut)
        Assault,    // Run off and attack the enemies on your radar (Runs off (beyond radar range!) to attack enemies)
        Aegis,      // Protects the nearest non-player allied Tech  (Follows nearest ally, will chase enemy some distance)

        Prospector, // Harvest Chunks and return them to base       (Returns chunks when full to nearest receiver)
        Scrapper,   // Grab loose blocks but avoid combat           (Return to nearest base when threatened)
        Energizer,  // Charges up and/or heals other techs          (Return to nearest base when out of power)

        // MISC        (MultiTech) - BuildBeam disabled, will fire at any angle.
        MTTurret,   // Only turns to aim at enemy                   
        MTStatic,    // Does not move on own but does shoot back     
        MTMimic,    // Copies the actions of the closest non-MT Tech in relation     

        // P13 TD-2: LEGACY — retained for SAVE COMPATIBILITY. DO NOT REMOVE OR REORDER.
        // These are no longer assigned to a live TankAIHelper.DediAI; the role is now dispatched
        // as AIType.Escort + AIDriverType (Pilot/Sailor/Astronaut) in AlliedOperationsController.
        // They must survive because:
        //   1. ModuleAIExtension.savedMode persists the raw AIType ordinal in old saves;
        //   2. KickStart.TransferLegacyIfNeeded migrates them on load via the ordinal-dependent
        //      test `type >= AIType.Aviator`, which needs these to stay the trailing, contiguous,
        //      highest-valued members;
        //   3. they remain aiIcons / AIWiki dictionary keys and appear in ManWorldRTS switches.
        Aviator,    // -> Escort + Pilot      (Flies aircraft, death from above) [unload distance will break!]
        Buccaneer,  // -> Escort + Sailor     (Avoids terrain above water level)
        Astrotech,  // -> Escort + Astronaut  (Follows player above ground level, can follow into the sky)
    }
    //All of their operating ranges are ultimately determined by the Tech's biggest provided vision/radar range.

    public enum AIDriverType
    {   //like the old plans, we make the AI do stuff
        Null = -1,
        AutoSet,      // Requested auto-set

        Tank,       // Classic wheeled
        Pilot,      // Flies aircraft, death from above, nuff said  (Flies above ground, by the player and keeps distance) [unload distance will break!]
        Sailor,     // Sails ships amongst ye seas~                 (Avoids terrain above water level)
        Astronaut,  // Flies hoverships and kicks Tech              (Follows player a certain distance above ground level and can follow into the sky)

        // STATIC (bases) - forces AI to stay in "Guard"
        Stationary,
    }
}
