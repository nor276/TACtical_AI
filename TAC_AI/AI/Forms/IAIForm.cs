using TAC_AI.AI.Engine;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Forms
{
    /// <summary>
    /// A self-contained, auto-discovered AI "form" - it owns the ENTIRE per-tech AI brain for one AI style:
    /// targeting, every locomotion's movement and pathing, combat, retreat, turret/weapon aim, idle, economy. The
    /// rest of the mod is a thin SHELL wrapped around the active form. Drop a new class implementing IAIForm into ANY
    /// folder and AIFormRegistry discovers it on load (by its existence) and publishes it as an in-game option. One
    /// form is ACTIVE globally; switching deinits the old and inits the new. Forms are stateless singletons - one
    /// instance shared across all techs; per-tech state lives in the shell's TankAIHelper.FormState slot.
    ///
    /// TWO layers of entry, both implemented by the form:
    ///  - The TICK HOOKS (OnTechSpawn/OnTechRecycle/Directors/Operations/PostUpdate/ControlFrame): the shell's
    ///    TankAIHelper calls these from its matching tick phase, handing the per-tech helper. These own the
    ///    orchestration (who drives, alignment/RunState gating, the per-frame control bridge). host=true on the MP
    ///    host / single player; host=false on a network client (lighter tick).
    ///  - The DISPATCH (RunEnemy/RunAllied): the per-tech combat/role dispatch, invoked from inside the operations
    ///    orchestration via the engine's per-tech controllers (kept from v1; receives the AIContext bus).
    /// </summary>
    public interface IAIForm
    {
        /// <summary>Stable id used for the active-form setting + selection. Must be unique.</summary>
        string Id { get; }
        /// <summary>Human label shown in the in-game form selector.</summary>
        string DisplayName { get; }

        // ---- global (mod-wide) lifecycle: this form becoming / ceasing to be the active form ----
        void InitGlobal();
        void DeInitGlobal();

        // ---- per-tech lifecycle (shell Subscribe / Recycled) ----
        void OnTechSpawn(TankAIHelper helper);
        void OnTechRecycle(TankAIHelper helper);

        // ---- per-tech tick hooks (called from the matching TankAIHelper phase) ----
        void Directors(TankAIHelper helper, bool host);    // recalibrate controller + gate pathing for this tick
        void Operations(TankAIHelper helper, bool host);   // the decision/orchestration tick
        void PostUpdate(TankAIHelper helper);               // post-tick brain (lock-on, block hold)

        // ---- per-FRAME control bridge (from RunMovementBridge/UpdateTechControl): translate bus intent to actuation ----
        void ControlFrame(TankAIHelper helper, TankControl control);

        // ---- v2 isolation seams: shell-driven hooks so the shell never names a form's pathfinder ----
        void OnWorldReset();          // world unload/teardown: reset any form-owned pathfinding caches (no-op if none)
        void DrawPathingDebugGUI();   // debug spawner GUI: draw the form's pathfinding diagnostics (no-op if none)

        // v2 isolation: the active form builds its own per-tech movement controller, so the shell holds only
        // IMovementAIController and never names a concrete controller type. Vanilla returns a no-op controller.
        IMovementAIController CreateMovementController(TankAIHelper helper, MovementContainerKind kind, Enemy.EnemyMind mind);

        // v2 isolation: airplane maneuver state for the status-text display (0 if not an airplane / no maneuver) -
        // lets the shell describe "diving / U-turning" without naming the form's AirplaneAICore.
        int GetAirDiveState(TankAIHelper helper);
        int GetAirUTurnState(TankAIHelper helper);

        // v2 isolation: enemy/allied combat-policy + per-tick combat dispatch. The shell orchestrates (housekeeping,
        // stance switch, retreat) and calls these so it names no form-combat type. Vanilla returns defaults / no-ops.
        EAttackMode SelectEnemyAttackStrat(Tank tank, EnemyMind mind);
        EAttackMode SelectAlliedAttackStrat(TankAIHelper helper);
        bool EnemyHasArtillery(BlockManager bm);
        void RunEnemyCombatChecking(TankAIHelper helper, Tank tank, EnemyMind mind);
        void RunEnemyMonitor(TankAIHelper helper, Tank tank, EnemyMind mind);
        void RunEnemyBolts(TankAIHelper helper, Tank tank, EnemyMind mind);
        void RunEnemyRepairStep(TankAIHelper helper, Tank tank, EnemyMind mind, bool venPower);
        void RunEnemyRTSCombat(TankAIHelper helper, Tank tank, EnemyMind mind);

        // ---- per-tech combat/role DISPATCH (invoked from the operations orchestration via the engine controllers) ----
        void RunEnemy(AIContext ctx, EnemyMind mind);
        void RunAllied(AIContext ctx);
    }
}
