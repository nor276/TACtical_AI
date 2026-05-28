using UnityEngine;
using TAC_AI.AI.Movement.AICores;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Forms.Vanilla
{
    // v2 isolation: Vanilla's no-op movement controller. Vanilla hands back to the stock game AI, so this drives
    // nothing - it exists only so the shell's per-tech lifecycle always has a non-null IMovementAIController
    // (e.g. EnemyMind.UpdateEnemyMind). SelectCore returns null (MovementControllerBase.Initiate null-guards it).
    internal class VanillaMovementController : MovementControllerBase
    {
        protected override IMovementAICore SelectCore(EnemyMind mind) => null;
        public override Vector3 PathPoint => Tank ? Tank.boundsCentreWorldNoCheck : Vector3.zero;
        public override void DriveDirector(ref EControlCoreSet core) { }
        public override void DriveDirectorRTS(ref EControlCoreSet core) { }
        public override void DriveMaintainer(ref EControlCoreSet core) { }
        public override void OnMoveWorldOrigin(IntVector3 move) { }
        public override Vector3 GetDestination() => Tank ? Tank.boundsCentreWorldNoCheck : Vector3.zero;
    }
}
