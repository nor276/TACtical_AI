using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;
using UnityEngine;
using TerraTechETCUtil;
#if DEBUG
using System.Text;
#endif

namespace TAC_AI.AI
{
    internal class AIControllerDefault : MovementControllerBase, IPathfindable
    {
        internal static FieldInfo boostGet = typeof(Thruster).GetField("m_Force", BindingFlags.NonPublic | BindingFlags.Instance);

        public override Vector3 PathPoint => PathPointMain.ScenePosition;
        private WorldPosition PathPointMain = WorldPosition.FromScenePosition(Vector3.zero);
        public Vector3 PathPointSet
        {
            set
            {
                if (value.IsNaN() || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z))
                {
                    DebugTAC_AI.Assert("AIControllerDefault.PathPointSet - lastDestination was NaN or infinity.  Defaulting to own position");
                    PathPointMain = WorldPosition.FromScenePosition(Tank.boundsCentreWorldNoCheck);
                    return;
                }
                PathPointMain = WorldPosition.FromScenePosition(value);
            }
        }

        public Vector3 BoostBias = Vector3.zero;

        public bool AutoPathfind { get; set; } = false;
        public bool Do3DPathing => Helper.Attempt3DNavi;
        public AIEAutoPather Pathfinder { get; set; }
        public WaterPathing WaterPathing { get; set; }
        public float PathingPrecision { get; set; } = 10;
        public byte MaxPathDifficulty { get; set; } = AIEAutoPather.DefaultMaxDifficulty;
        public readonly Queue<WorldPosition> PathPlanned = new Queue<WorldPosition>();
        public WorldPosition TargetDestination;

        public Vector3 CurrentPosition()
        {
            return Tank.boundsCentreWorldNoCheck;
        }
        public Vector3 GetTargetDestination()
        {
            return TargetDestination.ScenePosition;
        }
        public bool IsRunningLowOnPathPoints()
        {
            return PathPlanned.Count < 3;
        }
#if DEBUG
        private static StringBuilder SB = new StringBuilder();
#endif
        public void OnPartialPathfinding(List<WorldPosition> pos)
        {
            if (DebugTAC_AI.NoLogPathing)
            {
                if (pos.Count == 0)
                    return;
                foreach (var item in pos)
                {
                    PathPlanned.Enqueue(item);
                }
            }
            else
            {
                if (pos.Count == 0)
                {
                    DebugTAC_AI.Log(Tank.name + ": OnPartialPathfinding - Path - NONE");
                    return;
                }
#if DEBUG
                int num = 0;
                SB.Clear();
                foreach (var item in pos)
                {
                    PathPlanned.Enqueue(item);
                    SB.Append(" > " + item.ScenePosition.ToString());
                    num++;
                }
                DebugTAC_AI.Log(Tank.name + ": OnPartialPathfinding - Path -" + SB.ToString());
                SB.Clear();
#endif
            }
        }
        public void OnFinishedPathfinding(List<WorldPosition> pos)
        {
            if (pos == null)
            {
                PathPlanned.Clear();
                return;
            }
            PathPlanned.Clear();
            if (DebugTAC_AI.NoLogPathing)
            {
                if (pos.Count == 0)
                    return;
                foreach (var item in pos)
                {
                    PathPlanned.Enqueue(item);
                }
            }
            else
            {
                if (pos.Count == 0)
                {
                    DebugTAC_AI.Log(Tank.name + ": OnFinishedPathfinding - Finished AutoPathing with " + PathPlanned.Count + " waypoints to follow.");
                    DebugTAC_AI.Log(Tank.name + ": OnFinishedPathfinding - Path - NONE");
                    return;
                }
#if DEBUG
                int num = 0;
                SB.Clear();
                foreach (var item in pos)
                {
                    PathPlanned.Enqueue(item);
                    SB.Append(" > " + item.ScenePosition.ToString());
                    num++;
                }
                DebugTAC_AI.Log(Tank.name + ": OnFinishedPathfinding - Finished AutoPathing with " + PathPlanned.Count + " waypoints to follow.");
                DebugTAC_AI.Log(Tank.name + ": OnFinishedPathfinding - Path -" + SB.ToString());
                SB.Clear();
#endif
            }
        }

