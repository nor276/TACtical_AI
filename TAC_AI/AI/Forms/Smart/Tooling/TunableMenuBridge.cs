using System.Collections.Generic;
using TAC_AI.AI.Tunables;

namespace TAC_AI.AI.Forms.Smart.Tooling
{
    /// <summary>
    /// P10 (REV 3 fix N3): thin facade over the central <see cref="TunableRegistry"/> that
    /// filters entries with Smart-relevant key prefixes. Used by <see cref="LiveTweakerPanel"/>
    /// and console commands so they don't enumerate the full tunable catalog (~49 entries +
    /// growing) when the user is editing Smart-specific knobs.
    ///
    /// No new TunableRegistry class created — N3 drop avoids namespace collision with the
    /// existing central registry at <c>TAC_AI.AI.Tunables.TunableRegistry</c>. This is just
    /// a read facade.
    /// </summary>
    internal static class TunableMenuBridge
    {
        // Smart-key prefixes Smart tooling cares about (lowercase match against Tunable.Key).
        private static readonly string[] SmartPrefixes =
        {
            "smart.", "aether.", "chassis.", "threading.", "weaponspec.", "armormap.", "los.",
        };

        /// <summary>Enumerable of every Tunable whose key starts with a Smart-relevant prefix.</summary>
        internal static IEnumerable<Tunable> SmartEntries
        {
            get
            {
                var all = TunableRegistry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    var t = all[i];
                    if (t == null || t.Key == null) continue;
                    if (IsSmartKey(t.Key)) yield return t;
                }
            }
        }

        internal static bool IsSmartKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string lowered = key.ToLowerInvariant();
            for (int i = 0; i < SmartPrefixes.Length; i++)
                if (lowered.StartsWith(SmartPrefixes[i])) return true;
            return false;
        }
    }
}
