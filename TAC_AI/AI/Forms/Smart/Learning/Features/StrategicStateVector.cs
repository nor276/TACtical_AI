using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning.Features
{
    // Shared per-tech feature vector — one source of truth for slot layout across the
    // four Smart learned models (Intent / ActionValue / Residual / Threat). Per
    // FEATURE-EXPANSION-PLAN.md §3. Single backing float[] (Raw) laid out as four
    // contiguous views; per-view slot constants are view-local and compose with the
    // matching ViewBase at the call site.
    //
    // Immutability discipline: Raw is `public readonly float[]` — the field is non-
    // reassignable but ELEMENTS are mutable. The extractor must never touch a Raw[]
    // after StrategicStateBuffer.Write; readers must never write. The §4.2 NaN/Inf
    // scrub runs BEFORE atomic publish — last edit on the to-be-published instance.
    public sealed class StrategicStateVector
    {
        // Total slot count = MaxSlots. Layout:
        //   [Intent 0..39][ActionValue 40..167][Residual 168..215][Threat 216..263]
        public const int MaxSlots = 264;

        public const int IntentBase      = 0;       // [0..39]
        public const int ActionValueBase = 40;      // [40..167]
        public const int ResidualBase    = 168;     // [168..215]
        public const int ThreatBase      = 216;     // [216..263]

        // Per-view extents.
        public const int IntentDim       = 40;      // per-timestep, 30 timesteps total
        public const int IntentSeqLen    = 30;
        public const int ActionValueDim  = 128;
        public const int ResidualDim     = 48;
        public const int ThreatDim       = 48;

        public readonly float[] Raw;                // length == MaxSlots; row-major

        public readonly long PublishedTickMono;     // MonoClock at build
        public readonly TechId Subject;             // which tech this vector describes
        public readonly TeamId SubjectTeam;
        public readonly int Generation;             // monotonic; trips on lifecycle reset

        public StrategicStateVector(TechId subject, TeamId team, long publishedTickMono, int generation)
        {
            Subject = subject;
            SubjectTeam = team;
            PublishedTickMono = publishedTickMono;
            Generation = generation;
            Raw = new float[MaxSlots];
        }

        // NaN/Inf scrub — mirrors ContinuousController.cs:250-255. The daemon's LAST
        // edit before atomic publish. Zeros non-finite entries; the read side then
        // sees only finite values.
        public void ScrubNonFinite()
        {
            for (int i = 0; i < Raw.Length; i++)
            {
                float v = Raw[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) Raw[i] = 0f;
            }
        }

        // View-local slot accessors — call-site reads compose with the matching
        // ViewBase so the model file never owns its own offset constants.
        public float ReadIntent(int slot) { return Raw[IntentBase + slot]; }
        public float ReadActionValue(int slot) { return Raw[ActionValueBase + slot]; }
        public float ReadResidual(int slot) { return Raw[ResidualBase + slot]; }
        public float ReadThreat(int slot) { return Raw[ThreatBase + slot]; }
    }

    // Per-timestep slots for OpponentIntentClassifier (SeqLen=30, FeatureDim=40).
    // Per FEATURE-EXPANSION-PLAN.md §3.2. All indices are VIEW-LOCAL; call sites
    // address Raw[IntentBase + IntentSlots.X].
    public static class IntentSlots
    {
        // Kinematic vs target.
        public const int RelPosX                  = 0;
        public const int RelPosZ                  = 1;
        public const int RelHeadingSin            = 2;
        public const int RelHeadingCos            = 3;
        public const int RelVelX                  = 4;
        public const int RelVelZ                  = 5;
        public const int TargetSpeed              = 6;
        public const int TargetAgeSec             = 7;
        public const int TargetUncertaintyM       = 8;
        public const int TargetSightCode          = 9;
        public const int Distance                 = 10;
        public const int LosBlocked               = 11;
        public const int TargetTeamSignedDelta    = 12;
        public const int TargetIsHostile          = 13;

        // Health / composition.
        public const int SelfHp                   = 14;
        public const int TargetHp                 = 15;
        public const int SelfTotalWeapons         = 16;
        public const int TargetTotalWeapons       = 17;
        public const int SelfMass                 = 18;
        public const int TargetMassRatio          = 19;

        // Recent-damage windows.
        public const int RecentDamageDealtRate    = 20;
        public const int RecentDamageTakenRate    = 21;
        public const int RecentDamageDealtType    = 22;
        public const int RecentDamageTakenType    = 23;

        // Anchor + terrain.
        public const int SelfAnchored             = 24;
        public const int TargetAnchored           = 25;
        public const int RelSlopeAtSelf           = 26;
        public const int RelSlopeAtTarget         = 27;

        // Weapon aggregate.
        public const int SelfWeaponFireRateMean   = 28;
        public const int TargetWeaponFireRateMean = 29;
        public const int SelfWeaponRangeMax       = 30;
        public const int TargetWeaponRangeMax     = 31;
        public const int RecentShotsFiredRate     = 32;
        public const int CargoStateCode           = 33;

        // World context.
        public const int TimeOfDayCosine          = 34;
        public const int ProvokedFlag             = 35;

        // Reserved — mission-state ×2, shield-charge ×2 (~10% headroom).
        public const int ReservedMissionA         = 36;
        public const int ReservedMissionB         = 37;
        public const int ReservedShieldA          = 38;
        public const int ReservedShieldB          = 39;
    }

    // ActionValueEstimator (StateDim=128). 40 self + 40 target + 10 action one-hot
    // + 38 reserved. Per FEATURE-EXPANSION-PLAN.md §3.3. View-local indices.
    public static class ActionValueSlots
    {
        // Self kinematic [0..9].
        public const int SelfPosX                  = 0;
        public const int SelfPosZ                  = 1;
        public const int SelfHeadingSin            = 2;
        public const int SelfHeadingCos            = 3;
        public const int SelfVelMag                = 4;
        public const int SelfVelHeadingSin         = 5;
        public const int SelfVelHeadingCos         = 6;
        public const int SelfSlopeUnder            = 7;
        public const int SelfHeightAboveTerrain    = 8;
        public const int SelfWaterloggedFrac       = 9;

        // Self vehicle [10..19].
        public const int SelfMass                  = 10;
        public const int SelfBlockCount            = 11;
        public const int SelfWeaponCount           = 12;
        public const int SelfMeleeFraction         = 13;
        public const int SelfWeaponRangeMax        = 14;
        public const int SelfWeaponFireRateMean    = 15;
        public const int SelfWeaponDamagePerShotMean = 16;
        public const int SelfWeaponMuzzleVelMean   = 17;
        public const int SelfHpFraction            = 18;
        public const int SelfReadyFireFraction     = 19;

        // Self energy + anchor + cargo [20..27].
        public const int SelfElectricStored        = 20;   // currentAmount/10000
        public const int SelfElectricSpareCapacity = 21;
        public const int SelfElectricNetFlow       = 22;   // (currentAmount - previousAmount)/1000
        public const int SelfIsGeneratingFraction  = 23;
        public const int SelfAnchorState           = 24;
        public const int SelfCargoFill             = 25;
        public const int SelfCargoValue            = 26;
        public const int SelfCargoBlockCount       = 27;

        // Self armor + damage [28..35].
        public const int SelfWeakFaceForwardDot    = 28;
        public const int SelfWeakFaceHp            = 29;
        public const int SelfRecentDamageTaken1s   = 30;
        public const int SelfRecentDamageTaken5s   = 31;
        public const int SelfRecentDamageDealt1s   = 32;
        public const int SelfRecentDamageDealt5s   = 33;
        public const int SelfLastAttackerDistance  = 34;
        public const int SelfLastAttackerAge       = 35;

        // Self misc [36..39].
        public const int SelfProvokedFlag          = 36;
        public const int SelfMaxAccelEstimate      = 37;
        public const int SelfSpeedAtObserve        = 38;
        public const int SelfRoleHintCode          = 39;

        // Target block — same shape as self 0..39, offset +40. [40..79]
        public const int TargetPosX                  = 40;
        public const int TargetPosZ                  = 41;
        public const int TargetHeadingSin            = 42;
        public const int TargetHeadingCos            = 43;
        public const int TargetVelMag                = 44;
        public const int TargetVelHeadingSin         = 45;
        public const int TargetVelHeadingCos         = 46;
        public const int TargetSlopeUnder            = 47;
        public const int TargetHeightAboveTerrain    = 48;
        public const int TargetWaterloggedFrac       = 49;

        public const int TargetMass                  = 50;
        public const int TargetBlockCount            = 51;
        public const int TargetWeaponCount           = 52;
        public const int TargetMeleeFraction         = 53;
        public const int TargetWeaponRangeMax        = 54;
        public const int TargetWeaponFireRateMean    = 55;
        public const int TargetWeaponDamagePerShotMean = 56;
        public const int TargetWeaponMuzzleVelMean   = 57;
        public const int TargetHpFraction            = 58;
        public const int TargetReadyFireFraction     = 59;

        public const int TargetElectricStored        = 60;
        public const int TargetElectricSpareCapacity = 61;
        public const int TargetElectricNetFlow       = 62;
        public const int TargetIsGeneratingFraction  = 63;
        public const int TargetAnchorState           = 64;
        public const int TargetCargoFill             = 65;
        public const int TargetCargoValue            = 66;
        public const int TargetCargoBlockCount       = 67;

        public const int TargetWeakFaceForwardDot    = 68;
        public const int TargetWeakFaceHp            = 69;
        public const int TargetRecentDamageTaken1s   = 70;
        public const int TargetRecentDamageTaken5s   = 71;
        public const int TargetRecentDamageDealt1s   = 72;
        public const int TargetRecentDamageDealt5s   = 73;
        public const int TargetLastAttackerDistance  = 74;
        public const int TargetLastAttackerAge       = 75;

        public const int TargetProvokedFlag          = 76;
        public const int TargetMaxAccelEstimate      = 77;
        public const int TargetSpeedAtObserve        = 78;
        public const int TargetRoleHintCode          = 79;

        // Action one-hot [80..89] — 10 goal classes (3 precedence sources × goal kind).
        public const int ActionOneHotBase            = 80;
        public const int ActionOneHotCount           = 10;

        // Reserved [90..127] — 38 slots ≈ 30% headroom.
        //   90..99   reserved-mission-state    (×10)
        //   100..109 reserved-shield-charge    (×10)
        //   110..119 reserved-multi-target     (×10)
        //   120..127 reserved-coord-handoff    (×8)
        public const int ReservedMissionBase         = 90;
        public const int ReservedShieldBase          = 100;
        public const int ReservedMultiTargetBase     = 110;
        public const int ReservedCoordHandoffBase    = 120;
    }

    // TrajectoryResidualModel (FeatureDim=48). Inputs only — label
    // (ObservedResidual Vector3) lives on the separate ResidualEvent field and is
    // NEVER embedded inside Features[] (label-leakage guard). Per FEATURE-EXPANSION-PLAN.md
    // §3.4. View-local indices.
    public static class ResidualSlots
    {
        // Projection geometry [0..7].
        public const int DistAtFire              = 0;
        public const int ProjTimeOfFlight        = 1;
        public const int ElapsedSecSinceFire     = 2;
        public const int LosUnitVecX             = 3;
        public const int LosUnitVecY             = 4;
        public const int LosUnitVecZ             = 5;
        public const int LosBlockedAtFire        = 6;
        public const int TargetSightCode         = 7;

        // Shooter kinematics [8..15].
        public const int ShooterSpeed            = 8;
        public const int ShooterHeadingSin       = 9;
        public const int ShooterHeadingCos       = 10;
        public const int ShooterAngVelZ          = 11;
        public const int ShooterAccelMag         = 12;
        public const int ShooterSlopeUnder       = 13;
        public const int ShooterHeightAboveTerrain = 14;
        public const int ShooterAnchored         = 15;

        // Target kinematics [16..23].
        public const int TargetSpeed             = 16;
        public const int TargetHeadingSin        = 17;
        public const int TargetHeadingCos        = 18;
        public const int TargetAccelEstMag       = 19;
        public const int TargetAge               = 20;
        public const int TargetUncertaintyM      = 21;
        public const int TargetSlopeUnder        = 22;
        public const int TargetHeightAboveTerrain = 23;

        // Weapon spec at fire [24..31].
        public const int WeaponMuzzleVel         = 24;
        public const int WeaponRange             = 25;
        public const int WeaponYawArcRad         = 26;
        public const int WeaponPitchArcRad       = 27;
        public const int WeaponIsEnergy          = 28;
        public const int WeaponFireRateHz        = 29;
        public const int WeaponDamagePerShot     = 30;
        public const int WeaponKindCode          = 31;

        // Input-only cached extrap [32..34] — predicted impact point at fire.
        // INPUT FEATURE; the OBSERVED residual is the separate label.
        public const int LinearExtrapX           = 32;
        public const int LinearExtrapY           = 33;
        public const int LinearExtrapZ           = 34;

        // Reserved [35..47] — 13 slots ≈ 27% headroom.
        //   35..37 reserved-projectile-physics (×3)
        //   38..40 reserved-wind-state         (×3)
        //   41..47 reserved-multi-shooter      (×7)
        public const int ReservedProjPhysicsBase = 35;
        public const int ReservedWindBase        = 38;
        public const int ReservedMultiShooterBase = 41;

        // Number of input dims the LeadResidualRecorder fire-time callsite reads
        // [0..34]; reserved [35..47] stay zero on forward pass.
        public const int InputDimsAtFire         = 35;
    }

    // ThreatAssessmentModel (FeatureDim=48). Per FEATURE-EXPANSION-PLAN.md §3.5.
    // View-local indices.
    public static class ThreatSlots
    {
        // Attacker composition [0..7].
        public const int AttackerBlockCount      = 0;
        public const int AttackerMass            = 1;
        public const int AttackerWeaponCount     = 2;
        public const int AttackerMeleeFraction   = 3;
        public const int AttackerHpFraction      = 4;
        public const int AttackerAnchorState     = 5;
        public const int AttackerRoleHintCode    = 6;
        public const int AttackerProvokedFlag    = 7;

        // Attacker weapon aggregate [8..15].
        public const int AttackerWeaponRangeMax        = 8;
        public const int AttackerWeaponRangeMean       = 9;
        public const int AttackerWeaponFireRateMean    = 10;
        public const int AttackerWeaponDamagePerShotMean = 11;
        public const int AttackerWeaponMuzzleVelMean   = 12;
        public const int AttackerEnergyWeaponFraction  = 13;
        public const int AttackerWeaponKindMixGun      = 14;
        public const int AttackerWeaponKindMixBeamMelee = 15;

        // Attacker kinematic [16..23].
        public const int AttackerSpeed              = 16;
        public const int AttackerMaxAccelEstimate   = 17;
        public const int AttackerSlopeUnder         = 18;
        public const int AttackerHeightAboveTerrain = 19;
        public const int AttackerWaterloggedFrac    = 20;
        public const int AttackerCargoFill          = 21;
        public const int AttackerReadyFireFraction  = 22;
        public const int AttackerEnergyStored       = 23;

        // Engagement geometry [24..31].
        public const int DistanceToVictim                = 24;
        public const int AttackerHeadingTowardVictimDot  = 25;
        public const int VictimWeakFaceTowardAttackerDot = 26;
        public const int LosBlockedToVictim              = 27;
        public const int AttackerWeaponInRange           = 28;
        public const int AttackerAimableFlag             = 29;
        public const int VictimWeakFaceHp                = 30;
        public const int VictimHpFraction                = 31;

        // Recent-damage window [32..37]. Slots 30, 31, 36, 37 are victim-side;
        // 32..35 are attacker-side. Slot 35 = the DAMAGE TYPE the attacker dealt.
        public const int RecentDamageDealt1s         = 32;
        public const int RecentDamageDealt5s         = 33;
        public const int RecentShotsFired1s          = 34;
        public const int RecentDamageDealtTypeCode   = 35;
        public const int RecentDamageTakenByVictim1s = 36;
        public const int RecentDamageTakenByVictim5s = 37;

        // Reserved [38..47] — 10 slots ≈ 21% headroom.
        //   38..41 reserved-mission-state    (×4)
        //   42..44 reserved-team-context     (×3)
        //   45..47 reserved-shield-charge    (×3)
        public const int ReservedMissionBase       = 38;
        public const int ReservedTeamContextBase   = 42;
        public const int ReservedShieldBase        = 45;
    }
}
