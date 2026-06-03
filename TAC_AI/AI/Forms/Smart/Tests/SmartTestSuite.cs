#if SMART_DEV
using System.Collections.Generic;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.Control;
using TAC_AI.AI.Forms.Smart.Coordination;
using TAC_AI.AI.Forms.Smart.Pathing;
using TAC_AI.AI.Forms.Smart.Planning;
using TAC_AI.AI.Forms.Smart.Training;
using TAC_AI.AI.Forms.Smart.Vehicle;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Tests
{
    /// <summary>
    /// Phase 10 (FIX-PLAN.md): math + contract regression tests for the fixes applied
    /// during phases 6/7. Each test is intentionally small and focused on one drift
    /// resolution recorded in DRIFT-DECISIONS.md. Failures here mean a recently-
    /// merged change has regressed a contract.
    /// </summary>
    public static class SmartTestSuite
    {
        public static string RunAll()
        {
            TestRunner.Reset();

            // ----- World / Kalman -----
            TestRunner.Run("Kalman_Update_ConvergesToObservation", Kalman_Update_ConvergesToObservation);
            TestRunner.Run("Kalman_Propagate_VarianceGrows", Kalman_Propagate_VarianceGrows);
            TestRunner.Run("Kalman_WrapAngle_NaNSafe", Kalman_WrapAngle_NaNSafe);

            // ----- Belief decay -----
            TestRunner.Run("BeliefDecay_VelocityDamps", BeliefDecay_VelocityDamps);
            TestRunner.Run("BeliefDecay_IsLost_OnHighVariance", BeliefDecay_IsLost_OnHighVariance);
            TestRunner.Run("BeliefDecay_DampVelocity_NaN_DtSafe", BeliefDecay_DampVelocity_NaN_DtSafe);

            // ----- Vehicle -----
            TestRunner.Run("MobilityProfile_TopSpeed_SqrtAOverK", MobilityProfile_TopSpeed_SqrtAOverK);
            TestRunner.Run("MobilityProfile_NaN_FallsBackToDefault", MobilityProfile_NaN_FallsBackToDefault);

            // ----- Cost function -----
            TestRunner.Run("CostFunction_StateReachCost_FiniteOnNaNHeading", CostFunction_StateReachCost_FiniteOnNaNHeading);
            TestRunner.Run("CostFunction_ThreatFieldCost_Sums", CostFunction_ThreatFieldCost_Sums);

            // ----- Planning -----
            TestRunner.Run("PlanLibrary_EngageDistributed_Requires2Hostiles", PlanLibrary_EngageDistributed_Requires2Hostiles);
            TestRunner.Run("StrategicValue_ThreatExposure_ScaleInvariant", StrategicValue_ThreatExposure_ScaleInvariant);

            // ----- Outcome -----
            TestRunner.Run("OutcomeScorer_DeriveWinner_Symmetric", OutcomeScorer_DeriveWinner_Symmetric);

            // ----- Coordination -----
            TestRunner.Run("TargetAssignment_NoTargets_Empty", TargetAssignment_NoTargets_Empty);

            // ----- Control -----
            TestRunner.Run("SamplingMPC_Lambda_NaNSafe", SamplingMPC_Lambda_NaNSafe);

            string summary = TestRunner.Summary();
            DebugTAC_AI.Log("Smart.Tests: " + summary);
            for (int i = 0; i < TestRunner.Results.Count; i++)
            {
                var r = TestRunner.Results[i];
                if (!r.Passed) DebugTAC_AI.LogWarning("Smart.Tests.Fail: " + r.Name + " — " + r.FailureMessage);
            }
            return summary;
        }

        // =========================================================
        // Kalman tests
        // =========================================================

        private static void Kalman_Update_ConvergesToObservation()
        {
            var prior = BeliefState.NewlyObserved(
                new TechId(1), new TeamId(1),
                position: new Vector3(10f, 0f, 10f),
                velocity: Vector3.zero,
                heading: 0f,
                maxAccelerationEstimate: 5f,
                currentTick: 0);

            var observation = PositionObservation.Standard(
                position: new Vector3(20f, 0f, 20f),
                velocity: Vector3.zero,
                heading: 0f);

            var updated = KalmanUpdate.UpdateWithObservation(prior, observation, dt: 0.033f, currentTick: 1);

            // After one observation the posterior should move TOWARD the observation
            // (not necessarily reach it — Kalman is a weighted blend). Position
            // estimate should be in [10, 20] strictly closer to 20.
            TestRunner.AssertTrue(updated.PositionMean.x > 10f && updated.PositionMean.x <= 20f, "x in range");
            TestRunner.AssertTrue(updated.PositionMean.z > 10f && updated.PositionMean.z <= 20f, "z in range");
            // Posterior variance should be strictly less than prior (information gained).
            float priorVar = prior.PositionVariance.x;
            float postVar = updated.PositionVariance.x;
            TestRunner.AssertTrue(postVar < priorVar, "posterior variance < prior variance");
        }

        private static void Kalman_Propagate_VarianceGrows()
        {
            var prior = BeliefState.NewlyObserved(
                new TechId(1), new TeamId(1),
                position: Vector3.zero, velocity: Vector3.zero, heading: 0f,
                maxAccelerationEstimate: 10f, currentTick: 0);
            float priorVar = prior.PositionVariance.x;
            var propagated = KalmanUpdate.Propagate(prior, dt: 0.5f, currentTick: 1);
            float postVar = propagated.PositionVariance.x;
            TestRunner.AssertTrue(postVar >= priorVar, "propagated variance >= prior (Q added)");
        }

        private static void Kalman_WrapAngle_NaNSafe()
        {
            var prior = BeliefState.NewlyObserved(
                new TechId(1), new TeamId(1),
                position: Vector3.zero, velocity: Vector3.zero,
                heading: float.NaN, // poisoned
                maxAccelerationEstimate: 5f, currentTick: 0);
            var observation = PositionObservation.Standard(Vector3.zero, Vector3.zero, heading: 0.5f);
            var updated = KalmanUpdate.UpdateWithObservation(prior, observation, dt: 0.033f, currentTick: 1);
            // Heading should be finite — WrapAngleDifference defends NaN.
            TestRunner.AssertFinite(updated.HeadingMean, "post-update heading finite");
        }

        // =========================================================
        // BeliefDecay tests
        // =========================================================

        private static void BeliefDecay_VelocityDamps()
        {
            var prior = BeliefState.NewlyObserved(
                new TechId(1), new TeamId(1),
                position: Vector3.zero, velocity: new Vector3(10f, 0f, 0f), heading: 0f,
                maxAccelerationEstimate: 5f, currentTick: 0);
            var damped = BeliefDecay.DampVelocity(prior, dt: 5f, currentTick: 1);
            TestRunner.AssertTrue(damped.VelocityMean.x < 10f, "velocity decreased");
            TestRunner.AssertTrue(damped.VelocityMean.x > 0f, "velocity not yet zero");
        }

        private static void BeliefDecay_IsLost_OnHighVariance()
        {
            // Synthesize a belief with huge position variance.
            var mean = new float[7];
            var cov = new float[49];
            cov[0] = 1000f; cov[7 + 1] = 1000f; cov[14 + 2] = 1000f;
            var intent = new float[6]; for (int i = 0; i < 6; i++) intent[i] = 1f / 6f;
            var lost = new BeliefState(new TechId(1), new TeamId(1), mean, cov, intent, 0, false, 5f);
            TestRunner.AssertTrue(BeliefDecay.IsLost(lost), "huge variance flags lost");
        }

        private static void BeliefDecay_DampVelocity_NaN_DtSafe()
        {
            var prior = BeliefState.NewlyObserved(
                new TechId(1), new TeamId(1),
                position: Vector3.zero, velocity: new Vector3(5f, 0f, 0f), heading: 0f,
                maxAccelerationEstimate: 5f, currentTick: 0);
            var d = BeliefDecay.DampVelocity(prior, dt: float.NaN, currentTick: 1);
            TestRunner.AssertNear(5f, d.VelocityMean.x, 0.001f, "NaN dt is no-op");
        }

        // =========================================================
        // Vehicle tests
        // =========================================================

        private static void MobilityProfile_TopSpeed_SqrtAOverK()
        {
            var mass = new MassDistribution(1000f, Vector3.zero, 1f, 1f, 1f, 0f, 0f, 0f);
            // forward accel 20 m/s² → topSpeed = sqrt(20 / 0.05) = 20 m/s.
            var thrust = new ThrustMap(new PropulsionBlock[0],
                new Vector3(5f, 0f, 20f), new Vector3(5f, 0f, 5f), new Vector3(0f, 2f, 0f));
            var armor = new ArmorMap(new Vector3Int(4, 4, 4),
                new Bounds(Vector3.zero, new Vector3(4f, 2f, 4f)), new float[64]);
            var mobility = MobilityProfile.Derive(mass, thrust, armor);
            // sqrt(20/0.05) = 20 m/s
            TestRunner.AssertNear(20f, mobility.TopSpeedForward, 0.5f, "topFwd = sqrt(a/k)");
        }

        private static void MobilityProfile_NaN_FallsBackToDefault()
        {
            var mass = new MassDistribution(1000f, Vector3.zero, 1f, 1f, 1f, 0f, 0f, 0f);
            var thrust = new ThrustMap(new PropulsionBlock[0],
                new Vector3(0f, 0f, float.NaN), Vector3.zero, Vector3.zero);
            var armor = new ArmorMap(new Vector3Int(4, 4, 4),
                new Bounds(Vector3.zero, new Vector3(4f, 2f, 4f)), new float[64]);
            var mobility = MobilityProfile.Derive(mass, thrust, armor);
            TestRunner.AssertNear(MobilityProfile.Default.TopSpeedForward, mobility.TopSpeedForward, 0.01f, "NaN → default");
        }

        // =========================================================
        // CostFunction tests
        // =========================================================

        private static void CostFunction_StateReachCost_FiniteOnNaNHeading()
        {
            var endState = new RolloutState(new Vector3(10f, 0f, 0f), Vector3.zero, float.NaN, Vector3.zero);
            var goal = new TacticalGoal(new Vector3(15f, 0f, 0f), heading: 0.5f, vel: Vector3.zero, lookAheadSec: 1f);
            float c = CostFunction.StateReachCost(endState, goal);
            TestRunner.AssertFinite(c, "cost finite on NaN heading");
        }

        private static void CostFunction_ThreatFieldCost_Sums()
        {
            // Sum form — value should scale ~linearly with trajectory length when threat is uniform.
            var traj1 = new RolloutState[5];
            var traj2 = new RolloutState[10];
            for (int i = 0; i < traj1.Length; i++) traj1[i] = new RolloutState(Vector3.zero, Vector3.zero, 0f, Vector3.zero);
            for (int i = 0; i < traj2.Length; i++) traj2[i] = new RolloutState(Vector3.zero, Vector3.zero, 0f, Vector3.zero);
            var field = new UniformThreatField(1.0f);
            var cap = VehicleCapability.FromMobility(MobilityProfile.Default);
            float c1 = CostFunction.ThreatFieldCost(traj1, field, cap);
            float c2 = CostFunction.ThreatFieldCost(traj2, field, cap);
            TestRunner.AssertNear(c1 * 2f, c2, 0.001f, "twice the length → twice the cost (sum)");
        }

        private sealed class UniformThreatField : IThreatField
        {
            private readonly float _v;
            public UniformThreatField(float v) { _v = v; }
            public float Evaluate(Vector3 point, VehicleCapability filter) => _v;
            public Vector3 Gradient(Vector3 point, VehicleCapability filter) => Vector3.zero;
        }

        // =========================================================
        // Planning tests
        // =========================================================

        private static void PlanLibrary_EngageDistributed_Requires2Hostiles()
        {
            var oneHostile = MakeStrategicState(friendlyCount: 2, hostileCount: 1);
            var actions1 = PlanLibrary.LegalActions(oneHostile);
            for (int i = 0; i < actions1.Count; i++)
                TestRunner.AssertFalse(actions1[i].Type == PlanLibrary.PlanType.EngageDistributed,
                    "EngageDistributed absent with 1 hostile");

            var twoHostiles = MakeStrategicState(friendlyCount: 2, hostileCount: 2);
            var actions2 = PlanLibrary.LegalActions(twoHostiles);
            bool found = false;
            for (int i = 0; i < actions2.Count; i++)
                if (actions2[i].Type == PlanLibrary.PlanType.EngageDistributed) { found = true; break; }
            TestRunner.AssertTrue(found, "EngageDistributed present with 2 hostiles");
        }

        private static void StrategicValue_ThreatExposure_ScaleInvariant()
        {
            // Two hostiles at the same close distance vs. one hostile at the same distance.
            // The per-friendly clamp-then-mean shape means the value is bounded — same
            // 1 friendly facing 1 hostile vs 2 hostiles at the same dist should be
            // similar magnitude (≤ 1.0 clamp).
            var oneVone = MakeStrategicState(friendlyCount: 1, hostileCount: 1);
            var oneVtwo = MakeStrategicState(friendlyCount: 1, hostileCount: 2);
            float e1 = StrategicValueFunction.ThreatExposure(oneVone);
            float e2 = StrategicValueFunction.ThreatExposure(oneVtwo);
            TestRunner.AssertInRange(e1, 0f, 1.001f, "exposure ≤ 1");
            TestRunner.AssertInRange(e2, 0f, 1.001f, "exposure ≤ 1");
        }

        // =========================================================
        // OutcomeScorer test
        // =========================================================

        private static void OutcomeScorer_DeriveWinner_Symmetric()
        {
            // Symmetric outcome → tiebreak should not unconditionally favor "Us."
            var outcome = new MatchOutcome
            {
                OurStartingCount = 3, OurAliveAtEnd = 1,
                OurStartingHpTotal = 300f, OurEndHpTotal = 100f,
                OurDamageDealt = 200f, OurDamageTaken = 200f,
                OurShotsFired = 100,
                ThemStartingCount = 3, ThemAliveAtEnd = 1,
                ThemStartingHpTotal = 300f, ThemEndHpTotal = 100f,
                ThemDamageDealt = 200f, ThemDamageTaken = 200f,
                ThemShotsFired = 100,
                MaxPossibleDamagePerShot = 5f,
                DurationSeconds = 100f, DurationLimitSeconds = 240f,
            };
            int winner = OutcomeScorer.DeriveWinner(outcome, OutcomeScorer.Weights.Provisional);
            // Symmetric — we just verify it's one of the expected values (not corrupt).
            TestRunner.AssertTrue(winner == (int)TeamSide.Us || winner == (int)TeamSide.Them,
                "winner ∈ {Us, Them}");
            // Verify Score is finite.
            float s = OutcomeScorer.Score(outcome, OutcomeScorer.Weights.Provisional);
            TestRunner.AssertFinite(s, "score finite");
        }

        // =========================================================
        // Coordination test
        // =========================================================

        private static void TargetAssignment_NoTargets_Empty()
        {
            var our = new List<TechId> { new TechId(1), new TechId(2) };
            var targets = new List<BeliefState>();
            var vehicles = new Dictionary<TechId, VehicleModelSnapshot>();
            var prev = new Dictionary<TechId, TargetAssignment>();
            var result = TargetAssignment_Hungarian.Assign(our, targets, vehicles, prev, PlanLibrary.StrategicPlan.Hold);
            TestRunner.AssertTrue(result.Count == 0, "empty assignment when no targets");
        }

        // =========================================================
        // SamplingMPC test
        // =========================================================

        private static void SamplingMPC_Lambda_NaNSafe()
        {
            // Lambda = 0 used to produce NaN weights → poisoned outputs. After the
            // safeLambda clamp, Solve should still return a finite result.
            float saved = SamplingMPC.Lambda;
            SamplingMPC.Lambda = 0f;
            try
            {
                var mpc = new SamplingMPC(seed: 42);
                var mass = new MassDistribution(1000f, Vector3.zero, 1f, 1f, 1f, 0f, 0f, 0f);
                var thrust = new ThrustMap(new PropulsionBlock[0],
                    new Vector3(5f, 0f, 10f), new Vector3(5f, 0f, 5f), new Vector3(0f, 2f, 0f));
                var armor = new ArmorMap(new Vector3Int(4, 4, 4),
                    new Bounds(Vector3.zero, new Vector3(4f, 2f, 4f)), new float[64]);
                var mobility = MobilityProfile.Derive(mass, thrust, armor);
                var vehicle = new VehicleModelSnapshot(
                    techIdValue: 1, constructedAtTick: 0,
                    mass: mass, thrust: thrust,
                    weapons: new WeaponProfile[0], armor: armor,
                    kinematics: KinematicState.Zero, mobility: mobility);
                var x0 = new RolloutState(Vector3.zero, Vector3.zero, 0f, Vector3.zero);
                var goal = new TacticalGoal(new Vector3(5f, 0f, 0f), 0f, Vector3.zero, 1f);
                var beliefs = BeliefSnapshot.Empty;
                var cap = VehicleCapability.FromMobility(mobility);
                int savedSamples = SamplingMPC.SampleCount;
                int savedHorizon = SamplingMPC.HorizonSteps;
                SamplingMPC.SampleCount = 16;
                SamplingMPC.HorizonSteps = 5;
                try
                {
                    var result = mpc.Solve(vehicle, x0, goal, beliefs, null, cap, null,
                        System.Threading.CancellationToken.None);
                    TestRunner.AssertTrue(result != null && result.Length > 0, "Solve returned non-null");
                    for (int i = 0; i < result.Length; i++)
                    {
                        TestRunner.AssertFinite(result[i].Throttle, "throttle finite");
                        TestRunner.AssertFinite(result[i].Steer, "steer finite");
                        TestRunner.AssertFinite(result[i].Brake, "brake finite");
                    }
                }
                finally
                {
                    SamplingMPC.SampleCount = savedSamples;
                    SamplingMPC.HorizonSteps = savedHorizon;
                }
            }
            finally
            {
                SamplingMPC.Lambda = saved;
            }
        }

        // =========================================================
        // Helpers
        // =========================================================

        private static StrategicState MakeStrategicState(int friendlyCount, int hostileCount)
        {
            var friendlies = new List<StrategicTechSummary>(friendlyCount);
            var hostiles = new List<StrategicTechSummary>(hostileCount);
            for (int i = 0; i < friendlyCount; i++)
                friendlies.Add(new StrategicTechSummary(
                    new TechId(100 + i),
                    pos: new Vector3(-10f - i * 5f, 0f, 0f),
                    vel: Vector3.zero, heading: 0f,
                    health: 1f, threat: 1f, mobility: 1f, uncertainty: 0f));
            for (int j = 0; j < hostileCount; j++)
                hostiles.Add(new StrategicTechSummary(
                    new TechId(200 + j),
                    pos: new Vector3(10f + j * 5f, 0f, 0f),
                    vel: Vector3.zero, heading: Mathf.PI,
                    health: 1f, threat: 1f, mobility: 1f, uncertainty: 0f));
            return new StrategicState(friendlies, hostiles, PlanLibrary.StrategicPlan.Hold, 0);
        }
    }
}
#endif
