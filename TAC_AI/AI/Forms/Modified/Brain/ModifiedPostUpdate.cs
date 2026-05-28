using UnityEngine;
using TAC_AI.World;

namespace TAC_AI.AI
{
    /// <summary>
    /// Post-update brain - DETACHED from TankAIHelper into the Modified form (v2) as extension methods. Covers the two
    /// per-frame finalisers that run after the main combat decision: weapon lock-on target assignment (ManageAILockOn) and
    /// grabbed-block physics upkeep (UpdateBlockHold). Lock-on state (lastLockOnTarget/ActiveAimState) and block-hold
    /// state (heldBlock/blockHoldPos/blockHoldRot/blockHoldOffset) all stay on the shell bus; no brain-local storage is
    /// needed. DropBlock/HoldBlock are NOT detached here - they are called from many other sites and remain on the shell.
    /// </summary>
    public static class ModifiedPostUpdate
    {
        // ManageAILockOn  TankAIHelper.cs ~3911-3975
        public static void ManageAILockOn(this TankAIHelper helper)
        {
            switch (helper.ActiveAimState)
            {
                case AIWeaponState.Enemy:
                    if (helper.lastEnemyGet.IsNotNull())
                    {
                        if (!DebugTAC_AI.NoLogTargeting)
                            DebugTAC_AI.LogTargeting(helper.tank, "Overriding targeting to aim at " + helper.lastEnemy.name + " pos " + helper.lastEnemy.tank.boundsCentreWorldNoCheck);
                        helper.lastLockOnTarget = helper.lastEnemyGet;
                    }
                    break;
                case AIWeaponState.Obsticle:
                    if (helper.Obst != null && helper.Obst.gameObject)
                    {
                        var resTarget = helper.Obst.GetComponent<Visible>();
                        if (resTarget)
                        {
                            DebugTAC_AI.LogTargeting(helper.tank, "Overriding targeting to aim at obstruction");
                            helper.lastLockOnTarget = resTarget;
                        }
                    }
                    break;
                case AIWeaponState.Mimic:
                    if (helper.lastCloseAlly.IsNotNull())
                    {
                        DebugTAC_AI.LogTargeting(helper.tank, "Overriding targeting to aim at player's target");
                        var helperAlly = helper.lastCloseAlly.GetHelperInsured();
                        if (helperAlly.ActiveAimState == AIWeaponState.Enemy)
                            helper.lastLockOnTarget = helperAlly.lastEnemyGet;
                    }
                    break;
            }

            if (helper.lastLockOnTarget)
            {
                bool playerAim = helper.tank.PlayerFocused && !ManWorldRTS.PlayerIsInRTS;
                if (!helper.lastLockOnTarget.isActive || (playerAim && !helper.tank.control.FireControl))
                {
                    helper.lastLockOnTarget = null;
                    return;
                }
                if (helper.lastLockOnTarget == helper.tank.visible)
                {
                    DebugTAC_AI.Assert("Tech " + (helper.tank.name.NullOrEmpty() ? "<NULL>" : helper.tank.name) + " tried to lock-on to itself!!!");
                    helper.lastLockOnTarget = null;
                    return;
                }
                if (!playerAim && helper.lastLockOnTarget.resdisp && helper.ActiveAimState != AIWeaponState.Obsticle)
                {
                    helper.lastLockOnTarget = null;
                    return;
                }

                float maxDist;
                if (ManNetwork.IsNetworked)
                    maxDist = helper.tank.Weapons.m_ManualTargetingSettingsMAndKB.m_ManualTargetingRadiusMP;
                else
                    maxDist = helper.tank.Weapons.m_ManualTargetingSettingsMAndKB.m_ManualTargetingRadiusSP;
                if (helper._lastCombatRange > maxDist)
                {
                    helper.lastLockOnTarget = null;
                }
            }
        }

