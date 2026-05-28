using UnityEngine;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;
using TAC_AI.World;

namespace TAC_AI.AI
{
    /// <summary>
    /// Targeting brain - DETACHED from TankAIHelper into the Modified form (v2) as extension methods (callers keep the
    /// same helper.X() syntax). Target acquisition / validation / pursuit-lock + LOS-grace hysteresis. The target STATE
    /// (lastEnemy/lastEnemyGet/KeepEnemyFocus/_lastCombatRange/Provoked/BlockedLineOfSight) stays on the shell bus;
    /// brain-local timers (LastWeapCheck/_losLostGraceTimer/_losBlockedStreak) live in ModifiedTechState. SetPursuit
    /// SETS KeepEnemyFocus; EndPursuit drops the lock only; ReleaseTarget nulls the target (whose setter clears lock).
    /// </summary>
    public static class ModifiedTargeting
    {
        public enum SetPursuitResult : byte
        {
            Set            = 0,
            AlreadyTarget  = 1,
            BlockedByLock  = 2,
            NullTarget     = 3,
            InvalidTank    = 4,
        }

        public static void CheckEnemyAndAiming(this TankAIHelper helper)
        {
            var st = helper.MState();
            if (st.LastWeapCheck < Time.time)
            {
                st.LastWeapCheck = Time.time + AIGlobals.TargetValidationDelay;
                helper.SyncLineOfSight();
                bool wasBlockedThisCheck = false;
                if (helper.lastEnemyGet)
                {
                    if (!helper.lastEnemyGet.isActive || helper.lastEnemyGet.tank.blockman.blockCount == 0 ||
                        !Tank.IsEnemy(helper.tank.Team, helper.lastEnemyGet.tank.Team))
                    {
                        DebugTAC_AI.LogTargeting(helper.tank, "Target released CheckEnemyAndAiming: dead/wrong-team");
                        helper.ReleaseTarget();
                    }
                    else
                    {
                        Vector3 pos = helper.tank.boundsCentreWorld + Vector3.up;
                        Vector3 vec = helper.lastEnemy.tank.boundsCentreWorld - pos;
                        float targetDistance = vec.magnitude;
                        if (helper.NeedsLineOfSight && targetDistance > 0.05f)
                        {
                            Vector3 dir = vec / targetDistance;
                            if (Physics.Raycast(pos, dir, out RaycastHit hit,
                                Mathf.Min(targetDistance, helper.MaxCombatRange), TankAIHelper.TargetMask, QueryTriggerInteraction.Ignore)
                                && hit.distance < targetDistance)
                            {
                                wasBlockedThisCheck = true;
                            }
                        }
                        float hardCap = helper.MaxCombatRange * AIGlobals.RTSLockMaxRangeMultiplier;
                        if (targetDistance > hardCap
                            || (targetDistance > helper.MaxCombatRange * AIGlobals.CombatRangeRetentionMult && !helper.PreserveEnemyTarget))
                        {
                            DebugTAC_AI.LogTargeting(helper.tank, "Target released CheckEnemyAndAiming: out of range");
                            helper.ReleaseTarget();
                        }
                    }
                }
                if (wasBlockedThisCheck)
                {
                    helper._losBlockedStreak++;
                    if (helper._losBlockedStreak >= AIGlobals.LosBlockedStreakThreshold)
                        helper.BlockedLineOfSight = true;
                }
                else
                {
                    helper._losBlockedStreak = 0;
                    helper.BlockedLineOfSight = false;
                }
            }
        }

        public static Visible TryRefreshEnemyAllied(this TankAIHelper helper)
        {
            if ((bool)helper.lastPlayer)
            {
                Tank playerTank = helper.lastPlayer.tank;
                Visible playerTarget = playerTank.Weapons.GetManualTarget();
                if (playerTarget && playerTarget.tank != null && playerTarget.isActive &&
                    playerTarget.tank.CentralBlock &&
                    playerTarget.tank.Team != helper.tank.Team &&
                    !ManBaseTeams.IsTeammate(helper.tank.Team, playerTarget.tank.Team))
                {
                    int targTeam = playerTarget.tank.Team;
                    if (!ManBaseTeams.IsEnemy(helper.tank.Team, targTeam) &&
                        playerTank.control.FireControl &&
                        ManBaseTeams.CanAlterRelations(helper.tank.Team, targTeam))
                    {
                        ManBaseTeams.DegradeRelations(helper.tank.Team, targTeam, AIGlobals.DamageAngerDropRelations);
                    }
                    if (ManBaseTeams.IsEnemy(helper.tank.Team, targTeam))
                    {
                        helper.Provoked = AIGlobals.ProvokeTime;
                        helper.SetPursuit(playerTarget, force: true);
                        return helper.lastEnemy;
                    }
                }
            }
            if (helper.MovementController is AIControllerAir air && air.AICore is IAirMovementAICore aircore && aircore.IsFixedWing)
            {
                helper.lastEnemy = helper.FindEnemyAir(false);
            }
            else
                helper.lastEnemy = helper.FindEnemy(false);
            return helper.lastEnemy;
        }

