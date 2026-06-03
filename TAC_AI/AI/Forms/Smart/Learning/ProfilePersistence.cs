using System;
using System.IO;
using System.Text;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// In-memory representation of a loaded profile — one section per model + the schema
    /// version. The parameter arrays are owned by this struct; the model instances
    /// LoadParameters from them.
    /// </summary>
    public sealed class LoadedProfile
    {
        public uint SchemaVersion;
        public long SavedAtUnixMs;
        public bool FromBaseline;
        public Section[] Sections = new Section[4];

        public sealed class Section
        {
            public ModelId Id;
            public byte ArchitectureVersion;
            public float[] Weights;
        }
    }

    /// <summary>
    /// Persistent profile serialization with snapshot-before-write recoverability +
    /// corrupt-file preservation per LEARNING-CONTRACT §5–§6 and DOCTRINE §2.4.
    ///
    /// File layout per §5.2:
    ///   Header(16): magic[4] "SMRT", schema_version[4], saved_at_unix_ms[8]
    ///   Sections(4): id[1], architecture_version[1], param_count[4], param_bytes[4], weights[param_bytes]
    ///   Footer: crc32[4] over everything before the footer
    /// </summary>
    public static class ProfilePersistence
    {
        public const uint CurrentSchemaVersion = 1;
        public static readonly byte[] Magic = { (byte)'S', (byte)'M', (byte)'R', (byte)'T' };

        /// <summary>Serialize the four model snapshots into the profile binary at <paramref name="filePath"/>.</summary>
        public static void Save(string filePath, ILearnedModel[] models)
        {
            EnsureDirectory(filePath);
            string tmp = filePath + ".tmp";
            string previous = filePath + ".previous";

            // §6.1 step 1: if the prior file exists, snapshot it.
            try
            {
                if (File.Exists(filePath))
                {
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Copy(filePath, previous);
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.Save: snapshot-before-write failed: " + ex.Message);
                // Don't abort the save — better to have a fresh save than nothing.
            }

            // Build the byte stream.
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                bw.Write(Magic, 0, 4);
                bw.Write(CurrentSchemaVersion);
                bw.Write((long)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                for (int i = 0; i < models.Length; i++)
                {
                    var m = models[i];
                    var weights = new float[m.ParameterCount];
                    m.StoreParameters(weights);
                    bw.Write((byte)m.Id);
                    bw.Write(m.ArchitectureVersion);
                    bw.Write((uint)m.ParameterCount);
                    int bytes = m.ParameterCount * 4;
                    bw.Write((uint)bytes);
                    // Write floats little-endian (BinaryWriter is LE on .NET, fine on Windows targets).
                    for (int j = 0; j < weights.Length; j++) bw.Write(weights[j]);
                }
                bw.Flush();

                byte[] body = ms.ToArray();
                uint crc = Crc32(body, 0, body.Length);

                // Write tmp + atomic rename per §6.1 steps 3-5.
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs, Encoding.ASCII))
                {
                    w.Write(body);
                    w.Write(crc);
                    w.Flush();
                    fs.Flush(flushToDisk: true);
                }
            }

            // Phase 4 (FIX-PLAN.md) — R1 §3.9 / R2 1.R2-D atomic save: previous code
            // did `Delete(filePath); Move(tmp, filePath)` — a non-atomic Windows pattern
            // where a Move failure (AV lock, transient I/O, disk full) lost the most-
            // recent save. File.Replace IS atomic on NTFS and additionally swaps the
            // prior file into a sidecar slot, giving us a free generation cushion.
            // First-save fallback: target doesn't exist yet → File.Move works.
            if (File.Exists(filePath))
            {
                try
                {
                    File.Replace(tmp, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    // Non-NTFS filesystem; fall back to the old pattern.
                    File.Delete(filePath);
                    File.Move(tmp, filePath);
                }
            }
            else
            {
                File.Move(tmp, filePath);
            }
        }

        /// <summary>
        /// Load with corruption fallback per §6.2:
        ///   - magic / size / CRC validation
        ///   - corrupt file preserved as &lt;path&gt;.corrupt-&lt;unixMs&gt;
        ///   - fall through to .previous
        ///   - fall through to baseline (caller-supplied) if even .previous fails
        /// Never throws on corruption; returns null if no usable profile + no baseline.
        /// </summary>
        public static LoadedProfile Load(string filePath, byte[] baselineBytes)
        {
            // Try primary.
            if (TryLoad(filePath, out var p, out var failure)) { p.FromBaseline = false; return p; }
            if (failure != null) PreserveCorrupt(filePath, failure);

            // Try .previous.
            string prev = filePath + ".previous";
            if (TryLoad(prev, out var pp, out _)) { pp.FromBaseline = false; return pp; }

            // Try embedded baseline.
            if (baselineBytes != null && baselineBytes.Length > 0)
            {
                if (TryLoadFromBytes(baselineBytes, out var pb, out _)) { pb.FromBaseline = true; return pb; }
            }

            return null;
        }

        private static bool TryLoad(string filePath, out LoadedProfile profile, out string failureCategory)
        {
            profile = null;
            failureCategory = null;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                failureCategory = "missing";
                return false;
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(filePath); }
            catch (Exception ex)
            {
                failureCategory = "io:" + ex.GetType().Name;
                return false;
            }
            return TryLoadFromBytes(bytes, out profile, out failureCategory);
        }

        private static bool TryLoadFromBytes(byte[] bytes, out LoadedProfile profile, out string failureCategory)
        {
            profile = null;
            failureCategory = null;
            if (bytes == null || bytes.Length < 16 + 4) { failureCategory = "too-short"; return false; }

            // Footer CRC.
            int crcOffset = bytes.Length - 4;
            uint storedCrc = BitConverter.ToUInt32(bytes, crcOffset);
            uint computed = Crc32(bytes, 0, crcOffset);
            if (storedCrc != computed) { failureCategory = "crc-mismatch"; return false; }

            try
            {
                using (var ms = new MemoryStream(bytes, 0, crcOffset, writable: false))
                using (var br = new BinaryReader(ms, Encoding.ASCII))
                {
                    var magic = br.ReadBytes(4);
                    if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1]
                        || magic[2] != Magic[2] || magic[3] != Magic[3])
                    { failureCategory = "bad-magic"; return false; }

                    var p = new LoadedProfile
                    {
                        SchemaVersion = br.ReadUInt32(),
                        SavedAtUnixMs = br.ReadInt64(),
                        Sections = new LoadedProfile.Section[4],
                    };

                    for (int i = 0; i < 4; i++)
                    {
                        byte idByte = br.ReadByte();
                        byte archVer = br.ReadByte();
                        uint paramCount = br.ReadUInt32();
                        uint paramBytes = br.ReadUInt32();
                        if (paramBytes != paramCount * 4) { failureCategory = "section-size-mismatch"; return false; }

                        var weights = new float[paramCount];
                        for (uint j = 0; j < paramCount; j++) weights[j] = br.ReadSingle();
                        p.Sections[i] = new LoadedProfile.Section
                        {
                            Id = (ModelId)idByte,
                            ArchitectureVersion = archVer,
                            Weights = weights,
                        };
                    }

                    if (p.SchemaVersion < CurrentSchemaVersion)
                    {
                        MigrationRunner.RunForward(p, CurrentSchemaVersion);
                    }
                    else if (p.SchemaVersion > CurrentSchemaVersion)
                    {
                        // Future-versioned file — refuse to load (would discard unknown data).
                        failureCategory = "future-schema";
                        return false;
                    }

                    profile = p;
                    return true;
                }
            }
            catch (Exception ex)
            {
                failureCategory = "parse:" + ex.GetType().Name;
                return false;
            }
        }

        private static void PreserveCorrupt(string filePath, string category)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string corrupt = filePath + ".corrupt-" + ts;
                File.Copy(filePath, corrupt);
                DebugTAC_AI.LogWarning("Smart.Learning: profile corruption [" + category + "] — preserved at " + corrupt);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning: failed to preserve corrupt profile: " + ex.Message);
            }
        }

        private static void EnsureDirectory(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning: directory create failed: " + ex.Message);
            }
        }

        // ---- CRC-32 (IEEE polynomial, reflected) ----
        private static readonly uint[] _crcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            const uint Poly = 0xEDB88320;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? (Poly ^ (c >> 1)) : (c >> 1);
                table[i] = c;
            }
            return table;
        }

        public static uint Crc32(byte[] bytes, int offset, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < count; i++)
                crc = (crc >> 8) ^ _crcTable[(crc ^ bytes[offset + i]) & 0xFF];
            return ~crc;
        }
    }

    /// <summary>
    /// Per LEARNING-CONTRACT §8 + DOCTRINE §2.8 forward-only schema migrations.
    /// v0.1.0 ships schema 1 only; the runner exists for future revisions.
    /// </summary>
    public static class MigrationRunner
    {
        public static void RunForward(LoadedProfile profile, uint targetVersion)
        {
            // No migrations exist at v0.1.0. When schema 2 lands, a switch by FromVersion
            // dispatches to a per-step transform. Forward-only — no down paths.
            for (uint v = profile.SchemaVersion; v < targetVersion; v++)
            {
                switch (v)
                {
                    // case 1: Migrations.M0002_AddXxx.Up(profile); break;
                    default:
                        throw new InvalidOperationException(
                            "Smart.Learning: no forward migration registered for schema version " + v + " → " + (v + 1));
                }
            }
            profile.SchemaVersion = targetVersion;
        }
    }
}
