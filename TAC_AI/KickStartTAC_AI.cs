using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TAC_AI.AI;
using TAC_AI.Templates;

namespace TAC_AI
{

#if STEAM
    public class KickStartTAC_AI : ModBase
    {

        internal static TerraTechETCUtil.ModDataHandle oInst;

        bool isInit = false;
        public override bool HasEarlyInit()
        {
            return true;
        }

        // IDK what I should init here...
        public override void EarlyInit()
        {
            if (oInst == default)
            {
                oInst = new TerraTechETCUtil.ModDataHandle(KickStart.ModID);
            }
        }
        public IEnumerable<float> InitIterator()
        {
            return KickStart.MainOfficialInitIterate();
        }
        public override void Init()
        {
            // We do this check because this mod takes FOREVER to build, so we don't heed every reset
            //   request - the mod is already built to handle that because of Unofficial.
            KickStart.ShouldBeActive = true;
            if (!isInit)
            {
                if (oInst == default)
                    oInst = new TerraTechETCUtil.ModDataHandle(KickStart.ModID);
                try
                {
                    TerraTechETCUtil.ModStatusChecker.EncapsulateSafeInit(KickStart.ModID,
                        KickStart.MainOfficialInit, KickStart.DeInitALL);
                }
                // REVISED: init failure now logged via LogError instead of silently swallowed.
                catch (Exception e)
                {
                    DebugTAC_AI.LogError(KickStart.ModID + ": KickStartTAC_AI.Init - MainOfficialInit failed: " + e);
                }
                isInit = true;
            }
            else
            {
                KickStart.VALIDATE_MODS();
                SpecialAISpawner.DetermineActiveOnModeType();
                TankAIManager.inst.CorrectBlocksList();
            }
        }
        public override void DeInit()
        {
            KickStart.ShouldBeActive = false;
            if (isInit)
            {
                KickStart.DeInitCheck();
                isInit = false;
            }
        }

        public override void Update()
        {
            if (ManUI.inst.HasInitialised)
                DebugTAC_AI.DoShowWarnings();
        }

    }
#endif
}
