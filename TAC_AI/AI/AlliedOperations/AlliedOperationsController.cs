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


                            case AIDriverType.AutoSet:
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
                        BMultiTech.MimicDefend(helper, helper.tank);
                        BMultiTech.MTStatic(helper, helper.tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, helper.tank);
                        break;

                    case AIType.MTStatic:
                        BMultiTech.MimicDefend(helper, helper.tank);
                        BMultiTech.MTStatic(helper, helper.tank, ref direct);
                        BMultiTech.BeamLockWithinBounds(helper, helper.tank);
                        break;

                    case AIType.MTMimic:
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
