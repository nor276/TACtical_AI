using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.AI.Enemy;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement.AICores
{
    // Combat navigation (feature-complete component of AirplaneAICore, Step 3). Player + enemy combat
    // flight nav: side/front engage, melee, altitude-aware range dynamics, back-off-while-facing (DriveAwayFacingTowards).
    internal partial class AirplaneAICore
    {
        /// <summary>
        /// Tells the Player AI where to go (in lastDestination) to handle a moving target
        /// </summary>
        /// <returns>True if the AI can perform combat navigation</returns>
        public bool TryAdjustForCombat(bool between, ref Vector3 pos, ref EControlCoreSet core)
        {
            bool output = false;
            if (Helper.ChaseThreat && !Helper.Retreat && Helper.lastEnemyGet.IsLiveTechTarget())
            {
                output = true;
                Vector3 targPos = Helper.RoughPredictTarget(Helper.lastEnemyGet.tank);
                // REVISED: real null check (IsNotNull on resource + its tank) replaces the truthy theResource?.tank.
                if (between && Helper.theResource.IsLiveTechTarget() && Helper.theResource.tank.IsNotNull())
                {
                    targPos = Between(targPos, Helper.theResource.tank.boundsCentreWorldNoCheck);
                }
                Helper.UpdateEnemyDistance(targPos);
                float driveDyna = Mathf.Clamp((Helper.lastCombatRange - Helper.MinCombatRange) / 3f, -1, 1);

                if (Helper.SideToThreat)
                {
                    if (Helper.FullMelee)
                    {   //orbit WHILE at enemy!
                        core.DriveDir = EDriveFacing.Perpendicular;
                        pos = targPos;

                    }
                    else if (driveDyna == 1)
                    {
                        core.DriveDir = EDriveFacing.Perpendicular;
                        pos = AvoidAssist(targPos, TryGetVelocityOffset(pilot.Tank, pilot));
                    }
                    else if (driveDyna < 0)
                    {
                        core.DriveDir = EDriveFacing.Perpendicular;
                        core.DriveDest = EDriveDest.FromLastDestination;
                        pos = AvoidAssist(targPos, TryGetVelocityOffset(pilot.Tank, pilot));
                    }
                    else
                    {
                        core.DriveDir = EDriveFacing.Perpendicular;
                        pos = targPos;
                    }
                }
                else
                {
                    if (Helper.FullMelee)
                    {
                        core.DriveDir = EDriveFacing.Forwards;
                        pos = targPos;
                    }
                    else if (driveDyna == 1)
                    {
                        core.DriveDir = EDriveFacing.Forwards;
                        pos = AvoidAssist(targPos, TryGetVelocityOffset(pilot.Tank, pilot));
                    }
                    else if (driveDyna < 0)
                    {
                        core.DriveDir = EDriveFacing.Forwards;
                        core.DriveDest = EDriveDest.FromLastDestination;
                        pos = AvoidAssist(targPos, TryGetVelocityOffset(pilot.Tank, pilot));
                    }
                    else
                    {
                        pos = Helper.RoughPredictTarget(Helper.lastEnemyGet.tank);
                    }
                }

                Helper.UpdateEnemyDistance(Helper.lastEnemyGet.tank.boundsCentreWorld);

                pilot.TargetGrounded = !AIEPathing.AboveHeightFromGround(Helper.lastEnemyGet.tank.boundsCentreWorldNoCheck, pilot.AerofoilSluggishness + groundOffset);

                if (Helper.FullMelee)
                    pilot.AdvisedThrottle = 1;
                else
                    AdviseThrottleTarget(pilot, Helper, pilot.Tank, Helper.lastEnemyGet);
            }
            else
            {
                Helper.IgnoreEnemyDistance();
                pilot.TargetGrounded = false;
            }
            return output;
        }

        /// <summary>
        /// Tells the Non-Player AI where to go (in lastDestination) to handle a moving target
        /// </summary>
        /// <returns>True if the AI can perform combat navigation</returns>
        public bool TryAdjustForCombatEnemy(EnemyMind mind, ref Vector3 pos, ref EControlCoreSet core)
        {
            bool output = false;

            bool isCombatAttitude = mind.CommanderMind != EnemyAttitude.OnRails;
            if (!Helper.Retreat && Helper.lastEnemyGet.IsLiveTechTarget() && isCombatAttitude)
            {
                output = true;
                Helper.UpdateEnemyDistance(Helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);
                float driveDyna = Mathf.Clamp((Helper.lastCombatRange - Helper.MinCombatRange) / 3f, -1, 1);

                if (Helper.SideToThreat)
                {
                    if (Helper.FullMelee)
                    {   //orbit WHILE at enemy!
                        core.DriveDir = EDriveFacing.Perpendicular;
                        pos = Helper.RoughPredictTarget(Helper.lastEnemyGet.tank);

                    }
                    else if (driveDyna == 1)
                    {
                        core.DriveDir = EDriveFacing.Perpendicular;
                        pos = Helper.AvoidAssistPrediction(Helper.RoughPredictTarget(Helper.lastEnemyGet.tank), pilot.AerofoilSluggishness);
                    }
                    else if (driveDyna < 0)
                    {
                        core.DriveDir = EDriveFacing.Perpendicular;
                        core.DriveDest = EDriveDest.FromLastDestination;
                        pos = Helper.AvoidAssistPrediction(Helper.RoughPredictTarget(Helper.lastEnemyGet.tank), pilot.AerofoilSluggishness);
                    }
                    else
                    {
                        core.DriveDir = EDriveFacing.Perpendicular;
                        pos = Helper.RoughPredictTarget(Helper.lastEnemyGet.tank);
                    }
                }
                else
                {
                    if (Helper.FullMelee)
                    {
                        core.DriveDir = EDriveFacing.Forwards;
                        pos = Helper.RoughPredictTarget(Helper.lastEnemyGet.tank);
                    }
                    else if (driveDyna == 1)
                    {
                        core.DriveDir = EDriveFacing.Forwards;
                        pos = Helper.AvoidAssistPrediction(Helper.RoughPredictTarget(Helper.lastEnemyGet.tank), pilot.AerofoilSluggishness);
                    }
                    else if (driveDyna < 0)
                    {
                        // REVISED: too-close now backs off while keeping the front on target
                        // (DriveAwayFacingTowards), changed from DriveAwayFacingAway.
                        core.DriveAwayFacingTowards();
                        pos = Helper.AvoidAssistPrediction(Helper.RoughPredictTarget(Helper.lastEnemyGet.tank), pilot.AerofoilSluggishness);
                    }
                    else
                    {
                        pos = Helper.RoughPredictTarget(Helper.lastEnemyGet.tank);
                    }
                }

                Helper.UpdateEnemyDistance(Helper.lastEnemyGet.tank.boundsCentreWorldNoCheck);

                pilot.TargetGrounded = !AIEPathing.AboveHeightFromGround(Helper.lastEnemyGet.tank.boundsCentreWorldNoCheck, pilot.AerofoilSluggishness + groundOffset);
                if (mind.CommanderSmarts >= EnemySmarts.Meh)
                {
                    if (Helper.FullMelee)
                        pilot.AdvisedThrottle = 1;
                    else
                        AdviseThrottleTarget(pilot, Helper, pilot.Tank, Helper.lastEnemyGet);
                }
                else
                    pilot.AdvisedThrottle = 1;
            }
            else
            {
                Helper.IgnoreEnemyDistance();
                pilot.TargetGrounded = false;
            }
            return output;
        }
    }
}