        public static Visible TryRefreshEnemyEnemy(this TankAIHelper helper, bool InvertBullyPriority)
        {
            if (helper.MovementController is AIControllerAir air && air.AICore is IAirMovementAICore aircore && aircore.IsFixedWing)
            {
                helper.lastEnemy = helper.FindEnemyAir(InvertBullyPriority);
            }
            else
                helper.lastEnemy = helper.FindEnemy(InvertBullyPriority);
            return helper.lastEnemy;
        }

        public static void UpdateTargetCombatFocus(this TankAIHelper helper)
        {
            if (helper.Provoked > 0)
            {
                helper.Provoked -= KickStart.AIClockPeriod;
                helper._losLostGraceTimer = 0f;
                return;
            }
            helper.Provoked = 0;

            if (!helper.lastEnemyGet)
            {
                helper._losLostGraceTimer = 0f;
                helper.EndPursuit();
                return;
            }
            if (!helper.InRangeOfTarget(helper.MaxCombatRange))
            {
                helper._losLostGraceTimer = 0f;
                helper.EndPursuit();
                return;
            }
            if (helper.NeedsLineOfSight && helper.BlockedLineOfSight)
            {
                helper._losLostGraceTimer += KickStart.AIClockPeriod * Time.fixedDeltaTime;
                if (helper._losLostGraceTimer >= AIGlobals.LOSLostGraceTime)
                {
                    helper._losLostGraceTimer = 0f;
                    helper.EndPursuit();
                }
            }
            else
            {
                helper._losLostGraceTimer = 0f;
            }
        }

        public static float UpdateEnemyDistance(this TankAIHelper helper, Vector3 enemyPosition)
        {
            helper._lastCombatRange = (enemyPosition - helper.tank.boundsCentreWorldNoCheck).magnitude;
            return helper._lastCombatRange;
        }

        public static float IgnoreEnemyDistance(this TankAIHelper helper)
        {
            helper._lastCombatRange = float.MaxValue;
            return helper._lastCombatRange;
        }

        public static bool SetPursuit(this TankAIHelper helper, Visible target) => IsSetPursuitSuccess(helper.TrySetPursuit(target, false));
        public static bool SetPursuit(this TankAIHelper helper, Visible target, bool force) => IsSetPursuitSuccess(helper.TrySetPursuit(target, force));
        private static bool IsSetPursuitSuccess(SetPursuitResult r) =>
            r == SetPursuitResult.Set || r == SetPursuitResult.AlreadyTarget;
        public static SetPursuitResult TrySetPursuit(this TankAIHelper helper, Visible target) => helper.TrySetPursuit(target, false);
        public static SetPursuitResult TrySetPursuit(this TankAIHelper helper, Visible target, bool force)
        {
            if (target == null)
            {
                if (helper.KeepEnemyFocus) helper.KeepEnemyFocus = false;
                return SetPursuitResult.NullTarget;
            }
            if (!(bool)target.tank) return SetPursuitResult.InvalidTank;
            if (target == helper.lastEnemy)
            {
                helper.ControlOperator.SetLastDest(target.tank.boundsCentreWorldNoCheck);
                helper.MarkOperatorDirty();
                helper.KeepEnemyFocus = true;
                return SetPursuitResult.AlreadyTarget;
            }
            if (helper.KeepEnemyFocus && !force) return SetPursuitResult.BlockedByLock;
            helper.lastEnemy = target;
            helper.ControlOperator.SetLastDest(target.tank.boundsCentreWorldNoCheck);
            helper.MarkOperatorDirty();
            helper.KeepEnemyFocus = true;
            return SetPursuitResult.Set;
        }

        public static void EndPursuit(this TankAIHelper helper)
        {
            if (helper.KeepEnemyFocus)
            {
                helper.KeepEnemyFocus = false;
            }
        }

        public static void ReleaseTarget(this TankAIHelper helper)
        {
            helper.lastEnemy = null;
        }

        public static bool InRangeOfTarget(this TankAIHelper helper, float distance)
        {
            return helper.InRangeOfTarget(helper.lastEnemyGet, distance);
        }

        public static bool InRangeOfTarget(this TankAIHelper helper, Visible target, float distance)
        {
            return (target.tank.boundsCentreWorldNoCheck - helper.tank.boundsCentreWorldNoCheck).sqrMagnitude <= distance * distance;
        }
    }
}
