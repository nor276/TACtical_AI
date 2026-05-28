using UnityEngine;
using TerraTechETCUtil;

namespace TAC_AI.AI.Movement
{
    /// <summary>
    /// SHELL world-geometry queries (v2). The handful of terrain height / water functions that WORLD + SPAWNING code
    /// needs (base placement, raw-tech spawn terrain classification, attract). Extracted out of the form-owned
    /// AIEPathing so spawning never depends on a form's pathfinding. These read the game's terrain directly
    /// (ManWorld.TileManager.GetTerrainHeightAtPosition) rather than the form's per-chunk pathing cache - identical
    /// for AboveTheSeaForcedAccurate (which was already raw) and equivalent for loaded tiles on the rest (spawn checks
    /// run on loaded ground). The form keeps its own cached AIEPathing.* for the per-frame AI; only these world-side
    /// callers use TerrainQuery.
    /// </summary>
    public static class TerrainQuery
    {
        private static float Ground(Vector3 scenePos)
        {
            return ManWorld.inst.TileManager.GetTerrainHeightAtPosition(scenePos, out _);
        }

        public static bool AboveTheSea(Vector3 posScene)
        {
            if (!KickStart.isWaterModPresent)
                return false;
            return Ground(posScene) < KickStart.WaterHeight;
        }

        public static bool AboveTheSeaForcedAccurate(Vector3 posScene)
        {
            if (!KickStart.isWaterModPresent)
                return false;
            return Ground(posScene) < KickStart.WaterHeight;
        }

        public static bool AboveHeightFromGround(Vector3 posScene, float groundOffset = 50)
        {
            float height = Ground(posScene);
            float final_y = height + groundOffset;
            if (KickStart.isWaterModPresent && KickStart.WaterHeight > height)
                final_y = KickStart.WaterHeight + groundOffset;
            return posScene.y > final_y;
        }

        public static Vector3 OffsetFromGroundAAlt(Vector3 input, float groundOffset = 35)
        {
            float height = Ground(input);
            float final_y = height + groundOffset;
            if (KickStart.isWaterModPresent && KickStart.WaterHeight > height)
                final_y = KickStart.WaterHeight + groundOffset;
            Vector3 final = input;
            if (input.y < final_y)
                final.y = final_y;
            return final;
        }

        public static Vector3 SnapOffsetToSea(Vector3 input)
        {   // Lowest ground or sea
            Vector3 final = input;
            final.y = KickStart.WaterHeight;
            float height = Ground(input);
            if (height > final.y)
                final.y = height;
            return final;
        }

        // v2 isolation: world-obstruction queries the shell's RunRTSNaviEnemy needs - moved off the form's AIEPathing.
        // Any/SetPieceAny are pure ManVisible/ManWorld (no pathing cache -> behavior-identical to the form copy);
        // Terrain reads raw terrain instead of the form's cached GetHighestAltInRadius (single-point raw sample - the
        // accepted Tier-2 cached->raw delta, only on the enemy RTS-air nav path).
        public static bool ObstructionAwarenessAny(Vector3 posWorld, float radius)
        {
            try
            {
                foreach (Visible vis in Singleton.Manager<ManVisible>.inst.VisiblesTouchingRadius(posWorld, radius, AIGlobals.sceneryBitMask))
                {
                    if (vis.resdisp.IsNotNull() && vis.isActive)
                        return true;
                }
            }
            catch { }
            return false;
        }
        public static bool ObstructionAwarenessSetPieceAny(Vector3 posScene, bool isAnchored, float radius)
        {
            if (!isAnchored && ManWorld.inst.GetSetPiecePlacement().Count > 0)
            {
                if (ManWorld.inst.CheckIfInsideSceneryBlocker(SceneryBlocker.BlockMode.Spawn, posScene, radius))
                    return true;
            }
            return false;
        }
        public static bool ObstructionAwarenessTerrain(Vector3 posScene, bool isAnchored, float radius)
        {
            if (!isAnchored)
            {
                float height = Ground(posScene);
                if (height > posScene.y - radius)
                    return true;
            }
            return false;
        }
    }
}
