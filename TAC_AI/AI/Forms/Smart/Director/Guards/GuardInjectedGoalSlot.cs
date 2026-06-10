using TAC_AI.AI.Forms.Smart.Control;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Goal kind carried by an injected mailbox slot. Plain byte values so the slot stays
    // a cheap reference cell. HoverToAltitude = the 5 s recovery climb; MaintainAltitude =
    // the 2 s ramp-down handoff after the ceiling.
    public enum GuardGoalKind : byte
    {
        None = 0,
        HoverToAltitude = 1,
        MaintainAltitude = 2,
    }

    // Per-guard mailbox cell. MUST be a class (R3-B2): Interlocked.Exchange<T> needs a
    // reference type; a ~14-word struct can't be atomically swapped under .NET 4.6.1.
    // Writers Exchange a fresh instance; eviction-clear via CompareExchange(ref, null,
    // expectedStale) so a fresh write that races the clear survives. TechIdAtWrite carries
    // the owning tech (RM-9 defensive sweep) — SweepExpired + the consumer reject a slot
    // whose stamp does not match the live tech after a pool-recycle.
    public sealed class GuardInjectedGoalSlot
    {
        public TacticalGoal Goal;
        public long ExpiresMono;
        public byte GuardOrdinal;
        public byte Kind;
        public Vector3 ExtraTargetPosition;
        public float ExtraTargetHeading;
        public long TechIdAtWrite;
    }
}
