#if SMART_DEV
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Tests
{
    /// <summary>
    /// Tiny BCL-only test harness. Phase 10 (FIX-PLAN.md). No external dependencies
    /// (NUnit/MSTest deliberately avoided per project constraint). Tests are gated
    /// under SMART_DEV; in shipping builds the entire Tests namespace compiles out.
    ///
    /// Usage: <see cref="SmartTestSuite.RunAll"/> from <see cref="SmartTrainingDriver"/>'s
    /// dev UI. Each test method is a plain static method registered in the suite;
    /// failures are logged with stack traces but never abort the suite.
    /// </summary>
    public static class TestRunner
    {
        public sealed class Result
        {
            public string Name;
            public bool Passed;
            public string FailureMessage;
        }

        public static int PassCount { get; private set; }
        public static int FailCount { get; private set; }
        public static int SkipCount { get; private set; }
        public static List<Result> Results { get; } = new List<Result>();

        // P10 (REV 7): control-flow exception type used by Skip(). Wraps a reason; the
        // runner converts it to a SKIPPED result instead of a FAILED one.
        internal sealed class SkippedException : Exception
        {
            public SkippedException(string reason) : base(reason) { }
        }

        public static void Reset()
        {
            PassCount = 0;
            FailCount = 0;
            SkipCount = 0;
            Results.Clear();
        }

        public static void Run(string name, Action body)
        {
            var r = new Result { Name = name };
            try
            {
                body();
                r.Passed = true;
                PassCount++;
            }
            catch (SkippedException sx)
            {
                r.Passed = false;
                r.FailureMessage = "SKIPPED: " + sx.Message;
                SkipCount++;
            }
            catch (Exception ex)
            {
                r.Passed = false;
                r.FailureMessage = ex.GetType().Name + ": " + ex.Message;
                FailCount++;
            }
            Results.Add(r);
        }

        /// <summary>P10 (REV 7): one-arg assert convenience. Throws when condition is false.</summary>
        public static void Assert(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Assert failed: " + label);
        }

        /// <summary>P10 (REV 7): skip the current test with a reason — runner records as SKIPPED.</summary>
        public static void Skip(string reason)
        {
            throw new SkippedException(reason ?? "(no reason)");
        }

        /// <summary>Property-form access for callers that prefer it; same content as the method form.</summary>
        public static string Summary
            => "Smart.Tests: " + PassCount + " passed, " + FailCount + " failed, " + SkipCount + " skipped";

        // Method form kept for back-compat with any caller using SummaryAsString().
        public static string SummaryAsString() => Summary;

        // --- Assertions ---

        public static void AssertTrue(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("AssertTrue failed: " + label);
        }

        public static void AssertFalse(bool condition, string label)
        {
            if (condition) throw new InvalidOperationException("AssertFalse failed: " + label);
        }

        public static void AssertNear(float expected, float actual, float tolerance, string label)
        {
            if (float.IsNaN(actual) || float.IsInfinity(actual))
                throw new InvalidOperationException("AssertNear failed (NaN/Inf): " + label + " actual=" + actual);
            float diff = Mathf.Abs(expected - actual);
            if (diff > tolerance)
                throw new InvalidOperationException(
                    "AssertNear failed: " + label + " expected=" + expected + " actual=" + actual + " diff=" + diff + " tol=" + tolerance);
        }

        public static void AssertFinite(float v, string label)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                throw new InvalidOperationException("AssertFinite failed: " + label + " value=" + v);
        }

        public static void AssertInRange(float v, float min, float max, string label)
        {
            if (float.IsNaN(v) || v < min || v > max)
                throw new InvalidOperationException("AssertInRange failed: " + label + " value=" + v + " range=[" + min + "," + max + "]");
        }
    }
}
#endif
