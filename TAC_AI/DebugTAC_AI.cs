using System;
using System.Collections.Generic;
using System.Linq;
using TerraTechETCUtil;
using UnityEngine;

namespace TAC_AI
{
    internal static class DebugTAC_AI
    {
        internal static bool NotErrored = true;

        internal static bool ShouldLog = true;
        internal static bool DoLogInfos = false;
        internal static bool DoLogAISetup = false;
        internal static bool DoLogPathing = false;
        internal static bool DoLogSpawning = false;
        internal static bool DoLogLoading = false;
        internal static bool DoLogTeams = true;
        // T5: target-acquisition lifecycle events (target released / lost / out of range,
        // weapon lock-on overrides). Separate from DoLogOwnership which only tracks
        // lastEnemy *setter* transitions. Use LogTargeting / LogTargeting(tech, ...).
        internal static bool DoLogTargeting = false;
        private static bool DoLogNet = false;
#if DEBUG
        private static bool LogDev = true;
#else
        private static bool LogDev = false;
#endif

        private static Tank CalcTarget = null;
        private static System.Diagnostics.Stopwatch AILoadTimer = new System.Diagnostics.Stopwatch();
        internal static void BeginAICalculationTimer(Tank target)
        {
            CalcTarget = target;
            AILoadTimer.Restart();
            AILoadTimer.Start();
        }
        internal static void FinishAICalculationTimer(Tank target)
        {
            if (CalcTarget != target)
                return;
            AILoadTimer.Stop();
            Log("Calculations for AI " + target.name + " finished in " + 
                AILoadTimer.ElapsedMilliseconds.ToString() + " miliseconds");
            CalcTarget = null;
        }

        private static System.Diagnostics.Stopwatch AIWorldTimer = new System.Diagnostics.Stopwatch();
        internal static void BeginAIWorldTimer()
        {
            AILoadTimer.Restart();
            AILoadTimer.Start();
        }
        internal static long FinishAIWorldTimer()
        {
            AILoadTimer.Stop();
            return AILoadTimer.ElapsedMilliseconds;
        }

        internal static void DevPopupLog(string message)
        {
            if (!ShouldLog)
                return;
            UnityEngine.Debug.Log(message);
#if DEBUG
            ManUI.inst.ShowErrorPopup(message);
#endif
        }
        internal static void Info(string message)
        {
            if (!ShouldLog || !DoLogInfos)
                return;
            UnityEngine.Debug.Log(message);
        }
        internal static void Log(string message)
        {
            if (!ShouldLog)
                return;
            UnityEngine.Debug.Log(message);
        }
        internal static void LogSpecific(Tank tech, string message)
        {
            if (!ShouldLog || tech != Singleton.playerTank)
                return;
            UnityEngine.Debug.Log(message);
        }
        internal static void LogTeams(string message)
        {
            if (!ShouldLog || !DoLogTeams)
                return;
            UnityEngine.Debug.Log(message);
        }
        internal static void LogWarnPlayerOnce(string message, Exception e)
        {
            if (!ShouldLog)
                return;
            if (NotErrored)
            {
                ManModGUI.ShowErrorPopup("ERROR with Advanced AI\n" + message + "\nContinue with caution: \n" + e);
                NotErrored = false;
            }
            UnityEngine.Debug.Log(KickStart.ModID + ": "  + message + e);
        }
        private static readonly HashSet<string> warnedKeys = new HashSet<string>();
        /// <summary>
        /// Like LogWarnPlayerOnce, but dedups per-key so repeated failures across distinct
        /// keys (e.g. tank names) each surface their first occurrence instead of being
        /// hidden by the session-wide NotErrored gate.
        /// </summary>
        internal static void LogWarnPlayerOncePerKey(string key, string message, Exception e)
        {
            if (!ShouldLog)
                return;
            if (warnedKeys.Add(key))
            {
                ManModGUI.ShowErrorPopup("ERROR with Advanced AI\n" + message + "\nContinue with caution: \n" + e);
            }
            UnityEngine.Debug.Log(KickStart.ModID + ": " + message + e);
        }
        internal static void LogLoad(string message)
        {
            if (!ShouldLog || !DoLogLoading)
                return;
            UnityEngine.Debug.Log(message);
        }
        internal static bool NoInfoAISetup => !ShouldLog || !DoLogAISetup;
        internal static void LogAISetup(string message)
        {
            if (NoInfoAISetup)
                return;
            UnityEngine.Debug.Log(message);
        }
        internal static bool NoLogPathing => !ShouldLog || !DoLogPathing;
        internal static void LogPathing(string message)
        {
            if (NoLogPathing)
                return;
            UnityEngine.Debug.Log(message);
        }
        internal static bool NoLogSpawning => !ShouldLog || !DoLogSpawning;
        internal static void LogSpawn(string message)
        {
            if (NoLogSpawning)
                return;
            UnityEngine.Debug.Log(message);
        }
        // T5: target-acquisition lifecycle. Player-tank-filtered variant mirrors
        // LogSpecific so a fleet engagement doesn't spam — only the player's own tech
        // emits when the flag is on. Use bare LogTargeting(string) for non-tech-scoped
        // events (e.g. weapon lock overrides without an obvious "this tank" context).
        internal static bool NoLogTargeting => !ShouldLog || !DoLogTargeting;
        internal static void LogTargeting(string message)
        {
            if (NoLogTargeting) return;
            UnityEngine.Debug.Log(message);
        }
        internal static void LogTargeting(Tank tech, string message)
        {
            if (NoLogTargeting || tech != Singleton.playerTank) return;
            UnityEngine.Debug.Log(message);
        }
        internal static void Log(Exception e)
        {
            if (!ShouldLog)
                return;
            UnityEngine.Debug.Log(e);
        }

