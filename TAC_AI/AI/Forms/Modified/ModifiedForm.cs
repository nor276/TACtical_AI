using System.Collections.Generic;
using UnityEngine;
using TerraTechETCUtil;
using TAC_AI.AI.Behaviors;
using TAC_AI.AI.Engine;
using TAC_AI.AI.Engine.Registry;
using TAC_AI.AI.Engine.Profiles;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.AlliedOperations;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;
using TAC_AI.AI.Enemy.EnemyOperations;

namespace TAC_AI.AI.Forms
{
    /// <summary>
    /// The "Modified TAC AI" form - the full current behavior, and the default. Dispatches to the registered
    /// behavior modules by EvilCommander / DediAI x DriverType (+ per-tank profile override), reproducing the
    /// original *OperationsController.Execute() structure exactly. This logic was the entire ProfileRunner before
    /// the form system; it moved here unchanged (5-agent behavior-equivalence verified). Other forms live beside
    /// this one and are discovered the same way.
    /// </summary>
    public sealed class ModifiedForm : IAIForm
    {
        public string Id => "Modified";
        public string DisplayName => "Modified TAC AI";

        // ---- v2 global lifecycle (this form becoming / leaving active). Modified registers nothing extra here;
        // the shared AIModuleBootstrap handles module/tunable/profile registration globally. ----
        public void InitGlobal() { }
        // Drop cached IBehavior instances so a re-Init (form swap, AIModuleRegistry rebind) doesn't
        // dispatch into stale module IDs whose registrations were swapped out.
        public void DeInitGlobal() { cache.Clear(); }

        // ---- v2 per-tech lifecycle: own the per-tech brain state in the shell's FormState slot ----
        public void OnTechSpawn(TankAIHelper helper) { helper.FormState = new ModifiedTechState(); }
        public void OnTechRecycle(TankAIHelper helper) { helper.FormState = null; }

        // ---- v2 per-tech tick hooks: delegate to the detached tick bodies (ModifiedTick) + post-update brain ----
        public void Directors(TankAIHelper helper, bool host)
        {
            if (host) helper.HostDirectorsBody();
            else helper.ClientDirectorsBody();
        }
        public void Operations(TankAIHelper helper, bool host)
        {
            if (host) helper.HostOperationsBody();
            else helper.ClientOperationsBody();
        }
        public void PostUpdate(TankAIHelper helper)
        {
            helper.ManageAILockOn();
            helper.UpdateBlockHold();
        }
        public void ControlFrame(TankAIHelper helper, TankControl control)
        {
            helper.ControlFrameBody(control);
        }

        // ---- v2 isolation: this form owns the pathfinder (AIEPathMapper), so the shell reaches it only through here ----
        public void OnWorldReset() { AIEPathMapper.ResetAll(); }
        public void DrawPathingDebugGUI() { AIEPathMapper.GUIManaged.GUIGetTotalManaged(); }

        // v2 isolation: build the per-tech movement controller (the body moved out of TankAIHelper.SwapMovementController<T>).
        public IMovementAIController CreateMovementController(TankAIHelper helper, MovementContainerKind kind, EnemyMind mind)
        {
            switch (kind)
            {
                case MovementContainerKind.Static: return SwapTo<AIControllerStatic>(helper, mind);
                case MovementContainerKind.Air:    return SwapTo<AIControllerAir>(helper, mind);
                default:                           return SwapTo<AIControllerDefault>(helper, mind);
            }
        }
        private static T SwapTo<T>(TankAIHelper helper, EnemyMind mind) where T : Component, IMovementAIController
        {
            if (helper.MovementController is T existing) { existing.Initiate(helper.tank, helper, mind); return existing; }
            IMovementAIController previous = helper.MovementController;
            T built = helper.gameObject.AddComponent<T>();
            built.Initiate(helper.tank, helper, mind);
            if (previous != null) previous.Recycle();
            return built;
        }

