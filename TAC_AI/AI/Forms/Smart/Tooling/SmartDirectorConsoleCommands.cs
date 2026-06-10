using System.Text;
using DevCommands;
using TAC_AI.AI.Forms.Smart.Director;
using TAC_AI.AI.Forms.Smart.Director.Guards;

namespace TAC_AI.AI.Forms.Smart.Tooling
{
    // Sibling class (SmartConsoleCommands is not partial). DevCommand scanner is
    // class-agnostic. All Host + Cheat. The "tell" command requires a QUOTED directive —
    // the DevCommand framework splits args on whitespace, so an unquoted multi-word line
    // would only pass the first token. The reject message says so; positional aliases
    // (tell.lr_scale / tell.freeze / tell.rescind) cover the common one-shot cases.
    public static class SmartDirectorConsoleCommands
    {
        private const string Pfx = KickStart.ModCommandID + ".smart.director.";

        // tell "<full directive line>"  — must be QUOTED (framework splits on whitespace).
        [DevCommand(Name = Pfx + "tell", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn Tell(string line)
        {
            if (string.IsNullOrEmpty(line))
                return new CommandReturn { success = false, message = "QUOTE the whole directive, e.g. tell \"maintain hunter.kills_per_min >= 0.5\"" };
            var r = DirectiveSurface.AddLine(line);
            return new CommandReturn { success = r.Ok, message = r.Ok ? "accepted d-" + r.Id.ToString("D5") : r.Error };
        }

        [DevCommand(Name = Pfx + "tell.lr_scale", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn TellLrScale(string model, float factor)
        {
            var r = DirectiveSurface.AddLine("lr_scale " + model + " " + factor.ToString("R"));
            return new CommandReturn { success = r.Ok, message = r.Ok ? "accepted d-" + r.Id.ToString("D5") : r.Error };
        }

        [DevCommand(Name = Pfx + "tell.freeze", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn TellFreeze(string model, int sec)
        {
            var r = DirectiveSurface.AddLine("freeze " + model + " for " + sec + "s");
            return new CommandReturn { success = r.Ok, message = r.Ok ? "accepted d-" + r.Id.ToString("D5") : r.Error };
        }

        [DevCommand(Name = Pfx + "tell.rescind", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn TellRescind(string idOrTag)
        {
            int id;
            if (int.TryParse(idOrTag, out id))
                return new CommandReturn { success = DirectiveSurface.Rescind(id), message = "rescind id " + id };
            int n = DirectiveSurface.RescindByTag(idOrTag);
            return new CommandReturn { success = n > 0, message = "rescinded " + n + " by tag " + idOrTag };
        }

        [DevCommand(Name = Pfx + "list", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn List()
        {
            var snap = DirectiveSurface.SnapshotDirectives();
            if (snap.Count == 0) return new CommandReturn { success = true, message = "No active directives." };
            var sb = new StringBuilder();
            for (int i = 0; i < snap.Count; i++)
            {
                var d = snap[i];
                sb.Append("d-").Append(d.Id.ToString("D5")).Append(' ').Append(d.Verb)
                  .Append(' ').Append(d.Subject);
                if (!string.IsNullOrEmpty(d.Tag)) sb.Append(" [").Append(d.Tag).Append(']');
                sb.Append('\n');
            }
            return new CommandReturn { success = true, message = sb.ToString() };
        }

        [DevCommand(Name = Pfx + "rescind", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn Rescind(int id)
        {
            return new CommandReturn { success = DirectiveSurface.Rescind(id), message = "rescind d-" + id.ToString("D5") };
        }

        [DevCommand(Name = Pfx + "clear", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn Clear()
        {
            int n = DirectiveSurface.Clear();
            return new CommandReturn { success = true, message = "cleared " + n + " directives" };
        }

        [DevCommand(Name = Pfx + "status", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn Status()
        {
            string msg = "active=" + TrainingDirector.IsActive
                + " scenario=" + DirectorState.ActiveScenario
                + " intensity=" + DirectorState.Intensity.ToString("F2")
                + " constraints_ok=" + ConstraintEngine.OkCount() + "/" + ConstraintRegistry.ConstraintCount
                + " directives=" + DirectiveSurface.SnapshotDirectives().Count;
            return new CommandReturn { success = true, message = msg };
        }

        [DevCommand(Name = Pfx + "digest.now", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn DigestNow()
        {
            DigestBuilder.ForceEmit("operator");
            return new CommandReturn { success = true, message = "digest emitted." };
        }

        [DevCommand(Name = Pfx + "checkpoint.save", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn CheckpointSave(string name)
        {
            string dir = DirectorCheckpoint.Save(name);
            return new CommandReturn { success = dir != null, message = dir != null ? "saved " + name : "save failed" };
        }

        [DevCommand(Name = Pfx + "journal.tail", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn JournalTail(int count)
        {
            if (count <= 0) count = 20;
            var entries = new System.Collections.Generic.List<JournalEntry>();
            foreach (var e in DirectorState.JournalRingInMem) entries.Add(e);
            int from = entries.Count - count; if (from < 0) from = 0;
            var sb = new StringBuilder();
            for (int i = from; i < entries.Count; i++)
                sb.Append(entries[i].Kind).Append(": ").Append(entries[i].Body).Append('\n');
            return new CommandReturn { success = true, message = sb.Length > 0 ? sb.ToString() : "journal empty" };
        }

        [DevCommand(Name = Pfx + "constraints.list", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn ConstraintsList()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ConstraintRegistry.All.Length; i++)
            {
                var c = ConstraintRegistry.All[i];
                sb.Append(c.Name).Append(" : ").Append(ConstraintEngine.GetHealth(i))
                  .Append(" metric=").Append(ConstraintEngine.GetLastMetric(i).ToString("F3")).Append('\n');
            }
            return new CommandReturn { success = true, message = sb.ToString() };
        }

        [DevCommand(Name = Pfx + "actions.list", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn ActionsList()
        {
            return new CommandReturn { success = true,
                message = "lr_scale, temp_adjust, freeze_model, replay_bank, scenario_set, scenario_respawn, rollback" };
        }

        [DevCommand(Name = Pfx + "audit", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn Audit()
        {
            return new CommandReturn { success = true,
                message = "Director writes _params/_adam/tuning only via the 7 Action classes." };
        }

        [DevCommand(Name = Pfx + "guards.selftest", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn GuardsSelftest()
        {
            return new CommandReturn { success = true,
                message = "guards.selftest: " + GuardRegistry.GuardCount + " guards registered." };
        }

        [DevCommand(Name = Pfx + "guards.list", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn GuardsList()
        {
            var sb = new StringBuilder();
            for (int g = 0; g < GuardRegistry.GuardCount; g++)
                sb.Append(g).Append(": ").Append(GuardRegistry.GetName(g))
                  .Append(" fired=").Append(GuardTelemetry.GetFiredCount(g)).Append('\n');
            return new CommandReturn { success = true, message = sb.ToString() };
        }

        [DevCommand(Name = Pfx + "guards.snapshot", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn GuardsSnapshot()
        {
            var sb = new StringBuilder();
            sb.Append("soft_correct_fires=").Append(GuardTelemetry.SoftCorrectTelemetryFiresCount)
              .Append(" oscill=").Append(GuardTelemetry.OscillCount)
              .Append(" hard_on_active_target=").Append(GuardTelemetry.HardCorrectOnActiveTargetCount);
            return new CommandReturn { success = true, message = sb.ToString() };
        }
    }
}
