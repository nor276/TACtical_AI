using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Movement;

namespace TAC_AI.AI
{
    /// <summary>
    /// Target-scan brain - DETACHED from TankAIHelper into the Modified form (v2) as extension methods. Covers
    /// target-gather cache, ground-enemy scan (FindEnemy), air-enemy scan (FindEnemyAir + the two airborne
    /// sub-scanners), intercept prediction, and lead-prediction math. Cache fields (lastTargetGatherTime /
    /// lastTargetGatherRangeSqr / targetCache) and InvalidateTargetCache stay on the shell bus; NextFindTargetTime /
    /// AttackMode / MaxCombatRange / PreserveEnemyTarget stay on the bus too. The bounds-velocity clamp vectors are
    /// mirrored here as private statics (same values as TankAIHelper.Physics.cs) so RoughPredictTarget does not need
    /// access to the private statics on TankAIHelper.
    /// </summary>
    public static class ModifiedTargetScan
    {
        // Mirrors TankAIHelper.Physics.cs lowMaxBoundsVelo / highMaxBoundsVelo (same MaxBoundsVelo = 350 constant).
        private const float MaxBoundsVelo = 350f;
        private static readonly Vector3 lowMaxBoundsVelo  = -new Vector3(MaxBoundsVelo, MaxBoundsVelo, MaxBoundsVelo);
        private static readonly Vector3 highMaxBoundsVelo =  new Vector3(MaxBoundsVelo, MaxBoundsVelo, MaxBoundsVelo);

        // REVISED: target-scan cache is now range-aware (re-gathers if the requested range exceeds what was last
        // cached) and refreshes faster in combat (TargetCacheRefreshIntervalCombat when Provoked/has-target vs idle
        // TargetCacheRefreshInterval); InvalidateTargetCache forces a re-scan (called from OnHit).
        public static List<Tank> GatherTargetTechsInRange(this TankAIHelper helper, float gatherRangeSqr)
        {
            if (helper.lastTargetGatherTime > Time.time && gatherRangeSqr <= helper.lastTargetGatherRangeSqr)
            {
                return helper.targetCache;
            }
            float interval = (helper.Provoked > 0f || helper.lastEnemyGet?.tank)
                ? AIGlobals.TargetCacheRefreshIntervalCombat
                : AIGlobals.TargetCacheRefreshInterval;
            helper.lastTargetGatherTime = Time.time + interval;
            helper.lastTargetGatherRangeSqr = gatherRangeSqr;
            helper.targetCache.Clear();
            foreach (Tank cTank in TankAIManager.GetTargetTanks(helper.tank.Team))
            {
                if (cTank != helper.tank && cTank.visible.isActive)
                {
                    float dist = (cTank.boundsCentreWorldNoCheck - helper.tank.boundsCentreWorldNoCheck).sqrMagnitude;
                    if (dist < gatherRangeSqr)
                    {
                        helper.targetCache.Add(cTank);
                    }
                }
            }
            return helper.targetCache;
        }

        // REVISED: GatherTargetTechsInRange caches the candidate list across frames, so a tech can be destroyed/recycled
        // (blockless / null blockBounds) between gather and use. boundsCentreWorldNoCheck dereferences blockBounds with NO
        // null-check, so a stale candidate threw a per-frame NullReferenceException that bubbled up and ABORTED the entire
        // enemy AI operations tick (the output_log.txt NRE storm: FindEnemy/FindEnemyAir -> get_boundsCentreWorldNoCheck ->
        // get_blockBounds). Validate every candidate before ANY boundsCentreWorldNoCheck access.
        private static bool IsLiveTarget(Tank cTank)
        {
            return cTank != null && cTank.visible != null && cTank.visible.isActive
                && cTank.blockman != null && cTank.blockman.blockCount > 0;
        }

