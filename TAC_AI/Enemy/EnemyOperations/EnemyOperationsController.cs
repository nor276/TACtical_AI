namespace TAC_AI.AI.Enemy.EnemyOperations
{
    internal class EnemyOperationsController
    {
        private EnemyMind Mind;

        public EnemyOperationsController(EnemyMind Mind)
        {
            this.Mind = Mind;
        }

        public void Execute()
        {
            TankAIHelper helper = Mind.AIControl;
            Tank tank = helper.tank;

            EControlOperatorSet direct = helper.GetDirectedControl();

            // B7+T2+D1: centralized no-target dispatch. Each R* handler used to own its own
            // null-target check; the result was 6 inconsistent implementations (RStation
            // skipped LollyGag entirely per D1; RAircraft uses LollyGagAir; RWheeled added a
            // re-acquire + Provoked-hold that no other handler had). DispatchNoTargetIdle
            // centralizes that contract and revives RStation's commented LollyGag at the
            // same time. If the re-acquire succeeds, fall through to the R* switch.
            //
            // P13 BUG-2: the guard tests lastEnemyGet.tank, not just lastEnemyGet. lastEnemyGet
            // is a Visible, and Visible does NOT implement IAIFollowable, so Visible.IsNotNull()
            // resolves to the base-game UnityEngine.Object overload — it validates the Visible
            // only, never its .tank. Every R* handler immediately dereferences lastEnemyGet.tank
            // (boundsCentreWorldNoCheck), so a live Visible whose Tank was torn down would throw.
            // Treating a null .tank as "no target" here routes it through the re-acquire/idle
            // path, so .tank is guaranteed non-null for every handler dispatched below.
            if (helper.lastEnemyGet == null || helper.lastEnemyGet.tank == null)
            {
                RGeneral.DispatchNoTargetIdle(helper, tank, Mind, ref direct);
                if (helper.lastEnemyGet == null || helper.lastEnemyGet.tank == null)
                {
                    if (helper.Retreat)
                        RCore.GetRetreatLocation(helper, tank, Mind, ref direct);
                    helper.SetDirectedControl(direct);
                    return;
                }
            }

            switch (this.Mind.EvilCommander)
            {
                case EnemyHandling.Wheeled:
                    RWheeled.AttackVroom(helper, tank, Mind, ref direct);
                    break;
                case EnemyHandling.Airplane:
                    RAircraft.AttackWoosh(helper, tank, Mind, ref direct);
                    break;
                case EnemyHandling.Chopper:
                    RChopper.AttackShwa(helper, tank, Mind, ref direct);
                    break;
                case EnemyHandling.Starship:
                    RStarship.AttackZoom(helper, tank, Mind, ref direct);
                    break;
                case EnemyHandling.Naval:
                    RNaval.AttackWhish(helper, tank, Mind, ref direct);
                    break;
                case EnemyHandling.SuicideMissile:
                    // IDK, May make this obsolete and just use plane AI for this instead.
                    RCrashMissile.AttackCrash(helper, tank, Mind, ref direct);
                    break;
                case EnemyHandling.Stationary:
                    RStation.AttackWham(helper, tank, Mind, ref direct);
                    break;
                default:
                    // B8: unknown EnemyHandling -> sanitize stale direct via ResetValues,
                    // surface loudly (per-key dedup INCLUDING the original raw int so different
                    // bad values surface distinctly), and dispatch to RWheeled as a safe fallback
                    // WITHOUT mutating Mind.EvilCommander. Mind state preservation:
                    //  - audit trail survives so debuggers / save-round-trip see the true value
                    //  - avoids the hidden EnemyMind.EvilCommander setter side-effect (SetDriverType)
                    //  - if the corrupt value persists in save data, the warning re-fires on reload
                    //    (this is desired — the warning surfaces persistent corruption)
                    {
                        var origHandling = Mind.EvilCommander;
                        int origRaw = (int)origHandling;
                        BGeneral.ResetValues(helper, ref direct);
                        DebugTAC_AI.LogWarnPlayerOncePerKey(
                            "EnemyOps:UnknownHandling:" + origRaw,
                            KickStart.ModID + ": EnemyOperationsController.Execute encountered unknown EnemyHandling '"
                                + origHandling + "' (raw=" + origRaw + ") on tank '"
                                + (tank ? tank.name : "<null>")
                                + "'. Dispatching to RWheeled for this tick; Mind.EvilCommander left untouched (audit preserved).", null);
                        RWheeled.AttackVroom(helper, tank, Mind, ref direct);
                    }
                    break;
            }
            if (helper.Retreat)
            {
                RCore.GetRetreatLocation(helper, tank, Mind, ref direct);
            }
            helper.SetDirectedControl(direct);
        }
    }
}
