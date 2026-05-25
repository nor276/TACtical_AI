using System;

namespace TAC_AI.AI.Tunables
{
    public enum TunableKind { Float, Int, Bool }

    /// <summary>
    /// When a changed value may be acted upon. Classified per-knob so the UI never applies a value
    /// at an unsafe time (mid-tick integer-truncation, or a host-only world mutation on a client).
    /// </summary>
    public enum TunableApplyMode
    {
        /// <summary>Cadence / integer-timestep-sensitive (AIClockPeriod). Read only on reload.</summary>
        BootOnly,
        /// <summary>Safe to read fresh each frame / push to subscribers live.</summary>
        LiveSafe,
        /// <summary>Mutates vanilla Globals/ManPop/ManTechs or re-inits a subsystem - host only, with a DeInit restore.</summary>
        WorldAffectingHostOnly,
    }

    /// <summary>
    /// Declare-once descriptor for a single tunable value. The registry OWNS the value (Step 6 binds it
    /// to a persisted backing field). menuVisible/menuLabel are pure code switches that curate the in-game
    /// menu without ever touching the value, its key, or its readers. ReadSites documents who consumes it
    /// (the no-orphan accounting) so the UI can mark a knob global vs per-profile honestly.
    /// </summary>
    public sealed class Tunable
    {
        public readonly string Key;          // e.g. "Combat.ReverseInnerFraction"
        public readonly string Category;     // e.g. "Combat"
        public readonly TunableKind Kind;
        public TunableApplyMode ApplyMode;

        // In-game menu curation - one-line switches, never affect value/persistence/readers.
        public bool MenuVisible = true;
        public string MenuLabel;             // null => derive from Key

        // Bounds (used by both UI and clamping). Float/Int share these as needed.
        public float Min, Max, Step;
        public Func<float, string> Display;  // value -> menu string

        // Side-effecting apply, fired ONLY when the value actually changed (per-descriptor dirty-tracking).
        public Action OnApply;
        // Symmetric restore for any value that mutates vanilla state (WorldAffectingHostOnly).
        public Action OnDeInit;

        // Who reads this knob (file:purpose). Filled at declaration; drives the no-orphan map.
        public string[] ReadSites;

        // Backing value (registry-owned). Defaults captured at registration.
        internal float DefaultFloat;
        internal int DefaultInt;
        internal bool DefaultBool;
        internal float CurFloat;
        internal int CurInt;
        internal bool CurBool;

        // Optional binding to a mutable static field (e.g. an AIGlobals knob). Kept in sync on register + on
        // every change, so existing readers of that static field see tuned values without being repointed.
        internal Action<float> BindFloat;
        internal Action<int> BindInt;
        internal Action<bool> BindBool;

        internal Tunable(string key, string category, TunableKind kind)
        {
            Key = key;
            Category = category;
            Kind = kind;
        }

        public string ResolvedLabel => MenuLabel ?? Key;
    }

    // O(1) typed handles. A behavior/strategy resolves a handle ONCE (at install) then reads per-frame
    // with no string lookup. The handle holds the Tunable ref directly.
    public struct FloatHandle
    {
        internal readonly Tunable T;
        internal FloatHandle(Tunable t) { T = t; }
        public float Value => T.CurFloat;
        public bool IsValid => T != null;
    }
    public struct IntHandle
    {
        internal readonly Tunable T;
        internal IntHandle(Tunable t) { T = t; }
        public int Value => T.CurInt;
        public bool IsValid => T != null;
    }
    public struct BoolHandle
    {
        internal readonly Tunable T;
        internal BoolHandle(Tunable t) { T = t; }
        public bool Value => T.CurBool;
        public bool IsValid => T != null;
    }
}
