using System;
using System.Globalization;
using TAC_AI.AI.Forms.Smart.Director.Scenarios;
using TAC_AI.AI.Forms.Smart.World;
using UnityEngine;

namespace TAC_AI.AI.Forms.Smart.Director.Actions
{
    /// <summary>
    /// scenario_set verb. Switches the active scenario id with intensity multiplier.
    /// IntValue = scenarioId, Scalar = intensity. Records prior (id, intensity) on the
    /// JournalEntry body for rollback observability.
    /// </summary>
    public sealed class ScenarioSetAction : IDirectorAction
    {
        public string Verb => "scenario_set";

        public bool TryApply(DirectorActionContext ctx, out JournalEntry entry)
        {
            entry = default(JournalEntry);
            int scenarioId = ctx.IntValue;
            float intensity = ctx.Scalar;
            if (scenarioId <= 0 || scenarioId >= ScenarioRegistry.All.Length) return false;
            if (intensity <= 0f || float.IsNaN(intensity) || float.IsInfinity(intensity)) return false;

            int priorId = DirectorState.ActiveScenario;
            float priorIntensity = DirectorState.Intensity;

            // Centroid hint: zero anchors S1 at world origin; later phases may resolve
            // S6prime against the player base. The executor at drain time can override.
            Vector3 centroid = Vector3.zero;
            bool ok = ScenarioWorker.RequestScenarioSet(scenarioId, intensity, centroid);
            if (!ok)
            {
                DirectorJournal.Append("ACTION-SKIPPED",
                    "verb=scenario_set reason=request-rejected id=" + scenarioId
                    + " intensity=" + intensity.ToString("F2", CultureInfo.InvariantCulture)
                    + " constraint=" + ctx.ConstraintName);
                return false;
            }

            long now = MonoClock.Now();
            string body = "id=" + scenarioId
                + " intensity=" + intensity.ToString("F2", CultureInfo.InvariantCulture)
                + " priorId=" + priorId
                + " priorIntensity=" + priorIntensity.ToString("F2", CultureInfo.InvariantCulture)
                + " gen=" + DirectorState.ScenarioGeneration
                + " constraint=" + ctx.ConstraintName;
            entry = new JournalEntry(now, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "ACTION-SCENARIO-SET", body);
            DirectorJournal.Append(entry);
            return true;
        }

        public void Undo(JournalEntry entry)
        {
            // Rollback parses priorId/priorIntensity out of the journal body and
            // re-applies via a fresh Apply. Nothing to do here.
        }

        public string Describe(DirectorActionContext ctx)
        {
            return "scenario_set " + ctx.IntValue + " intensity=" + ctx.Scalar.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