        internal static void LogNet(string message)
        {
            if (!DoLogNet)
                return;
            UnityEngine.Debug.Log(message);
        }

        internal static void Assert(string message)
        {
            if (!ShouldLog)
                return;
            UnityEngine.Debug.Log(message + "\n" + StackTraceUtility.ExtractStackTrace().ToString());
        }
        internal static void Assert(bool shouldAssert, string message)
        {
            if (!ShouldLog || !shouldAssert)
                return;
            UnityEngine.Debug.Log(message + "\n" + StackTraceUtility.ExtractStackTrace().ToString());
        }
        internal static void LogError(string message)
        {
            if (!ShouldLog)
                return;
            UnityEngine.Debug.Log(message + "\n" + StackTraceUtility.ExtractStackTrace().ToString());
        }
        internal static void LogError(string message, Exception e)
        {
            if (!ShouldLog)
                return;
            UnityEngine.Debug.Log(message + " - " + e + "\n" + StackTraceUtility.ExtractStackTrace().ToString());
        }
        internal static void LogDevOnly(string message)
        {
            if (!LogDev)
                return;
            UnityEngine.Debug.Log(message);
        }
        internal static void LogDevOnlyAssert(string message)
        {
            if (!LogDev)
                return;
            UnityEngine.Debug.Log(message + "\n" + StackTraceUtility.ExtractStackTrace().ToString());
        }
        internal static void Exception(string message)
        {
            throw new Exception(KickStart.ModID + ": Exception - ", new Exception(message));
        }
        private static List<string> warning = new List<string>();
        private static bool postStartup = false;
        private static bool seriousError = false;
        internal static void ErrorReport(string Warning)
        {
            warning.Add(Warning);
            Debug.Log("Advanced AI: Error happened "+ Warning + " - " + StackTraceUtility.ExtractStackTrace());
            seriousError = true;
        }
        internal static void Warning(string Warning)
        {
            Debug.Log("Advanced AI: Warning happened " + Warning + " - " + StackTraceUtility.ExtractStackTrace());
            warning.Add(Warning);
        }
        internal static void DoShowWarnings()
        {
            if (warning.Any())
            {
                foreach (var item in warning)
                {
                    ManUI.inst.ShowErrorPopup("Adv.AI: " + item);
                }
                warning.Clear();
                if (!postStartup && seriousError)
                {
                    Debug.Log("Advanced AI: Error happened " + StackTraceUtility.ExtractStackTrace());
                    if (TerraTechETCUtil.MassPatcher.CheckIfUnstable())
                        ManUI.inst.ShowErrorPopup("Advanced AI: Error happened on Unstable Branch.  If the issue persists, switch back to Stable Branch.");
                    else
                        ManUI.inst.ShowErrorPopup("Advanced AI: Error happened during startup!  Advanced AI might not work correctly.");
                }
                seriousError = false;
            }
            postStartup = true;
        }
        internal static void FatalError()
        {
            ManUI.inst.ShowErrorPopup(KickStart.ModID + ": ENCOUNTERED CRITICAL ERROR");
            Assert(StackTraceUtility.ExtractStackTrace());
            UnityEngine.Debug.Log(KickStart.ModID + ": ENCOUNTERED CRITICAL ERROR");
            UnityEngine.Debug.Log(KickStart.ModID + ": MAY NOT WORK PROPERLY AFTER THIS ERROR, PLEASE REPORT!");
        }
        internal static void FatalError(string e)
        {
            ManUI.inst.ShowErrorPopup(KickStart.ModID + ": ENCOUNTERED CRITICAL ERROR: " + e);
            Assert(e);
            UnityEngine.Debug.Log(KickStart.ModID + ": ENCOUNTERED CRITICAL ERROR");
            UnityEngine.Debug.Log(KickStart.ModID + ": MAY NOT WORK PROPERLY AFTER THIS ERROR, PLEASE REPORT!");
        }
        private static int count =-1;
        internal static void EndlessLoopBreaker()
        {
            if (count == -1)
            {
                InvokeHelper.InvokeSingleRepeat(() => { count = 0; }, 0.001f);
                count = 0;
            }
            if (count > 30)
            {
                throw new InvalidOperationException("Endless loop!");
            }
        }

