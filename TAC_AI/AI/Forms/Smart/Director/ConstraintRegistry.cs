using System;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// Kind of constraint metric source — drives ConstraintEngine dispatch.
    /// </summary>
    public enum ConstraintKind : byte
    {
        ModelEntropyRatio = 0,       // intent.entropy_ratio
        ModelLossVarianceRatio = 1,  // actionvalue_loss_variance_ratio
        ModelParamDeltaL2 = 2,       // threat_param_delta_l2_norm
        IdentityRatePerMin = 3,      // hunter_kills_per_min, gatherer_delivery_rate
        IdentityWeightedRate = 4,    // ally_protected_weighted_rate
        BaseHpRetention = 5,         // base_hp_retention_rate (BaseHeld / (Held+Lost))
        LiveSmartTechFloor = 6,      // live_smart_tech_floor
        DamageEventsPerMin = 7,      // damage_events_per_min
        TtlDiscardRate = 8,          // ttl_discard_rate
        SubNeutralRelationsIntact = 9, // subneutral_relations_intact
        GuardBucketRate = 10,        // movement_pathology_rate / role_task_pathology_rate / combat_pathology_rate
        GuardInterventionRate = 11,  // grounded_aircraft_intervention_rate
    }

    /// <summary>
    /// Band shape — most constraints use Min/Max; some are one-sided (just a ceiling).
    /// </summary>
    public enum BandShape : byte
    {
        TwoSided = 0,
        MaxOnly = 1,
        MinOnly = 2,
    }

    /// <summary>
    /// Constraint health classification — drives DirectorJournal entries + ActionPicker.
    /// </summary>
    public enum ConstraintHealth : byte
    {
        Healthy = 0,
        Violated = 1,
        InsufficientData = 2,
        WarmupSilent = 3,
    }

    /// <summary>
    /// One constraint row. Static struct; all 15 entries live in <see cref="ConstraintRegistry.All"/>.
    /// </summary>
    public readonly struct Constraint
    {
        public readonly string Name;
        public readonly ConstraintKind Kind;
        public readonly BandShape Shape;
        public readonly float BandMin;
        public readonly float BandMax;
        public readonly ModelId Model;            // unused for non-model constraints
        public readonly SmartIdentity Identity;   // unused for non-identity constraints
        public readonly OutcomeKind OutcomeKindRef; // for identity-rate / guard-bucket constraints
        public readonly int WindowSec;
        // Severity weight from §12.1 — multiplied by deviation/band_width for ActionPicker.
        public readonly float DependsOnPublisherWeight;
        // Name of the verb the picker WOULD have invoked. Phase 2 emits this to the journal
        // instead of actually firing the verb. Phase 3+ wires verbs.
        public readonly string PrimaryVerb;

        public Constraint(string name, ConstraintKind kind, BandShape shape, float bandMin, float bandMax,
            ModelId model, SmartIdentity identity, OutcomeKind outcomeKind, int windowSec,
            float dependsOnPublisherWeight, string primaryVerb)
        {
            Name = name;
            Kind = kind;
            Shape = shape;
            BandMin = bandMin;
            BandMax = bandMax;
            Model = model;
            Identity = identity;
            OutcomeKindRef = outcomeKind;
            WindowSec = windowSec;
            DependsOnPublisherWeight = dependsOnPublisherWeight;
            PrimaryVerb = primaryVerb;
        }
    }

    /// <summary>
    /// Static array of 15 Constraint rows. 11 baseline §4 + 4 guard-bucket. Guard-bucket
    /// metrics return INSUFFICIENT_DATA in Phase 2 (no guard publishers yet); the rows
    /// SHIP NOW so Phase 5+6+8 just emit events into them.
    /// </summary>
    public static class ConstraintRegistry
    {
        public const int ConstraintCount = 15;
        public const int ColdStartWarmupSec = 60;
        // Silence every constraint for this long after a scenario_set/respawn so the world
        // can populate and combat develop before the Director judges (and respawns) it.
        public const int ScenarioWarmupSec = 60;

        public static readonly Constraint[] All = new Constraint[ConstraintCount]
        {
            // 1. intent_entropy_ratio — band [0.30, 0.85]
            new Constraint("intent_entropy_ratio", ConstraintKind.ModelEntropyRatio, BandShape.TwoSided,
                0.30f, 0.85f, ModelId.Intent, SmartIdentity.Generic, OutcomeKind.KillScored,
                60, 1.0f, "temp_adjust"),

            // 2. actionvalue_loss_variance_ratio — max 3.0
            new Constraint("actionvalue_loss_variance_ratio", ConstraintKind.ModelLossVarianceRatio, BandShape.MaxOnly,
                0f, 3.0f, ModelId.ActionValue, SmartIdentity.Generic, OutcomeKind.KillScored,
                60, 1.0f, "lr_scale"),

            // 3. threat_param_delta_l2_norm — p99 < 0.01
            new Constraint("threat_param_delta_l2_norm", ConstraintKind.ModelParamDeltaL2, BandShape.MaxOnly,
                0f, 0.01f, ModelId.ThreatAssessment, SmartIdentity.Generic, OutcomeKind.KillScored,
                60, 1.0f, "lr_scale"),

            // 4. hunter_kills_per_min — min 0.5/min
            new Constraint("hunter_kills_per_min", ConstraintKind.IdentityRatePerMin, BandShape.MinOnly,
                0.5f, float.PositiveInfinity, ModelId.Intent, SmartIdentity.Hunter, OutcomeKind.KillScored,
                300, 0.9f, "replay_bank"),

            // 5. gatherer_delivery_rate — min 0.5/min
            new Constraint("gatherer_delivery_rate", ConstraintKind.IdentityRatePerMin, BandShape.MinOnly,
                0.5f, float.PositiveInfinity, ModelId.ActionValue, SmartIdentity.Gatherer, OutcomeKind.BlockDelivered,
                300, 0.85f, "replay_bank"),

            // 6. base_hp_retention_rate — EWMA > 0.6 (treated as min)
            new Constraint("base_hp_retention_rate", ConstraintKind.BaseHpRetention, BandShape.MinOnly,
                0.6f, float.PositiveInfinity, ModelId.ThreatAssessment, SmartIdentity.Base, OutcomeKind.BaseHeld,
                60, 0.9f, "replay_bank"),

            // 7. ally_protected_weighted_rate — min 0.10/min/instance
            new Constraint("ally_protected_weighted_rate", ConstraintKind.IdentityWeightedRate, BandShape.MinOnly,
                0.10f, float.PositiveInfinity, ModelId.ActionValue, SmartIdentity.AircraftSupport, OutcomeKind.AllyProtected,
                300, 0.8f, "replay_bank"),

            // 8. live_smart_tech_floor — at least 4 (gated on ActiveScenario)
            new Constraint("live_smart_tech_floor", ConstraintKind.LiveSmartTechFloor, BandShape.MinOnly,
                4f, float.PositiveInfinity, ModelId.Intent, SmartIdentity.Generic, OutcomeKind.KillScored,
                10, 0.7f, "scenario_respawn"),

            // 9. damage_events_per_min — at least 5/min (engaged scenarios only)
            new Constraint("damage_events_per_min", ConstraintKind.DamageEventsPerMin, BandShape.MinOnly,
                5f, float.PositiveInfinity, ModelId.Intent, SmartIdentity.Generic, OutcomeKind.KillScored,
                60, 0.7f, "scenario_respawn"),

            // 10. ttl_discard_rate — max 1000/10min (diagnostic only — no verb)
            new Constraint("ttl_discard_rate", ConstraintKind.TtlDiscardRate, BandShape.MaxOnly,
                0f, 1000f, ModelId.Intent, SmartIdentity.Generic, OutcomeKind.KillScored,
                600, 0.3f, "diagnostic"),

            // 11. subneutral_relations_intact — count must be 0 (treated as max=0)
            new Constraint("subneutral_relations_intact", ConstraintKind.SubNeutralRelationsIntact, BandShape.MaxOnly,
                0f, 0f, ModelId.Intent, SmartIdentity.Generic, OutcomeKind.KillScored,
                10, 0.6f, "scenario_respawn"),

            // 12. movement_pathology_rate — Phase 5 fills (guards do not exist yet)
            new Constraint("movement_pathology_rate", ConstraintKind.GuardBucketRate, BandShape.MaxOnly,
                0f, 0.10f, ModelId.ThreatAssessment, SmartIdentity.Generic, OutcomeKind.KillScored,
                300, 0.8f, "diagnostic"),

            // 13. role_task_pathology_rate — Phase 5 fills
            new Constraint("role_task_pathology_rate", ConstraintKind.GuardBucketRate, BandShape.MaxOnly,
                0f, 0.12f, ModelId.ActionValue, SmartIdentity.Generic, OutcomeKind.KillScored,
                300, 0.8f, "replay_bank"),

            // 14. combat_pathology_rate — Phase 5 fills
            new Constraint("combat_pathology_rate", ConstraintKind.GuardBucketRate, BandShape.MaxOnly,
                0f, 0.10f, ModelId.ThreatAssessment, SmartIdentity.Generic, OutcomeKind.KillScored,
                300, 0.8f, "replay_bank"),

            // 15. grounded_aircraft_intervention_rate — Phase 5 fills
            new Constraint("grounded_aircraft_intervention_rate", ConstraintKind.GuardInterventionRate, BandShape.MaxOnly,
                0f, 2f, ModelId.Intent, SmartIdentity.AircraftHunter, OutcomeKind.KillScored,
                600, 0.5f, "diagnostic"),
        };
    }
}
