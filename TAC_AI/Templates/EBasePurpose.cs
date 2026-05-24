using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TAC_AI.Templates
{
    class EBasePurpose
    {
    }
    public enum BaseTypeLevel
    {
        Basic,
        Advanced,
        Headquarters,
        Overkill,
        InvaderSpecific,
    }
    public enum BaseTerrain
    {
        Any,
        AnyNonSea,
        Land,
        Sea,
        Air,
        Chopper,
        Space
    }
    public enum BasePurpose
    {
        AnyNonHQ,
        HarvestingNoHQ,
        Defense,
        Harvesting,
        Autominer,
        TechProduction,
        Headquarters,
        MPUnsafe,
        HasReceivers,
        NotStationary,
        AttractTech,
        NoWeapons,
        Fallback,
        Sniper,
        NANI,
    }
}