        internal static string Prefix(string subsystem, string severity = "INFO")
            => "[TAC_AI:" + subsystem + ":" + severity + "]";

        internal static void LogTagged(string subsystem, string message)
        {
            if (!ShouldLog) return;
            UnityEngine.Debug.Log(Prefix(subsystem) + " " + message);
        }

        internal static void WarnTagged(string subsystem, string message)
        {
            if (!ShouldLog) return;
            UnityEngine.Debug.Log(Prefix(subsystem, "WARN") + " " + message);
        }

        internal static void ErrorTagged(string subsystem, string message)
        {
            if (!ShouldLog) return;
            UnityEngine.Debug.Log(Prefix(subsystem, "ERROR") + " " + message + "\n"
                + StackTraceUtility.ExtractStackTrace().ToString());
        }

        internal static void DebugTagged(string subsystem, string message)
        {
            if (!ShouldLog || !LogDev) return;
            UnityEngine.Debug.Log(Prefix(subsystem, "DEBUG") + " " + message);
        }

        internal static string VisibleName(int id)
        {
            try
            {
                var tv = ManVisible.inst?.GetTrackedVisible(id);
                if (tv != null)
                {
                    string n = null;
                    try { n = tv.visible?.name; } catch { }
                    if (!string.IsNullOrEmpty(n))
                        return "'" + n + "' #" + id;
                }
            }
            catch { /* swallow — caller may be deep in a tick; we never want logging
                       to throw. Fall through to the id-only fallback. */ }
            return "#" + id;
        }

        internal static string VisibleName(Tank tank)
        {
            if (tank == null) return "<null>";
            try
            {
                int id = tank.visible?.ID ?? 0;
                string n = tank.name;
                return string.IsNullOrEmpty(n) ? ("#" + id) : ("'" + n + "' #" + id);
            }
            catch { return "<tank?>"; }
        }

        internal static void LogOwnership(string field, object oldVal, object newVal)
        {
            if (!ShouldLog) return;
            string callerHint = "?";
            try
            {
                // skip(1) = our caller (the property setter); skip(2) = the actual writing subsystem.
                var sf = new System.Diagnostics.StackFrame(2, false);
                var m = sf.GetMethod();
                if (m != null)
                    callerHint = (m.DeclaringType != null ? m.DeclaringType.Name : "?") + "." + m.Name;
            }
            catch { /* never let the diagnostic crash the setter */ }
            UnityEngine.Debug.Log(Prefix("Ownership") + " " + field + ": "
                + (oldVal ?? "<null>") + " → " + (newVal ?? "<null>") + " by " + callerHint);
        }

        internal static void DumpStateHistory(AI.TankAIHelper helper, string reason)
        {
            if (!ShouldLog) return;
            if (helper == null)
            {
                UnityEngine.Debug.Log(Prefix("History", "WARN") + " null helper. reason=" + reason);
                return;
            }
            try
            {
                UnityEngine.Debug.Log(Prefix("History") + " " + VisibleName(helper.tank)
                    + " (reason=" + reason + "):\n  " + helper.GetStateHistorySnapshot());
            }
            catch { /* swallow — dumper must never crash the caller */ }
        }

        private static readonly HashSet<string> firstFireKeys = new HashSet<string>();
        internal static void FirstFire(string key, string description = null)
        {
            if (!ShouldLog) return;
            if (firstFireKeys.Add(key))
            {
                UnityEngine.Debug.Log(Prefix("Patch") + " first-fire: " + key
                    + (string.IsNullOrEmpty(description) ? "" : " — " + description));
            }
        }
    }
}