        public override void DriveDirector(ref EControlCoreSet core)
        {
            if (this.Helper == null)
            {
                string tankName = this.Tank.IsNotNull() ? this.Tank.name : "UNKNOWN_TANK";
                DebugTAC_AI.Log(KickStart.ModID + ": AI " + tankName + ":  FIRED DriveDirector WITHOUT THE REQUIRED TankAIHelper MODULE!!!");
                return;
            }

            if (this.Helper.AIAlign == AIAlignment.Player)
            {
                if (this.AICore == null)
                {
                    string tankName = this.Tank.IsNotNull() ? this.Tank.name : "UNKNOWN_TANK";
                    DebugTAC_AI.Log(KickStart.ModID + ": AI " + tankName + ":  FIRED DriveDirector WITHOUT ANY SET AICore!!!");
                    return;
                }
                this.AICore.DriveDirector(ref core);
            }
            else
            {
                this.AICore.DriveDirectorEnemy(this.EnemyMind, ref core);
            }
        }
        public override void DriveDirectorRTS(ref EControlCoreSet core)
        {
            if (this.Helper == null)
            {
                string tankName = this.Tank.IsNotNull() ? this.Tank.name : "UNKNOWN_TANK";
                DebugTAC_AI.Log(KickStart.ModID + ": AI " + tankName + ":  FIRED DriveDirectorRTS WITHOUT THE REQUIRED TankAIHelper MODULE!!!");
                return;
            }

            if (this.Helper.AIAlign == AIAlignment.Player)
            {
                if (this.AICore == null)
                {
                    string tankName = this.Tank.IsNotNull() ? this.Tank.name : "UNKNOWN_TANK";
                    DebugTAC_AI.Log(KickStart.ModID + ": AI " + tankName + ":  FIRED DriveDirectorRTS WITHOUT ANY SET AICore!!!");
                    return;
                }
                this.AICore.DriveDirectorRTS(ref core);
            }
            else
            {
                this.AICore.DriveDirectorEnemyRTS(this.EnemyMind, ref core);
            }
        }

        public override void DriveMaintainer(ref EControlCoreSet core)
        {
            AICore.DriveMaintainer(Helper, Tank, ref core);
        }

        protected override IMovementAICore SelectCore(EnemyMind mind)
        {
            MovementCoreKind kind = mind.IsNull()
                ? MovementDispatch.CoreForPlayer(Helper.DriverType)
                : MovementDispatch.CoreForEnemy(mind.EvilCommander);
            if (kind == MovementCoreKind.None)
            {
                string who = mind.IsNull() ? "DriverType '" + Helper.DriverType + "'" : "EvilCommander '" + mind.EvilCommander + "'";
                DebugTAC_AI.LogWarnPlayerOncePerKey("AIControllerDefault.unmapped." + who,
                    KickStart.ModID + ": AIControllerDefault.SelectCore: unmapped " + who
                    + " on " + (Tank.IsNotNull() ? Tank.name : "<null>") + " - falling back to LandAICore.", null);
                kind = MovementCoreKind.Land;
            }
            switch (kind)
            {
                case MovementCoreKind.Sea:   return new SeaAICore();
                case MovementCoreKind.Space: return new SpaceAICore();
                default:                     return new LandAICore();
            }
        }

        protected override void OnPostInitiate()
        {
            CheckBoosters();
            DebugTAC_AI.LogAISetup(KickStart.ModID + ": Added ground AI for " + Tank.name);
        }
        private void CheckBoosters()
        {
            float lowestDelta = 100;
            float guzzleLevel = 0;
            int consumeBoosters = 0;
            Vector3 biasDirection = Vector3.zero;
            Vector3 boostBiasDirection = Vector3.zero;

            float fanThrust = 0.0f;
            float boosterThrust = 0.0f;

            foreach (ModuleBooster module in Tank.blockman.IterateBlockComponents<ModuleBooster>())
            {
                foreach (FanJet jet in module.transform.GetComponentsInChildren<FanJet>())
                {
                    float thrust = (float)RawTechBase.thrustRate.GetValue(jet);
                    float spin = (float)RawTechBase.spinDat.GetValue(jet);
                    if (spin <= 10)
                    {
                        biasDirection -= Tank.rootBlockTrans.InverseTransformDirection(jet.EffectorForward) * thrust;
                        if (spin < lowestDelta)
                            lowestDelta = spin;
                    }
                    if (Tank.rootBlockTrans.InverseTransformDirection(jet.EffectorForward).z < -0.5)
                    {
                        fanThrust += thrust;
                    }
                }
                foreach (BoosterJet boost in module.transform.GetComponentsInChildren<BoosterJet>())
                {
                    if (boost.ConsumesFuel)
                    {
                        consumeBoosters++;
                        guzzleLevel += boost.BurnRate;
                    }

                    float force = (float)boostGet.GetValue(boost);
                    if (Tank.rootBlockTrans.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection)).z < -0.5)
                    {
                        boosterThrust += force;
                    }

                    boostBiasDirection -= Tank.rootBlockTrans.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection)) * force;
                }
            }

            BoostBias = boostBiasDirection.normalized;
        }
        public void TryBoost(bool forwardsOnly = true)
        {
            if (Helper.Obst)
                return;
            if (forwardsOnly)
            {
                if (BoostBias.z > 0.75f)
                    Helper.MaxBoost();
            }
            else
            {
                Helper.MaxBoost();
            }
        }
        public void TryBoost(Vector3 headingLocalCab)
        {
            if (Helper.Obst)
                return;
            if (Vector3.Dot(BoostBias, headingLocalCab) > 0.75f)
                Helper.MaxBoost();
        }

        public override void OnMoveWorldOrigin(IntVector3 move)
        {

        }
        public override Vector3 GetDestination()
        {
            return Helper.lastDestinationCore;
        }

        protected override void OnRecycle()
        {
            this.SetAutoPathfinding(false);
        }
    }
}
