using UnityEngine;

namespace TAC_AI.AI
{
    /// <summary>
    /// Orbit detection + distance-accounting brain - DETACHED from TankAIHelper into the Modified form (v2) as extension
    /// methods (callers keep the same helper.X() syntax). IsOrbiting tests whether the tech is stuck in a close orbit
    /// (net closing-rate below threshold AND not facing PathPoint). GetDistanceFromTask / GetDistanceFromTask2D write
    /// lastOperatorRange and return it. GetPathPointDeltaDistSq / GetPathPointDeltaDistSq2D return the delta-squared
    /// distance to PathPoint this tick (positive = moving away). SetDistanceFromTaskUnneeded resets lastOperatorRange to
    /// 96 when the operator range check is irrelevant. NOTE: lastOperatorRange has `private set` on the property; the
    /// backing field _lastOperatorRange (private) is written directly here following the same pattern as ModifiedTargeting
    /// writing _lastCombatRange.  Both will require the backing field to be widened to `internal` (or a write-accessor
    /// added) before this file compiles cleanly.
    /// </summary>
    public static class ModifiedNavi
    {
        // REVISED: the AIClockPeriod/40 scale factor is now float division ((float)AIClockPeriod/40f); it was integer
        // division that evaluated to 0 in both SP and MP, disabling the orbit check. IsOrbiting_LEGACY was removed.
        public static bool IsOrbiting(this TankAIHelper helper, float minimumCloseInSpeedSqr = AIGlobals.MinimumCloseInSpeedSqr)
        {
            return helper.GetPathPointDeltaDistSq() * ((float)KickStart.AIClockPeriod / 40f) <
                Mathf.Max(minimumCloseInSpeedSqr, helper.EstTopSped / 3) && !helper.Avoiding &&
                Vector3.Dot((helper.PathPoint - helper.tank.boundsCentreWorldNoCheck).normalized, helper.tank.rootBlockTrans.forward) < 0.5f;
        }

        public static float GetDistanceFromTask(this TankAIHelper helper, Vector3 taskLocation, float additionalSpacing = 0)
        {
            if (helper.Attempt3DNavi)
            {
                Vector3 veloFlat;
                if ((bool)helper.tank.rbody)
                {
                    veloFlat = helper.SafeVelocity;
                    veloFlat.y = 0;
                }
                else
                    veloFlat = Vector3.zero;
                helper._lastOperatorRange = (helper.tank.boundsCentreWorldNoCheck + veloFlat - taskLocation).magnitude - additionalSpacing;                return helper.lastOperatorRange;
            }
            else
            {
                return helper.GetDistanceFromTask2D(taskLocation, additionalSpacing);
            }
        }

        public static float GetDistanceFromTask2D(this TankAIHelper helper, Vector3 taskLocation, float additionalSpacing = 0)
        {
            Vector3 veloFlat;
            if ((bool)helper.tank.rbody)
            {
                veloFlat = helper.SafeVelocity;
                veloFlat.y = 0;
            }
            else
                veloFlat = Vector3.zero;
            helper._lastOperatorRange = (helper.tank.boundsCentreWorldNoCheck.ToVector2XZ() + veloFlat.ToVector2XZ() - taskLocation.ToVector2XZ()).magnitude - additionalSpacing;            return helper.lastOperatorRange;
        }

        public static float GetPathPointDeltaDistSq(this TankAIHelper helper)
        {
            if (helper.Attempt3DNavi)
            {
                Vector3 veloFlat;
                if ((bool)helper.tank.rbody)
                {
                    veloFlat = helper.SafeVelocity;
                    veloFlat.y = 0;
                }
                else
                    veloFlat = Vector3.zero;
                float distPrev = helper.lastPathPointRange;
                helper.lastPathPointRange = (helper.tank.boundsCentreWorldNoCheck + veloFlat - helper.PathPoint).sqrMagnitude;
                return helper.lastPathPointRange - distPrev;
            }
            else
                return helper.GetPathPointDeltaDistSq2D();
        }

        // Local helper called by GetPathPointDeltaDistSq; included because it is part of the same distance-accounting
        // group and is only meaningfully called from within this group.
        public static float GetPathPointDeltaDistSq2D(this TankAIHelper helper)
        {
            Vector3 veloFlat;
            if ((bool)helper.tank.rbody)
            {
                veloFlat = helper.SafeVelocity;
                veloFlat.y = 0;
            }
            else
                veloFlat = Vector3.zero;
            float distPrev = helper.lastPathPointRange;
            helper.lastPathPointRange = (helper.tank.boundsCentreWorldNoCheck.ToVector2XZ() + veloFlat.ToVector2XZ() - helper.PathPoint.ToVector2XZ()).sqrMagnitude;
            return helper.lastPathPointRange - distPrev;
        }

        public static void SetDistanceFromTaskUnneeded(this TankAIHelper helper)
        {
            helper._lastOperatorRange = 96;        }
    }
}
