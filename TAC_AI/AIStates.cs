
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
    { // 0 Normal, 1 Enemy, 2 HoldFire, 3 Obsticle, 4 Mimic
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
    /// <summary>
    /// Throttle application mode. Declaration order is NOT load-bearing — all consumers use
    /// named values via switch or `==`/`!=`. Free to reorder if needed.
    /// </summary>
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
