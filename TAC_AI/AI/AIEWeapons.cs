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
                            {   // Stationary AI should NEVER lower guard - even against Sub-Neutrals
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
                {   // Building with the build beam does NOT suppress weapons - the tech may
                    // still aim and fire normally while the beam is active.
                    helper.WeaponState = AIWeaponState.Normal;
                    helper.SuppressFiring(false);
                }
            }
            catch (Exception e)
            {
                // TD-5: keep this catch - WeaponDirector runs every director tick and dereferences
                // tank.beam / helper.lastEnemyGet?.tank / tank.Team, all of which can race a
                // destroyed-or-recycled tech (MP respawn, streaming unload). But dedup per-tech so a
                // genuinely broken tech can't spam the log every tick, and surface the real exception.
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
                // P12 BUG-4: player-RTS hold-fire is owned by ManWorldRTS.Update (one per-frame clock)
                // instead of self-promoting FIRE_ALL here. The old self-promote ran on the per-tick
                // Maintainer clock while BGeneral.ResetValues cleared it on the slower operations clock,
                // racing each other into a one-tick FireControl flicker on selected SP-RTS techs.

                if (helper.IsMultiTech)
                {   // sync to host tech
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
                        {   // P12 BUG-10: mirror the host's trigger AND its aim. Previously this fired
                            // with no aim point written, so each gun shot at its own stale cached target.
                            // Copy the host's committed aim so the follower fires where the host fires.
                            helper.ActiveAimState = AIWeaponState.Mimic;
                            helper.AimAndFireWeapons(closeAlly.control.TargetPositionWorld, closeAlly.control.TargetRadiusWorld);
                            helper.FireAllWeapons();
                        }
                    }
                }
                else
                {
                    // P12 BUG-1 / TD-3: the Director owns suppression but runs on a slower (rate-limited)
                    // clock than this Maintainer, so a HoldFire intent could leak a shot in the gap before
                    // the Director re-asserted SuppressFiring. Assert it here every tick from the committed
                    // WeaponState - cheap (the enable flip + log are edge-gated inside SuppressFiring) and
                    // makes the Maintainer the single per-tick authority for the hard weapons gate.
                    helper.SuppressFiring(helper.WeaponState == AIWeaponState.HoldFire);
                    switch (helper.WeaponState)
                    {
                        case AIWeaponState.Enemy:
                            if (helper.lastEnemyGet != null)
                            {
                                var targetTank = helper.lastEnemyGet.tank;
                                // P12 BUG-5: WeaponState is produced by the Director, which lags this
                                // Maintainer by up to ~AIDodgeCheapness ticks. Re-validate the held target
                                // with the same predicate CheckEnemyAndAiming uses, so we stop firing the
                                // instant it dies / leaves / turns friendly instead of waiting for the next
                                // Director pass to demote Enemy->Normal.
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
                                // P12 BUG-2: open fire on a confirmed hostile when the operation wants to
                                // fight - not only when FIRE_ALL was reactively latched on-hit. WantsToFight
                                // is the operation states' sustained "engage" intent and had no consumer in
                                // the firing pipeline, so non-tiny techs aimed but never opened fire.
                                // FireControl does not latch across ticks, so this is re-asserted each tick.
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
                                // P12 BUG-12: the old "target - 0.5*range" point went far underground at
                                // long range. radius 0 means this is never sent to the gimbals (the visible
                                // HoldFire pose is owned by UpdateAim_Prefix); it only feeds the lock-on
                                // reticule, so keep it near our own height with a capped downward offset.
                                float holdDrop = Mathf.Min((target - tank.boundsCentreWorldNoCheck).magnitude * 0.5f, 8f);
                                helper.AimAndFireWeapons(new Vector3(target.x, tank.boundsCentreWorldNoCheck.y, target.z) + Vector3.down * holdDrop, 0);
                            }
                            break;
                        case AIWeaponState.Obsticle:
                            // P12 BUG-3: Obst is only ever a ResourceDispenser scenery transform
                            // (GetObstruction filters on vis.resdisp; TankAIManager player-inject gates on
                            // GetComponent<ResourceDispenser>()). Vanilla IsNotNull() is (object)==null only,
                            // so a Unity-destroyed-but-not-GC'd transform passes it yet NREs on .position /
                            // GetComponent. Resolve the Visible once and let its Unity '==' operator catch the
                            // destroyed-pseudo-null case; clear Obst on every dead end so the tech stops
                            // re-aiming at gone/invulnerable scenery forever instead of logging per-tick.
                            if (helper.Obst.IsNotNull())
                            {
                                Visible obstVis = helper.Obst.GetComponent<Visible>();
                                if (obstVis == null || !obstVis.isActive)
                                {   // destroyed mid-frame, recycled, or no longer a real scenery target
                                    helper.Obst = null;
                                    break;
                                }
                                helper.ActiveAimState = AIWeaponState.Obsticle;
                                helper.AimAndFireWeapons(helper.Obst.position + Vector3.up, 3f);
                                if (helper.FIRE_ALL)
                                    helper.FireAllWeapons();
                                // Invulnerable scenery can never be cleared by shooting it - drop the
                                // target so the Director re-evaluates next tick instead of looping forever.
                                Damageable obstDmg = obstVis.damageable;
                                if (obstDmg == null || obstDmg.Invulnerable)
                                    helper.Obst = null;
                            }
                            break;
                        case AIWeaponState.Normal:
                        // P12 BUG-11: Mimic aim/fire is handled in the IsMultiTech branch above,
                        // before this switch is ever reached - the WeaponState here is never Mimic.
                        // No explicit Mimic case: a non-multitech in WeaponState.Mimic (shouldn't
                        // happen) correctly lands on default and only honors the FIRE_ALL latch.
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
