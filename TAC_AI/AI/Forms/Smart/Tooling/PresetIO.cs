using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TAC_AI.AI.Tunables;

namespace TAC_AI.AI.Forms.Smart.Tooling
{
    // P11 T7 Item 63: PresetRecord class deleted — never instantiated. PresetIO uses the
    // flat key=value text format directly via TunableMenuBridge.SmartEntries, with no
    // category-grouped intermediate object. Resurrect from git when a category-grouped
    // format becomes useful (e.g. selective per-category presets).

    /// <summary>
    /// P10 Item 34: save / load named tunable presets to disk. Console-command surface in
    /// <see cref="SmartConsoleCommands"/> exposes <c>smart.preset save</c> / <c>load</c> / <c>list</c>.
    ///
    /// File format: simple key=value text (one tunable per line), category-prefix
    /// preserved in the key. No JSON dependency — keeps the v0.2 surface portable across
    /// TerraTech mod-loader versions. SchemaVersion captured as the first line for forward-
    /// compat with format changes.
    ///
    /// File location: <c>{cwd}/Mods/SmartAI/Presets/{name}.preset</c>.
    /// </summary>
    public static class PresetIO
    {
        public const string PresetDirSubpath = "SmartAI/Presets";
        public const string PresetExtension = ".preset";
        public const int CurrentSchemaVersion = 1;

        private static string PresetDir()
            => Path.Combine(Environment.CurrentDirectory, "Mods", PresetDirSubpath);

        public static bool Save(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                string dir = PresetDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, SanitizeName(name) + PresetExtension);

                var sb = new StringBuilder(1024);
                sb.Append("# Smart preset — schema=");
                sb.Append(CurrentSchemaVersion);
                sb.AppendLine();
                sb.Append("schema=");
                sb.Append(CurrentSchemaVersion);
                sb.AppendLine();

                int count = 0;
                foreach (var t in TunableMenuBridge.SmartEntries)
                {
                    if (t == null) continue;
                    sb.Append(t.Key);
                    sb.Append('=');
                    switch (t.Kind)
                    {
                        case TunableKind.Float: sb.Append(t.CurFloat.ToString("R")); break;
                        case TunableKind.Int:   sb.Append(t.CurInt); break;
                        case TunableKind.Bool:  sb.Append(t.CurBool ? "true" : "false"); break;
                        default: sb.Append("?"); break;
                    }
                    sb.AppendLine();
                    count++;
                }
                File.WriteAllText(path, sb.ToString());
                DebugTAC_AI.Log("Smart.PresetIO: saved '" + name + "' (" + count + " tunables) → " + path);
                return true;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.PresetIO.Save('" + name + "'): " + ex.Message);
                return false;
            }
        }

        public static bool Load(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                string dir = PresetDir();
                string path = Path.Combine(dir, SanitizeName(name) + PresetExtension);
                if (!File.Exists(path))
                {
                    DebugTAC_AI.LogWarning("Smart.PresetIO.Load: '" + name + "' not found at " + path);
                    return false;
                }
                var lines = File.ReadAllLines(path);
                int applied = 0;
                int skipped = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("schema=")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || eq == line.Length - 1) { skipped++; continue; }
                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);
                    if (!ApplyOne(key, value)) { skipped++; continue; }
                    applied++;
                }
                DebugTAC_AI.Log("Smart.PresetIO: loaded '" + name + "' (applied=" + applied + ", skipped=" + skipped + ")");
                return true;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.PresetIO.Load('" + name + "'): " + ex.Message);
                return false;
            }
        }

        public static IReadOnlyList<string> List()
        {
            var result = new List<string>();
            try
            {
                string dir = PresetDir();
                if (!Directory.Exists(dir)) return result;
                var files = Directory.GetFiles(dir, "*" + PresetExtension);
                for (int i = 0; i < files.Length; i++)
                    result.Add(Path.GetFileNameWithoutExtension(files[i]));
            }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.PresetIO.List: " + ex.Message); }
            return result;
        }

        private static bool ApplyOne(string key, string value)
        {
            Tunable t;
            if (!TunableRegistry.TryGet(key, out t)) return false;
            // Tolerate stale presets that reference renamed/removed keys.
            try
            {
                switch (t.Kind)
                {
                    case TunableKind.Float:
                    {
                        float f;
                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out f)) return false;
                        TunableRegistry.SetFloat(key, f);
                        return true;
                    }
                    case TunableKind.Int:
                    {
                        int n;
                        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out n)) return false;
                        TunableRegistry.SetInt(key, n);
                        return true;
                    }
                    case TunableKind.Bool:
                    {
                        bool b;
                        if (!bool.TryParse(value, out b)) return false;
                        TunableRegistry.SetBool(key, b);
                        return true;
                    }
                    default: return false;
                }
            }
            catch { return false; }
        }

        private static string SanitizeName(string name)
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
