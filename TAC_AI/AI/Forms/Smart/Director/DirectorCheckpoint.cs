using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.Tooling;
using TAC_AI.AI.Forms.Smart.World;
using TAC_AI.AI.Tunables;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// T3 disk Director-named checkpoint trio (§7.2 / §7.4). Each label lives under
    /// SmartAI/Director/checkpoints/&lt;label&gt;/ and holds:
    ///   params.bin          — full ProfilePersistence dump of the 4 models
    ///   tunables.json       — smart.director.* + smart.training.* current values
    ///   director-config.json — scenario + intensity + DirectiveTable hash + journal cursor
    /// Ring of 5 named slots; auto-prune &gt; 14 d at Init; a slot referenced by a directive
    /// is never pruned. Each ≤ 200 KB target. Atomic-write mirrors ProfilePersistence Step 4.
    /// </summary>
    public static class DirectorCheckpoint
    {
        public const int RingSlots = 5;
        public const int AutoPruneDays = 14;
        public const string ParamsFile = "params.bin";
        public const string TunablesFile = "tunables.json";
        public const string ConfigFile = "director-config.json";

        private static string _checkpointRoot;

        public static string CheckpointRoot => _checkpointRoot;

        /// <summary>Comma-separated named-checkpoint labels for the digest ROLLBACK_AVAIL line.</summary>
        public static string ListLabelsCsv()
        {
            if (string.IsNullOrEmpty(_checkpointRoot) || !Directory.Exists(_checkpointRoot)) return "";
            try
            {
                string[] dirs = Directory.GetDirectories(_checkpointRoot);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < dirs.Length; i++)
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(Path.GetFileName(dirs[i]));
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// Init order (§7.4 MAJOR-fix): (1) directives/journal already loaded by
        /// TrainingDirector.Init; (2) build referenced-label set; (3) prune unreferenced
        /// older than 14 d; (4) caller registers the canonical daemon afterward.
        /// </summary>
        public static void Init(string modDirectory)
        {
            if (string.IsNullOrEmpty(modDirectory)) { _checkpointRoot = null; return; }
            try
            {
                _checkpointRoot = Path.Combine(modDirectory, "SmartAI", "Director", "checkpoints");
                if (!Directory.Exists(_checkpointRoot)) Directory.CreateDirectory(_checkpointRoot);
                PruneAged();
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-checkpoint-init",
                    "[DIRECTOR-CHECKPOINT] Init threw " + ex.GetType().Name + ": " + ex.Message);
                _checkpointRoot = null;
            }
        }

        // Directive-referenced labels are never pruned. DirectiveTable has no label field yet,
        // so the referenced set is empty; the hook stays so adding a label join later is one line.
        private static HashSet<string> ReferencedLabels()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void PruneAged()
        {
            if (string.IsNullOrEmpty(_checkpointRoot) || !Directory.Exists(_checkpointRoot)) return;
            var referenced = ReferencedLabels();
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(AutoPruneDays);
            string[] dirs = Directory.GetDirectories(_checkpointRoot);
            for (int i = 0; i < dirs.Length; i++)
            {
                string label = Path.GetFileName(dirs[i]);
                if (referenced.Contains(label)) continue;
                try
                {
                    DateTime mt = Directory.GetLastWriteTimeUtc(dirs[i]);
                    if (mt < cutoff)
                    {
                        Directory.Delete(dirs[i], recursive: true);
                        DirectorJournal.Append("CHECKPOINT-PRUNED", "label=" + label + " reason=aged");
                    }
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("director-checkpoint-prune-" + label,
                        "[DIRECTOR-CHECKPOINT] prune threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        // Enforce the 5-slot named-ring cap: evict the oldest non-referenced labels.
        private static void EnforceRingCap()
        {
            if (string.IsNullOrEmpty(_checkpointRoot) || !Directory.Exists(_checkpointRoot)) return;
            var referenced = ReferencedLabels();
            string[] dirs = Directory.GetDirectories(_checkpointRoot);
            if (dirs.Length <= RingSlots) return;
            // Sort by mtime ascending so the oldest sit first.
            Array.Sort(dirs, (a, b) => Directory.GetLastWriteTimeUtc(a).CompareTo(Directory.GetLastWriteTimeUtc(b)));
            int toEvict = dirs.Length - RingSlots;
            for (int i = 0; i < dirs.Length && toEvict > 0; i++)
            {
                string label = Path.GetFileName(dirs[i]);
                if (referenced.Contains(label)) continue;
                try
                {
                    Directory.Delete(dirs[i], recursive: true);
                    DirectorJournal.Append("CHECKPOINT-PRUNED", "label=" + label + " reason=ring-cap");
                    toEvict--;
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("director-checkpoint-ringcap-" + label,
                        "[DIRECTOR-CHECKPOINT] ring-cap evict threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Write the full trio for <paramref name="label"/>. Returns the checkpoint dir or
        /// null on failure. Host-only.
        /// </summary>
        public static string Save(string label)
        {
            if (string.IsNullOrEmpty(_checkpointRoot)) return null;
            if (!SmartRuntime.IsHost) return null;
            string safe = Sanitize(label);
            string dir = Path.Combine(_checkpointRoot, safe);
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                WriteParamsBin(Path.Combine(dir, ParamsFile));
                WriteTunablesJson(Path.Combine(dir, TunablesFile));
                WriteConfigJson(Path.Combine(dir, ConfigFile));
                EnforceRingCap();
                DirectorJournal.Append("CHECKPOINT-SAVED", "label=" + safe + " dir=" + dir);
                return dir;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-checkpoint-save-" + safe,
                    "[DIRECTOR-CHECKPOINT] Save threw " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>params.bin path for a label, or null if absent.</summary>
        public static string ParamsBinPathFor(string label)
        {
            if (string.IsNullOrEmpty(_checkpointRoot)) return null;
            string p = Path.Combine(_checkpointRoot, Sanitize(label), ParamsFile);
            return File.Exists(p) ? p : null;
        }

        // Adapter: full ProfilePersistence dump of the live models into params.bin.
        private static void WriteParamsBin(string path)
        {
            if (!LearningService.IsRunning) return;
            var models = new ILearnedModel[]
            {
                LearningService.Intent, LearningService.ActionValue,
                LearningService.Residual, LearningService.Threat,
            };
            ProfilePersistence.Save(path, models);
        }

        // PresetIO-style dump filtered to smart.director.* + smart.training.* into the
        // checkpoint dir (PresetIO.Save writes to a fixed preset dir, so we serialise here).
        private static void WriteTunablesJson(string path)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"schema\":1,\"tunables\":{");
            bool first = true;
            foreach (var t in TunableMenuBridge.SmartEntries)
            {
                if (t == null || t.Key == null) continue;
                if (!t.Key.StartsWith("smart.director.", StringComparison.OrdinalIgnoreCase)
                    && !t.Key.StartsWith("smart.training.", StringComparison.OrdinalIgnoreCase)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"');
                JsonEscape(sb, t.Key);
                sb.Append("\":");
                switch (t.Kind)
                {
                    case TunableKind.Float: AppendFloat(sb, t.CurFloat); break;
                    case TunableKind.Int: sb.Append(t.CurInt); break;
                    case TunableKind.Bool: sb.Append(t.CurBool ? "true" : "false"); break;
                    default: sb.Append("null"); break;
                }
            }
            sb.Append("}}");
            AtomicWrite(path, sb.ToString());
        }

        private static void WriteConfigJson(string path)
        {
            // Journal cursor = the freshest in-mem journal monoTick (rollback step 7 keys
            // the scenario revert on this). DirectiveTable hash is a cheap count+sum stamp.
            long journalCursorMono = 0L;
            long journalCursorWall = 0L;
            foreach (var je in DirectorState.JournalRingInMem)
            {
                if (je.MonoTick > journalCursorMono) journalCursorMono = je.MonoTick;
                if (je.WallclockUtcSec > journalCursorWall) journalCursorWall = je.WallclockUtcSec;
            }
            int directiveHash = 0;
            foreach (var kv in DirectorState.DirectiveTable) directiveHash = unchecked(directiveHash * 31 + kv.Key);

            var sb = new StringBuilder(256);
            sb.Append("{\"schema\":1");
            sb.Append(",\"activeScenario\":").Append(DirectorState.ActiveScenario);
            sb.Append(",\"intensity\":");
            AppendFloat(sb, DirectorState.Intensity);
            sb.Append(",\"directiveTableHash\":").Append(directiveHash);
            sb.Append(",\"directiveCount\":").Append(DirectorState.DirectiveTable.Count);
            sb.Append(",\"scenarioGeneration\":").Append(DirectorState.ScenarioGeneration);
            sb.Append(",\"journalCursorMono\":").Append(journalCursorMono);
            sb.Append(",\"journalCursorWallUtcSec\":").Append(journalCursorWall);
            sb.Append("}");
            AtomicWrite(path, sb.ToString());
        }

        // Atomic write-temp + File.Replace; mirrors ProfilePersistence.cs Step 4 (231-249).
        private static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(content);
                w.Flush();
                fs.Flush(true);
            }
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        private static void AppendFloat(StringBuilder sb, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) { sb.Append('0'); return; }
            sb.Append(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void JsonEscape(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\') { sb.Append('\\').Append(c); }
                else if (c < 0x20) sb.Append('?');
                else sb.Append(c);
            }
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
