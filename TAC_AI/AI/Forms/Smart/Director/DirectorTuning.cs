using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TAC_AI.AI.Forms.Smart.Director
{
    /// <summary>
    /// Director-scoped tuning fields. Renamed from LearningTuning to avoid namespace
    /// collision with the existing TAC_AI.AI.Forms.Smart.Learning.LearningTuning
    /// (kept unchanged with UseFullBPTT).
    ///
    /// Read-once-per-batch invariant: trainers snapshot the relevant field via
    /// Volatile.Read into a local at the top of TrainOneMinibatch — torn reads
    /// mid-batch would mix two tuning regimes inside one Adam step. Director
    /// writers respect a dedicated _tuningSnapshotLock so checkpoint-time captures
    /// and live temp_adjust writers queue.
    /// </summary>
    public static class DirectorTuning
    {
        // Per-model Adam-LR scale. Indexed by (int)ModelId. Restored on rollback via
        // per-model snapshot.lrScales array (Phase 7).
        public static readonly float[] LrScale = new float[4] { 1f, 1f, 1f, 1f };

        // Snapshotted once per batch by OpponentIntentClassifier.TrainOneMinibatch_FullBptt.
        public static volatile float IntentTemperature = 1f;

        // Per-OutcomeKind reward weights. Sized to (int)OutcomeKind.Count so Phase 5's
        // GuardViolation_* enum insertions resize the array automatically.
        public static readonly float[] OutcomeWeights = NewOutcomeWeights();
        private static float[] NewOutcomeWeights()
        {
            var arr = new float[(int)TAC_AI.AI.Forms.Smart.World.OutcomeKind.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = 1f;
            return arr;
        }

        // Per-model diagnostic-freeze ticket. Plain long with Interlocked accessors —
        // volatile long is illegal CS0677.
        public static readonly long[] FrozenUntilTickMono = new long[4];

        // Dedicated lock for DirectorCheckpoint.Capture (Phase 7) — temp_adjust writers
        // will queue behind a capture in progress. Declared now so the surface exists.
        public static readonly object TuningSnapshotLock = new object();

        public static long GetFrozenUntilTickMono(int modelId)
        {
            if (modelId < 0 || modelId >= FrozenUntilTickMono.Length) return 0L;
            return Interlocked.Read(ref FrozenUntilTickMono[modelId]);
        }

        public static void SetFrozenUntilTickMono(int modelId, long tickMono)
        {
            if (modelId < 0 || modelId >= FrozenUntilTickMono.Length) return;
            Interlocked.Exchange(ref FrozenUntilTickMono[modelId], tickMono);
        }

        public static float GetLrScale(int modelId)
        {
            if (modelId < 0 || modelId >= LrScale.Length) return 1f;
            return Volatile.Read(ref LrScale[modelId]);
        }

        public static void SetLrScale(int modelId, float scale)
        {
            if (modelId < 0 || modelId >= LrScale.Length) return;
            Volatile.Write(ref LrScale[modelId], scale);
        }

        public static float GetOutcomeWeight(int kind)
        {
            if (kind < 0 || kind >= OutcomeWeights.Length) return 0f;
            return Volatile.Read(ref OutcomeWeights[kind]);
        }

        public static void SetOutcomeWeight(int kind, float weight)
        {
            if (kind < 0 || kind >= OutcomeWeights.Length) return;
            Volatile.Write(ref OutcomeWeights[kind], weight);
        }

        /// <summary>Atomic write-temp + File.Replace; mirrors ProfilePersistence Step 4.</summary>
        public static void Save(string modDirectory)
        {
            if (string.IsNullOrEmpty(modDirectory)) return;
            try
            {
                string dir = Path.Combine(modDirectory, "SmartAI", "Director");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "state.json");
                string tmp = path + ".tmp";

                // Lift snapshot under the same lock checkpoint capture uses so a
                // mid-flight temp_adjust doesn't ship a torn write.
                float[] lrCopy = new float[LrScale.Length];
                float intentTemp;
                float[] outcomeCopy = new float[OutcomeWeights.Length];
                long[] frozenCopy = new long[FrozenUntilTickMono.Length];
                lock (TuningSnapshotLock)
                {
                    for (int i = 0; i < LrScale.Length; i++) lrCopy[i] = Volatile.Read(ref LrScale[i]);
                    intentTemp = IntentTemperature;
                    for (int i = 0; i < OutcomeWeights.Length; i++) outcomeCopy[i] = Volatile.Read(ref OutcomeWeights[i]);
                    for (int i = 0; i < FrozenUntilTickMono.Length; i++) frozenCopy[i] = Interlocked.Read(ref FrozenUntilTickMono[i]);
                }

                var sb = new StringBuilder(512);
                sb.Append("{");
                sb.Append("\"schema\":1,");
                sb.Append("\"intentTemperature\":");
                AppendFloat(sb, intentTemp);
                sb.Append(",\"lrScale\":[");
                for (int i = 0; i < lrCopy.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendFloat(sb, lrCopy[i]);
                }
                sb.Append("],\"outcomeWeights\":[");
                for (int i = 0; i < outcomeCopy.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendFloat(sb, outcomeCopy[i]);
                }
                sb.Append("],\"frozenUntilTickMono\":[");
                for (int i = 0; i < frozenCopy.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(frozenCopy[i]);
                }
                sb.Append("]}");

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(sb.ToString());
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
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-tuning-save",
                    "[DIRECTOR-TUNING] Save threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Best-effort load on Director.Init. Missing file = keep defaults.</summary>
        public static void Load(string modDirectory)
        {
            if (string.IsNullOrEmpty(modDirectory)) return;
            try
            {
                string path = Path.Combine(modDirectory, "SmartAI", "Director", "state.json");
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path);
                ParseAndApply(json);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-tuning-load",
                    "[DIRECTOR-TUNING] Load threw " + ex.GetType().Name + ": " + ex.Message
                    + " — defaults retained");
            }
        }

        // Tiny hand-rolled JSON reader — the file shape is fixed and small.
        private static void ParseAndApply(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            float intentTemp;
            if (TryReadScalar(json, "intentTemperature", out intentTemp) && IsFinitePositive(intentTemp))
                IntentTemperature = intentTemp;

            ReadFloatArrayInto(json, "lrScale", LrScale, defaultVal: 1f);
            ReadFloatArrayInto(json, "outcomeWeights", OutcomeWeights, defaultVal: 1f);
            ReadLongArrayInto(json, "frozenUntilTickMono", FrozenUntilTickMono);
        }

        private static bool IsFinitePositive(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v) && v > 0f;
        }

        private static bool TryReadScalar(string json, string key, out float value)
        {
            value = 0f;
            int idx = FindKey(json, key);
            if (idx < 0) return false;
            int start = idx;
            int end = NextDelim(json, start);
            string slice = json.Substring(start, end - start).Trim();
            return float.TryParse(slice, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static void ReadFloatArrayInto(string json, string key, float[] dest, float defaultVal)
        {
            int idx = FindKey(json, key);
            if (idx < 0) return;
            int lb = json.IndexOf('[', idx);
            int rb = json.IndexOf(']', lb + 1);
            if (lb < 0 || rb < 0 || rb <= lb) return;
            string body = json.Substring(lb + 1, rb - lb - 1);
            string[] parts = body.Split(',');
            int n = Math.Min(parts.Length, dest.Length);
            for (int i = 0; i < n; i++)
            {
                float v;
                if (float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v) && !float.IsNaN(v))
                {
                    Volatile.Write(ref dest[i], v);
                }
                else
                {
                    Volatile.Write(ref dest[i], defaultVal);
                }
            }
        }

        private static void ReadLongArrayInto(string json, string key, long[] dest)
        {
            int idx = FindKey(json, key);
            if (idx < 0) return;
            int lb = json.IndexOf('[', idx);
            int rb = json.IndexOf(']', lb + 1);
            if (lb < 0 || rb < 0 || rb <= lb) return;
            string body = json.Substring(lb + 1, rb - lb - 1);
            string[] parts = body.Split(',');
            int n = Math.Min(parts.Length, dest.Length);
            for (int i = 0; i < n; i++)
            {
                long v;
                if (long.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out v))
                {
                    Interlocked.Exchange(ref dest[i], v);
                }
            }
        }

        private static int FindKey(string json, string key)
        {
            string pat = "\"" + key + "\"";
            int k = json.IndexOf(pat, StringComparison.Ordinal);
            if (k < 0) return -1;
            int colon = json.IndexOf(':', k + pat.Length);
            return colon < 0 ? -1 : colon + 1;
        }

        private static int NextDelim(string json, int start)
        {
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == ',' || c == '}' || c == ']') return i;
            }
            return json.Length;
        }

        private static void AppendFloat(StringBuilder sb, float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) { sb.Append('0'); return; }
            sb.Append(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
