using System;
using System.Collections.Concurrent;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P7 Item 21: per-identity outcome consumer. Subscribes to
    /// <c>WorldEventBus&lt;IdentityOutcome&gt;</c> and routes events into per-identity
    /// counters for learning telemetry. v0.2 ship: counter aggregation only (no actual
    /// stratified training fork — per the plan, "Generic+KillScored is a no-op stratifier").
    /// Counter values feed the [TIMING]/diagnostic log periodically.
    ///
    /// Generic-classified techs now appear in IdentityOutcome routing per P6 Item 27's
    /// Generic registration. Distortion to per-identity learning signals from this is
    /// documented as a v0.2 acceptance — consumers that want pre-v0.2 semantics can filter
    /// `evt.Identity == SmartIdentity.Generic` explicitly.
    ///
    /// Lifecycle: <see cref="Install"/> from LearningService.Init; <see cref="Uninstall"/>
    /// from LearningService.Shutdown. Subscription delegate held in a static field so GC
    /// doesn't collect it between Install and Uninstall.
    /// </summary>
    public sealed class IdentityOutcomeConsumer
    {
        private static Action<IdentityOutcome> _subscription;
        private static IdentityOutcomeConsumer _wiredInstance;

        private readonly ConcurrentDictionary<long, long> _counters =
            new ConcurrentDictionary<long, long>();

        public static void Install(IdentityOutcomeConsumer inst)
        {
            if (_subscription != null) WorldEventBus.Unsubscribe(_subscription);
            _wiredInstance = inst;
            _subscription = OnOutcomeStatic;
            WorldEventBus.Subscribe(_subscription);
        }

        public static void Uninstall()
        {
            if (_subscription != null) WorldEventBus.Unsubscribe(_subscription);
            _subscription = null;
            _wiredInstance = null;
        }

        // Static trampoline so the captured delegate identity is stable for Unsubscribe.
        private static void OnOutcomeStatic(IdentityOutcome evt)
        {
            var inst = _wiredInstance;
            if (inst == null) return;
            inst.OnOutcome(evt);
        }

        /// <summary>
        /// Increment the per-(identity, outcome-kind) counter. Pack the two byte enums
        /// into a long so we can use ConcurrentDictionary's atomic AddOrUpdate. Magnitude
        /// is currently not consumed — v0.3 stratifier could weight counters by magnitude.
        /// </summary>
        public void OnOutcome(IdentityOutcome evt)
        {
            long key = ((long)(byte)evt.Identity << 8) | (long)(byte)evt.Kind;
            _counters.AddOrUpdate(key, 1L, (_, c) => c + 1L);
        }

        public long GetCount(SmartIdentity identity, OutcomeKind kind)
        {
            long key = ((long)(byte)identity << 8) | (long)(byte)kind;
            long c;
            return _counters.TryGetValue(key, out c) ? c : 0L;
        }

        public void Clear() => _counters.Clear();
    }
}
