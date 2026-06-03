using UnityEngine;
using TAC_AI.AI.Movement.AICores;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Forms.Smart
{
    /// <summary>
    /// Smart's IMovementAIController. v1.3 scaffold skeleton.
    ///
    /// Extends MovementControllerBase to inherit the common Tank/Helper/EnemyMind
    /// storage, Initiate template, GetDrive forwarding, and Recycle plumbing —
    /// mirrors VanillaMovementController's pattern.
    ///
    /// Per CONTROL-CONTRACT.md §8 (OQ-10 resolution): this is a thin shell. The
    /// substantive control work happens in SmartForm.ControlFrame via direct
    /// TankControl write (OQ-6 resolution). The Drive* callbacks here will, in
    /// future workflow steps, mirror the latest MPC-derived intent to the
    /// EControlCoreSet bus for engine-side consistency; at v1.3 they no-op.
    ///
    /// SelectCore returns null — Smart does not use the per-vehicle-class
    /// IMovementAICore system (Default/Air/Static cores); the MPC dispatches
    /// per-tech without that abstraction. MovementControllerBase.Initiate
    /// null-guards a null core.
    /// </summary>
    internal class SmartMovementController : MovementControllerBase
    {
        protected override IMovementAICore SelectCore(EnemyMind mind) => null;

        public override Vector3 PathPoint =>
            Tank != null ? Tank.boundsCentreWorldNoCheck : Vector3.zero;

        public override void DriveDirector(ref EControlCoreSet core)
        {
            // v1.3: no-op.
            // Future per CONTROL §8.3: mirror MPC's latest intent to `core` so
            // any engine-side reader of the bus sees consistent state. Actual
            // commits go through ControlFrame, not this path.
        }

        public override void DriveDirectorRTS(ref EControlCoreSet core)
        {
            // v1.3: no-op. Future: same as DriveDirector under RTS control mode.
        }

        public override void DriveMaintainer(ref EControlCoreSet core)
        {
            // v1.3: no-op. Future: same as DriveDirector during the per-frame
            // maintenance pass.
        }

        public override void OnMoveWorldOrigin(IntVector3 move)
        {
            // v1.3: no-op.
            // Future per CONTROL §8.3: apply the world-recenter offset to any
            // cached world-space positions in Smart's per-tech state (planned
            // path samples, tactical-goal points).
        }

        public override Vector3 GetDestination() =>
            Tank != null ? Tank.boundsCentreWorldNoCheck : Vector3.zero;
    }
}
