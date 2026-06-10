using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    public struct AddResult
    {
        public bool Ok;
        public int Id;
        public string Error;   // [DIRECTOR-REJECT] reason on failure
    }

    // Owns DirectiveTable insertion/dedup/expiry/overflow + atomic persistence to
    // directives.json + journal mirror. The 32-cap eviction race is closed by wrapping
    // find-evict-insert in lock(_evictLock).
    public static class DirectiveSurface
    {
        public const int MaxActive = 32;

        private static readonly object _evictLock = new object();
        private static int _nextId = 1;
        private static string _dir;
        private static string _path;
        private static int _initialised;

        public static void Init(string modDirectory)
        {
            if (Interlocked.CompareExchange(ref _initialised, 1, 0) != 0) return;
            if (string.IsNullOrEmpty(modDirectory)) { _dir = null; _path = null; return; }
            try
            {
                _dir = Path.Combine(modDirectory, "SmartAI", "Director");
                if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
                _path = Path.Combine(_dir, "directives.json");
                LoadFromDisk();
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-directives-init",
                    "[DIRECTOR-DIRECTIVE] init threw " + ex.GetType().Name + ": " + ex.Message);
                _dir = null; _path = null;
            }
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _initialised, 0);
            _dir = null; _path = null;
            Volatile.Write(ref _nextId, 1);
        }

        // Parse + add one directive line. Returns AddResult with the assigned id or a
        // [DIRECTOR-REJECT] error string.
        public static AddResult AddLine(string line)
        {
            var pr = DirectiveParser.Parse(line);
            if (!pr.Ok)
            {
                DirectorJournal.Append("DIRECTIVE-REJECT", "line=" + (line ?? "") + " err=" + pr.Error);
                return new AddResult { Ok = false, Error = "[DIRECTOR-REJECT] " + pr.Error };
            }
            return Add(pr.Directive);
        }

        public static AddResult Add(Directive d)
        {
            lock (_evictLock)
            {
                var table = DirectorState.DirectiveTable;

                // Tag dedup: a new directive with the same tag replaces the prior.
                if (!string.IsNullOrEmpty(d.Tag))
                {
                    foreach (var kv in table)
                    {
                        if (kv.Value.Tag == d.Tag)
                        {
                            Directive _;
                            table.TryRemove(kv.Key, out _);
                            JournalRecord("expire", kv.Value, "dedup-tag");
                            break;
                        }
                    }
                }

                // Overflow eviction at 32-cap.
                if (table.Count >= MaxActive)
                {
                    int evictKey;
                    if (TryFindOldestNonSticky(out evictKey))
                    {
                        Directive ev;
                        table.TryRemove(evictKey, out ev);
                        JournalRecord("expire", ev, "directive-evicted");
                    }
                    else
                    {
                        DirectorJournal.Append("DIRECTIVE-OVERFLOW", "all-sticky");
                        return new AddResult { Ok = false, Error = "[DIRECTOR-REJECT] directive-overflow-all-sticky" };
                    }
                }

                int id = _nextId++;
                var stamped = new Directive(id, d.Tag, d.Verb, d.Subject, d.Op, d.Value,
                    d.CreatedMono, d.ExpiresMono, d.UntilExpr, d.Src, d.Description);
                table[id] = stamped;
                JournalRecord("add", stamped, null);
                Persist();
                return new AddResult { Ok = true, Id = id };
            }
        }

        public static bool Rescind(int id)
        {
            Directive removed;
            if (DirectorState.DirectiveTable.TryRemove(id, out removed))
            {
                JournalRecord("rescind", removed, null);
                Persist();
                return true;
            }
            return false;
        }

        public static int RescindByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 0;
            int count = 0;
            lock (_evictLock)
            {
                var keys = new List<int>();
                foreach (var kv in DirectorState.DirectiveTable)
                    if (kv.Value.Tag == tag) keys.Add(kv.Key);
                for (int i = 0; i < keys.Count; i++)
                {
                    Directive removed;
                    if (DirectorState.DirectiveTable.TryRemove(keys[i], out removed))
                    {
                        JournalRecord("rescind", removed, "by-tag");
                        count++;
                    }
                }
                if (count > 0) Persist();
            }
            return count;
        }

        public static int Clear()
        {
            int count = 0;
            lock (_evictLock)
            {
                foreach (var kv in DirectorState.DirectiveTable)
                {
                    JournalRecord("rescind", kv.Value, "clear");
                    count++;
                }
                DirectorState.DirectiveTable.Clear();
                Persist();
            }
            return count;
        }

        // 1 Hz sweep — only reads ExpiresMono. The until-expr branch is evaluated on the
        // 0.2 Hz constraint pass elsewhere; this sweep handles wall-clock expiry.
        public static void SweepExpired()
        {
            long now = MonoClock.Now();
            List<int> expired = null;
            foreach (var kv in DirectorState.DirectiveTable)
            {
                long exp = kv.Value.ExpiresMono;
                if (exp != 0L && now >= exp)
                {
                    if (expired == null) expired = new List<int>();
                    expired.Add(kv.Key);
                }
            }
            if (expired == null) return;
            bool any = false;
            for (int i = 0; i < expired.Count; i++)
            {
                Directive removed;
                if (DirectorState.DirectiveTable.TryRemove(expired[i], out removed))
                {
                    JournalRecord("expire", removed, "for-duration");
                    any = true;
                }
            }
            if (any) Persist();
        }

        // Immutable copy for the OnGUI thread.
        public static List<Directive> SnapshotDirectives()
        {
            var list = new List<Directive>(DirectorState.DirectiveTable.Count);
            foreach (var kv in DirectorState.DirectiveTable) list.Add(kv.Value);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        private static bool TryFindOldestNonSticky(out int key)
        {
            key = -1;
            long oldest = long.MaxValue;
            foreach (var kv in DirectorState.DirectiveTable)
            {
                if (kv.Value.IsSticky) continue;
                if (kv.Value.CreatedMono < oldest)
                {
                    oldest = kv.Value.CreatedMono;
                    key = kv.Key;
                }
            }
            return key >= 0;
        }

        private static void JournalRecord(string action, Directive d, string reason)
        {
            string body = "id=" + d.Id + " verb=" + d.Verb + " subject=" + (d.Subject ?? "")
                + (d.Tag != null ? " tag=" + d.Tag : "")
                + (reason != null ? " reason=" + reason : "");
            DirectorJournal.Append("DIRECTIVE-" + action.ToUpperInvariant(), body);
        }

        // ---- persistence ----
        private static void LoadFromDisk()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
            try
            {
                string json = File.ReadAllText(_path);
                int maxId = 0;
                // Very small fixed shape: an array of objects under "directives". We re-load
                // ids + verb + subject + tag + lifetime; the value tag-struct is re-derived
                // lazily by re-parsing the description text would be lossy, so we store the
                // canonical fields directly.
                var loaded = DirectiveJson.Parse(json);
                foreach (var d in loaded)
                {
                    DirectorState.DirectiveTable[d.Id] = d;
                    if (d.Id > maxId) maxId = d.Id;
                }
                // _nextId high-water: max(loadedIds, journalCursor) + 1.
                int cursor = DirectorState.DirectiveTable.Count; // conservative fallback
                int hw = Math.Max(maxId, cursor) + 1;
                Volatile.Write(ref _nextId, hw);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-directives-load",
                    "[DIRECTOR-DIRECTIVE] load threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Persist()
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                string tmp = _path + ".tmp";
                string json = DirectiveJson.Serialize(DirectorState.DirectiveTable);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(json);
                    w.Flush();
                    fs.Flush(true);
                }
                if (File.Exists(_path))
                {
                    try { File.Replace(tmp, _path, _path + ".bak", true); }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(_path);
                        File.Move(tmp, _path);
                    }
                }
                else File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-directives-persist",
                    "[DIRECTOR-DIRECTIVE] persist threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    // Minimal hand-rolled JSON for the directive table. Fixed flat shape; the value tag
    // struct round-trips its kind + the relevant scalar fields.
    internal static class DirectiveJson
    {
        public static string Serialize(System.Collections.Concurrent.ConcurrentDictionary<int, Directive> table)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1024);
            sb.Append("{\"directives\":[");
            bool first = true;
            foreach (var kv in table)
            {
                var d = kv.Value;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append("\"id\":").Append(d.Id).Append(',');
                sb.Append("\"verb\":").Append((int)d.Verb).Append(',');
                sb.Append("\"op\":").Append((int)d.Op).Append(',');
                sb.Append("\"subject\":\"").Append(Esc(d.Subject)).Append("\",");
                sb.Append("\"tag\":\"").Append(Esc(d.Tag)).Append("\",");
                sb.Append("\"created\":").Append(d.CreatedMono).Append(',');
                sb.Append("\"expires\":").Append(d.ExpiresMono).Append(',');
                sb.Append("\"until\":\"").Append(Esc(d.UntilExpr)).Append("\",");
                sb.Append("\"src\":").Append((int)d.Src).Append(',');
                sb.Append("\"vkind\":").Append((int)d.Value.Kind).Append(',');
                sb.Append("\"vscalar\":").Append(d.Value.Scalar.ToString("R", inv)).Append(',');
                sb.Append("\"vlo\":").Append(d.Value.IntervalLo.ToString("R", inv)).Append(',');
                sb.Append("\"vhi\":").Append(d.Value.IntervalHi.ToString("R", inv)).Append(',');
                sb.Append("\"vrate\":").Append(d.Value.RateNumerator.ToString("R", inv)).Append(',');
                sb.Append("\"vlabel\":\"").Append(Esc(d.Value.Label)).Append("\"");
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static List<Directive> Parse(string json)
        {
            var list = new List<Directive>();
            if (string.IsNullOrEmpty(json)) return list;
            int p = 0;
            while (true)
            {
                int obj = json.IndexOf("\"id\":", p, StringComparison.Ordinal);
                if (obj < 0) break;
                int end = json.IndexOf('}', obj);
                if (end < 0) break;
                string slice = json.Substring(obj, end - obj);
                int id = ReadInt(slice, "id");
                var verb = (DirectiveVerb)ReadInt(slice, "verb");
                var op = (DirectiveOp)ReadInt(slice, "op");
                string subject = ReadStr(slice, "subject");
                string tag = ReadStr(slice, "tag");
                long created = ReadLong(slice, "created");
                long expires = ReadLong(slice, "expires");
                string until = ReadStr(slice, "until");
                var src = (DirectiveSource)ReadInt(slice, "src");
                var vkind = (ValueKind)ReadInt(slice, "vkind");
                float vscalar = ReadFloat(slice, "vscalar");
                float vlo = ReadFloat(slice, "vlo");
                float vhi = ReadFloat(slice, "vhi");
                float vrate = ReadFloat(slice, "vrate");
                string vlabel = ReadStr(slice, "vlabel");
                var val = new DirectiveValue(vkind, vscalar, vlo, vhi,
                    string.IsNullOrEmpty(vlabel) ? null : vlabel, vrate, "min");
                list.Add(new Directive(id, string.IsNullOrEmpty(tag) ? null : tag, verb, subject, op,
                    val, created, expires, string.IsNullOrEmpty(until) ? null : until, src, null));
                p = end + 1;
            }
            return list;
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static int ReadInt(string s, string key)
        {
            int idx = FindVal(s, key); if (idx < 0) return 0;
            int e = idx; while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-')) e++;
            int v; int.TryParse(s.Substring(idx, e - idx), out v); return v;
        }
        private static long ReadLong(string s, string key)
        {
            int idx = FindVal(s, key); if (idx < 0) return 0L;
            int e = idx; while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-')) e++;
            long v; long.TryParse(s.Substring(idx, e - idx), out v); return v;
        }
        private static float ReadFloat(string s, string key)
        {
            int idx = FindVal(s, key); if (idx < 0) return 0f;
            int e = idx; while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-' || s[e] == '.' || s[e] == 'E' || s[e] == 'e' || s[e] == '+')) e++;
            float v; float.TryParse(s.Substring(idx, e - idx),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
            return v;
        }
        private static string ReadStr(string s, string key)
        {
            string pat = "\"" + key + "\":\"";
            int k = s.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return null;
            int start = k + pat.Length;
            var sb = new StringBuilder();
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i++; continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }
        private static int FindVal(string s, string key)
        {
            string pat = "\"" + key + "\":";
            int k = s.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return -1;
            return k + pat.Length;
        }
    }
}
