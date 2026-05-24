using System;
using UnityEngine;
using TAC_AI.World;

namespace TAC_AI.AI
{
    internal static class AIEWeapons
    {
        public static void WeaponDirector(TankControl thisControl, TankAIHelper helper, Tank tank)
        {
            DebugTAC_AI.Assert(tank == null, "WeaponDirector: tank is null");
            DebugTAC_AI.Assert(helper == null, "WeaponDirector: helper is null");
            try
            {
                if (!tank.beam.IsActive)
                {
                    if (helper.lastEnemyGet?.tank)
                    {
                        if (AIGlobals.AllowWeaponsDisarm2)
                        {
                            if (helper.DriverType == AIDriverType.Stationary || ManBaseTeams.IsEnemy(tank.Team, helper.lastEnemyGet.tank.Team))
                            {
                                helper.WeaponState = AIWeaponState.Enemy;
                                helper.SuppressFiring(false);
                            }
                            else
                            {
                                helper.WeaponState = AIWeaponState.HoldFire;
                                helper.SuppressFiring(true);
                            }
                        }
                        else
                            helper.WeaponState = AIWeaponState.Enemy;
                    }
                    else if (helper.Obst.IsNotNull())
                    {
                        helper.WeaponState = AIWeaponState.Obsticle;
                        helper.SuppressFiring(false);
                    }
                    else
                    {
                        if (AIGlobals.AllowWeaponsDisarm2)
                        {
                            if (tank.TechIsActivePlayer())
                                helper.WeaponState = AIWeaponState.Normal;
                            else
                                helper.WeaponState = AIWeaponState.HoldFire;
                        }
                        else
                            helper.WeaponState = AIWeaponState.Normal;
                        helper.SuppressFiring(false);
                    }
                }
                else
                {
                    helper.WeaponState = AIWeaponState.Normal;
                    helper.SuppressFiring(false);
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOncePerKey(
                    "WeaponDirector:" + (tank != null ? tank.name : "<recycled>"),
                    "WeaponDirector - Error on handling", e);
            }
        }

        public static void WeaponMaintainer(TankAIHelper helper, Tank tank)
        {
            helper.ActiveAimState = AIWeaponState.Normal;
            if (!tank.beam.IsActive)
            {

                if (helper.IsMultiTech)
                {
                    Tank closeAlly;
                    if ((bool)helper.theResource?.tank)
                        closeAlly = helper.theResource.tank;
                    else
                        closeAlly = helper.lastCloseAlly;
                    if (closeAlly)
                    {
                        Visible targetEnemy = closeAlly.GetHelperInsured().lastEnemyGet;
                        if (targetEnemy == null)
                            targetEnemy = helper.lastEnemyGet;
                        if (targetEnemy.IsNotNull())
                        {
                            helper.ActiveAimState = AIWeaponState.Mimic;
                            Tank targTank = targetEnemy.tank;
                            if (targTank != null)
                                helper.AimAndFireWeapons(targTank.boundsCentreWorldNoCheck, targTank.GetCheapBounds());
                            else
                                helper.AimAndFireWeapons(targetEnemy.centrePosition, targetEnemy.GetCheapBounds() + 1);
                            if (helper.FIRE_ALL)
                                helper.FireAllWeapons();
                        }
                        else if (closeAlly.control.FireControl)
                        {
                            helper.ActiveAimState = AIWeaponState.Mimic;
                            helper.AimAndFireWeapons(closeAlly.control.TargetPositionWorld, closeAlly.control.TargetRadiusWorld);
                            helper.FireAllWeapons();
                        }
                    }
                }
                else
                {
                    helper.SuppressFiring(helper.WeaponState == AIWeaponState.HoldFire);
                    switch (helper.WeaponState)
                    {
                        case AIWeaponState.Enemy:
                            if (helper.lastEnemyGet != null)
                            {
                                var targetTank = helper.lastEnemyGet.tank;
                                if (!helper.lastEnemyGet.isActive ||
                                    (targetTank != null && (targetTank.blockman.blockCount == 0 ||
                                     !Tank.IsEnemy(tank.Team, targetTank.Team))))
                                {
                                    helper.WeaponState = AIWeaponState.Normal;
                                    break;
                                }
                                helper.ActiveAimState = AIWeaponState.Enemy;
                                if (targetTank)
                                    helper.AimAndFireWeapons(targetTank.boundsCentreWorldNoCheck, targetTank.GetCheapBounds());
                                else
                                    helper.AimAndFireWeapons(helper.lastEnemyGet.centrePosition, helper.lastEnemyGet.GetCheapBounds() + 1);
                                if (helper.FIRE_ALL || helper.WantsToFight)
                                    helper.FireAllWeapons();
                            }
                            break;
                        case AIWeaponState.HoldFire:
                            helper.ActiveAimState = AIWeaponState.HoldFire;
                            if (helper.lastEnemyGet != null)
                            {
                                var targetTank = helper.lastEnemyGet.tank;
                                Vector3 target;
                                if (targetTank)
                                    target = targetTank.boundsCentreWorldNoCheck;
                                else
                                    target = helper.lastEnemyGet.centrePosition;
                                float holdDrop = Mathf.Min((target - tank.boundsCentreWorldNoCheck).magnitude * 0.5f, 8f);
                                helper.AimAndFireWeapons(new Vector3(target.x, tank.boundsCentreWorldNoCheck.y, target.z) + Vector3.down * holdDrop, 0);
                            }
                            break;
                        case AIWeaponState.Obsticle:
                            if (helper.Obst.IsNotNull())
                            {
                                Visible obstVis = helper.Obst.GetComponent<Visible>();
                                if (obstVis == null || !obstVis.isActive)
                                {
                                    helper.Obst = null;
                                    break;
                                }
                                helper.ActiveAimState = AIWeaponState.Obsticle;
                                helper.AimAndFireWeapons(helper.Obst.position + Vector3.up, 3f);
                                if (helper.FIRE_ALL)
                                    helper.FireAllWeapons();
                                Damageable obstDmg = obstVis.damageable;
                                if (obstDmg == null || obstDmg.Invulnerable)
                                    helper.Obst = null;
                            }
                            break;
                        case AIWeaponState.Normal:
                        default:
                            if (helper.FIRE_ALL)
                                helper.FireAllWeapons();
                            break;
                    }
                }
            }
        }

    }
}
