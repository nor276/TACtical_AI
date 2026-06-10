using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.Director.Guards;
using TAC_AI.AI.Forms.Smart.Identity;

namespace TAC_AI.AI.Forms.Smart.Director
{
    // Per-model row in the digest. One per ModelId.
    public struct ModelStat
    {
        public float LossMean;
        public float LossVarOverMeanRewardNorm;
        public float EntropyRatio;
        public float DL2P99;
        public float LrNow;
        public float StepsPerMin;
        public bool Frozen;
        public bool HasEntropy;       // Intent only
        public bool HasDL2;           // ActionValue / Threat only
        public bool HasLossVar;
    }

    // One identity-rate line. Only populated cells are rendered.
    public struct RatePayload
    {
        public string Label;          // e.g. "kills/min", "deliv/min", "hp_retain_ewma"
        public float Value;
        public int SampleCount;       // -1 when n/a
        public int BankFullness;      // -1 when n/a
        public int HeldCount;         // base-hp only; -1 otherwise
        public int LostCount;
    }

    public struct GuardRate
    {
        public float RatePerMin;
        public char SeverityTag;      // 'o' observe, 'W' warn, 's' soft, 'h' hard
        public long FiresInWindow;
        public bool BucketViolated;
    }

    public struct SystemStats
    {
        public int Techs;
        public float DamagePerMin;
        public long TtlDiscardPer10Min;
        public string HealthySnapAge;
        public int AllyProtectEvictPerMin;
    }

    public struct ViolationLine
    {
        public string Name;
        public float Metric;
        public string Band;
        public int ConsecutiveTicks;
    }

    public struct InterventionLine
    {
        public string Timestamp;
        public string Verb;
        public string Cause;
        public string JournalRef;
    }

    public struct DirectiveLine
    {
        public int Id;
        public string Tag;
        public string Text;
        public string Age;
    }

    public struct RollbackAvailability
    {
        public bool MemoryAvailable;
        public string MemoryRef;
        public bool PreviousAvailable;
        public bool PenultimateAvailable;
        public string NamedList;
    }

    // The digest snapshot. Both text + JSON projections derive PURELY from this — neither
    // may read DirectorState. Built once per emission by DigestBuilder.BuildSnapshot.
    public sealed class DigestSnapshot
    {
        public long WallclockUtcSec;
        public long ProcessUptimeMonoSec;
        public string Trigger;
        public int Host;

        public int Scenario;
        public float Intensity;
        public int ScenarioAgeSec;
        public int ChunkLive;
        public int ChunkTarget;
        public int TechDef;
        public bool SubNeutralOk;

        public ModelStat[] ModelStats;   // length 4 (Intent, ActionValue, Residual, Threat)
        public Dictionary<SmartIdentity, RatePayload> IdentityRates;
        public Dictionary<SmartIdentity, Dictionary<GuardId, GuardRate>> GuardRates;

        // Guard rollup line.
        public long PathologyTotal;
        public int DistinctTechs;
        public int MaxPerTech;
        public int P90PerTech;
        public long SoftCorrects;
        public long HardCorrects;
        public long GroundedAircraftFires;
        public long GroundedAircraftReleases;
        public long Oscill;

        public SystemStats System;
        public List<ViolationLine> ConstraintViolations;
        public int ConstraintOkCount;
        public List<InterventionLine> Interventions;
        public List<DirectiveLine> DirectivesActive;
        public RollbackAvailability Rollback;
        public string HintLine;

        public DigestSnapshot()
        {
            ModelStats = new ModelStat[4];
            IdentityRates = new Dictionary<SmartIdentity, RatePayload>();
            GuardRates = new Dictionary<SmartIdentity, Dictionary<GuardId, GuardRate>>();
            ConstraintViolations = new List<ViolationLine>();
            Interventions = new List<InterventionLine>();
            DirectivesActive = new List<DirectiveLine>();
        }
    }
}
