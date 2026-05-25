namespace TAC_AI.AI.Engine.Profiles
{
    /// <summary>
    /// A selectable, composable AI profile: behavior ids for the runner's functional slots. When a tech has a
    /// selected profile, ProfileRunner uses these ids to OVERRIDE the default EvilCommander / DediAI auto-
    /// assignment; an unset selection means auto-assign (behavior preserved). Allies are player-selectable
    /// (Decision 4); enemies are auto-only. The composable promise ("define a tank as Attack profile + Retreat
    /// profile") lives here: a profile names a movement behavior plus optional idle pre-pass and retreat post-pass,
    /// independently. The in-game picker + persistence + MP sync are wired in Step 7.
    /// </summary>
    public sealed class AIProfile
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly string PrePassId;    // no-target / idle pre-pass (null = use the alignment default)
        public readonly string MovementId;   // the role behavior (the meaningful core of a profile)
        public readonly string PostPassId;   // retreat post-pass (null = use the alignment default)

        public AIProfile(string id, string displayName, string movementId,
            string prePassId = null, string postPassId = null)
        {
            Id = id;
            DisplayName = displayName;
            MovementId = movementId;
            PrePassId = prePassId;
            PostPassId = postPassId;
        }
    }
}