        // v2 isolation: airplane maneuver state for status text (the shell switches on these ints without naming AirplaneAICore).
        public int GetAirDiveState(TankAIHelper helper) => (helper.MovementController?.AICore is AirplaneAICore plane) ? plane.PerformDiveAttack : 0;
        public int GetAirUTurnState(TankAIHelper helper) => (helper.MovementController?.AICore is AirplaneAICore plane) ? plane.PerformUTurn : 0;

        // v2 isolation: enemy/allied combat-policy + dispatch (verbatim from the old RCore.CombatChecking / WeapSetup call sites).
        public EAttackMode SelectEnemyAttackStrat(Tank tank, EnemyMind mind) => RWeapSetup.GetAttackStrat(tank, mind);
        public EAttackMode SelectAlliedAttackStrat(TankAIHelper helper) => EWeapSetup.GetAttackStrat(helper.tank, helper);
        public bool EnemyHasArtillery(BlockManager bm) => EWeapSetup.HasArtilleryWeapon(bm);
        public void RunEnemyMonitor(TankAIHelper helper, Tank tank, EnemyMind mind) => RGeneral.Monitor(helper, tank, mind);
        public void RunEnemyBolts(TankAIHelper helper, Tank tank, EnemyMind mind) => RBolts.ManageBolts(helper, tank, mind);
        public void RunEnemyRepairStep(TankAIHelper helper, Tank tank, EnemyMind mind, bool venPower) => RRepair.EnemyRepairStepper(helper, tank, mind, venPower);
        public void RunEnemyRTSCombat(TankAIHelper helper, Tank tank, EnemyMind mind) => RGeneral.RTSCombat(helper, tank, mind);
        public void RunEnemyCombatChecking(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            switch (mind.EvilCommander)
            {
                case EnemyHandling.Airplane:
                    RAircraft.EnemyDogfighting(helper, tank, mind);
                    break;
                case EnemyHandling.Stationary:
                    RGeneral.BaseAttack(helper, tank, mind);
                    break;
                default:
                    switch (mind.CommanderAttack)
                    {
                        case EAttackMode.Safety: RGeneral.SelfDefense(helper, tank, mind); break;
                        case EAttackMode.Ranged: RGeneral.AimAttack(helper, tank, mind); break;
                        case EAttackMode.Chase:  RGeneral.AidAttack(helper, tank, mind); break;
                        default:                 RGeneral.AidAttack(helper, tank, mind); break;
                    }
                    break;
            }
        }

        private static readonly Dictionary<string, IBehavior> cache = new Dictionary<string, IBehavior>();

        private static IBehavior Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (cache.TryGetValue(id, out var b)) return b;
            b = AIModuleRegistry.TryGet(id, out var e) ? e.Create() : null;
            if (b != null) cache[id] = b;
            return b;
        }

        private static bool NoTarget(TankAIHelper helper)
        {
            var e = helper.lastEnemyGet;
            return e == null || e.tank == null;
        }

        // ---- enemy: reproduces EnemyOperationsController.Execute ----
        public void RunEnemy(AIContext ctx, EnemyMind mind)
        {
            var helper = ctx.Helper;
            var tank = ctx.Tank;
            ctx.LoadOperator();

            if (NoTarget(helper))
            {
                Get("Enemy.Idle.NoTarget")?.Tick(ctx);
                if (NoTarget(helper))
                {
                    if (helper.Retreat) Get("Enemy.Retreat")?.Tick(ctx);
                    ctx.CommitOperator();
                    return;
                }
            }

            var move = Get(EnemyMovementId(mind.EvilCommander));
            if (move != null)
            {
                move.Tick(ctx);
            }
            else
            {
                ref EControlOperatorSet op = ref ctx.OperatorRef();
                BGeneral.ResetValues(helper, ref op);
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "EnemyOps:UnknownHandling:" + (int)mind.EvilCommander,
                    KickStart.ModID + ": ProfileRunner encountered unknown EnemyHandling '" + mind.EvilCommander
                        + "' on '" + (tank ? tank.name : "<null>") + "'. Dispatching to Wheeled this tick.", null);
                Get("Enemy.Attack.Wheeled")?.Tick(ctx);
            }

