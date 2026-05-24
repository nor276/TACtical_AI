using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TAC_AI.Templates;
using TerraTechETCUtil;

namespace TAC_AI
{
    public class CursorChanger : MonoBehaviour
    {

        public static CursorChangeHelper.CursorChangeCache Cache;
        public static bool AddedNewCursors = false;
        public static CursorChangeHelper.CursorChangeCache CursorIndexCache => Cache.CursorIndexCache;

        public static void AddNewCursors()
        {
            if (AddedNewCursors)
                return;
            if (ResourcesHelper.TryGetModContainer("Advanced AI", out ModContainer MC))
            {
                Cache = CursorChangeHelper.GetCursorChangeCache(RawTechExporter.DLLDirectory, "AI_Icons", MC,
                    new KeyValuePair<string, bool>("AIOrderAttack", false),
                    new KeyValuePair<string, bool>("AIOrderEmpty", false),
                    new KeyValuePair<string, bool>("AIOrderMove", false),
                    new KeyValuePair<string, bool>("AIOrderSelect", false),
                    new KeyValuePair<string, bool>("AIOrderBlock", false),
                    new KeyValuePair<string, bool>("AIOrderMine", false),
                    new KeyValuePair<string, bool>("AIOrderAegis", false),
                    new KeyValuePair<string, bool>("AIOrderScout", false)
                    );
            }
            else
                DebugTAC_AI.Assert(true, "CursorChanger: AddNewCursors - Could not find ModContainer for Advanced AI!");

            AddedNewCursors = true;
        }
    }
}
