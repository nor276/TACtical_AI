#if SMART_DEV
using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Training
{
    /// <summary>Match identification for the two teams. The harness assigns roles per match.</summary>
    public enum TeamSide { Us = 0, Them = 1 }

    /// <summary>Match mode dispatched by <see cref="SelfPlayHarness"/>.</summary>
    public enum MatchMode { SelfPlay, BaselineBenchmark }

    /// <summary>
    /// Raw per-side telemetry collected during a match. <see cref="TrainingMatch"/>
    /// produces this; <see cref="OutcomeScorer"/> reduces it to a scalar score.
    /// Per TRAINING-CONTRACT §2.2 + §4.
    /// </summary>
    public sealed class MatchOutcome
    {
        public string ScenarioId;
        public MatchMode Mode;
        public float DurationSeconds;
        public float DurationLimitSeconds;
        public bool Stalemate;

        public int OurStartingCount;
        public int OurAliveAtEnd;
        public float OurStartingHpTotal;
        public float OurEndHpTotal;
        public float OurDamageDealt;
        public float OurDamageTaken;
        public int OurShotsFired;

        public int ThemStartingCount;
        public int ThemAliveAtEnd;
        public float ThemDamageDealt;
        public float ThemDamageTaken;
        // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6: previously DeriveWinner's symmetric-swap
        // zeroed Them HP totals and shots fired, so Them's FriendlyPreservation and
        // WeaponEfficiency terms returned 0 for tiebreak — biasing every score-comparison
        // tiebreak toward Us. Tracking these per-team makes the swap symmetric.
        public float ThemStartingHpTotal;
        public float ThemEndHpTotal;
        public int ThemShotsFired;

        public float MaxPossibleDamagePerShot = 30f; // provisional, used for WeaponEfficiency normalization
    }

    /// <summary>
    /// Multi-objective composite scorer. Per TRAINING-CONTRACT §4.
    ///
    /// score = w_survival * SurvivalFraction
    ///       + w_damage   * DamageRatio          (capped at 5.0)
    ///       + w_time     * TimeEfficiency
    ///       + w_preserve * FriendlyPreservation
    ///       + w_efficiency * WeaponEfficiency
    /// </summary>
    public static class OutcomeScorer
    {
        public const float DamageRatioCap = 5.0f;

        public struct Weights
        {
            public float Survival;
            public float Damage;
            public float Time;
            public float Preserve;
            public float Efficiency;

            public static Weights Provisional => new Weights
            {
                Survival = 5.0f,
                Damage = 2.0f,
                Time = 1.0f,
                Preserve = 2.0f,
                Efficiency = 0.5f,
            };
        }

        public static float Score(MatchOutcome o, Weights w)
        {
            return w.Survival * SurvivalFraction(o)
                 + w.Damage * DamageRatio(o)
                 + w.Time * TimeEfficiency(o)
                 + w.Preserve * FriendlyPreservation(o)
                 + w.Efficiency * WeaponEfficiency(o);
        }

        public static float SurvivalFraction(MatchOutcome o)
        {
            if (o.OurStartingCount <= 0) return 0f;
            return (float)o.OurAliveAtEnd / o.OurStartingCount;
        }

        public static float DamageRatio(MatchOutcome o)
        {
            const float Eps = 1f;
            float r = o.OurDamageDealt / (o.OurDamageTaken + Eps);
            return Mathf.Min(r, DamageRatioCap);
        }

        public static float TimeEfficiency(MatchOutcome o)
        {
            if (o.DurationLimitSeconds <= 0f) return 0f;
            return Mathf.Clamp01(1f - o.DurationSeconds / o.DurationLimitSeconds);
        }

        public static float FriendlyPreservation(MatchOutcome o)
        {
            if (o.OurStartingHpTotal <= 0f) return 0f;
            return Mathf.Clamp01(o.OurEndHpTotal / o.OurStartingHpTotal);
        }

        public static float WeaponEfficiency(MatchOutcome o)
        {
            if (o.OurShotsFired <= 0 || o.MaxPossibleDamagePerShot <= 0f) return 0f;
            float dpsPerShot = o.OurDamageDealt / o.OurShotsFired;
            return Mathf.Clamp01(dpsPerShot / o.MaxPossibleDamagePerShot);
        }

        /// <summary>Per §4.3: binary win/loss derivation for reporting (separate from composite score).</summary>
        public static int DeriveWinner(MatchOutcome o, Weights w)
        {
            if (o.Stalemate) return -1;
            if (o.OurAliveAtEnd > 0 && o.ThemAliveAtEnd == 0) return (int)TeamSide.Us;
            if (o.ThemAliveAtEnd > 0 && o.OurAliveAtEnd == 0) return (int)TeamSide.Them;
            // Both alive at termination → composite-score comparison. We compute "their score"
            // symmetrically by swapping team accumulators.
            float ourScore = Score(o, w);
            // Phase 7 (FIX-PLAN.md) — AUDIT R1 2.6: now populate Them's HP totals and shots
            // fired into the "Our" slots of the swapped outcome so FriendlyPreservation
            // and WeaponEfficiency produce real numbers from Them's perspective. The
            // previous swap zeroed these, silently giving Us a +2.0×FriendlyPreservation
            // and +0.5×WeaponEfficiency bias in every both-alive tiebreak.
            var swapped = new MatchOutcome
            {
                ScenarioId = o.ScenarioId, Mode = o.Mode,
                DurationSeconds = o.DurationSeconds, DurationLimitSeconds = o.DurationLimitSeconds,
                OurStartingCount = o.ThemStartingCount, OurAliveAtEnd = o.ThemAliveAtEnd,
                OurStartingHpTotal = o.ThemStartingHpTotal, OurEndHpTotal = o.ThemEndHpTotal,
                OurDamageDealt = o.ThemDamageDealt, OurDamageTaken = o.ThemDamageTaken,
                OurShotsFired = o.ThemShotsFired,
                MaxPossibleDamagePerShot = o.MaxPossibleDamagePerShot,
                ThemStartingCount = o.OurStartingCount, ThemAliveAtEnd = o.OurAliveAtEnd,
                ThemStartingHpTotal = o.OurStartingHpTotal, ThemEndHpTotal = o.OurEndHpTotal,
                ThemDamageDealt = o.OurDamageDealt, ThemDamageTaken = o.OurDamageTaken,
                ThemShotsFired = o.OurShotsFired,
            };
            float theirScore = Score(swapped, w);
            return ourScore >= theirScore ? (int)TeamSide.Us : (int)TeamSide.Them;
        }
    }
}
#endif
