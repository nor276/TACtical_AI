using DevCommands;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Tunables;

namespace TAC_AI.AI.Forms.Smart.Tooling
{
    /// <summary>
    /// P10 Item 35: console-command surface for Smart subsystem inspection + tweaking.
    ///
    /// Uses the existing TerraTech <c>[DevCommand]</c> attribute pattern (same as
    /// <c>AICommands.cs</c>) — registration is automatic at mod load; no explicit
    /// Install/Uninstall needed. The plan's spec listed Install/Uninstall methods, but
    /// the attribute-driven registration is the verified-live TerraTech pattern.
    ///
    /// Commands (Cheat access, Host-only):
    ///   smart.snapshot.save &lt;name&gt;       — save current learning profile
    ///   smart.snapshot.restore &lt;name&gt;    — restore a named snapshot
    ///   smart.snapshot.list               — list available snapshots
    ///   smart.snapshot.reset.threat       — Glorot re-init Threat model
    ///   smart.snapshot.reset.all          — Glorot re-init all 4 models
    ///   smart.snapshot.reset.tunables     — reset Smart-keyed tunables to defaults
    ///   smart.preset.save &lt;name&gt;         — save current tunable preset
    ///   smart.preset.load &lt;name&gt;         — load a tunable preset
    ///   smart.preset.list                 — list available presets
    ///   smart.tunables.list               — enumerate Smart-keyed tunables + current values
    ///   smart.runtime.status              — print Smart runtime state summary
    /// </summary>
    public static class SmartConsoleCommands
    {
        // ---- Snapshot ----

