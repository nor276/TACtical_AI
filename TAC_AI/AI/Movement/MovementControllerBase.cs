using UnityEngine;
using TAC_AI.AI.Movement.AICores;
using TerraTechETCUtil;

namespace TAC_AI.AI
{
    // Shared plumbing for the three IMovementAIController implementations (AIControllerDefault /
    // AIControllerAir / AIControllerStatic). Owns the common Tank / Helper / AICore / EnemyMind state,
    // the Initiate template, Recycle, UpdateEnemyMind and GetDrive. Concrete controllers supply:
    //   - SelectCore(mind): which inner IMovementAICore to build (Default: by DriverType/EvilCommander,
    //     Air: by thrust geometry, Static: always StaticAICore),
    //   - OnPreInitiate/OnPostInitiate/OnRecycle hooks for their controller-specific setup/teardown,
    //   - the per-tick Drive* members, PathPoint, OnMoveWorldOrigin and GetDestination.
    // Initiate order is fixed: OnPreInitiate -> SelectCore -> AICore.Initiate -> OnPostInitiate, so a
    // controller whose SelectCore depends on prep work (e.g. Air's CheckAllFlightBlocks) does it in
    // OnPreInitiate.
    internal abstract class MovementControllerBase : MonoBehaviour, IMovementAIController
    {
        private Tank _tank;
        public Tank Tank { get => _tank; internal set => _tank = value; }
        private TankAIHelper _helper;
        public TankAIHelper Helper { get => _helper; internal set => _helper = value; }
        private IMovementAICore _AI;
        public IMovementAICore AICore { get => _AI; internal set => _AI = value; }
        private Enemy.EnemyMind _mind;
        public Enemy.EnemyMind EnemyMind { get => _mind; internal set => _mind = value; }

        public float GetDrive => _AI != null ? _AI.GetDrive : 0f;

        public void Initiate(Tank tank, TankAIHelper helper, Enemy.EnemyMind mind = null)
        {
            Tank = tank;
            Helper = helper;
            EnemyMind = mind;
            OnPreInitiate();
            AICore = SelectCore(mind);
            AICore.Initiate(tank, this);
            OnPostInitiate();
        }

        protected abstract IMovementAICore SelectCore(Enemy.EnemyMind mind);
        protected virtual void OnPreInitiate() { }
        protected virtual void OnPostInitiate() { }
        protected virtual void OnRecycle() { }

        public void UpdateEnemyMind(Enemy.EnemyMind mind) => EnemyMind = mind;

        public void Recycle()
        {
            AICore = null;
            if (this.IsNotNull())
            {
                OnRecycle();
                DestroyImmediate(this);
            }
        }

        public abstract Vector3 PathPoint { get; }
        public abstract void DriveDirector(ref EControlCoreSet core);
        public abstract void DriveDirectorRTS(ref EControlCoreSet core);
        public abstract void DriveMaintainer(ref EControlCoreSet core);
        public abstract void OnMoveWorldOrigin(IntVector3 move);
        public abstract Vector3 GetDestination();
    }
}
