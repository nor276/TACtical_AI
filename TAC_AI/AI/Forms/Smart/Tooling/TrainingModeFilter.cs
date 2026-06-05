using System;
using System.Threading;
using HarmonyLib;
using TAC_AI.Templates;
using TerraTechETCUtil;

namespace TAC_AI.AI.Forms.Smart.Tooling
{
    /// <summary>
    /// Training-mode tech-spawn filter. Operator opt-in (default OFF). When enabled,
    /// rejects RawTech candidates whose <c>savedTech</c> blueprint string contains any of
    /// <see cref="BannedKeywords"/> (case-insensitive). Designed for AI-training sessions
    /// where the operator wants mixed-range brawler engagements rather than attract-mode's
    /// sniper/missile bias.
    ///
    /// Hook: Harmony postfix on <see cref="RawTechLoader.FilteredSelectFromAll"/>. On
    /// reject, re-rolls up to <see cref="MaxRerollAttempts"/> times by calling the same
    /// method recursively (SearchSingleUse is cleared in its finally so re-entry is safe).
    /// If every re-roll fails, falls through to the last attempt — better to spawn a
    /// banned tech than to spawn nothing.
    ///
    /// Flag values are static fields so console + in-game tunable can mutate live. Default
    /// keyword set covers TerraTech's long-range / missile / artillery blocks across all
    /// corps. Operator can extend via <see cref="AddKeyword"/>.
    /// </summary>
    public static class TrainingModeFilter
    {
        public static volatile bool Enabled = false;
        public const int MaxRerollAttempts = 5;

        // Default keyword blocklist — case-insensitive substring match against RawTech.savedTech.
        // Covers the common long-range / heavy-ordnance blocks across all corps without
        // banning brawler-tier weapons (cannons, lasers, machine guns, ion cannons are fine).
        private static readonly string[] _defaultKeywords =
        {
            "missile",    // GSO_Missile_*, HE_*Missile*, BF_Missile_*, VEN_Missile_*
            "artillery",  // BF / GC artillery pieces
            "rail",       // EXP Railgun + railcannon variants
            "torpedo",    // Submarine torpedoes
            "mortar",     // VEN_Mortar_*, BF mortars
            "cruise",     // Cruise missile variants
            "sniper",     // Sniper rifle / sniper cannon
        };
        private static readonly System.Collections.Generic.List<string> _keywords
            = new System.Collections.Generic.List<string>(_defaultKeywords);
        private static readonly object _keywordsLock = new object();

        // Counters for the status command. `internal` so the postfix class can bump them.
        internal static long _candidatesEvaluated;
        internal static long _candidatesRejected;
        internal static long _rerollsExhausted;

        public static long CandidatesEvaluated => Interlocked.Read(ref _candidatesEvaluated);
        public static long CandidatesRejected => Interlocked.Read(ref _candidatesRejected);
        public static long RerollsExhausted => Interlocked.Read(ref _rerollsExhausted);

        public static void ResetCounters()
        {
            Interlocked.Exchange(ref _candidatesEvaluated, 0L);
            Interlocked.Exchange(ref _candidatesRejected, 0L);
            Interlocked.Exchange(ref _rerollsExhausted, 0L);
        }

        public static System.Collections.Generic.IReadOnlyList<string> Keywords
        {
            get { lock (_keywordsLock) return _keywords.ToArray(); }
        }

        public static void AddKeyword(string kw)
        {
            if (string.IsNullOrEmpty(kw)) return;
            lock (_keywordsLock)
            {
                foreach (var existing in _keywords)
                    if (string.Equals(existing, kw, StringComparison.OrdinalIgnoreCase)) return;
                _keywords.Add(kw);
            }
        }

        public static bool RemoveKeyword(string kw)
        {
            if (string.IsNullOrEmpty(kw)) return false;
            lock (_keywordsLock)
            {
                for (int i = 0; i < _keywords.Count; i++)
                {
                    if (string.Equals(_keywords[i], kw, StringComparison.OrdinalIgnoreCase))
                    {
                        _keywords.RemoveAt(i);
                        return true;
                    }
                }
            }
            return false;
        }

