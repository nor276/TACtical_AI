using TAC_AI.AI.Engine;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Forms
{
    /// <summary>
    /// A self-contained, auto-discovered AI "form" (flavor) - it owns the per-tick AI dispatch for a whole AI
    /// style. This is the top-level extension point: drop a new class implementing IAIForm into ANY folder and the
    /// AIFormRegistry discovers it on load (by its existence - no central switch to edit) and publishes it as an
    /// in-game option. The player picks one ACTIVE form globally. A form may dispatch to the shared behavior
    /// modules (as ModifiedForm does) or implement entirely different logic/pathing of its own.
    ///
    /// RunEnemy/RunAllied receive the per-tech AIContext (and, for enemies, the EnemyMind) and are responsible for
    /// the whole tick: load the drive operator, decide + apply intent, commit it (see ModifiedForm for the
    /// reference implementation). Forms are stateless singletons - one instance is shared across all techs.
    /// </summary>
    public interface IAIForm
    {
        /// <summary>Stable id used for the active-form setting + selection. Must be unique.</summary>
        string Id { get; }
        /// <summary>Human label shown in the in-game form selector.</summary>
        string DisplayName { get; }

        void RunEnemy(AIContext ctx, EnemyMind mind);
        void RunAllied(AIContext ctx);
    }
}
