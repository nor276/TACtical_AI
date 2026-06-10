using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;

namespace TAC_AI.AI.Forms.Smart.Director.Actions
{
    /// <summary>
    /// Per-call context for action.Apply. Tagged-struct mirrors §3.2 DirectiveValue pattern:
    /// the same context type covers every verb so the picker constructs one struct and the
    /// action picks the fields it needs.
    /// </summary>
    public readonly struct DirectorActionContext
    {
        public readonly ModelId Model;          // lr_scale / temp_adjust / freeze_model
        public readonly SmartIdentity Identity; // replay_bank
        public readonly float Scalar;           // lr_scale.factor, temp_adjust.delta
        public readonly int IntValue;           // freeze_model.durationSec, replay_bank.count
        public readonly byte Mode;              // freeze_model.mode (0=diagnostic, 1=soft)
        public readonly string ConstraintName;  // who triggered us (for journal)

        public DirectorActionContext(ModelId model, SmartIdentity identity, float scalar, int intValue, byte mode, string constraintName)
        {
            Model = model;
            Identity = identity;
            Scalar = scalar;
            IntValue = intValue;
            Mode = mode;
            ConstraintName = constraintName ?? string.Empty;
        }
    }

    /// <summary>
    /// One of the 7 Action verbs in §5. Apply returns a JournalEntry describing the
    /// intervention (kind, body). Undo lets the rollback machinery reverse the change
    /// where possible (lr_scale carries prior LR; freeze_model carries prior deadline;
    /// replay_bank is irreversible — events fold into Adam).
    /// </summary>
    public interface IDirectorAction
    {
        string Verb { get; }
        bool TryApply(DirectorActionContext ctx, out JournalEntry entry);
        void Undo(JournalEntry entry);
        string Describe(DirectorActionContext ctx);
    }
}
