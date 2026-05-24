using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement.AICores;

namespace TAC_AI.AI
{
    /// <summary>
    /// Handles all anchored operations
    /// </summary>
    internal class AIControllerStatic : MovementControllerBase
    {
        public Vector3 AimTarget = Vector3.zero;
        public WorldPosition SceneStayPos = WorldPosition.FromGameWorldPosition(Vector3.zero);
        public float HoldHeight = 0;

        public override Vector3 PathPoint => SceneStayPos.ScenePosition.SetY(HoldHeight);
        public Vector2 IdleFacingDirect = Vector2.up;
        public Vector3 RestFacing = Vector3.forward;

        protected override IMovementAICore SelectCore(EnemyMind mind) => new StaticAICore();

        protected override void OnPreInitiate()
        {
            HoldHeight = Tank.boundsCentreWorld.y;
            SceneStayPos = WorldPosition.FromScenePosition(Tank.boundsCentreWorld);
            IdleFacingDirect = Vector2.up;
            RestFacing = Tank.rootBlockTrans.forward;
            AimTarget = Tank.boundsCentreWorld + Tank.rootBlockTrans.forward * 100f;
        }

        protected override void OnPostInitiate()
        {
            DebugTAC_AI.LogAISetup(KickStart.ModID + ": Added static (anchored) AI for " + Tank.name);
        }

        public override void DriveDirector(ref EControlCoreSet core)
        {
            if (Helper == null)
            {
                string tankName = Tank.IsNotNull() ? Tank.name : "UNKNOWN_TANK";
                DebugTAC_AI.Assert(true, KickStart.ModID + ": AI " + tankName + ":  FIRED DriveDirector WITHOUT THE REQUIRED TankAIHelper MODULE!!!");
                return;
            }
            Helper.TryInsureAutoAnchor();

            if (Helper.AIAlign == AIAlignment.Player)// Allied
            {
                if (AICore == null)
                {
                    string tankName = Tank.IsNotNull() ? Tank.name : "UNKNOWN_TANK";
                    DebugTAC_AI.Assert(true, KickStart.ModID + ": AI " + tankName + ":  FIRED DriveDirector WITHOUT ANY SET AICore!!!");
                    return;
                }
                AICore.DriveDirector(ref core);
            }
            else//ENEMY
            {
                AICore.DriveDirectorEnemy(EnemyMind, ref core);
            }
        }

        public override void DriveDirectorRTS(ref EControlCoreSet core)
        {   // Ignore player movement commands but follow attack commands
            if (Helper == null)
            {
                string tankName = Tank.IsNotNull() ? Tank.name : "UNKNOWN_TANK";
                DebugTAC_AI.Assert(true, KickStart.ModID + ": AI " + tankName + ":  FIRED DriveDirectorRTS WITHOUT THE REQUIRED TankAIHelper MODULE!!!");
                return;
            }
            Helper.TryInsureAutoAnchor();

            if (Helper.AIAlign == AIAlignment.Player)// Allied
            {
                if (AICore == null)
                {
                    string tankName = Tank.IsNotNull() ? Tank.name : "UNKNOWN_TANK";
                    DebugTAC_AI.Assert(true, KickStart.ModID + ": AI " + tankName + ":  FIRED DriveDirectorRTS WITHOUT ANY SET AICore!!!");
                    return;
                }
                AICore.DriveDirectorRTS(ref core);
            }
            else//ENEMY
            {
                AICore.DriveDirectorEnemyRTS(EnemyMind, ref core);
            }
        }

        public override void DriveMaintainer(ref EControlCoreSet core)
        {
            AICore.DriveMaintainer(Helper, Tank, ref core);
        }

        public override void OnMoveWorldOrigin(IntVector3 move)
        {
            AimTarget += move;
        }
        public override Vector3 GetDestination()
        {
            return PathPoint;
        }

        public bool IsTurretable => !Tank.Anchors.Fixed;
        public bool IsSkyAnchoredOnly => !Tank.Anchors.Fixed && Tank.IsSkyAnchored;

    }
}
