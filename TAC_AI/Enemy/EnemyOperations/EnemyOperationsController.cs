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
                    RCrashMissile.AttackCrash(helper, tank, Mind, ref direct);
                    break;
                case EnemyHandling.Stationary:
                    RStation.AttackWham(helper, tank, Mind, ref direct);
                    break;
                default:
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
