using TAC_AI.AI.Enemy;

namespace TAC_AI.AI
{
    /// <summary>
    /// Retreat-posture brain - DETACHED from TankAIHelper into the Modified form (v2) as extension methods (callers keep
    /// the same helper.X() syntax). DetermineRetreatPosture (allied) evaluates whether to disengage (returns bool, does
    /// NOT set Retreat itself - caller does `Retreat = DetermineRetreatPosture()`).
    /// DetermineRetreatPostureEnemy (enemy) writes Retreat directly.  Both honour the optional AnimeAI compat hook
    /// (#if !STEAM) and the global AIECore.RetreatingTeams set.
    /// </summary>
    public static class ModifiedRetreat
    {
        // REVISED: renamed from DetermineCombat and now RETURNS the retreat verdict (callers do
        // `Retreat = DetermineRetreatPosture()`) instead of writing Retreat as a side effect; it no
        // longer acquires targets, only drops a now-friendly held target and decides retreat.
        public static bool DetermineRetreatPosture(this TankAIHelper helper)
        {
            if (helper.lastEnemyGet?.tank)
                if (!Tank.IsEnemy(helper.tank.Team, helper.lastEnemyGet.tank.Team))
                {
                    helper.ReleaseTarget();
                }
            if (AIECore.RetreatingTeams.Contains(helper.tank.Team))
                return true;

#if !STEAM
                if (KickStart.isAnimeAIPresent)
                {
                    if (AnimeAICompat.PollShouldRetreat(helper.tank, helper, out bool verdict))
                        return verdict;
                }
#endif

            bool DoNotEngage = false;
            if (helper.DediAI == AIType.Assault && helper.lastBasePos.IsNotNull())
            {
                if (helper.MaxCombatRange * 2 < (helper.lastBasePos.position - helper.tank.boundsCentreWorldNoCheck).magnitude)
                {
                    DoNotEngage = true;
                }
                else if (helper.AdvancedAI)
                {
                    if (helper.DamageThreshold > 30)
                    {
                        DoNotEngage = true;
                    }
                }
            }
            else if (helper.lastPlayer.IsNotNull())
            {
                if (helper.DriverType == AIDriverType.Pilot)
                {
                    if (!helper.RTSControlled && helper.MaxCombatRange * 4 < (helper.lastPlayer.tank.boundsCentreWorldNoCheck - helper.tank.boundsCentreWorldNoCheck).magnitude)
                    {
                        DoNotEngage = true;
                    }
                    else if (helper.AdvancedAI)
                    {
                        if (helper.DamageThreshold > 20)
                        {
                            DoNotEngage = true;
                        }
                    }
                }
                else if (helper.DediAI != AIType.Assault)
                {
                    if (!helper.RTSControlled && helper.MaxCombatRange < (helper.lastPlayer.tank.boundsCentreWorldNoCheck - helper.tank.boundsCentreWorldNoCheck).magnitude)
                    {
                        DoNotEngage = true;
                    }
                    else if (helper.AdvancedAI)
                    {
                        if (helper.DamageThreshold > 30)
                        {
                            DoNotEngage = true;
                        }
                    }
                }
            }
            return DoNotEngage;
        }

        public static void DetermineRetreatPostureEnemy(this TankAIHelper helper)
        {
            helper.Retreat = AIGlobals.IsNotAttract && AIECore.RetreatingTeams.Contains(helper.tank.Team);

#if !STEAM
                if (KickStart.isAnimeAIPresent)
                {
                    if (AnimeAICompat.PollShouldRetreat(helper.tank, helper, out bool verdict))
                    {
                        helper.Retreat = verdict;
                        return;
                    }
                }
#endif
        }
    }
}
