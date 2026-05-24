using System;
using UnityEngine;
using TAC_AI.AI.Movement;
using TAC_AI.AI.Movement.AICores;

namespace TAC_AI.AI.AlliedOperations
{
    internal class AlliedOperationsController
    {
        private TankAIHelper helper;

        public AlliedOperationsController(TankAIHelper helper)
        {
            this.helper = helper;
        }

        public void Execute()
        {
            EControlOperatorSet direct = helper.GetDirectedControl();
            if (helper.DriverType == AIDriverType.Stationary)
            {
                switch (helper.DediAI)
                {
                    case AIType.Assault:
                        // Stationary-Assault = turret. ShootToDestroy leads aim and gates
                        // WantsToFight on aim alignment — what a turret actually wants.
                        // HoldProtect keeps it anchored (Stationary is non-mobile).
                        BAssassin.ShootToDestroy(helper, helper.tank);
                        BBase.HoldProtect(helper, helper.tank, ref direct);
                        break;
                    default:
                        BBase.HoldProtect(helper, helper.tank, ref direct);
                        BGeneral.AidDefend(helper, helper.tank);
                        break;
                }
            }
            else
            {
                switch (helper.DediAI)
                {
                    case AIType.Escort:
                        switch (helper.DriverType)
                        {
                            case AIDriverType.Tank:
                                BGeneral.AidDefend(helper, helper.tank);
                                BEscort.MotivateMove(helper, helper.tank, ref direct);
                                break;

                            case AIDriverType.Astronaut:
                                BGeneral.AidDefend(helper, helper.tank);
                                BAstrotech.MotivateSpace(helper, helper.tank, ref direct);
                                break;

                            case AIDriverType.Sailor:
                                BGeneral.AidDefend(helper, helper.tank);
                                BBuccaneer.MotivateBote(helper, helper.tank, ref direct);
                                break;

                            case AIDriverType.Pilot:
                                BAviator.Dogfighting(helper, helper.tank);
                                BAviator.MotivateFly(helper, helper.tank, ref direct);
                                break;

                            // P13 TD-3: removed the Escort + Stationary case. It was unreachable —
                            // the top-level `if (helper.DriverType == AIDriverType.Stationary)` at the
                            // head of Execute() intercepts every stationary tech before this switch and
                            // routes it to BBase.HoldProtect (+ AidDefend). Its old body
                            // (AidDefend + HoldSupport) was behavior-identical anyway, since
                            // HoldSupport is byte-for-byte equal to HoldProtect.

                            case AIDriverType.AutoSet:
                                // T5: with SetDriverType now resolving AutoSet at the chokepoint,
                                // reaching dispatch with AutoSet means an upstream caller bypassed
                                // SetDriverType (e.g. raw `DriverType = AutoSet` via reflection or
                                // network deserialization). Self-heal and surface loudly.
                                DebugTAC_AI.LogError(KickStart.ModID + ": AutoSet reached dispatch for "
                                    + helper.tank.name + " — upstream resolve missing. DediAI=" + helper.DediAI);
                                helper.ExecuteAutoSetNoCalibrate();
                                break;

                            default:
                                DebugTAC_AI.Log(KickStart.ModID + ": AIDriver is set to an invalid state - " + helper.DriverType);
                                DebugTAC_AI.Log(KickStart.ModID + ": RESETTING TO DEFAULTS");
                                helper.SetDriverType(AIDriverType.Tank);
                                break;
                        }
                        break;
                    case AIType.Assault:
                        BAssassin.ShootToDestroy(helper, helper.tank);
                        BAssassin.MotivateKill(helper, helper.tank, ref direct);
                        break;

                    case AIType.Aegis:
                        // I fight for my friends (priority resource techs pending)
                        BGeneral.AidDefend(helper, helper.tank);
                        BAegis.MotivateProtect(helper, helper.tank, ref direct);
                        break;

                    case AIType.Prospector:
                        BGeneral.SelfDefend(helper, helper.tank);
                        BProspector.MotivateMine(helper, helper.tank, ref direct);
                        break;

                    case AIType.Scrapper:
                        BGeneral.SelfDefend(helper, helper.tank);
                        BScrapper.MotivateFind(helper, helper.tank, ref direct);
                        break;

                    case AIType.Energizer:
                        BGeneral.SelfDefend(helper, helper.tank);
                        BEnergizer.MotivateCharge(helper, helper.tank, ref direct);
                        break;

                    case AIType.MTTurret:
                        // Load, Aim,    FIIIIIRRRRRRRRRRRRRRRRRRRRRRRRRRRE!!!
                        BMultiTech.MimicDefend(helper, helper.tank);
                        BMultiTech.MTStatic(helper, helper.tank, ref direct);
                        //EMultiTech.FollowTurretBelow(helper, helper.tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, helper.tank); //lock rigidbody with closest non-MT Tech on build beam
                        break;

                    case AIType.MTStatic:
                        BMultiTech.MimicDefend(helper, helper.tank);
                        BMultiTech.MTStatic(helper, helper.tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, helper.tank); //lock rigidbody with closest non-MT Tech on build beam
                        break;

                    case AIType.MTMimic:
                        // P13 TD-1: no MimicDefend/AidDefend companion here by design. MimicAllClosestAlly
                        // copies the host tech's control state wholesale (FullBoost/FirePROPS etc.), so
                        // firing is inherited from the copied controls, not computed locally. MTTurret/
                        // MTStatic differ because they aim themselves via MimicDefend; an independent
                        // defend call here would fight the copy instead of mirroring the host.
                        BMultiTech.MimicAllClosestAlly(helper, helper.tank, ref direct);
                        break;

                    default:
                        DebugTAC_AI.Log(KickStart.ModID + ": AIType is set to an invalid state - " + helper.DediAI);
                        DebugTAC_AI.Log(KickStart.ModID + ": RESETTING TO DEFAULTS");
                        helper.DediAI = AIType.Escort;
                        break;
                }
            }
            helper.SetDirectedControl(direct);
        }

    }
}
