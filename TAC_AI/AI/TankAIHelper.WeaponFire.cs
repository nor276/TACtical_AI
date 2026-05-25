using System;
using TerraTechETCUtil;

namespace TAC_AI.AI
{
    // WeaponFire / LOS service (partial-class split of TankAIHelper, Step 2). Detects the tech's weapon aim
    // type (sets WeaponAimType, read by Targeting + the combat strategies) and toggles weapon firing. The LOS
    // fields (WeaponAimType / BlockedLineOfSight / _losBlockedStreak) stay on the core bus (read tech-wide);
    // only the logic lives here. SuppressFiring writes tank.control.FireControl (a control sink).
    public partial class TankAIHelper
    {
        private void SyncLineOfSight()
        {
            try
            {
                if (WeaponAimType == AIWeaponType.Unknown)
                {
                    int WeaponsNeedLOS = 0;
                    int WeaponsNoNeedLOS = 0;
                    foreach (var item in tank.blockman.IterateBlocks())
                    {
                        BlockDetails BD = BlockIndexer.GetBlockDetails(item.BlockType);
                        if (BD.IsWeapon && !BD.IsCab)
                        {
                            if (BD.IsMelee || BD.IsShortRanged)
                                WeaponsNoNeedLOS++;
                            else
                            {
                                var gun = item.GetComponent<ModuleWeaponGun>();
                                if (gun && gun.AimWithTrajectory() && gun.GetRange() > 500 &&
                                    (gun.m_SeekingRounds || gun.GetVelocity() < 60f))
                                    WeaponsNoNeedLOS++;
                                else
                                    WeaponsNeedLOS++;
                            }
                        }
                    }
                    if (WeaponsNeedLOS < WeaponsNoNeedLOS)
                        WeaponAimType = AIWeaponType.Indirect;
                    else
                        WeaponAimType = AIWeaponType.Direct;
                }
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("CheckCanHitTarget() Critical error", e);
            }
        }

        // REVISED: drops the lastSuppressedState cache; now keys off the actual tank.Weapons.enabled state and, when disabling, just clears FireControl instead of forcing an aim at the tech's own center.
        internal void SuppressFiring(bool Disable)
        {
            try
            {
                if (tank.Weapons.enabled == Disable)
                {
                    DebugTAC_AI.Info(KickStart.ModID + ": AI " + tank.name + " of Team " + tank.Team + ":  Disabled weapons: " + Disable);
                    tank.Weapons.enabled = !Disable;
                }
                if (Disable)
                    tank.control.FireControl = false;
            }
            catch (Exception e)
            {
                DebugTAC_AI.LogWarnPlayerOnce("SuppressFiring() Critical error", e);
            }
        }
    }
}
