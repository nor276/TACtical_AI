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
            /// <summary>
            /// L-079: TLV body — preserved unknown tags from disk so re-emit on next Save
            /// keeps fields we don't yet understand. Tag 0x0001 = Weights (canonical view);
            /// any other tag id stays in this list verbatim. Empty for v≤2 files.
            /// Use <see cref="System.Collections.Generic.KeyValuePair{TKey,TValue}"/> rather
            /// than ValueTuple because .NET 4.6.1 BCL lacks System.ValueTuple.
            /// </summary>
            public System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<ushort, byte[]>> UnknownTags
                = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<ushort, byte[]>>();
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
        // P8 Item 19 (REV 7): bumped 1 → 2 marking GRU BPTT unfreeze. Parameter array
        // layout unchanged across the bump; M0002_BpttUnfreeze is a no-op forward migration.
        // L-079: schema 3 = TLV per-section body. Tag 0x0001 = weights (canonical view
        // surviving consumers); future tags can be appended without invalidating old files.
        // Load is schema-conditional (flat for v<3, TLV for v>=3); Save always emits v4.
        // FEATURE-EXPANSION-PLAN §7.5: schema 4 marks coordinated ArchitectureVersion=3
        // bump across all four learned models. TLV body layout unchanged; arch mismatch
        // dispatches to ApplyProfile's per-model LoadParameters catch (Glorot re-init).
        public const uint CurrentSchemaVersion = 4;
        public const ushort TagId_Weights = 0x0001;
        public static readonly byte[] Magic = { (byte)'S', (byte)'M', (byte)'R', (byte)'T' };

        /// <summary>Serialize the four model snapshots into the profile binary at <paramref name="filePath"/>.</summary>
        public static void Save(string filePath, ILearnedModel[] models)
        {
            EnsureDirectory(filePath);
            string tmp = filePath + ".tmp";
            string previous = filePath + ".previous";
            string penultimate = filePath + ".penultimate";

            // L-015: rotate two-deep backup ring BEFORE the new save.
            //   .previous → .penultimate  (delete old penultimate first to free the slot)
            //   current   → .previous
            //   new save  → .tmp → File.Replace into current
            // If any rotation step fails, log + continue: a fresh save is more valuable than
            // perfect backup chain. Recover via primary on next load; on primary corruption
            // the fallback chain in Load still tries .previous and .penultimate independently.
            try
            {
                if (File.Exists(previous))
                {
                    if (File.Exists(penultimate)) File.Delete(penultimate);
                    File.Move(previous, penultimate);   // promote previous → penultimate
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.Save: previous→penultimate rotation failed: " + ex.Message);
            }
            try
            {
                if (File.Exists(filePath))
                {
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Copy(filePath, previous);   // snapshot current → previous
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.Save: current→previous snapshot failed: " + ex.Message);
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
                    // L-079: schema 3 TLV body. tag_count[2] then per-tag (tag_id[2], byte_length[4], payload).
                    // Tag 0x0001 = weights. UnknownTags from a prior Load (forward-compat
                    // payload from a newer schema) are re-emitted verbatim so a save-from-old
                    // doesn't strip fields a future client knew about. Save() takes
                    // ILearnedModel[] so we have no Section reference here for UnknownTags —
                    // re-emit only the weights tag for the live-model path. The
                    // UnknownTag preservation path activates only on Load→ApplyProfile
                    // re-Save flows that pass through a LoadedProfile.Section (LearningService
                    // currently rebuilds via per-model LoadParameters so UnknownTags drop on
                    // intentional save-after-load — documented limitation; future Wave can
                    // route Save through LoadedProfile to preserve them across roundtrips).
                    int weightBytes = m.ParameterCount * 4;
                    ushort tagCount = 1;
                    bw.Write(tagCount);
                    bw.Write(TagId_Weights);
                    bw.Write((uint)weightBytes);
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
            // L-015: four-tier fallback chain. Each corrupt tier is preserved as
            // <path>.corrupt-<unixMs> so an operator can inspect what went wrong without
            // losing the bytes. `[PROFILE-LOAD-FAIL] tier=<name>` is emitted by LearningService
            // (which has the playerId context) only when we fell through past the primary;
            // cold-start (everything missing) uses `[PROFILE-COLD-START]` instead.
            //
            // Tiers: primary → .previous → .penultimate → embedded baseline → null.
            LastLoadTier = LoadTier.None;
            if (TryLoad(filePath, out var p, out var failure)) { p.FromBaseline = false; LastLoadTier = LoadTier.Primary; return p; }
            if (failure != null) PreserveCorrupt(filePath, failure);

            string prev = filePath + ".previous";
            if (TryLoad(prev, out var pp, out var prevFailure)) { pp.FromBaseline = false; LastLoadTier = LoadTier.Previous; return pp; }
            if (prevFailure != null) PreserveCorrupt(prev, prevFailure);

            string pen = filePath + ".penultimate";
            if (TryLoad(pen, out var ppp, out var penFailure)) { ppp.FromBaseline = false; LastLoadTier = LoadTier.Penultimate; return ppp; }
            if (penFailure != null) PreserveCorrupt(pen, penFailure);

            if (baselineBytes != null && baselineBytes.Length > 0)
            {
                if (TryLoadFromBytes(baselineBytes, out var pb, out _)) { pb.FromBaseline = true; LastLoadTier = LoadTier.Baseline; return pb; }
            }

            return null;
        }

        /// <summary>
        /// L-015: which tier the last <see cref="Load"/> call resolved at. Read by
        /// LearningService immediately after Load to decide whether to emit
        /// `[PROFILE-LOAD-FAIL] tier=N` vs `[PROFILE-COLD-START]`. Reset to None at the
        /// start of every Load call.
        /// </summary>
        public enum LoadTier { None, Primary, Previous, Penultimate, Baseline }
        public static LoadTier LastLoadTier { get; private set; } = LoadTier.None;

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
                        // L-079: schema-conditional body. v<3 uses flat float[] payload;
                        // v>=3 uses TLV (tag_count[2] then per-tag tag_id[2] byte_length[4] payload).
                        // Existing size-assertion only applies to the flat-float branch.
                        var section = new LoadedProfile.Section
                        {
                            Id = (ModelId)idByte,
                            ArchitectureVersion = archVer,
                        };
                        if (p.SchemaVersion < 3)
                        {
                            uint paramBytes = br.ReadUInt32();
                            if (paramBytes != paramCount * 4) { failureCategory = "section-size-mismatch"; return false; }
                            var weights = new float[paramCount];
                            for (uint j = 0; j < paramCount; j++) weights[j] = br.ReadSingle();
                            section.Weights = weights;
                        }
                        else
                        {
                            ushort tagCount = br.ReadUInt16();
                            for (int t = 0; t < tagCount; t++)
                            {
                                ushort tagId = br.ReadUInt16();
                                uint bodyLen = br.ReadUInt32();
                                if (tagId == TagId_Weights)
                                {
                                    if (bodyLen != paramCount * 4) { failureCategory = "tlv-weights-size-mismatch"; return false; }
                                    var weights = new float[paramCount];
                                    for (uint j = 0; j < paramCount; j++) weights[j] = br.ReadSingle();
                                    section.Weights = weights;
                                }
                                else
                                {
                                    // Unknown tag — preserve verbatim so Save re-emits it.
                                    var payload = br.ReadBytes((int)bodyLen);
                                    section.UnknownTags.Add(
                                        new System.Collections.Generic.KeyValuePair<ushort, byte[]>(tagId, payload));
                                }
                            }
                            if (section.Weights == null)
                            {
                                // TLV file without a weights tag — refuse rather than load zeros.
                                failureCategory = "tlv-missing-weights"; return false;
                            }
                        }
                        p.Sections[i] = section;
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
    ///
    /// L-014: the v0.2 switch-statement is gone. Migrations self-register via
    /// `[SmartMigration(fromVersion: N)]` on a static class exposing `public static void
    /// Up(LoadedProfile)`. The registry's static ctor walks the executing assembly,
    /// asserts coverage of `[0, MaxSchemaVersion)` without gaps, and dispatches by
    /// FromVersion. A missing migration is a `TypeInitializationException` at first
    /// MigrationRunner.RunForward — schema bumps cannot ship with version holes.
    /// </summary>
    public static class MigrationRunner
    {
        // L-014: highest FromVersion any migration claims (= max(FromVersion) + 1 = current
        // CurrentSchemaVersion floor). Computed in static ctor; profile saves write
        // `profile.SchemaVersion = MaxSchemaVersion` after RunForward returns successfully.
        public static uint MaxSchemaVersion { get; private set; }

        // Indexed by FromVersion → migration delegate (Up).
        private static readonly System.Collections.Generic.Dictionary<uint, Action<LoadedProfile>> _byFromVersion
            = new System.Collections.Generic.Dictionary<uint, Action<LoadedProfile>>();

        // Throw on first use if registry init failed — preserves the stop-the-world
        // behaviour the original switch had on a missing case.
        private static readonly Exception _initError;

        static MigrationRunner()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                uint maxFrom = 0;
                bool anyMigration = false;
                foreach (var type in asm.GetTypes())
                {
                    var attr = (Migrations.SmartMigrationAttribute)System.Attribute.GetCustomAttribute(
                        type, typeof(Migrations.SmartMigrationAttribute));
                    if (attr == null) continue;
                    anyMigration = true;
                    var up = type.GetMethod("Up",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null, new[] { typeof(LoadedProfile) }, null);
                    if (up == null)
                    {
                        throw new InvalidOperationException(
                            "Smart.Learning: [SmartMigration] type '" + type.FullName +
                            "' missing required `public static void Up(LoadedProfile)` method.");
                    }
                    if (_byFromVersion.ContainsKey(attr.FromVersion))
                    {
                        throw new InvalidOperationException(
                            "Smart.Learning: duplicate [SmartMigration(fromVersion=" + attr.FromVersion
                            + ")] — '" + type.FullName + "' collides with prior registration.");
                    }
                    var del = (Action<LoadedProfile>)System.Delegate.CreateDelegate(
                        typeof(Action<LoadedProfile>), up);
                    _byFromVersion[attr.FromVersion] = del;
                    if (attr.FromVersion + 1 > maxFrom) maxFrom = attr.FromVersion + 1;
                }
                if (!anyMigration)
                {
                    throw new InvalidOperationException(
                        "Smart.Learning: no [SmartMigration] types found in assembly. Schema floor undefined.");
                }
                // Coverage assertion: every from-version in [0, maxFrom) must be registered.
                for (uint v = 0; v < maxFrom; v++)
                {
                    if (!_byFromVersion.ContainsKey(v))
                    {
                        throw new InvalidOperationException(
                            "Smart.Learning: schema-migration ladder has a hole at v" + v + " → v" + (v + 1)
                            + ". Add a [SmartMigration(fromVersion=" + v + ")] type.");
                    }
                }
                MaxSchemaVersion = maxFrom;
            }
            catch (Exception ex)
            {
                _initError = ex;
            }
        }

        public static void RunForward(LoadedProfile profile, uint targetVersion)
        {
            if (_initError != null) throw _initError;
            for (uint v = profile.SchemaVersion; v < targetVersion; v++)
            {
                if (!_byFromVersion.TryGetValue(v, out var up))
                {
                    throw new InvalidOperationException(
                        "Smart.Learning: no forward migration registered for schema version " + v + " → " + (v + 1));
                }
                up(profile);
            }
            profile.SchemaVersion = targetVersion;
        }
    }
}
