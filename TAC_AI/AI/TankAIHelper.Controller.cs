using System;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    // ControllerManager service (partial-class split of TankAIHelper, Step 2). Builds/swaps the per-tech
    // IMovementAIController (Default/Air/Static) to match DriverType/EvilCommander, reusing or recycling the
    // old one. RequestMovementControllerSwap (the request) + MovementSwapReason + MCD stay in core near the
    // identity block; the swap is CONSUMED here by RecalibrateMovementAIController, which the tick entry
    // (OnUpdateHost/ClientAIDirectors, staggered Directors phase) calls when MovementAIControllerDirty.
    public partial class TankAIHelper
    {
        // REVISED: single generic swap helper replacing the duplicated null-out/Recycle/GetOrAddComponent blocks in every Recal* path; reuses the existing controller if it is already the right type, else recycles the old one after building the new.
        private T SwapMovementController<T>(EnemyMind mind) where T : Component, IMovementAIController
        {
            if (MovementController is T existing)
            {
                existing.Initiate(tank, this, mind);
                return existing;
            }
            IMovementAIController previous = MovementController;
            T built = gameObject.AddComponent<T>();
            built.Initiate(tank, this, mind);
            MovementController = built;
            if (previous != null)
                previous.Recycle();
            return built;
        }

        private void SetupDefaultMovementAIController()
        {
            UsingAirControls = false;
            SwapMovementController<AIControllerDefault>(null);
            LogMovementControllerSwapIfChanged();
        }

        // REVISED: controller selection now keyed on MovementDispatch.ContainerForEnemy(EvilCommander) (Static/Air/Default) instead of explicit EnemyHandling checks (Stationary, Chopper/Airplane).
        // REVISED: a flying enemy whose air controller reports Grounded (no longer flightworthy) is demoted via BlockSetEnemyHandling/Wheeled rather than being kept airborne; null EnemyMind now self-heals to Default instead of throwing.
        private bool RecalMoveAIControllerNPT(EnemyMind enemy)
        {
            if (enemy.IsNotNull())
            {
                if ((MovementDispatch.ContainerForEnemy(enemy.EvilCommander) == MovementContainerKind.Static || enemy.StartedAnchored) && AnchorState != AIAnchorState.Unanchor)
                {
                    DriverType = AIDriverType.Stationary;
                    SwapMovementController<AIControllerStatic>(enemy);
                    return false;
                }
                if (MovementDispatch.ContainerForEnemy(enemy.EvilCommander) == MovementContainerKind.Air)
                {
                    if (MovementController is AIControllerAir existingAir && existingAir.Grounded)
                    {
                        var oldClass = enemy.EvilCommander;
                        Enemy.RCore.BlockSetEnemyHandling(tank, enemy, false);
                        if (MovementDispatch.ContainerForEnemy(enemy.EvilCommander) == MovementContainerKind.Air)
                            enemy.EvilCommander = EnemyHandling.Wheeled;
                        DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name + " demoting from "
                            + oldClass + " → " + enemy.EvilCommander + " (Grounded — no longer flightworthy)");
                    }
                    else
                    {
                        SwapMovementController<AIControllerAir>(enemy);
                        UsingAirControls = true;
                        return false;
                    }
                }
                return true;
            }
            else
            {
                DebugTAC_AI.LogError(KickStart.ModID + ": RecalMoveAIControllerNPT for " + tank.name
                    + " reached with null EnemyMind despite caller guard — falling back to default.");
                return true;
            }
        }
        // REVISED: keyed on MovementDispatch.ContainerForPlayer(DriverType) instead of explicit Stationary/Pilot checks; a grounded (non-flightworthy) Pilot tech is demoted to Tank rather than kept on air controls.
        private bool RecalMoveAIControllerPlayer()
        {
            if (MovementDispatch.ContainerForPlayer(DriverType) == MovementContainerKind.Static && AnchorState != AIAnchorState.Unanchor)
            {
                SwapMovementController<AIControllerStatic>(null);
                return false;
            }
            else if (MovementDispatch.ContainerForPlayer(DriverType) == MovementContainerKind.Air)
            {
                if (MovementController is AIControllerAir existingAir && existingAir.Grounded)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": " + tank.name + " (player) demoting from Pilot (Grounded — no longer flightworthy)");
                    DriverType = AIDriverType.Tank;
                }
                else
                {
                    SwapMovementController<AIControllerAir>(null);
                    UsingAirControls = true;
                    return false;
                }
            }
            return true;
        }
        private string lastLoggedMovementControllerType = null;
        private void LogMovementControllerSwapIfChanged()
        {
            string current = MovementController?.GetType().Name ?? "<null>";
            if (current != lastLoggedMovementControllerType)
            {
                DebugTAC_AI.LogTagged("Movement", "Tech " + DebugTAC_AI.VisibleName(tank) + " core swap "
                    + (lastLoggedMovementControllerType ?? "<init>") + " → " + current
                    + " (DriverType=" + DriverType + ", AIAlign=" + AIAlign + ", requestedBy=" + lastSwapRequest + ")");
                AppendHistory("CoreSwap " + (lastLoggedMovementControllerType ?? "<init>") + "→" + current);
                lastLoggedMovementControllerType = current;
            }
        }
        private void RecalibrateMovementAIController()
        {
            try
            {
                UsingAirControls = false;
                var enemy = gameObject.GetComponent<EnemyMind>();
                // REVISED: a NonPlayer tech missing its EnemyMind now self-heals (client demotes to Static+dirty; host regenerates the enemy AI) instead of the controller swap throwing on the missing mind.
                if (AIAlign == AIAlignment.NonPlayer && enemy.IsNull())
                {
                    DebugTAC_AI.LogWarnPlayerOnce(KickStart.ModID +
                        ": Recalibrate found NonPlayer alignment with no EnemyMind on " +
                        tank.name + " - invariant violation, self-healing.", null);
                    if (ManNetwork.IsNetworked && !ManNetwork.IsHost)
                    {
                        AIAlign = AIAlignment.Static;
                        dirtyAI = AIDirtyState.Dirty;
                    }
                    else
                    {
                        Enemy.RCore.GenerateEnemyAI(this, tank);
                        enemy = gameObject.GetComponent<EnemyMind>();
                    }
                }
                if (AIAlign == AIAlignment.NonPlayer)
                {
                    if (!RecalMoveAIControllerNPT(enemy))
                        return;
                    enemy = gameObject.GetComponent<EnemyMind>();
                }
                else
                {
                    if (!RecalMoveAIControllerPlayer())
                        return;
                }
                SwapMovementController<AIControllerDefault>(enemy);
                return;
            }
            finally
            {
                MovementAIControllerDirty = false;
                LogMovementControllerSwapIfChanged();
            }
        }
    }
}
