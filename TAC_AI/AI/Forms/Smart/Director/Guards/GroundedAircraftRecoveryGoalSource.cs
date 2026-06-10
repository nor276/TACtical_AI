using System.Threading;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Mailbox writer for the GroundedAircraft hard-correct. Resolves the helper, builds a
    // GuardInjectedGoalSlot, and Interlocked.Exchanges it into the per-tech slot 0. Self-
    // expires deterministically via the MonoClock check inside GuardCorrectiveActuator.
    // SweepExpired - this layer does NOT poll. The strafing-target suppression is enforced
    // upstream in GroundedAircraftGuard (it never fires the actuator while diving on a live
    // ground target), so the writer is pure.
    public static class GroundedAircraftRecoveryGoalSource
    {
        private const int Slot = 0; // GroundedAircraft slot index, per GuardRegistry.

        public static void Write(TechId techId, Vector3 goalPos, float heading, long expiresMono,
            GuardGoalKind kind)
        {
            var helper = SmartRuntime.LookupHelperByTechId(techId);
            if (helper == null || helper.tank == null) return;

            var slot = new GuardInjectedGoalSlot
            {
                Goal = new TacticalGoal(goalPos, heading, Vector3.up * GuardCorrectiveActuator.ClimbRate, 1f),
                ExpiresMono = expiresMono,
                GuardOrdinal = (byte)GuardId.GroundedAircraft,
                Kind = (byte)kind,
                ExtraTargetPosition = goalPos,
                ExtraTargetHeading = heading,
                TechIdAtWrite = techId.Value,
            };
            Interlocked.Exchange(ref helper.GuardInjectedGoals[Slot], slot);
        }

        // 2 s ramp-down handoff: hold current altitude instead of an instant Goal.None cliff.
        // Runs on the GuardWorker bg thread (SweepExpired) - position MUST come from the
        // published SelfProbeSnapshot, never off the Transform.
        public static void WriteRampDown(TechId techId, long expiresMono)
        {
            var helper = SmartRuntime.LookupHelperByTechId(techId);
            if (helper == null || helper.tank == null) return;
            var probe = SmartRuntime.LookupSelfStateProbe(techId);
            var snap = probe != null ? probe.TryRead() : null;
            if (snap == null) return;

            Vector3 pos = snap.PositionWorld;
            var slot = new GuardInjectedGoalSlot
            {
                Goal = new TacticalGoal(pos, 0f, Vector3.zero, 1f),
                ExpiresMono = expiresMono,
                GuardOrdinal = (byte)GuardId.GroundedAircraft,
                Kind = (byte)GuardGoalKind.MaintainAltitude,
                ExtraTargetPosition = pos,
                ExtraTargetHeading = 0f,
                TechIdAtWrite = techId.Value,
            };
            Interlocked.Exchange(ref helper.GuardInjectedGoals[Slot], slot);
        }

        public static void Clear(TechId techId)
        {
            var helper = SmartRuntime.LookupHelperByTechId(techId);
            if (helper == null) return;
            Interlocked.Exchange(ref helper.GuardInjectedGoals[Slot], null);
        }
    }
}