        [DevCommand(Name = KickStart.ModCommandID + ".smart.snapshot.save", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn SnapshotSave(string name)
        {
            bool ok = SnapshotManager.Save(name);
            return new CommandReturn { success = ok, message = ok ? "Saved snapshot '" + name + "'" : "Failed to save '" + name + "'" };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.snapshot.restore", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn SnapshotRestore(string name)
        {
            bool ok = SnapshotManager.Restore(name);
            return new CommandReturn { success = ok, message = ok ? "Restored snapshot '" + name + "'" : "Failed to restore '" + name + "'" };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.snapshot.list", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn SnapshotList()
        {
            var list = SnapshotManager.List();
            string msg = list.Count == 0 ? "No snapshots." : "Snapshots: " + string.Join(", ", list);
            return new CommandReturn { success = true, message = msg };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.snapshot.reset.threat", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn ResetThreat()
        {
            SnapshotManager.ResetThreat();
            return new CommandReturn { success = true, message = "Threat model reset." };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.snapshot.reset.all", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn ResetAll()
        {
            SnapshotManager.ResetAll();
            return new CommandReturn { success = true, message = "All 4 models reset." };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.snapshot.reset.tunables", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn ResetTunables()
        {
            SnapshotManager.ResetTunables();
            return new CommandReturn { success = true, message = "Smart-keyed tunables reset to defaults." };
        }

        // ---- Preset ----

        [DevCommand(Name = KickStart.ModCommandID + ".smart.preset.save", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn PresetSave(string name)
        {
            bool ok = PresetIO.Save(name);
            return new CommandReturn { success = ok, message = ok ? "Saved preset '" + name + "'" : "Failed to save preset '" + name + "'" };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.preset.load", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn PresetLoad(string name)
        {
            bool ok = PresetIO.Load(name);
            return new CommandReturn { success = ok, message = ok ? "Loaded preset '" + name + "'" : "Failed to load preset '" + name + "'" };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.preset.list", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn PresetList()
        {
            var list = PresetIO.List();
            string msg = list.Count == 0 ? "No presets." : "Presets: " + string.Join(", ", list);
            return new CommandReturn { success = true, message = msg };
        }

        // ---- Inspectors ----

        [DevCommand(Name = KickStart.ModCommandID + ".smart.tunables.list", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn TunablesList()
        {
            var sb = new System.Text.StringBuilder(512);
            int count = 0;
            foreach (var t in TunableMenuBridge.SmartEntries)
            {
                if (t == null) continue;
                sb.Append(t.Key);
                sb.Append(" = ");
                switch (t.Kind)
                {
                    case TunableKind.Float: sb.Append(t.CurFloat.ToString("R")); break;
                    case TunableKind.Int:   sb.Append(t.CurInt); break;
                    case TunableKind.Bool:  sb.Append(t.CurBool); break;
                }
                sb.Append('\n');
                count++;
            }
            return new CommandReturn { success = true, message = count == 0 ? "(no Smart-keyed tunables)" : sb.ToString() };
        }

        // P11 T2 Item 52: dump the IdentityOutcome counters to console + force-emit one
        // [OUTCOMES] file-only line. Useful during a spawn-test to see the live state of
        // per-(identity, kind) counters without waiting for the 60s periodic tick.
        [DevCommand(Name = KickStart.ModCommandID + ".smart.outcomes.dump", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn OutcomesDump()
        {
            if (!LearningService.IsRunning)
                return new CommandReturn { success = false, message = "LearningService not running." };
            LearningService.EmitOutcomesLogLine();
            var outcomes = LearningService.IdentityOutcomes;
            if (outcomes == null)
                return new CommandReturn { success = false, message = "IdentityOutcomes consumer null." };
            var sb = new System.Text.StringBuilder(256);
            int rows = 0;
            foreach (TAC_AI.AI.Forms.Smart.Identity.SmartIdentity ident
                in System.Enum.GetValues(typeof(TAC_AI.AI.Forms.Smart.Identity.SmartIdentity)))
            {
                foreach (TAC_AI.AI.Forms.Smart.World.OutcomeKind kind
                    in System.Enum.GetValues(typeof(TAC_AI.AI.Forms.Smart.World.OutcomeKind)))
                {
                    long c = outcomes.GetCount(ident, kind);
                    if (c <= 0) continue;
                    sb.Append(ident).Append(':').Append(kind).Append('=').Append(c).Append('\n');
                    rows++;
                }
            }
            if (rows == 0) sb.Append("(no outcomes recorded yet)");
            return new CommandReturn { success = true, message = sb.ToString() };
        }

        // P11 T8 Item 64: ControlFrame-output recorder console commands.
        [DevCommand(Name = KickStart.ModCommandID + ".smart.recording.begin", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn RecordingBegin(string name)
        {
            bool ok = Tests.FrameRecorder.Begin(name);
            return new CommandReturn { success = ok,
                message = ok ? "FrameRecorder BEGIN '" + name + "'" : "FrameRecorder.Begin failed (see log)" };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.recording.stop", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn RecordingStop()
        {
            Tests.FrameRecorder.Stop();
            return new CommandReturn { success = true, message = "FrameRecorder STOP" };
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.recording.diff", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn RecordingDiff(string baselineName, string compareeName)
        {
            try
            {
                string dir = System.IO.Path.Combine(System.Environment.CurrentDirectory, "Mods", "SmartAI", "Recordings");
                string baseline = System.IO.Path.Combine(dir, baselineName + ".bin");
                string comparee = System.IO.Path.Combine(dir, compareeName + ".bin");
                string outCsv = System.IO.Path.Combine(dir, baselineName + "_vs_" + compareeName + ".csv");
                int drifts = Tests.FrameRecorder.DiffToFile(baseline, comparee, outCsv);
                return new CommandReturn { success = true,
                    message = "Diff wrote " + drifts + " drift lines → " + outCsv };
            }
            catch (System.Exception ex)
            {
                return new CommandReturn { success = false,
                    message = "Diff failed: " + ex.GetType().Name + ": " + ex.Message };
            }
        }

        [DevCommand(Name = KickStart.ModCommandID + ".smart.runtime.status", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn RuntimeStatus()
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append("Smart.Runtime: ");
            sb.Append(SmartRuntime.IsRunning ? "RUNNING" : "STOPPED");
            sb.Append("\nLearning: "); sb.Append(LearningService.IsRunning ? "RUNNING" : "STOPPED");
            sb.Append("\nPlayerId: "); sb.Append(LearningService.CurrentPlayerId);
            sb.Append("\nBlockCatalog entries: ");
            sb.Append(SmartRuntime.BlockCatalog != null ? SmartRuntime.BlockCatalog.Count : 0);
            sb.Append("\nIntentSidecar entries: ");
            sb.Append(SmartRuntime.IntentSidecar != null ? SmartRuntime.IntentSidecar.Count : 0);
            sb.Append("\nHealthSidecar entries: ");
            sb.Append(SmartRuntime.Health != null ? SmartRuntime.Health.Count : 0);
            sb.Append("\nAetherFuser TotalSpikes: ");
            sb.Append(SmartRuntime.Perception != null ? SmartRuntime.Perception.TotalSpikes : 0L);

            // L-039: pathing backpressure block.
            sb.Append("\nPathing.Backpressure: ");
            var bp = Pathing.PathingService.Backpressure;
            if (bp == null)
            {
                sb.Append("null (PathingService stopped)");
            }
            else
            {
                sb.Append("Queue=").Append(bp.QueueDepth).Append('/').Append(bp.QueueCapacity);
                sb.Append("  Shed=").Append(bp.ShedActive ? "ON" : "off");
                sb.Append("  Drop=").Append(bp.DroppedTotal);
                sb.Append("  Exp=").Append(bp.ExpiredTotal);
                sb.Append("  p50=").Append(bp.SolveLatencyMsP50.ToString("0.0")).Append("ms");
                sb.Append("  p99=").Append(bp.SolveLatencyMsP99.ToString("0.0")).Append("ms");
            }

            // L-039: worker watchdog status (count of canonical workers expected vs
            // respawn-attempt counters).
            sb.Append("\nWatchdog: expected=");
            sb.Append(Threading.WorkerHealthMonitor.ExpectedCount);
            sb.Append("  respawnSuccess=").Append(Threading.WorkerHealthMonitor.RespawnSuccessTotal);
            sb.Append("  respawnFailed=").Append(Threading.WorkerHealthMonitor.RespawnFailureTotal);

            // L-039: team lifecycle counters (TeamReaperDaemon).
            sb.Append("\nTeams: created=").Append(Coordination.TeamReaperDaemon.TeamsCreatedObserved);
            sb.Append("  evicted=").Append(Coordination.TeamReaperDaemon.TeamsEvictedTotal);

            // L-076: AetherFuser spikes (already covered via the prior block, but expose it
            // explicitly with a "last spike at" timestamp if available).
            sb.Append("\nAetherFuser: spikes=");
            sb.Append(SmartRuntime.Perception != null ? SmartRuntime.Perception.TotalSpikes : 0L);

            // L-076: TechLeakWatchdog count.
            sb.Append("\nTechLeak: total=").Append(World.TechLeakWatchdog.LeaksFoundTotal);
            sb.Append("  sidecars=").Append(World.TechLifecycleRegistry.Count);

            // L-076: trainer profile autosave totals.
            sb.Append("\nAutosave: ok=").Append(Learning.AutosaveWorker.AutosavesTotal);
            sb.Append("  skipped=").Append(Learning.AutosaveWorker.AutosavesSkipped);

            return new CommandReturn { success = true, message = sb.ToString() };
        }

        /// <summary>
        /// L-074: smart.workers.list — per-line worker handle with thread state, CTS
        /// status, age. Sorted alphabetically.
        /// </summary>
        [DevCommand(Name = KickStart.ModCommandID + ".smart.workers.list", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn WorkersList()
        {
            var sb = new System.Text.StringBuilder(512);
            var live = Threading.WorkerLifecycleRegistry.SnapshotLive();
            var names = new System.Collections.Generic.List<string>();
            var lines = new System.Collections.Generic.Dictionary<string, string>(live.Count);
            int now = System.Environment.TickCount;
            for (int i = 0; i < live.Count; i++)
            {
                var h = live[i];
                if (h == null) continue;
                bool alive = false;
                string state = "?";
                try { alive = h.Thread.IsAlive; state = h.Thread.ThreadState.ToString(); }
                catch { }
                bool cancelled = false;
                try { cancelled = h.Cts.IsCancellationRequested; } catch { }
                names.Add(h.Name);
                lines[h.Name] = "name=" + h.Name + " thread.IsAlive=" + alive + " ThreadState=" + state
                    + " cts=" + (cancelled ? "cancelled" : "active");
            }
            names.Sort(System.StringComparer.Ordinal);
            foreach (var n in names) sb.AppendLine(lines[n]);
            int expected = Threading.WorkerHealthMonitor.ExpectedCount;
            sb.Append("TOTAL: live=").Append(live.Count)
              .Append(" expected=").Append(expected)
              .Append(" missing=").Append(System.Math.Max(0, expected - live.Count));
            return new CommandReturn { success = true, message = sb.ToString() };
        }

        /// <summary>
        /// L-075: smart.workers.respawn — force-respawn every canonical daemon + replace
        /// any dead pool worker. Optional single-name arg.
        /// </summary>
        [DevCommand(Name = KickStart.ModCommandID + ".smart.workers.respawn", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn WorkersRespawn()
        {
            // Force a watchdog scan + pool sweep regardless of debounce.
            var sb = new System.Text.StringBuilder(256);
            try
            {
                Threading.DaemonWatchdog.ScanAndRespawn();
                sb.Append("DaemonWatchdog: scan triggered  ");
            }
            catch (System.Exception ex) { sb.Append("DaemonWatchdog: threw ").Append(ex.Message).Append("  "); }
            int replaced = 0;
            try { replaced = SmartRuntime.Pool?.ReplaceDeadWorkers() ?? 0; }
            catch (System.Exception ex) { sb.Append("Pool: threw ").Append(ex.Message).Append("  "); }
            sb.Append("pool replaced: ").Append(replaced);
            return new CommandReturn { success = true, message = sb.ToString() };
        }

        /// <summary>
        /// smart.training.toggle — flip the training-mode filter on/off. Resets counters.
        /// </summary>
        [DevCommand(Name = KickStart.ModCommandID + ".smart.training.toggle", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn TrainingToggle()
        {
            TrainingModeFilter.Enabled = !TrainingModeFilter.Enabled;
            TrainingModeFilter.ResetCounters();
            // Mirror the in-game tunable so the menu + F8 panel reflect the new state.
            try { TunableRegistry.SetBool("training.enabled", TrainingModeFilter.Enabled); }
            catch { /* registry may not have the key yet on early-init flip — fine, bind() syncs next */ }
            return new CommandReturn { success = true, message = "Training mode: " + (TrainingModeFilter.Enabled ? "ENABLED" : "off") };
        }

        /// <summary>
        /// smart.training.status — show whether training-mode filter is on + reject stats.
        /// </summary>
        [DevCommand(Name = KickStart.ModCommandID + ".smart.training.status", Access = Access.Cheat, Users = User.Host)]
        public static CommandReturn TrainingStatus()
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append("Training mode: ").Append(TrainingModeFilter.Enabled ? "ENABLED" : "off");
            sb.Append("\nKeywords: ");
            foreach (var k in TrainingModeFilter.Keywords) sb.Append(k).Append(' ');
            sb.Append("\nCandidates evaluated: ").Append(TrainingModeFilter.CandidatesEvaluated);
            sb.Append("\nCandidates rejected: ").Append(TrainingModeFilter.CandidatesRejected);
            sb.Append("\nRe-rolls exhausted: ").Append(TrainingModeFilter.RerollsExhausted);
            sb.Append("\n(toggle via in-game tunable 'training.enabled' or F8 panel)");
            return new CommandReturn { success = true, message = sb.ToString() };
        }
    }
}
