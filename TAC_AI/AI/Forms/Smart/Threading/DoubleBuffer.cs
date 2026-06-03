using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Threading
{
    /// <summary>
    /// Two-slot atomic-reference-swap primitive for publishing immutable snapshots from
    /// one writer to many readers. Standard cross-thread snapshot pattern.
    ///
    /// Contract: <see cref="THREADING-CONTRACT.md §4"/>. <typeparamref name="T"/> MUST be
    /// immutable BY DISCIPLINE (a sealed class with readonly fields set in the constructor,
    /// or a readonly struct). Mutating a snapshot after Write is undefined behavior; the
    /// writer constructs a fresh snapshot for each Write. The type system cannot enforce
    /// this — it's a documented protocol the writer must honour.
    ///
    /// Per FIX-PLAN.md Phase 1.2: the previous `where T : class` constraint blocked
    /// publication of readonly-struct snapshots (StrategicPlan, KinematicState,
    /// TacticalGoal, TargetAssignment). The internal storage uses `object` so both
    /// reference- and value-typed T compose uniformly. For struct T, Write boxes once
    /// per call (equivalent cost to a wrapper class); for class T, the reference is
    /// stored directly with no boxing.
    ///
    /// Memory ordering: Interlocked.Exchange provides full-fence semantics on .NET
    /// Framework 4.6.1. Writes to the T instance's fields that complete before Write
    /// are observable to any subsequent Read on any thread. No additional barriers
    /// required.
    /// </summary>
    public sealed class DoubleBuffer<T>
    {
        private object _current;

        public DoubleBuffer(T initial)
        {
            _current = (object)initial;
        }

        /// <summary>Returns the currently-published snapshot. Lock-free.</summary>
        public T Read() => (T)Volatile.Read(ref _current);

        /// <summary>Atomically swaps the published reference to <paramref name="newSnapshot"/>.</summary>
        public void Write(T newSnapshot)
        {
            Interlocked.Exchange(ref _current, (object)newSnapshot);
        }
    }
}