            if (helper.Retreat) Get("Enemy.Retreat")?.Tick(ctx);
            ctx.CommitOperator();
        }

        private static string EnemyMovementId(EnemyHandling h)
        {
            switch (h)
            {
                case EnemyHandling.Wheeled:        return "Enemy.Attack.Wheeled";
                case EnemyHandling.Airplane:       return "Enemy.Attack.Aircraft";
                case EnemyHandling.Chopper:        return "Enemy.Attack.Chopper";
                case EnemyHandling.Starship:       return "Enemy.Attack.Starship";
                case EnemyHandling.Naval:          return "Enemy.Attack.Naval";
                case EnemyHandling.SuicideMissile: return "Enemy.Attack.CrashMissile";
                case EnemyHandling.Stationary:     return "Enemy.Attack.Station";
                default:                           return null;
            }
        }

        // ---- allied: reproduces AlliedOperationsController.Execute ----
        public void RunAllied(AIContext ctx)
        {
            var helper = ctx.Helper;
            ctx.LoadOperator();

            var move = Get(SelectedMovementId(helper) ?? AlliedMovementId(helper));
            if (move != null)
            {
                move.Tick(ctx);
            }
            else
            {
                // Demotion path: no behavior ran this tick. Clear stale operator flags (DriveDest, FIRE_ALL,
                // etc.) so the freshly-defaulted Escort run next tick doesn't pick up last tick's drive state.
                ref EControlOperatorSet op = ref ctx.OperatorRef();
                BGeneral.ResetValues(helper, ref op);
                DebugTAC_AI.Log(KickStart.ModID + ": AIType is set to an invalid state - " + helper.DediAI);
                DebugTAC_AI.Log(KickStart.ModID + ": RESETTING TO DEFAULTS");
                helper.DediAI = AIType.Escort;
            }
            ctx.CommitOperator();
        }

        private static string SelectedMovementId(TankAIHelper helper)
        {
            var id = helper.SelectedAIProfileId;
            if (string.IsNullOrEmpty(id)) return null;
            return ProfileStore.TryGet(id, out var p) ? p.MovementId : null;
        }

        private static string AlliedMovementId(TankAIHelper helper)
        {
            if (helper.DriverType == AIDriverType.Stationary)
                return helper.DediAI == AIType.Assault ? "Allied.Base.Assault" : "Allied.Base.Hold";

            switch (helper.DediAI)
            {
                case AIType.Escort:    return "Allied.Escort";
                case AIType.Assault:   return "Allied.Assault";
                case AIType.Aegis:     return "Support.Protect";
                case AIType.Prospector:return "Economy.Mine";
                case AIType.Scrapper:  return "Economy.Scavenge";
                case AIType.Energizer: return "Allied.Energizer";
                case AIType.MTTurret:  return "Allied.MultiTech.Static";
                case AIType.MTStatic:  return "Allied.MultiTech.Static";
                case AIType.MTMimic:   return "Allied.MultiTech.Mimic";
                // Legacy AIType values that pair with a non-Tank DriverType (Pilot/Sailor/Astronaut).
                // KickStart.TransferLegacyIfNeeded maps them to Escort; we follow that mapping here so
                // save-loaded techs whose DediAI persisted as one of these don't silently demote to Escort
                // each tick (ReValidateAI keeps the legacy DediAI when the corresponding *Avail flag is set).
                case AIType.Aviator:   return "Allied.Escort";
                case AIType.Buccaneer: return "Allied.Escort";
                case AIType.Astrotech: return "Allied.Escort";
                default:               return null;
            }
        }
    }
}
