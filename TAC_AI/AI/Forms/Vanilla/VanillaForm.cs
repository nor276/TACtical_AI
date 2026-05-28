using TAC_AI.AI.Engine;
using TAC_AI.AI.Enemy;

namespace TAC_AI.AI.Forms.Vanilla
{
    /// <summary>
    /// "Vanilla (stock game AI)" - a TRUE handback to TerraTech's own AI (v2). When this form is active, each tech is
    /// flipped to AIRunState.Default + ForceVanillaAI, which makes the mod's Harmony prefixes (TechAI.ControlTech /
    /// ModuleTechController.ExecuteControl) pass control to the STOCK AI instead of driving it. The form's own tick
    /// hooks are no-ops (the mod does not drive). The ForceVanillaAI guard in the alignment FSM (CheckRebuildAlignment/
    /// RefreshAI) keeps the handback from being auto-promoted back to Advanced. Switching away restores Advanced.
    /// </summary>
    public sealed class VanillaForm : IAIForm
    {
        public string Id => "Vanilla";
        public string DisplayName => "Vanilla (stock game AI)";

        // ---- global lifecycle: flip every live tech to / from the stock-AI handback ----
        public void InitGlobal()
        {
            foreach (var h in AIECore.IterateAllHelpers())
                SetHandback(h, true);
        }
        public void DeInitGlobal()
        {
            foreach (var h in AIECore.IterateAllHelpers())
                SetHandback(h, false);
        }

        // ---- per-tech lifecycle ----
        public void OnTechSpawn(TankAIHelper helper) => SetHandback(helper, true);
        public void OnTechRecycle(TankAIHelper helper) { }

        // ---- tick hooks: the mod does not drive while handed back; the stock AI does ----
        public void Directors(TankAIHelper helper, bool host) { }
        public void Operations(TankAIHelper helper, bool host) { }
        public void PostUpdate(TankAIHelper helper) { }
        public void ControlFrame(TankAIHelper helper, TankControl control) { }

        // ---- v2 isolation seams: Vanilla has no pathfinder, so these are no-ops ----
        public void OnWorldReset() { }
        public void DrawPathingDebugGUI() { }

        // v2 isolation: Vanilla hands back to the stock AI, so it builds a no-op controller; the shell still needs a
        // non-null IMovementAIController for its per-tech lifecycle (e.g. EnemyMind.UpdateEnemyMind).
        public IMovementAIController CreateMovementController(TankAIHelper helper, MovementContainerKind kind, EnemyMind mind)
        {
            if (helper.MovementController is VanillaMovementController existing) { existing.Initiate(helper.tank, helper, mind); return existing; }
            IMovementAIController previous = helper.MovementController;
            var built = helper.gameObject.AddComponent<VanillaMovementController>();
            built.Initiate(helper.tank, helper, mind);
            if (previous != null) previous.Recycle();
            return built;
        }

        // v2 isolation: Vanilla has no airplane core, so no maneuver status.
        public int GetAirDiveState(TankAIHelper helper) => 0;
        public int GetAirUTurnState(TankAIHelper helper) => 0;

        // v2 isolation: Vanilla hands back to the stock AI, so combat policy/dispatch are inert defaults/no-ops.
        public EAttackMode SelectEnemyAttackStrat(Tank tank, EnemyMind mind) => EAttackMode.Chase;
        public EAttackMode SelectAlliedAttackStrat(TankAIHelper helper) => EAttackMode.Chase;
        public bool EnemyHasArtillery(BlockManager bm) => false;
        public void RunEnemyCombatChecking(TankAIHelper helper, Tank tank, EnemyMind mind) { }
        public void RunEnemyMonitor(TankAIHelper helper, Tank tank, EnemyMind mind) { }
        public void RunEnemyBolts(TankAIHelper helper, Tank tank, EnemyMind mind) { }
        public void RunEnemyRepairStep(TankAIHelper helper, Tank tank, EnemyMind mind, bool venPower) { }
        public void RunEnemyRTSCombat(TankAIHelper helper, Tank tank, EnemyMind mind) { }

        // ---- dispatch (not exercised while handed back; harmless stand-down if ever reached) ----
        public void RunEnemy(AIContext ctx, EnemyMind mind) { }
        public void RunAllied(AIContext ctx) { }

        // Sets/clears the stock-AI handback for one tech. RunState.Default routes the Harmony prefixes to the stock
        // AI; ForceVanillaAI keeps the alignment FSM from auto-promoting it back. ForceRebuildAlignment re-applies.
        private static void SetHandback(TankAIHelper helper, bool on)
        {
            if (helper == null)
                return;
            helper.ForceVanillaAI = on;
            helper.RunState = on ? AIRunState.Default : AIRunState.Advanced;
            helper.ForceRebuildAlignment();
        }
    }
}
