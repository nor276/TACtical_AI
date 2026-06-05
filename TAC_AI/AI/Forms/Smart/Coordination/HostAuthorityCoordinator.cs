using System;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Coordination
{
    /// <summary>
    /// L-030: edge-detector for <see cref="SmartRuntime.IsHost"/> transitions. Called from
    /// SmartForm.Operations (L-057) on the main thread. When IsHost flips, publishes
    /// <see cref="HostChanged"/> on the WorldEventBus so LearningService (L-070) can
    /// checkpoint + reload models, and TrainerWorker FSM (L-058) can pause/resume.
    ///
    /// Debounced: a transition that immediately flips back within
    /// <see cref="DebounceMs"/> is treated as a glitch and not republished. This keeps the
    /// trainer-pause path from thrashing during host-migration spin-up.
    ///
    /// Pure poll-driven: no daemon. Notify() is cheap (one read of IsHost + one branch).
    /// </summary>
    public static class HostAuthorityCoordinator
    {
        public const int DebounceMs = 1000;

        private static bool _initialized;
        private static bool _lastObservedIsHost;
        // Pending transition timestamp: when set, we observed a candidate transition that
        // is being held for DebounceMs before publishing.
        private static int _pendingTransitionTickMs;
        private static bool _pendingNewValue;

        /// <summary>SMART_DEV / Init hook.</summary>
        public static void Reset()
        {
            _initialized = false;
            _lastObservedIsHost = false;
            _pendingTransitionTickMs = 0;
            _pendingNewValue = false;
        }

        /// <summary>
        /// Called every Operations tick. Cheap fast-path when nothing changed.
        /// </summary>
        public static void Notify()
        {
            bool now = SmartRuntime.IsHost;
            int nowMs = Environment.TickCount;

            // First call: establish baseline + publish Initial transition.
            if (!_initialized)
            {
                _initialized = true;
                _lastObservedIsHost = now;
                Publish(wasHost: now, isHost: now, HostAuthorityPhase.Initial);
                return;
            }

            // No change vs last observed and no pending transition — fast-path no-op.
            if (now == _lastObservedIsHost && _pendingTransitionTickMs == 0) return;

            // New candidate transition detected — start the debounce timer.
            if (now != _lastObservedIsHost && _pendingTransitionTickMs == 0)
            {
                _pendingNewValue = now;
                _pendingTransitionTickMs = nowMs;
                return;
            }

            // Pending transition in flight.
            if (_pendingTransitionTickMs != 0)
            {
                if (now != _pendingNewValue)
                {
                    // Flip-back within debounce — discard the pending transition.
                    DebugTAC_AI.LogWarnFileOnly("hostauth-debounce-flipback",
                        "[HOSTAUTH-CHANGE] candidate transition cancelled by flip-back within " + DebounceMs + "ms");
                    _pendingTransitionTickMs = 0;
                    return;
                }
                if (unchecked(nowMs - _pendingTransitionTickMs) >= DebounceMs)
                {
                    // Debounce window passed with the new value stable — publish.
                    bool wasHost = _lastObservedIsHost;
                    _lastObservedIsHost = now;
                    _pendingTransitionTickMs = 0;
                    Publish(wasHost, now, HostAuthorityPhase.IsHostPoll);
                }
            }
        }

        /// <summary>SMART_DEV test hook: force a publish for repro.</summary>
        public static void ForceTransition(bool toIsHost)
        {
            bool wasHost = _lastObservedIsHost;
            _lastObservedIsHost = toIsHost;
            _pendingTransitionTickMs = 0;
            Publish(wasHost, toIsHost, HostAuthorityPhase.Forced);
        }

        private static void Publish(bool wasHost, bool isHost, HostAuthorityPhase phase)
        {
            long tickMono = MonoClock.Now();
            try
            {
                WorldEventBus.Publish(new HostChanged(wasHost, isHost, tickMono, phase));
                DebugTAC_AI.LogWarnFileOnly("hostauth-change-" + phase,
                    "[HOSTAUTH-CHANGE] was=" + wasHost + " now=" + isHost + " phase=" + phase + " tickMono=" + tickMono);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("HostAuthorityCoordinator.Publish: " + ex.Message);
            }
        }
    }
}