        // REVISED: dropped the `pos` ranked-target param and the whole 1st/2nd/3rd-closest pos-switch machinery, plus
        // the UseVanillaTargetFetching shortcut; held-target retain now uses the same two-tier range hysteresis
        // (hard/soft cap) as CheckEnemyAndAiming and Random mode picks uniformly from in-range candidates.
        public static Visible FindEnemy(this TankAIHelper helper, bool InvertBullyPriority)
        {
            Visible target = helper.lastEnemyGet;

            float TargetRangeSqr = helper.MaxCombatRange * helper.MaxCombatRange;
            Vector3 scanCenter = helper.tank.boundsCentreWorldNoCheck;

            if (target?.tank)
            {
                if (!target.isActive || !ManBaseTeams.IsEnemy(helper.tank.Team, target.tank.Team))
                {
                    target = null;
                }
                else
                {
                    float sqr = (target.tank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                    bool pastHardCap = sqr > TargetRangeSqr * AIGlobals.RTSLockMaxRangeMultiplierSqr;
                    bool pastSoftCap = sqr > TargetRangeSqr * AIGlobals.CombatRangeRetentionMultSqr;
                    if (pastHardCap || (pastSoftCap && !helper.PreserveEnemyTarget))
                    {
                        DebugTAC_AI.LogTargeting(helper.tank, "Target released FindEnemy: out of range");
                        target = null;
                        helper.EndPursuit();
                    }
                    else if (helper.PreserveEnemyTarget || helper.NextFindTargetTime <= Time.time)
                    {
                        return target;
                    }
                }
            }

            if (helper.AttackMode == EAttackMode.Random)
            {
                List<Tank> techs = helper.GatherTargetTechsInRange(TargetRangeSqr);
                var valid = new List<Visible>(techs.Count);
                for (int step = 0; step < techs.Count; step++)
                {
                    Tank cTank = techs[step];
                    if (cTank == helper.tank || !IsLiveTarget(cTank)) continue;
                    if (!ManBaseTeams.IsEnemy(helper.tank.Team, cTank.Team)) continue;
                    float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                    if (dist < TargetRangeSqr) valid.Add(cTank.visible);
                }
                if (valid.Count > 0)
                    target = valid[UnityEngine.Random.Range(0, valid.Count)];
                helper.NextFindTargetTime = Time.time + AIGlobals.PestererSwitchDelay;
            }
            else if (helper.AttackMode == EAttackMode.Strong)
            {
                List<Tank> techs = helper.GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                if (InvertBullyPriority)
                {
                    int BlockCount = 0;
                    for (int step = 0; step < launchCount; step++)
                    {
                        Tank cTank = techs.ElementAt(step);
                        if (cTank != helper.tank && IsLiveTarget(cTank) && ManBaseTeams.IsEnemy(helper.tank.Team, cTank.Team))
                        {
                            float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                            if (cTank.blockman.blockCount > BlockCount && dist < TargetRangeSqr)
                            {
                                BlockCount = cTank.blockman.blockCount;
                                target = cTank.visible;
                            }
                        }
                    }
                }
                else
                {
                    int BlockCount = 262144;
                    for (int step = 0; step < launchCount; step++)
                    {
                        Tank cTank = techs.ElementAt(step);
                        if (cTank != helper.tank && IsLiveTarget(cTank) && ManBaseTeams.IsEnemy(helper.tank.Team, cTank.Team))
                        {
                            float dist = (cTank.boundsCentreWorldNoCheck - helper.tank.boundsCentreWorldNoCheck).sqrMagnitude;
                            if (cTank.blockman.blockCount < BlockCount && dist < TargetRangeSqr)
                            {
                                BlockCount = cTank.blockman.blockCount;
                                target = cTank.visible;
                            }
                        }
                    }
                }
                helper.NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
            }
            else
            {
                helper.NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
                if (helper.AttackMode == EAttackMode.Chase && target != null)
                {
                    if (target.isActive)
                        return target;
                }

                List<Tank> techs = helper.GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                for (int step = 0; step < launchCount; step++)
                {
                    Tank cTank = techs.ElementAt(step);
                    if (cTank != helper.tank && IsLiveTarget(cTank) && ManBaseTeams.IsEnemy(helper.tank.Team, cTank.Team))
                    {
                        float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                        if (dist < TargetRangeSqr)
                        {
                            TargetRangeSqr = dist;
                            target = cTank.visible;
                        }
                    }
                }
            }
            return target;
        }

        // REVISED: new extracted air-scan helpers. They replace FindEnemyAir's inline altitude-sorted scans;
        // FindEnemyAir now calls them twice (airborne-first, then ground) so air targets are preferred without
        // the old per-tech altitudeHigh comparison.
        public static Visible ScanAirborneRandom(this TankAIHelper helper, List<Tank> techs, int launchCount, bool preferAirborne, ref float TargetRangeSqr, Vector3 scanCenter)
        {
            var valid = new List<Visible>(launchCount);
            for (int step = 0; step < launchCount; step++)
            {
                Tank cTank = techs[step];
                if (cTank == helper.tank || !IsLiveTarget(cTank) || !ManBaseTeams.IsEnemy(helper.tank.Team, cTank.Team)) continue;
                if (AIEPathing.AboveHeightFromGround(cTank.boundsCentreWorldNoCheck) != preferAirborne) continue;
                float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                if (dist < TargetRangeSqr) valid.Add(cTank.visible);
            }
            if (valid.Count == 0) return null;
            return valid[UnityEngine.Random.Range(0, valid.Count)];
        }

        public static Visible ScanAirborneStrong(this TankAIHelper helper, List<Tank> techs, int launchCount, bool preferAirborne, bool invertBully, ref float TargetRangeSqr, Vector3 scanCenter)
        {
            Visible target = null;
            int BlockCount = invertBully ? 0 : 262144;
            for (int step = 0; step < launchCount; step++)
            {
                Tank cTank = techs.ElementAt(step);
                if (cTank == helper.tank || !IsLiveTarget(cTank) || !ManBaseTeams.IsEnemy(helper.tank.Team, cTank.Team))
                    continue;
                bool isAirborne = AIEPathing.AboveHeightFromGround(cTank.boundsCentreWorldNoCheck);
                if (isAirborne != preferAirborne)
                    continue;
                float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                if (dist >= TargetRangeSqr)
                    continue;
                int bc = cTank.blockman.blockCount;
                bool better = invertBully ? (bc > BlockCount) : (bc < BlockCount);
                if (better)
                {
                    BlockCount = bc;
                    target = cTank.visible;
                }
            }
            return target;
        }

        // REVISED: dropped the `pos` param and the inline altitude-ranked scans; scanCenter is now DodgeSphereCenter
        // throughout, held-target retain uses the two-tier range hysteresis, and Random/Strong delegate to
        // ScanAirborne* (airborne-preferred, then ground).
        public static Visible FindEnemyAir(this TankAIHelper helper, bool InvertBullyPriority)
        {
            Visible target = helper.lastEnemyGet;

            float TargetRangeSqr = helper.MaxCombatRange * helper.MaxCombatRange;
            Vector3 scanCenter = helper.DodgeSphereCenter;

            if (target?.tank)
            {
                if (!target.isActive || !ManBaseTeams.IsEnemy(helper.tank.Team, target.tank.Team))
                {
                    target = null;
                }
                else
                {
                    float sqr = (target.tank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                    bool pastHardCap = sqr > TargetRangeSqr * AIGlobals.RTSLockMaxRangeMultiplierSqr;
                    bool pastSoftCap = sqr > TargetRangeSqr * AIGlobals.CombatRangeRetentionMultSqr;
                    if (pastHardCap || (pastSoftCap && !helper.PreserveEnemyTarget))
                    {
                        DebugTAC_AI.LogTargeting(helper.tank, "Target released FindEnemy: out of range");
                        target = null;
                        helper.EndPursuit();
                    }
                    else if (helper.PreserveEnemyTarget || helper.NextFindTargetTime <= Time.time)
                    {
                        return target;
                    }
                }
            }
            if (helper.AttackMode == EAttackMode.Random)
            {
                List<Tank> techs = helper.GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                target = helper.ScanAirborneRandom(techs, launchCount, true, ref TargetRangeSqr, scanCenter)
                      ?? helper.ScanAirborneRandom(techs, launchCount, false, ref TargetRangeSqr, scanCenter);
                helper.NextFindTargetTime = Time.time + AIGlobals.PestererSwitchDelay;
            }
            else if (helper.AttackMode == EAttackMode.Strong)
            {
                List<Tank> techs = helper.GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                if (InvertBullyPriority)
                {
                    target = helper.ScanAirborneStrong(techs, launchCount, preferAirborne: false, invertBully: true, ref TargetRangeSqr, scanCenter)
                          ?? helper.ScanAirborneStrong(techs, launchCount, preferAirborne: true, invertBully: true, ref TargetRangeSqr, scanCenter);
                }
                else
                {
                    target = helper.ScanAirborneStrong(techs, launchCount, preferAirborne: true, invertBully: false, ref TargetRangeSqr, scanCenter)
                          ?? helper.ScanAirborneStrong(techs, launchCount, preferAirborne: false, invertBully: false, ref TargetRangeSqr, scanCenter);
                }
                helper.NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
            }
            else
            {
                helper.NextFindTargetTime = Time.time + AIGlobals.ScanDelay;
                if (helper.AttackMode == EAttackMode.Chase && target != null)
                {
                    if (target.isActive)
                        return target;
                }

                List<Tank> techs = helper.GatherTargetTechsInRange(TargetRangeSqr);
                int launchCount = techs.Count();
                for (int step = 0; step < launchCount; step++)
                {
                    Tank cTank = techs.ElementAt(step);
                    if (cTank != helper.tank && IsLiveTarget(cTank) && ManBaseTeams.IsEnemy(helper.tank.Team, cTank.Team))
                    {
                        float dist = (cTank.boundsCentreWorldNoCheck - scanCenter).sqrMagnitude;
                        if (dist < TargetRangeSqr)
                        {
                            TargetRangeSqr = dist;
                            target = cTank.visible;
                        }
                    }
                }
            }
            return target;
        }

        public static Vector3 InterceptTargetDriving(this TankAIHelper helper, Visible targetTank)
        {
            if (targetTank.IsNull() || targetTank.tank.IsNull())
                return helper.tank.boundsCentreWorldNoCheck;
            if (helper.AdvancedAI)
            {
                return helper.RoughPredictTarget(targetTank.tank);
            }
            else
                return targetTank.tank.boundsCentreWorldNoCheck;
        }

        // REVISED: null-guards the target. Lead prediction is gated to lastCombatRange <= EnemyExtendActionRange (the
        // original cap) — NOT every finite range. This point also feeds the DRIVE destination (InterceptTargetDriving),
        // so leading it at long range threw the drive target far to the side of a moving enemy (a wrong-direction beeline);
        // the cap keeps close-range leading while driving straight at distant targets.
        public static Vector3 RoughPredictTarget(this TankAIHelper helper, Tank targetTank)
        {
            if (targetTank.IsNull())
                return helper.tank.boundsCentreWorldNoCheck;
            if (helper.DriverType != AIDriverType.Stationary && targetTank.rbody.IsNotNull())
            {
                var velo = targetTank.rbody.velocity;
                if (!velo.IsNaN() && !float.IsInfinity(velo.x)
                    && !float.IsInfinity(velo.z) && !float.IsInfinity(velo.y)
                    && helper.lastCombatRange <= AIGlobals.EnemyExtendActionRange)
                {
                    return targetTank.boundsCentreWorldNoCheck + (velo.Clamp(lowMaxBoundsVelo, highMaxBoundsVelo) *
                        (helper.lastCombatRange * AIGlobals.TargetVelocityLeadPredictionMulti));
                }
            }
            return targetTank.boundsCentreWorldNoCheck;
        }
    }
}