        public static void ResetKeywords()
        {
            lock (_keywordsLock)
            {
                _keywords.Clear();
                _keywords.AddRange(_defaultKeywords);
            }
        }

        /// <summary>
        /// Returns true when the candidate passes the filter (allowed to spawn). Null
        /// candidates pass through (caller's NullIfErrorTech path handles them).
        ///
        /// Walks <see cref="RawTech.savedTech"/> (a List&lt;RawBlockMem&gt; where
        /// <c>.t</c> is the block-name string per AIERepair.cs:189 + :344 usage). A tech
        /// is rejected when ANY of its blocks' name contains ANY of the banned keywords
        /// (case-insensitive substring match).
        /// </summary>
        public static bool IsAllowed(RawTech rt)
        {
            if (rt == null) return true;
            var blocks = rt.savedTech;
            if (blocks == null || blocks.Count == 0) return true;
            string[] snapshot;
            lock (_keywordsLock) snapshot = _keywords.ToArray();
            for (int b = 0; b < blocks.Count; b++)
            {
                string name = blocks[b].t;
                if (string.IsNullOrEmpty(name)) continue;
                for (int k = 0; k < snapshot.Length; k++)
                {
                    if (name.IndexOf(snapshot[k], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Harmony postfix on <see cref="RawTechLoader.FilteredSelectFromAll"/>. Re-rolls
    /// when TrainingMode is enabled and the picked candidate contains banned keywords.
    /// </summary>
    [HarmonyPatch(typeof(RawTechLoader), "FilteredSelectFromAll")]
    internal static class TrainingModeFilter_FilteredSelectFromAll_Postfix
    {
        // Recursion guard — postfix on a method that internally calls itself (we re-call
        // FilteredSelectFromAll inside the postfix for re-roll) would loop forever. The
        // [ThreadStatic] depth counter caps re-entry at MaxRerollAttempts; deeper calls
        // see a non-zero depth and short-circuit to the original behavior.
        [ThreadStatic] private static int _depth;

        // Per-postfix counter to count rerolls (matches MaxRerollAttempts).
        static void Postfix(ref RawTech __result, RawTechPopParams filter, bool handleFallback, bool nullIfErrorTech)
        {
            if (!TrainingModeFilter.Enabled) return;
            if (__result == null) return;
            if (_depth > 0) return;   // we are inside a re-roll — don't recurse further

            Interlocked.Increment(ref TrainingModeFilter._candidatesEvaluated);
            if (TrainingModeFilter.IsAllowed(__result)) return;

            // Reject + re-roll.
            _depth = 1;
            try
            {
                for (int attempt = 0; attempt < TrainingModeFilter.MaxRerollAttempts; attempt++)
                {
                    Interlocked.Increment(ref TrainingModeFilter._candidatesRejected);
                    var retry = RawTechLoader.FilteredSelectFromAll(filter, handleFallback, nullIfErrorTech);
                    if (retry == null) { __result = retry; return; }
                    Interlocked.Increment(ref TrainingModeFilter._candidatesEvaluated);
                    if (TrainingModeFilter.IsAllowed(retry))
                    {
                        __result = retry;
                        return;
                    }
                    __result = retry;   // keep the latest attempt as fallback if every retry fails
                }
                // Fell through — every re-roll failed. Log + keep the last attempt so the
                // spawn pipeline gets SOMETHING rather than null.
                Interlocked.Increment(ref TrainingModeFilter._rerollsExhausted);
                DebugTAC_AI.LogWarnFileOnly("training-mode-rerolls-exhausted",
                    "[TRAINING-MODE] " + TrainingModeFilter.MaxRerollAttempts
                    + " re-rolls exhausted for faction=" + filter.Faction + " terrain=" + filter.Terrain
                    + " — keeping last candidate (will violate filter)");
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("TrainingModeFilter postfix threw: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _depth = 0;
            }
        }

    }
}
