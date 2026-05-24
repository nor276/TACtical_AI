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
            // P08 B-NEW10-4: bypass-of-NaN-guard fix. Constructor was assigning `lastDest` field
            // directly; route through the `lastDestination` property so the NaN check fires.
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

    /// <summary>
    /// Combat-target-selection mode. Movement layer is uniform per locomotion (RWheeled/RChopper/...)
    /// — the per-mode differentiation lives in <see cref="TAC_AI.AI.TankAIHelper.FindEnemy"/>
    /// (Random rotates targets, Strong picks largest, Chase sticks, Closest/default picks nearest).
    /// FSM `default:` arms covering Chase/Strong/Random is intentional (T2).
    /// </summary>
    public enum EAttackMode
    {
        /// <summary>Sentinel: resolved by <see cref="TAC_AI.AI.EWeapSetup.GetAttackStrat"/> on next
        /// `TankAIHelper.OnRecycle`/equivalent — auto-picks from weapon loadout. Never reaches the
        /// combat FSM as `AutoSet`. The GUI slider (GUIAIManager.cs:1279) excludes this value.</summary>
        AutoSet,
        Circle,
        Chase,
        Strong,
        Random,
        Ranged,
        Safety
    }

    /// <summary>
    /// Drive-destination semantics for the Maintainer phase.
    /// **Ordering is LOAD-BEARING**: LandAICore.DriveMaintainer (and Sea/Space/Vehicle equivalents)
    /// use `>= ToLastDestination` comparisons to mean "has a targeted destination or stronger"
    /// (accepts AvoidenceActive / ToBase / ToMine / Override). Inserting a new value mid-list, or
    /// renumbering, will silently change drive behaviour. Add new values at the end.
    /// </summary>
    public enum EDriveDest
    {   //Control the AI drive direction
        None, // No target

        // Coordinate-Based Targets
        FromLastDestination,

        ToLastDestination,

        AvoidenceActive,

        ToBase,

        ToMine,

        Override
    }

    public enum ESteeringStrength
    {   //Control the AI drive steering
        Lazy,
        Strict,
        MaxSteering
    }
    public enum EDriveFacing
    {   //Control the AI drive direction
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
