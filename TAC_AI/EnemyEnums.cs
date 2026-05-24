using System;

namespace TAC_AI.AI.Enemy
{
    [Flags]
    public enum MissionSetupResult
    {
        None = 0,
        PartialMind = 1,
        FullyConfigured = 2,
    }

    public enum EnemyHandling
    {
        Wheeled,
        Chopper,
        Airplane,
        Starship,
        Naval,
        SuicideMissile,
        Stationary,
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
        Enemy,
        Friendly,
        SubNeutral,
        Neutral,
        MissionControl,
    }

    public enum EnemySmarts
    {
        Default,
        Mild,
        Meh,
        Smrt,
        IntAIligent
    }

    public enum EnemyBolts
    {
        Default,
        AtFull,
        AtFullOnAggro,
        Countdown,
        MissionTrigger,
    }
}
