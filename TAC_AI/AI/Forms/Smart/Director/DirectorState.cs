using System.Collections.Concurrent;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Director
{
    // Verb taxonomy (distinct from DirectiveKind below). The shape the parser produces.
    public enum DirectiveVerb : byte
    {
        Maintain = 0, Prefer = 1, Force = 2, Freeze = 3, LrScale = 4, ReplayBank = 5,
        Rollback = 6, Checkpoint = 7, Forbid = 8, Rescind = 9, Clear = 10,
        Digest = 11, Status = 12, Calibrate = 13,
    }

    // Value tagging — C# 7.3 has no DU so this is a tagged struct.
    public enum ValueKind : byte
    {
        None = 0, Scalar = 1, Interval = 2, Rate = 3, Label = 4,
    }

    public readonly struct DirectiveValue
    {
        public readonly ValueKind Kind;
        public readonly float Scalar;
        public readonly float IntervalLo;
        public readonly float IntervalHi;
        public readonly string Label;
        public readonly float RateNumerator;
        public readonly string RateUnit;

        public DirectiveValue(ValueKind kind, float scalar, float lo, float hi, string label,
            float rateNum, string rateUnit)
        {
            Kind = kind; Scalar = scalar; IntervalLo = lo; IntervalHi = hi;
            Label = label; RateNumerator = rateNum; RateUnit = rateUnit;
        }

        public static DirectiveValue OfScalar(float s)
            => new DirectiveValue(ValueKind.Scalar, s, 0f, 0f, null, 0f, null);
        public static DirectiveValue OfInterval(float lo, float hi)
            => new DirectiveValue(ValueKind.Interval, 0f, lo, hi, null, 0f, null);
        public static DirectiveValue OfRate(float num, string unit)
            => new DirectiveValue(ValueKind.Rate, 0f, 0f, 0f, null, num, unit);
        public static DirectiveValue OfLabel(string label)
            => new DirectiveValue(ValueKind.Label, 0f, 0f, 0f, label, 0f, null);
        public static DirectiveValue None_ => new DirectiveValue(ValueKind.None, 0f, 0f, 0f, null, 0f, null);
    }

    public enum DirectiveSource : byte { Op = 0, LLM = 1, Auto = 2 }

    // Comparison operator for maintain-band constraints.
    public enum DirectiveOp : byte { None = 0, Lt = 1, Gt = 2, Le = 3, Ge = 4, Eq = 5, In = 6 }

    // A parsed operator directive. Persisted to directives.json + mirror-journaled.
    public readonly struct Directive
    {
        public readonly int Id;
        public readonly string Tag;
        public readonly DirectiveVerb Verb;
        public readonly string Subject;
        public readonly DirectiveOp Op;
        public readonly DirectiveValue Value;
        // Lifetime: ExpiresMono != 0 -> for <duration>; UntilExpr != null -> until <expr>;
        // else sticky.
        public readonly long CreatedMono;
        public readonly long ExpiresMono;
        public readonly string UntilExpr;
        public readonly DirectiveSource Src;
        public readonly string Description;

        public Directive(int id, string tag, DirectiveVerb verb, string subject, DirectiveOp op,
            DirectiveValue value, long createdMono, long expiresMono, string untilExpr,
            DirectiveSource src, string description)
        {
            Id = id; Tag = tag; Verb = verb; Subject = subject; Op = op; Value = value;
            CreatedMono = createdMono; ExpiresMono = expiresMono; UntilExpr = untilExpr;
            Src = src; Description = description;
        }

        public bool IsSticky => ExpiresMono == 0L && string.IsNullOrEmpty(UntilExpr);
    }

    /// <summary>
    /// Static singleton holding Director runtime state. Cleared on world reset and
    /// repopulated as scenarios spin up. Guard slots live on TankAIHelper.GuardInjectedGoals,
    /// NOT here.
    /// </summary>
    public static class DirectorState
    {
        public const int JournalRingCap = 2048;

        // Active scenario id. 0 = none.
        public static volatile int ActiveScenario;

        // Operator-driven scenario intensity.
        public static volatile float Intensity = 1f;

        // Operator directives keyed by id. Phase 9 (DirectiveSurface) writes; nothing
        // reads yet.
        public static readonly ConcurrentDictionary<int, Directive> DirectiveTable
            = new ConcurrentDictionary<int, Directive>();

        // RAM journal ring. Producer enqueues here AND fire-and-forgets to the disk
        // worker; cap-hit drops oldest silently (disk has them). 2048 entries.
        public static readonly ConcurrentQueue<JournalEntry> JournalRingInMem
            = new ConcurrentQueue<JournalEntry>();

        // Atomic ref-swap from DigestBuilder; main-thread OnGUI reads as immutable
        // string ref. Volatile string is legal under ECMA-335 (unlike volatile long).
        public static volatile string LatestDigest;

        // Phase 2 owns the content shape; ship the dictionary now so the type is
        // available. Packed key = (constraintId<<32) | severityBucket; byte count.
        public static readonly ConcurrentDictionary<long, byte> ViolationCounters
            = new ConcurrentDictionary<long, byte>();

        // Scenario ownership of teams + techs. Replaces the prior Tank.name-suffix
        // tagging. Phase 2 ScenarioWorker writes.
        public static readonly ConcurrentDictionary<int, byte> ScenarioOwnedTeams
            = new ConcurrentDictionary<int, byte>();
        public static readonly ConcurrentDictionary<int, byte> ScenarioOwnedTechs
            = new ConcurrentDictionary<int, byte>();

        // Stamped onto envelopes to invalidate in-flight stale spawns post-rollback.
        // Interlocked.Increment in Phase 2 scenario actions; no callers yet.
        public static int ScenarioGeneration;

        /// <summary>Reset everything on world reset.</summary>
        public static void Clear()
        {
            ActiveScenario = 0;
            Intensity = 1f;
            DirectiveTable.Clear();
            // Drain the journal ring without losing thread-safety.
            JournalEntry _;
            while (JournalRingInMem.TryDequeue(out _)) { }
            LatestDigest = null;
            ViolationCounters.Clear();
            ScenarioOwnedTeams.Clear();
            ScenarioOwnedTechs.Clear();
            Interlocked.Exchange(ref ScenarioGeneration, 0);
            DirectorParamRing.Clear();
        }

        /// <summary>Push to ring with oldest-drop trim semantics.</summary>
        public static void EnqueueJournal(JournalEntry entry)
        {
            JournalRingInMem.Enqueue(entry);
            // Trim to cap; ConcurrentQueue has no Count cap, so dequeue oldest until
            // approximate count drops. Approximate is fine — multiple writers may run
            // this trim concurrently; worst case the ring sits a touch under cap.
            while (JournalRingInMem.Count > JournalRingCap)
            {
                JournalEntry drop;
                if (!JournalRingInMem.TryDequeue(out drop)) break;
            }
        }
    }
}
