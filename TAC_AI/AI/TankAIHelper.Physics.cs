using UnityEngine;

namespace TAC_AI.AI
{
    // PhysicsInfo service (partial-class split of TankAIHelper, Step 2). Per-tick velocity / dodge-sphere /
    // net-progress sampling. Runs FIRST in the Operations phase (everyone reads recentSpeed/MakingNetProgress/
    // EstTopSped). Writes only helper bus fields, never tank.control. recentSpeed/recentSpeedSigned/EstTopSped/
    // lastTechExtents stay on the core bus (read tech-wide); the computed dodge-sphere + net-progress state live
    // here. The bounds-velocity clamp statics depend on the MaxBoundsVelo const (kept in the core file).
    public partial class TankAIHelper
    {
        private static Vector3 lowMaxBoundsVelo = -new Vector3(MaxBoundsVelo, MaxBoundsVelo, MaxBoundsVelo);
        private static Vector3 highMaxBoundsVelo = new Vector3(MaxBoundsVelo, MaxBoundsVelo, MaxBoundsVelo);

        public Vector3 DodgeSphereCenter { get; private set; } = Vector3.zero;
        public Vector3 SafeVelocity { get; private set; } = Vector3.zero;
        public Vector3 LocalSafeVelocity { get; private set; } = Vector3.zero;
        public float DodgeSphereRadius { get; private set; } = 1;

        // REVISED: windowed net-progress tracker (see AIGlobals.StuckNetProgress*). MakingNetProgress goes false when the
        // tech has barely moved over the last window - lets IsTechMoving*/the unjam soft-decay tell a real wedge (driving
        // hard but pinned) from an in-place pivot or jitter, which the angular-velocity fallback alone could not.
        private Vector3 netProgressLastPos = Vector3.zero;
        private float netProgressNextCheck = 0f;
        public bool MakingNetProgress { get; private set; } = true;
        internal void UpdatePhysicsInfo()
        {
            if (Time.time >= netProgressNextCheck)
            {
                Vector3 curProgressPos = tank.boundsCentreWorldNoCheck;
                if (netProgressNextCheck > 0f)
                {
                    // scale the "made progress" bar by the tech's own top speed so a slow/heavy mover isn't falsely flagged stuck
                    float minProg = Mathf.Max(AIGlobals.StuckNetProgressFloor,
                        EstTopSped * AIGlobals.StuckNetProgressWindow * AIGlobals.StuckNetProgressFraction);
                    MakingNetProgress = (curProgressPos - netProgressLastPos).sqrMagnitude > minProg * minProg;
                }
                netProgressLastPos = curProgressPos;
                netProgressNextCheck = Time.time + AIGlobals.StuckNetProgressWindow;
            }
            if (tank.rbody.IsNotNull())
            {
                var velo = tank.rbody.velocity;
                if (!velo.IsNaN() && !float.IsInfinity(velo.x)
                    && !float.IsInfinity(velo.z) && !float.IsInfinity(velo.y))
                {
                    DodgeSphereCenter = tank.boundsCentreWorldNoCheck + velo.Clamp(lowMaxBoundsVelo, highMaxBoundsVelo);
                    DodgeSphereRadius = lastTechExtents + Mathf.Clamp(recentSpeed / 2f, 1f, 63f);
                    SafeVelocity = velo;
                    LocalSafeVelocity = tank.rootBlockTrans.InverseTransformVector(velo);
                    return;
                }
            }
            DodgeSphereCenter = tank.boundsCentreWorldNoCheck;
            DodgeSphereRadius = lastTechExtents;
            SafeVelocity = Vector3.zero;
            LocalSafeVelocity = Vector3.zero;
        }
    }
}
