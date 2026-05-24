
namespace TAC_AI
{
    public enum AIRunState
    {
        Off,
        Default,
        Advanced,
    }
    public enum AIAlignment
    {
        Static,
        PlayerNoAI,
        Player,
        NonPlayer,
        Neutral,
    }

    public enum AIWeaponType
    {
        Unknown,
        Direct,
        Indirect,
    }
    public enum AIWeaponState
    {
        Normal,
        Enemy,
        HoldFire,
        Obsticle,
        Mimic,
    }
    public enum AIAnchorState
    {
        None,
        Anchored,
        Anchor,
        AnchorAuto,
        AnchorStaticAI,
        ForceAnchor,
        Unanchor,
    }

    public enum AIDriveState
    {
        None,
        Driving,
        NonPlayer,
        Neutral,
    }
    public enum AIThrottleState
    {
        PivotOnly,
        Yield,
        FullSpeed,
        ForceSpeed
    }

    public enum NP_Types
    {
        Player,
        NonNPT,
        Friendly,
        Neutral,
        NonAggressive,
        SubNeutral,
        Enemy,
    }
}
