using System;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement;
using TerraTechETCUtil;

namespace TAC_AI.AI
{
    /// <summary>
    /// Per-frame tick brain - DETACHED from TankAIHelper into the Modified form (v2) as extension methods.
    /// Covers the five top-level tick entrypoints: HostDirectorsBody (sets UpdateDirectorsAndPathing for the
    /// host path), ClientDirectorsBody (same for client path), HostOperationsBody (full Operations dispatch
    /// including unjam, allied, enemy, and static paths), ClientOperationsBody (client-side static-only
    /// operations dispatch), and ControlFrameBody (per-frame movement bridge: beam, weapons, drive director,
    /// drive maintainer). State accessed through the TankAIHelper bus: MovementAIControllerDirty, RunState,
    /// TickAIAlign, UpdateDirectorsAndPathing, DriveVar, AIControlOverride, DediAI, IsMultiTech,
    /// IsTryingToUnjam, lastOperatorRange, ControlOperator, MovementController, ControlCore, CurHeight,
    /// consecutiveNullMovementControllerTicks, IsControlOperatorStale, ControlOperatorAgeFrames,
    /// NotInBeam, UpdateDirectorsAndPathing, Avoiding, RTSControlled, tank.
    /// </summary>
    public static class ModifiedTick
    {
        // REVISED: renamed from OnUpdateHostAIDirectors; detached into the Modified form as an extension method.
        public static void HostDirectorsBody(this TankAIHelper helper)
        {
            try
            {
                if (helper.MovementAIControllerDirty)
                    helper.RecalibrateMovementAIController();
                if (helper.RunState == AIRunState.Advanced)
                {
                    switch (helper.TickAIAlign)
                    {
                        case AIAlignment.Player:
                            helper.UpdateDirectorsAndPathing = true;
                            break;
                        case AIAlignment.NonPlayer:
                            if (KickStart.enablePainMode)
                                helper.UpdateDirectorsAndPathing = true;
                            break;
                        default:
                            helper.DriveVar = 0;
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnUpdateHostAIDirectors:" + (helper.tank ? helper.tank.name : "<recycled>"),
                    "OnUpdateHostAIDirectors() Critical error on " + (helper.tank ? helper.tank.name : "<recycled>"), e);
            }
        }

        // REVISED: renamed from OnUpdateHostAIOperations; detached into the Modified form as an extension method.
        // REVISED: unjam paths now MarkOperatorDirty after TryHandleObstruction so the freshly-written ControlOperator is not flagged stale; per-branch EstTopSped tracking removed (moved to OnPreUpdate).
        public static void HostOperationsBody(this TankAIHelper helper)
        {
            try
            {
                helper.UpdatePhysicsInfo();
                bool OverrideControl = helper.AIControlOverride != null;
                if (OverrideControl)
                {
                    helper.CheckEnemyAndAiming();
                    if (!helper.AIControlOverride(helper, TankAIHelper.ExtControlStatus.Operators))
                        return;
                }
                switch (helper.DediAI)
                {
                    case AIType.MTTurret:
                    case AIType.MTStatic:
                    case AIType.MTMimic:
                        helper.IsMultiTech = true;
                        break;
                    default:
                        helper.IsMultiTech = false;
                        break;
                }
                switch (helper.TickAIAlign)
                {
                    // REVISED: unjam paths (here and the NonPlayer cases below) now MarkOperatorDirty after TryHandleObstruction so the freshly-written ControlOperator is not flagged stale; per-branch EstTopSped tracking removed (moved to OnPreUpdate).
                    case AIAlignment.Player:
                        if (!OverrideControl)
                            helper.CheckEnemyAndAiming();
                        if (helper.IsTryingToUnjam)
                        {
                            helper.TryHandleObstruction(true, helper.lastOperatorRange, false, true, ref helper.ControlOperator);
                            helper.MarkOperatorDirty();
                        }
                        else
                            helper.RunAlliedOperations();
                        break;
                    case AIAlignment.NonPlayer:
                        if (KickStart.enablePainMode)
                        {
                            switch (helper.RunState)
                            {
                                case AIRunState.Off:
                                    break;
                                case AIRunState.Default:
                                    if (!OverrideControl)
                                        helper.CheckEnemyAndAiming();
                                    if (helper.IsTryingToUnjam)
                                    {
                                        helper.TryHandleObstruction(true, helper.lastOperatorRange, false, true, ref helper.ControlOperator);
                                        helper.MarkOperatorDirty();
                                        var mind = helper.GetComponent<EnemyMind>();
                                        if (mind)
                                            RCore.ScarePlayer(mind, helper, helper.tank);
                                    }
                                    else
                                        helper.RunEnemyOperations(true);
                                    break;
                                case AIRunState.Advanced:
                                    if (!OverrideControl)
                                        helper.CheckEnemyAndAiming();
                                    if (helper.IsTryingToUnjam)
                                    {
                                        helper.TryHandleObstruction(true, helper.lastOperatorRange, false, true, ref helper.ControlOperator);
                                        helper.MarkOperatorDirty();
                                        var mind = helper.GetComponent<EnemyMind>();
                                        if (mind)
                                            RCore.ScarePlayer(mind, helper, helper.tank);
                                    }
                                    else
                                        helper.RunEnemyOperations();
                                    break;
                            }
                        }
                        break;
                    default:
                        helper.DriveVar = 0;
                        helper.RunStaticOperations();
                        break;
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnUpdateHostAIOperations:" + (helper.tank ? helper.tank.name : "<recycled>"),
                    "OnUpdateHostAIOperations() Critical error on " + (helper.tank ? helper.tank.name : "<recycled>"), e);
            }
        }

        // REVISED: renamed from OnUpdateClientAIDirectors; detached into the Modified form as an extension method.
        public static void ClientDirectorsBody(this TankAIHelper helper)
        {
            if (helper.MovementAIControllerDirty)
                helper.RecalibrateMovementAIController();
            switch (helper.TickAIAlign)
            {
                case AIAlignment.Static:
                    helper.DriveVar = 0;
                    break;
                case AIAlignment.Player:
                    helper.UpdateDirectorsAndPathing = true;
                    break;
                case AIAlignment.NonPlayer:
                    helper.UpdateDirectorsAndPathing = true;
                    break;
            }
        }

        // REVISED: renamed from OnUpdateClientAIOperations; detached into the Modified form as an extension method.
        // REVISED: client operations now actually runs RunStaticOperations for Static techs (was a no-op that only cached EstTopSped); wrapped in try/catch with throttled logging. Player/NonPlayer no longer do per-tick work here.
        public static void ClientOperationsBody(this TankAIHelper helper)
        {
            try
            {
                if (helper.TickAIAlign == AIAlignment.Static)
                {
                    helper.DriveVar = 0;
                    helper.RunStaticOperations();
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "OnUpdateClientAIOperations:" + (helper.tank ? helper.tank.name : "<recycled>"),
                    "OnUpdateClientAIOperations() Critical error on " + (helper.tank ? helper.tank.name : "<recycled>"), e);
            }
        }

        // REVISED: renamed from UpdateTechControl; detached into the Modified form as an extension method.
        // REVISED: a null MovementController now triggers a recalibrate-and-retry (and a throttled file-only warning, escalating at 30 ticks) instead of just logging; the tech skips driving this tick rather than NRE-ing.
        public static void ControlFrameBody(this TankAIHelper helper, TankControl thisControl)
        {
            if (helper.AIControlOverride != null && !helper.AIControlOverride(helper, TankAIHelper.ExtControlStatus.MaintainersAndDirectors))
                return;
            helper.CurHeight = -500;

            // REVISED: a null MovementController now triggers a recalibrate-and-retry (and a throttled file-only warning, escalating at 30 ticks) instead of just logging; the tech skips driving this tick rather than NRE-ing.
            if (helper.MovementController is null)
            {
                helper.RecalibrateMovementAIController();
                if (helper.MovementController is null)
                {
                    helper.consecutiveNullMovementControllerTicks++;                    string tankKey = helper.tank ? helper.tank.name : "<recycled>";
                    if (helper.consecutiveNullMovementControllerTicks == 1)                        DebugTAC_AI.LogWarnFileOnly(
                            "NullMovementController:" + tankKey,
                            "AI " + tankKey + ": MovementController null after recalibrate - tank will not drive this tick.", null);
                    else if (helper.consecutiveNullMovementControllerTicks == 30)                        DebugTAC_AI.LogWarnPlayerOncePerKey(
                            "NullMovementController:persistent:" + tankKey,
                            "AI " + tankKey + ": MovementController STILL null after 30 ticks - persistent invariant violation, manual intervention required.", null);
                    return;
                }
            }
            if (helper.consecutiveNullMovementControllerTicks != 0)                helper.consecutiveNullMovementControllerTicks = 0;
            if (helper.IsControlOperatorStale)                DebugTAC_AI.LogWarnFileOnly(
                    "ControlOperatorStale:" + (helper.tank ? helper.tank.name : "<recycled>"),
                    "AI " + (helper.tank ? helper.tank.name : "<recycled>") + ": ControlOperator stale by " +
                    helper.ControlOperatorAgeFrames + " frames (>" + (KickStart.AIClockPeriod * 3) + ").", null);
            AIEBeam.BeamMaintainer(thisControl, helper, helper.tank);
            if (helper.UpdateDirectorsAndPathing)
            {
                try
                {
                    AIEWeapons.WeaponDirector(thisControl, helper, helper.tank);
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "UTC-WeaponDirector-" + helper.tank.name,
                        "AI " + helper.tank.name + ": WeaponDirector error", e);
                }

                try
                {
                    if (!helper.IsTryingToUnjam)
                    {
                        helper.Avoiding = false;                        EControlCoreSet coreCont = new EControlCoreSet(helper.ControlOperator);                        if (helper.RTSControlled)
                            helper.MovementController.DriveDirectorRTS(ref coreCont);
                        else
                            helper.MovementController.DriveDirector(ref coreCont);
                        helper.SetCoreControl(coreCont);
                    }
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "UTC-DriveDirector-" + helper.tank.name,
                        "AI " + helper.tank.name + ": DriveDirector error", e);
                }

                helper.UpdateDirectorsAndPathing = false;
            }
            if (helper.NotInBeam)
            {
                try
                {
                    AIEWeapons.WeaponMaintainer(helper, helper.tank);
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "UTC-WeaponMaintainer-" + helper.tank.name,
                        "AI " + helper.tank.name + ":  WeaponMaintainer error", e);
                }
                try
                {
                    helper.MovementController.DriveMaintainer(ref helper.ControlCore);                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogWarnPlayerOncePerKey(
                        "UTC-DriveMaintainer-" + helper.tank.name,
                        "AI " + helper.tank.name + ": DriveMaintainer error", e);
                }
            }
        }
    }
}