        // UpdateBlockHold  TankAIHelper.cs ~4014-4099
        public static void UpdateBlockHold(this TankAIHelper helper)
        {
            if (helper.HeldBlock?.visible)
            {
                if (!ManNetwork.IsNetworked)
                {
                    if (!helper.HeldBlock.visible.isActive)
                    {
                        try
                        {
                            helper.DropBlock();
                        }
                        catch { }
                        helper.heldBlock = null;
                    }
                    else if (helper.HeldBlock.visible.InBeam || helper.HeldBlock.IsAttached)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + helper.tank.name + "'s grabbed block was thefted!");
                        helper.DropBlock();
                    }
                    else if (ManPointer.inst.targetVisible == helper.HeldBlock.visible)
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Tech " + helper.tank.name + "'s grabbed block was grabbed by player!");
                        helper.DropBlock();
                    }
                    else
                    {
                        Vector3 moveVec;
                        if (helper.blockHoldOffset)
                        {
                            moveVec = helper.tank.trans.TransformPoint(helper.blockHoldPos) - helper.HeldBlock.transform.position;
                            float dotVal = Vector3.Dot(moveVec.normalized, Vector3.down);
                            if (dotVal > 0.75f)
                                moveVec.y += moveVec.ToVector2XZ().magnitude / 3;
                            else
                            {
                                moveVec.y -= moveVec.ToVector2XZ().magnitude / 3;
                            }
                            Vector3 finalPos = helper.HeldBlock.transform.position;
                            finalPos += moveVec / ((100 / AIGlobals.BlockAttachDelay) * Time.fixedDeltaTime);
                            if (finalPos.y < helper.tank.trans.TransformPoint(helper.blockHoldPos).y)                                finalPos.y = helper.tank.trans.TransformPoint(helper.blockHoldPos).y;                            helper.HeldBlock.transform.position = finalPos;
                            if (helper.HeldBlock.rbody)
                            {
                                if (helper.tank.rbody)
                                    helper.HeldBlock.rbody.velocity = helper.SafeVelocity.SetY(0);
                                helper.HeldBlock.rbody.AddForce(-(TankAIManager.GravVector * helper.HeldBlock.AverageGravityScaleFactor), ForceMode.Acceleration);
                                Vector3 forward = helper.tank.trans.TransformDirection(helper.blockHoldRot * Vector3.forward);
                                Vector3 up = helper.tank.trans.TransformDirection(helper.blockHoldRot * Vector3.up);
                                Quaternion rotChangeWorld = AIGlobals.LookRot(forward, up);
                                helper.HeldBlock.rbody.MoveRotation(Quaternion.RotateTowards(helper.HeldBlock.transform.rotation, rotChangeWorld,
                                    (360 / AIGlobals.BlockAttachDelay) * Time.fixedDeltaTime));
                            }
                            helper.HeldBlock.visible.SetLockTimout(Visible.LockTimerTypes.Interactible, 0.25f);
                        }
                        else
                        {
                            moveVec = helper.tank.boundsCentreWorldNoCheck + (Vector3.up * (helper.lastTechExtents + 3)) - helper.HeldBlock.visible.centrePosition;
                            moveVec = Vector3.ClampMagnitude(moveVec * 4, AIGlobals.ItemGrabStrength);
                            if (helper.HeldBlock.rbody)
                                helper.HeldBlock.rbody.AddForce(moveVec - (TankAIManager.GravVector * helper.HeldBlock.AverageGravityScaleFactor), ForceMode.Acceleration);
                            helper.HeldBlock.visible.SetLockTimout(Visible.LockTimerTypes.Interactible, 0.25f);
                        }
                    }
                }
                else if (ManNetwork.IsHost)
                {
                    if (!helper.HeldBlock.visible.isActive)
                    {
                        helper.DropBlock();
                    }
                    else if (helper.HeldBlock.visible.InBeam || helper.HeldBlock.IsAttached)
                    {
                        helper.DropBlock();
                    }
                    else
                    {
                        if (helper.tank.CentralBlock)
                            helper.HeldBlock.visible.centrePosition = helper.tank.CentralBlock.centreOfMassWorld;
                        else
                            helper.HeldBlock.visible.centrePosition = helper.tank.boundsCentreWorldNoCheck;
                    }
                }
            }
        }
    }
}
