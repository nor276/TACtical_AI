using TAC_AI.AI.Forms.Smart.Identity;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Pathology severity. Numeric order is load-bearing - chronic-suppression and digest
    // sorting key on it.
    public enum GuardSeverity : byte
    {
        Observe = 0,
        Warn = 1,
        SoftCorrect = 2,
        HardCorrect = 3,
    }

    // Edge-transition verdict for one guard tick. Fired==true on the rising edge of the
    // pathology test (NOT every tick the test is true). ExceptionMatched short-circuits
    // when one of the sane-exception conditions matched — distinguishes "test failed"
    // from "test held but the tech is doing something legitimate". EvidenceFields is a
    // free-form short string for digest attribution; guards keep it terse.
    public struct GuardVerdict
    {
        public bool Fired;
        public bool ExceptionMatched;
        public byte ExceptionId;
        public float Magnitude;
        public string EvidenceFields;
    }

    public interface IBehaviorGuard
    {
        string Name { get; }
        GuardSeverity Severity { get; }
        int WindowSec { get; }
        // Identity gate — guard skipped when context identity is not in this set. Null
        // or empty means "any identity".
        SmartIdentity[] AppliesToIdentities { get; }
        // Scenario gate — guard skipped when ActiveScenario is not in this set. Null or
        // empty means "any scenario".
        int[] AppliesToScenarios { get; }

        // Cheap gate run before any data is touched. Identity + scenario + helper-state
        // sanity check. Return false to short-circuit the tick.
        bool ShouldEvaluate(GuardContext ctx);

        // Pathology evaluation. Fills the rolling window from ctx then computes the
        // verdict. May return Fired=false even when the window indicates pathology if a
        // sane exception matched — guards must populate ExceptionMatched in that case so
        // GuardTelemetry can record the suppression.
        GuardVerdict Evaluate(GuardContext ctx);
    }
}
