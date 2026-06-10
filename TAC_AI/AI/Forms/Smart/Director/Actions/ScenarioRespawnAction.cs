using System;
using System.Globalization;
using System.Threading;
using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director.Actions
{
    /// <summary>
    /// scenario_respawn verb. Re-runs the active scenario without changing id.
    /// Globally rate-limited 1 per 30 s via Interlocked CAS on _lastRespawnMono.
    /// </summary>
    public sealed class ScenarioRespawnAction : IDirectorAction
    {
        public const int RateLimitSec = 30;

        private static long _lastRespawnMono;

        public string Verb => "scenario_respawn";

        public bool TryApply(DirectorActionContext ctx, out JournalEntry entry)
        {
            entry = default(JournalEntry);
            int sid = DirectorState.ActiveScenario;
            if (sid == 0)
            {
                DirectorJournal.Append("ACTION-SKIPPED",
                    "verb=scenario_respawn reason=no-active-scenario constraint=" + ctx.ConstraintName);
                return false;
            }

            long now = MonoClock.Now();
            long prev = Interlocked.Read(ref _lastRespawnMono);
            if (prev != 0L && MonoClock.Seconds(prev, now) < RateLimitSec)
            {
                DirectorJournal.Append("ACTION-RATE-LIMITED",
                    "verb=scenario_respawn sinceLastSec="
                    + MonoClock.Seconds(prev, now).ToString("F1", CultureInfo.InvariantCulture)
                    + " constraint=" + ctx.ConstraintName);
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastRespawnMono, now, prev) != prev)
                return false;

            bool ok = ScenarioWorker.RequestRespawn();
            if (!ok)
            {
                DirectorJournal.Append("ACTION-SKIPPED",
                    "verb=scenario_respawn reason=request-rejected sid=" + sid
                    + " constraint=" + ctx.ConstraintName);
                return false;
            }

            string body = "id=" + sid
                + " gen=" + DirectorState.ScenarioGeneration
                + " constraint=" + ctx.ConstraintName;
            entry = new JournalEntry(now, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "ACTION-SCENARIO-RESPAWN", body);
            DirectorJournal.Append(entry);
            return true;
        }

        public void Undo(JournalEntry entry)
        {
            // Irreversible — population churn already happened on the engine side.
        }

        public string Describe(DirectorActionContext ctx)
        {
            return "scenario_respawn";
        }
    }
}
