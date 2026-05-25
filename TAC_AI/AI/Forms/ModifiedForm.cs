using System.Collections.Generic;
using TerraTechETCUtil;
using TAC_AI.AI.Behaviors;
using TAC_AI.AI.Engine;
using TAC_AI.AI.Engine.Registry;
using TAC_AI.AI.Engine.Profiles;
using TAC_AI.AI.Enemy;
using TAC_AI.AI.AlliedOperations;

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
                default:               return null;
            }
        }
    }
}
