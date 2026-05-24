namespace TAC_AI.AI.Enemy
{
    public static class RBolts
    {
        internal static void ManageBolts(TankAIHelper helper, Tank tank, EnemyMind mind)
        {
            switch (mind.CommanderBolts)
            {
                case EnemyBolts.MissionTrigger:
                    break;
                case EnemyBolts.AtFull:
                    if (RLoadedBases.TeamGlobalMobileTechCount(tank.Team) < KickStart.EnemyTeamTechLimit &&
                        !helper.PendingDamageCheck && AIGlobals.CanSplitTech())
                        mind.BlowBolts();
                    break;
                case EnemyBolts.AtFullOnAggro:
                    if (helper.lastEnemyGet.IsNotNull() && RLoadedBases.TeamGlobalMobileTechCount(tank.Team) < KickStart.EnemyTeamTechLimit &&
                        !helper.PendingDamageCheck && AIGlobals.CanSplitTech())
                        mind.BlowBolts();
                    break;
                case EnemyBolts.Default:
                default:
                    if (helper.lastEnemyGet.IsNotNull() && AIGlobals.CanSplitTech())
                        mind.BlowBolts();
                    break;
            }
            if (mind.BoltsQueued > 0)
                mind.BoltsQueued--;
        }
        public static void BlowBolts(this EnemyMind mind)
        {
            if (AIGlobals.AtSceneTechMaxSpawnLimit())
                return;
            if (mind.TechMemor)
            {
                mind.TechMemor.ReserveSuperGrabs = -256;
            }
            mind.BoltsQueued = 2;
            mind.AIControl.tank.control.ServerDetonateExplosiveBolt();
        }

    }
}
