namespace TAC_AI.AI.Forms.Smart.Pathing
{
    /// <summary>
    /// L-016: per-cache reset contract. Classes that hold per-mission state implement this
    /// and register themselves with <see cref="WorldResetRegistry"/> at Init. On world reset
    /// (save load, attract-mode mission swap, OnWorldReset hook), the registry walks every
    /// registered resettable and calls <see cref="ResetForWorldChange"/>.
    ///
    /// Implementers MUST:
    ///   - Be idempotent (called multiple times on adjacent resets).
    ///   - Be safe from any thread (registry walks on main thread, but a daemon may also
    ///     reset its own state from its loop — guard with locks or volatile flags as
    ///     appropriate).
    ///   - NOT throw — log to <c>[WORLD-RESET-FAIL]</c> and continue.
    ///   - Return quickly (&lt;10 ms per call) — registry walk is on the main thread.
    /// </summary>
    public interface IWorldResettable
    {
        /// <summary>Stable name for the per-hook <c>[WORLD-RESET]</c> log line.</summary>
        string ResetName { get; }

        /// <summary>Drop per-mission state; preserve cross-mission state (model params, registries).</summary>
        void ResetForWorldChange();
    }
}
