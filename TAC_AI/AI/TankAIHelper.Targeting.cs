using UnityEngine;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;
using TAC_AI.World;

namespace TAC_AI.AI
{
    // Targeting service (partial-class split of TankAIHelper, Step 2). Target acquisition / validation /
    // pursuit-lock ownership + LOS-grace hysteresis. The target STATE (lastEnemy/_lastEnemy/lastEnemyGet/
    // KeepEnemyFocus/_lastCombatRange/Provoked) stays on the core bus; this file owns the logic. SetPursuit
    // SETS KeepEnemyFocus; EndPursuit drops the lock only; ReleaseTarget nulls the target (whose setter then
    // also clears the lock). No tank.control writes.
    public partial class TankAIHelper
    {
        private float LastWeapCheck = 0;
        // REVISED: LOS is now debounced - it accumulates into _losBlockedStreak and only sets BlockedLineOfSight after LosBlockedStreakThreshold consecutive blocked checks; raycast distance is clamped to the target distance and skips when nearly coincident.
        // REVISED: target drop now uses two-tier range hysteresis (hard cap MaxCombatRange*RTSLockMaxRangeMultiplier, soft cap *CombatRangeRetentionMult honoured only when not PreserveEnemyTarget) and goes through ReleaseTarget.
        private void CheckEnemyAndAiming()
        {
            if (LastWeapCheck < Time.time)
            {
                LastWeapCheck = Time.time + AIGlobals.TargetValidationDelay;
                SyncLineOfSight();
                bool wasBlockedThisCheck = false;
                if (lastEnemyGet)
                {
                    if (!lastEnemyGet.isActive || lastEnemyGet.tank.blockman.blockCount == 0 ||
                        !Tank.IsEnemy(tank.Team, lastEnemyGet.tank.Team))
                    {
                        DebugTAC_AI.LogTargeting(tank, "Target released CheckEnemyAndAiming: dead/wrong-team");
                        ReleaseTarget();
                    }
                    else
                    {
                        Vector3 pos = tank.boundsCentreWorld + Vector3.up;
                        Vector3 vec = lastEnemy.tank.boundsCentreWorld - pos;
                        float targetDistance = vec.magnitude;
                        if (NeedsLineOfSight && targetDistance > 0.05f)
                        {
                            Vector3 dir = vec / targetDistance;
                            if (Physics.Raycast(pos, dir, out RaycastHit hit,
                                Mathf.Min(targetDistance, MaxCombatRange), TargetMask, QueryTriggerInteraction.Ignore)
                                && hit.distance < targetDistance)
                            {
                                wasBlockedThisCheck = true;
                            }
                        }
                        float hardCap = MaxCombatRange * AIGlobals.RTSLockMaxRangeMultiplier;
                        if (targetDistance > hardCap
                            || (targetDistance > MaxCombatRange * AIGlobals.CombatRangeRetentionMult && !PreserveEnemyTarget))
                        {
                            DebugTAC_AI.LogTargeting(tank, "Target released CheckEnemyAndAiming: out of range");
                            ReleaseTarget();
                        }
                    }
                }
                if (wasBlockedThisCheck)
                {
                    _losBlockedStreak++;
                    if (_losBlockedStreak >= AIGlobals.LosBlockedStreakThreshold)
                        BlockedLineOfSight = true;
                }
                else
                {
                    _losBlockedStreak = 0;
                    BlockedLineOfSight = false;
                }
            }
        }
        // REVISED: adopting the player's manual target now validates team/teammate, degrades relations (DegradeRelations) when the player fires on a not-yet-enemy alterable team, and only locks pursuit (SetPursuit force) once the target is actually an enemy - instead of unconditionally setting lastEnemy to the player's target.
        // REVISED: air-vs-ground branch keyed on AICore IsFixedWing instead of AIControllerAir.FlyStyle==Aircraft.
        public Visible TryRefreshEnemyAllied()
        {
            if ((bool)lastPlayer)
            {
                Tank playerTank = lastPlayer.tank;
                Visible playerTarget = playerTank.Weapons.GetManualTarget();
                if (playerTarget && playerTarget.tank != null && playerTarget.isActive &&
                    playerTarget.tank.CentralBlock &&
                    playerTarget.tank.Team != tank.Team &&
                    !ManBaseTeams.IsTeammate(tank.Team, playerTarget.tank.Team))
                {
                    int targTeam = playerTarget.tank.Team;
                    if (!ManBaseTeams.IsEnemy(tank.Team, targTeam) &&
                        playerTank.control.FireControl &&
                        ManBaseTeams.CanAlterRelations(tank.Team, targTeam))
                    {
                        ManBaseTeams.DegradeRelations(tank.Team, targTeam, AIGlobals.DamageAngerDropRelations);
                    }
                    if (ManBaseTeams.IsEnemy(tank.Team, targTeam))
                    {
                        Provoked = AIGlobals.ProvokeTime;
                        SetPursuit(playerTarget, force: true);
                        return lastEnemy;
                    }
                }
            }
            if (MovementController is AIControllerAir air && air.AICore is IAirMovementAICore aircore && aircore.IsFixedWing)
            {
                lastEnemy = FindEnemyAir(false);
            }
            else
                lastEnemy = FindEnemy(false);
            return lastEnemy;
        }
        // REVISED: dropped the unused `pos` ranked-target param (1st/2nd/3rd-closest selection removed downstream); air branch keyed on IsFixedWing.
        public Visible TryRefreshEnemyEnemy(bool InvertBullyPriority)
        {
            if (MovementController is AIControllerAir air && air.AICore is IAirMovementAICore aircore && aircore.IsFixedWing)
            {
                lastEnemy = FindEnemyAir(InvertBullyPriority);
            }
            else
                lastEnemy = FindEnemy(InvertBullyPriority);
            return lastEnemy;
        }
        // REVISED: rewritten as early-return cascade and now owns the Provoked decrement (Provoked-=AIClockPeriod moved here). Adds a LOS-lost grace window: while Provoke=0 and the held target's LOS is blocked, it holds for LOSLostGraceTime before EndPursuit, instead of dropping immediately - so a target behind brief cover is kept.
        private float _losLostGraceTimer = 0f;
        private void UpdateTargetCombatFocus()
        {
            if (Provoked > 0)
            {
                Provoked -= KickStart.AIClockPeriod;
                _losLostGraceTimer = 0f;
                return;
            }
            Provoked = 0;

            if (!lastEnemyGet)
            {
                _losLostGraceTimer = 0f;
                EndPursuit();
                return;
            }
            if (!InRangeOfTarget(MaxCombatRange))
            {
                _losLostGraceTimer = 0f;
                EndPursuit();
                return;
            }
            if (NeedsLineOfSight && BlockedLineOfSight)
            {
                _losLostGraceTimer += KickStart.AIClockPeriod * Time.fixedDeltaTime;
                if (_losLostGraceTimer >= AIGlobals.LOSLostGraceTime)
                {
                    _losLostGraceTimer = 0f;
                    EndPursuit();
                }
            }
            else
            {
                _losLostGraceTimer = 0f;
            }
        }
        internal float UpdateEnemyDistance(Vector3 enemyPosition)
        {
            _lastCombatRange = (enemyPosition - tank.boundsCentreWorldNoCheck).magnitude;
            return _lastCombatRange;
        }
        internal float IgnoreEnemyDistance()
        {
            _lastCombatRange = float.MaxValue;
            return _lastCombatRange;
        }
        public enum SetPursuitResult : byte
        {
            Set            = 0,
            AlreadyTarget  = 1,
            BlockedByLock  = 2,
            NullTarget     = 3,
            InvalidTank    = 4,
        }
        // REVISED: SetPursuit reworked over TrySetPursuit which returns a SetPursuitResult and takes a `force` flag. A held KeepEnemyFocus target now blocks a new pursuit (BlockedByLock) unless force=true; previously KeepEnemyFocus simply short-circuited and only-null cleared focus. Setting a target also MarkOperatorDirty.
        public bool SetPursuit(Visible target) => IsSetPursuitSuccess(TrySetPursuit(target, false));
        public bool SetPursuit(Visible target, bool force) => IsSetPursuitSuccess(TrySetPursuit(target, force));
        private static bool IsSetPursuitSuccess(SetPursuitResult r) =>
            r == SetPursuitResult.Set || r == SetPursuitResult.AlreadyTarget;
        public SetPursuitResult TrySetPursuit(Visible target) => TrySetPursuit(target, false);
        public SetPursuitResult TrySetPursuit(Visible target, bool force)
        {
            if (target == null)
            {
                if (KeepEnemyFocus) KeepEnemyFocus = false;
                return SetPursuitResult.NullTarget;
            }
            if (!(bool)target.tank) return SetPursuitResult.InvalidTank;
            if (target == lastEnemy)
            {
                ControlOperator.SetLastDest(target.tank.boundsCentreWorldNoCheck);
                MarkOperatorDirty();
                KeepEnemyFocus = true;
                return SetPursuitResult.AlreadyTarget;
            }
            if (KeepEnemyFocus && !force) return SetPursuitResult.BlockedByLock;
            lastEnemy = target;
            ControlOperator.SetLastDest(target.tank.boundsCentreWorldNoCheck);
            MarkOperatorDirty();
            KeepEnemyFocus = true;
            return SetPursuitResult.Set;
        }
        public void EndPursuit()
        {
            if (KeepEnemyFocus)
            {
                KeepEnemyFocus = false;
            }
        }
        // REVISED: new hard target-clear. Unlike EndPursuit (which only drops the KeepEnemyFocus lock), ReleaseTarget nulls lastEnemy outright (and the lastEnemy setter then also clears KeepEnemyFocus).
        public void ReleaseTarget()
        {
            lastEnemy = null;
        }
        public bool InRangeOfTarget(float distance)
        {
            return InRangeOfTarget(lastEnemyGet, distance);
        }
        public bool InRangeOfTarget(Visible target, float distance)
        {
            return (target.tank.boundsCentreWorldNoCheck - tank.boundsCentreWorldNoCheck).sqrMagnitude <= distance * distance;
        }
    }
}
