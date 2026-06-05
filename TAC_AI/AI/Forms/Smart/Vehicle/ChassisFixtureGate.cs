using System.Threading;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Vehicle
{
    /// <summary>
    /// Chassis Step 3.5: HARD GATE diagnostic. Runs once per session on the first
    /// non-trivial probed tech (any tech with at least one BoosterImpulse or FanJet
    /// emitter -- the geometry-sensitive cases). Logs the four rotation read patterns
    /// + the probe-vs-production frame-equivalence assertion per CHASSIS-DESIGN.md
    /// Step 3.5 (REV 3.1 5th bullet).
    ///
    /// All emissions are file-only via DebugTAC_AI.LogWarnFileOnly (key=[CHASSIS-FIXTURE])
    /// so the gate never pops a popup. A failed assertion still surfaces to the dev log
    /// but does NOT terminate -- v0.1 ships max-rated even if the gate disagrees by &lt;1%,
    /// so the gate is observational, not enforcement.
    /// </summary>
    public static class ChassisFixtureGate
    {
        /// <summary>
        /// P10 (REV 7): when true, a fixture failure (geometry mismatch or unexpected
        /// frame-equivalence delta) throws instead of logging file-only. Default false
        /// preserves v0.1 observational-only behavior; SmartTestSuite flips this true
        /// for the test-suite invocation so failures surface to the test runner.
        /// </summary>
        public static bool AssertOnFailure = false;

        private static int _hasRun;     // 0 = pending, 1 = done

        /// <summary>
        /// Call from ChassisCapture (Step 4) AFTER a tech has been probed and the
        /// pose array is built. No-op after the first interesting tech in the session.
        /// </summary>
        public static void TryEmitOnce(Tank tank, TypedBlockCatalog catalog, BlockInstancePose[] poses)
        {
            if (Interlocked.CompareExchange(ref _hasRun, 1, 0) != 0) return;
            if (tank == null || catalog == null || poses == null || poses.Length == 0) { _hasRun = 0; return; }

            try
            {
                EmitFixture(tank, catalog, poses);
            }
            catch (System.Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("[CHASSIS-FIXTURE]:exception",
                    "ChassisFixtureGate threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EmitFixture(Tank tank, TypedBlockCatalog catalog, BlockInstancePose[] poses)
        {
            // Find the first pose whose archetype has any BoosterImpulse or FanJet emitter.
            // If none, keep the gate pending (reset _hasRun) so a later tech triggers it.
            int targetIdx = -1;
            ThrustEmitter target = default(ThrustEmitter);
            BlockArchetype archetype = null;
            for (int i = 0; i < poses.Length; i++)
            {
                var a = catalog.TryGet(poses[i].TypeKey);
                if (a == null || a.Emitters == null) continue;
                for (int e = 0; e < a.Emitters.Length; e++)
                {
                    var em = a.Emitters[e];
                    if (em.Kind == EmitterKind.BoosterImpulse || em.Kind == EmitterKind.FanJet)
                    {
                        targetIdx = i; target = em; archetype = a;
                        break;
                    }
                }
                if (targetIdx >= 0) break;
            }

            if (targetIdx < 0)
            {
                // No geometry-sensitive emitters; defer gate to a later tech.
                Interlocked.Exchange(ref _hasRun, 0);
                return;
            }

            var pose = poses[targetIdx];
            Vector3 composed = pose.LocalRotation * target.LocalAxis;

            // Build the production-aggregator equivalent by finding the live block + child.
            // Walk the tank's blocks looking for one whose BlockType matches the pose's TypeKey
            // AND has a matching BoosterJet/FanJet child whose stored LocalAxis matches.
            Vector3? direct = TryReadProductionDirect(tank, pose.TypeKey, target.Kind);

            // P11 T1 Item 48: compute the frame-equivalence delta + fail condition.
            // Step-3.5 invariant: composed and direct should match within 1e-4 — beyond that,
            // the chassis pose math has drifted vs the production InverseTransformDirection
            // path and downstream consumers (ThrustField aggregation, MPC rollout) will see
            // mis-oriented thrust vectors.
            const float FixtureToleranceMeters = 1e-4f;
            float dist = direct.HasValue ? Vector3.Distance(composed, direct.Value) : 0f;
            bool failed = direct.HasValue && dist > FixtureToleranceMeters;

            DebugTAC_AI.LogWarnFileOnly("[CHASSIS-FIXTURE]:hardgate",
                (failed ? "[FIXTURE-FAIL] " : "") + "ChassisFixtureGate fired on tech '" + (tank.name ?? "<null>")
                + "' typeKey=" + pose.TypeKey
                + " emitterKind=" + target.Kind
                + " specMass=" + archetype.SpecMass.ToString("F2")
                + "\n  pose.LocalRotation = " + FormatQuat(pose.LocalRotation)
                + "\n  emitter.LocalAxis (block-local push direction) = " + FormatVec(target.LocalAxis)
                + "\n  composed = pose.LocalRotation * emitter.LocalAxis = " + FormatVec(composed)
                + "\n  direct (production-style InverseTransformDirection of child world-direction) = "
                  + (direct.HasValue ? FormatVec(direct.Value) : "<unable-to-locate-live-child>")
                + (direct.HasValue
                    ? ("\n  Vector3.Distance(composed, direct) = "
                       + dist.ToString("F6")
                       + " (expected < " + FixtureToleranceMeters.ToString("F0e-04") + " per Step 3.5 invariant)")
                    : "\n  (frame-equivalence skipped -- live child not located on this tech instance)"));

            // P11 T1 Item 48: the AssertOnFailure flag's consumer. When the fixture fails
            // (geometry mismatch > tolerance) AND the flag is true, throw so the failure
            // surfaces to the test runner. Default-off keeps v0.1 observational behavior.
            if (failed && AssertOnFailure)
            {
                throw new System.InvalidOperationException(
                    "ChassisFixtureGate frame-equivalence FAILED on tech '" + (tank.name ?? "<null>")
                    + "' typeKey=" + pose.TypeKey + " delta=" + dist.ToString("F6")
                    + " (tolerance " + FixtureToleranceMeters.ToString("F0e-04") + "). "
                    + "AssertOnFailure=true — pose-math drift vs production frame.");
            }
        }

        private static Vector3? TryReadProductionDirect(Tank tank, int typeKey, EmitterKind kind)
        {
            try
            {
                foreach (TankBlock block in tank.blockman.IterateBlocks())
                {
                    if (block == null) continue;
                    if ((int)block.BlockType != typeKey) continue;

                    if (kind == EmitterKind.BoosterImpulse)
                    {
                        foreach (BoosterJet boost in block.transform.GetComponentsInChildren<BoosterJet>(true))
                        {
                            if (boost == null) continue;
                            // Production pattern: rootBlockTrans.InverseTransformDirection(boost.transform.TransformDirection(boost.LocalThrustDirection))
                            if (tank.rootBlockTrans == null) return null;
                            return tank.rootBlockTrans.InverseTransformDirection(
                                boost.transform.TransformDirection(boost.LocalThrustDirection));
                        }
                    }
                    else if (kind == EmitterKind.FanJet)
                    {
                        foreach (FanJet jet in block.transform.GetComponentsInChildren<FanJet>(true))
                        {
                            if (jet == null) continue;
                            if (tank.rootBlockTrans == null) return null;
                            return tank.rootBlockTrans.InverseTransformDirection(
                                jet.transform.TransformDirection(jet.LocalThrustDirection));
                        }
                    }
                }
            }
            catch { /* swallow; gate is observational */ }
            return null;
        }

        private static string FormatVec(Vector3 v)
        {
            return "(" + v.x.ToString("F4") + ", " + v.y.ToString("F4") + ", " + v.z.ToString("F4") + ")";
        }
        private static string FormatQuat(Quaternion q)
        {
            return "(" + q.x.ToString("F4") + ", " + q.y.ToString("F4") + ", "
                 + q.z.ToString("F4") + ", " + q.w.ToString("F4") + ")";
        }

        /// <summary>P11 T1: re-arm so the next geometry-sensitive tech retriggers the gate.</summary>
        internal static void Reset()
        {
            Interlocked.Exchange(ref _hasRun, 0);
        }
    }
}
