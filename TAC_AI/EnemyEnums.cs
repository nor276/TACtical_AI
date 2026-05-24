using System;

namespace TAC_AI.AI.Enemy
{
    // T2: result of RMission.SetupBaseOrMissionAI. Distinguishes branches that fully populate
    // EvilCommander/CommanderMind/CommanderAttack/CommanderSmarts (FullyConfigured) from
    // branches that only nudge one field and rely on the AutoSet/Handling/Smart chain to fill
    // in the rest (PartialMind). Replaces the previous bool which conflated the two cases.
    [Flags]
    public enum MissionSetupResult
    {
        None = 0,
        PartialMind = 1,
        FullyConfigured = 2,
    }

    // EnemyHandling is the COARSE role/dispatch axis only (which control rig + R*-combat handler +
    // movement-controller family). Air SUB-style (fixed-wing / helicopter / VTOL) is intentionally
    // NOT modelled here - it lives on AIControllerAir.FlyStyle, derived from thrust geometry inside
    // AIControllerAir.Initiate. Do NOT add an EnemyHandling.VTOL value: a VTOL's combat role is
    // Airplane (or Chopper) and only its flight mechanics differ (VtolAICore : AirplaneAICore).
    // Adding it would force writing the geometry verdict back onto EvilCommander - the exact
    // controller->classifier layering bug removed in P08 G.7 (see AIControllerAir.Initiate) - and
    // would slip past the Chopper||Airplane Grounded-demote guard in RecalMoveAIControllerNPT.
    public enum EnemyHandling
    {
        Wheeled,
        Chopper,
        Airplane,       // also covers VTOLs at the role level; sub-style tracked on AIControllerAir.FlyStyle
        Starship,
        Naval,
        SuicideMissile, // set for bolt tech splitoffs with less than 2 weapons (excluding cabs)
        Stationary,     // sit there like a good wingnut
    }

    public enum EnemyAttitude
    {
        Default,
        Homing, 
        Miner,
        Junker,
        OnRails,
        NPCBaseHost,
        Boss,
        Invader,
        Guardian,

        PartTurret,
        PartStatic,
        PartMimic,
    }

    public enum EnemyStanding
    {
        Enemy,      // Attack. Always
        Friendly,
        SubNeutral,
        Neutral,
        MissionControl,
    }

    public enum EnemySmarts
    {   // retroactive for each step lower on this.
        Default,
        Mild,
        Meh,
        Smrt,
        IntAIligent
    }

    public enum EnemyBolts
    {                   // Handler for how you want your bolts used
        Default,
        AtFull,
        AtFullOnAggro,
        Countdown,
        MissionTrigger,
    }
}
