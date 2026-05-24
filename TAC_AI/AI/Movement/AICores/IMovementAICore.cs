using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    public interface IMovementAICore
    {
        bool DriveMaintainer(TankAIHelper helper, Tank tank, ref EControlCoreSet core);

        void Initiate(Tank tank, IMovementAIController controller);

        bool DriveDirector(ref EControlCoreSet core);

        bool DriveDirectorRTS(ref EControlCoreSet core);

        bool DriveDirectorEnemy(Enemy.EnemyMind mind, ref EControlCoreSet core);
        bool DriveDirectorEnemyRTS(Enemy.EnemyMind mind, ref EControlCoreSet core);

        bool TryAdjustForCombat(bool between, ref Vector3 pos, ref EControlCoreSet core);

        bool TryAdjustForCombatEnemy(Enemy.EnemyMind mind, ref Vector3 pos, ref EControlCoreSet core);

        Vector3 AvoidAssist(Vector3 targetIn, Vector3 predictionOffset);

        float GetDrive { get; }
    }

    public interface IAirMovementAICore : IMovementAICore
    {
        bool IsRotorcraft { get; }
        bool IsFixedWing { get; }
    }
}
