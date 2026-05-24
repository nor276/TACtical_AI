using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TAC_AI.AI
{
    public struct EControlOperatorSet
    {
        public EDriveDest DriveDest;
        public EDriveFacing DriveDir;
        private Vector3 lastDest;
        public Vector3 lastDestination => lastDest;
        public void SetLastDest(Vector3 posScene)
        {
            if (posScene.IsNaN())
                DebugTAC_AI.Exception("EControlOperatorSet - lastDestination was NaN!");
            lastDest = posScene;
        }

        public static EControlOperatorSet Default => new EControlOperatorSet(EDriveDest.None, EDriveFacing.Stop);

        private EControlOperatorSet(EDriveDest move, EDriveFacing facing)
        {
            DriveDest = move;
            DriveDir = facing;
            lastDest = Vector3.zero;
        }
        internal EControlOperatorSet(EControlOperatorSet prev)
        {
            DriveDest = EDriveDest.None;
            DriveDir = EDriveFacing.Stop;
            lastDest = prev.lastDestination;
        }

        public void STOP(TankAIHelper helper)
        {
            DriveDest = EDriveDest.None;
            DriveDir = EDriveFacing.Stop;
            helper.DriveVar = 0;
        }
        public void Forwards(TankAIHelper helper)
        {
            DriveDest = EDriveDest.Override;
            DriveDir = EDriveFacing.Forwards;
            helper.ThrottleState = AIThrottleState.ForceSpeed;
            helper.DriveVar = 1;
        }
        public void Reverse(TankAIHelper helper)
        {
            DriveDest = EDriveDest.Override;
            DriveDir = EDriveFacing.Forwards;
            helper.ThrottleState = AIThrottleState.ForceSpeed;
            helper.DriveVar = -1;
        }

        public void ResetActions()
        {
            DriveDest = EDriveDest.None;
            DriveDir = EDriveFacing.Stop;
        }

        public void FaceDest()
        {
            DriveDest = EDriveDest.ToLastDestination;
            DriveDir = EDriveFacing.Neutral;
        }
        public void DriveToFacingTowards()
        {
            DriveDest = EDriveDest.ToLastDestination;
            DriveDir = EDriveFacing.Forwards;
        }
        public void DriveToFacingBackwards()
        {
            DriveDest = EDriveDest.ToLastDestination;
            DriveDir = EDriveFacing.Backwards;
        }

        public void DriveAwayFacingTowards()
        {
            DriveDest = EDriveDest.FromLastDestination;
            DriveDir = EDriveFacing.Forwards;
        }
        public void DriveAwayFacingAway()
        {
            DriveDest = EDriveDest.FromLastDestination;
            DriveDir = EDriveFacing.Backwards;
        }

        public void DriveToFacingPerp()
        {
            DriveDest = EDriveDest.ToLastDestination;
            DriveDir = EDriveFacing.Perpendicular;
        }
        public void DriveAwayFacingPerp()
        {
            DriveDest = EDriveDest.FromLastDestination;
            DriveDir = EDriveFacing.Perpendicular;
        }
    }
    public struct EControlCoreSet
    {
        public EDriveDest DriveDest
        {
            get => _DriveDest;
            set => _DriveDest = value;
        }
        public EDriveFacing DriveDir
        {
            get => _DriveDir;
            set => _DriveDir = value;
        }
        public Vector3 lastDestination
        {
            get => lastDest;
            set
            {
                if (value.IsNaN())
                    DebugTAC_AI.Exception("EControlCoreSet.SetLastDest - lastDestination was NaN!");
                lastDest = value;
            }
        }
        private Vector3 lastDest;
        private EDriveDest _DriveDest;
        private EDriveFacing _DriveDir;
        public EDrivePathing DrivePathing;
        public ESteeringStrength TurningStrictness { get; set; }

        public static EControlCoreSet Default => new EControlCoreSet(EDriveDest.None, EDriveFacing.Stop);

        public EControlCoreSet(EControlOperatorSet direct)
        {
            _DriveDest = direct.DriveDest;
            _DriveDir = direct.DriveDir;
            DrivePathing = EDrivePathing.OnlyImmedeate;
            lastDest = Vector3.zero;
            TurningStrictness = ESteeringStrength.Lazy;
            lastDestination = direct.lastDestination;
        }
        private EControlCoreSet(EDriveDest move, EDriveFacing facing)
        {
            _DriveDest = move;
            _DriveDir = facing;
            DrivePathing = EDrivePathing.OnlyImmedeate;
            lastDest = Vector3.zero;
            TurningStrictness = ESteeringStrength.Lazy;
        }
        public void Stop()
        {
            DriveDest = EDriveDest.None;
            DriveDir = EDriveFacing.Stop;
        }
        public void NoBrakes()
        {
            DriveDest = EDriveDest.None;
            DriveDir = EDriveFacing.Neutral;
        }

        public void DriveToFacingTowards()
        {
            DriveDest = EDriveDest.ToLastDestination;
            DriveDir = EDriveFacing.Forwards;
        }
        public void DriveToFacingBackwards()
        {
            DriveDest = EDriveDest.ToLastDestination;
            DriveDir = EDriveFacing.Backwards;
        }
        public void FlagBusyUnstucking()
        {
            DriveDest = EDriveDest.AvoidenceActive;
            DriveDir = EDriveFacing.Neutral;
        }

        public void DriveAwayFacingTowards()
        {
            DriveDest = EDriveDest.FromLastDestination;
            DriveDir = EDriveFacing.Forwards;
        }
        public void DriveAwayFacingAway()
        {
            DriveDest = EDriveDest.FromLastDestination;
            DriveDir = EDriveFacing.Backwards;
        }

        public void DriveToFacingPerp()
        {
            DriveDest = EDriveDest.ToLastDestination;
            DriveDir = EDriveFacing.Perpendicular;
        }
        public void DriveAwayFacingPerp()
        {
            DriveDest = EDriveDest.FromLastDestination;
            DriveDir = EDriveFacing.Perpendicular;
        }

        public override string ToString()
        {
            return "EControlCoreSet - " + DriveDest.ToString() + " | " + DriveDir.ToString() + " | " + lastDestination;
        }
    }

    public enum EAttackMode
    {
        AutoSet,
        Circle,
        Chase,
        Strong,
        Random,
        Ranged,
        Safety
    }

    public enum EDriveDest
    {
        None,

        FromLastDestination,

        ToLastDestination,

        AvoidenceActive,

        ToBase,

        ToMine,

        Override
    }

    public enum ESteeringStrength
    {
        Lazy,
        Strict,
        MaxSteering
    }
    public enum EDriveFacing
    {
        Stop,
        Neutral,
        Forwards,
        Perpendicular,
        Backwards
    }
    public enum EDrivePathing
    {
        IgnoreAll,
        OnlyImmedeate,
        Path,
        PrecisePathIgnoreScenery,
        PrecisePath,
    }
    public enum RequestSeverity
    {
        ThinkMcFly,
        Warn,
        SameTeam,
        AllHandsOnDeck,
    }
}
