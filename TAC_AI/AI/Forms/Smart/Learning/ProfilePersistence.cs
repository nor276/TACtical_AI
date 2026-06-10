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
            /// BUG-046: serialized Adam optimizer state (M, V, T, LR). Null when the disk
            /// image lacks the 0x0002 tag (pre-fix profiles). ApplyProfile reflects this
            /// back into the model's _adam field so training continues across reloads
            /// without the documented ~10× first-step LR spike from a T=1 reset.
            /// </summary>
            public byte[] AdamStatePayload;
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
        // BUG-046: Adam optimizer state TLV tag. Payload layout per section:
        //   uint  : paramCount       (sanity vs section header — bail if mismatched)
        //   long  : T (step counter)
        //   float : LearningRate
        //   float[paramCount] : M
        //   float[paramCount] : V
        // Reading the tag is non-fatal: a v4 reader that doesn't know 0x0002 stashes it
        // as an UnknownTag and re-emits on next Save (BUG-023). No schema bump required.
        public const ushort TagId_AdamState = 0x0002;
        public static readonly byte[] Magic = { (byte)'S', (byte)'M', (byte)'R', (byte)'T' };

        /// <summary>
        /// Serialize the model snapshots into the profile binary at <paramref name="filePath"/>.
        /// Two-arg overload preserved for callers that have no LoadedProfile context.
        /// </summary>
        public static void Save(string filePath, ILearnedModel[] models)
        {
            Save(filePath, models, priorLoaded: null);
        }

        /// <summary>
        /// BUG-023 / BUG-076: full Save with optional <paramref name="priorLoaded"/> for
        /// UnknownTag re-emission. When supplied, per-section UnknownTags (forward-compat
        /// TLV payload from a newer client) are written verbatim after the weights tag, so
        /// a save-from-old client doesn't strip fields a future client knew about.
        ///
        /// BUG-076 ordering: the new save's bytes are built INTO .tmp BEFORE any backup
        /// rotation. If StoreParameters / write throws, .previous and .penultimate are
        /// untouched and the prior generations survive. The rotation block only runs once
        /// the tmp file is fully written + fsync'd.
        /// </summary>
        public static void Save(string filePath, ILearnedModel[] models, LoadedProfile priorLoaded)
        {
            EnsureDirectory(filePath);
            string tmp = filePath + ".tmp";
            string previous = filePath + ".previous";
            string penultimate = filePath + ".penultimate";

            // ---- Step 1: build the byte stream entirely in memory (so any failure here
            // leaves the existing on-disk ring intact — BUG-076 root fix).
            byte[] body;
            uint crc;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                bw.Write(Magic, 0, 4);
                bw.Write(CurrentSchemaVersion);
                bw.Write((long)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                for (int i = 0; i < models.Length; i++)
                {
                    var m = models[i];
                    if (m == null) continue;   // defensive — Shutdown nulls model refs
                    var weights = new float[m.ParameterCount];
                    // BUG-022 + BUG-046: serialise StoreParameters + Adam state read under
                    // SaveMutex. TrainerWorker takes the same lock around Adam.Step, so
                    // weights and M/V/T are sampled from the same consistent moment.
                    byte[] adamPayload;
                    lock (m.SaveMutex)
                    {
                        m.StoreParameters(weights);
                        adamPayload = BuildAdamPayload(GetAdamStateOrNull(m));
                    }
                    bw.Write((byte)m.Id);
                    bw.Write(m.ArchitectureVersion);
                    bw.Write((uint)m.ParameterCount);

                    // Schema 3+ TLV body. Tag 0x0001 = weights, 0x0002 = AdamState (BUG-046).
                    // Any UnknownTags carried in priorLoaded for this section get re-emitted
                    // verbatim so a save-after-load roundtrip doesn't strip forward-compat
                    // fields (BUG-023). UnknownTags that overlap a tag id we now understand
                    // (e.g. priorLoaded carried 0x0002 from a future client and we emit our
                    // own 0x0002 too) get skipped from the unknown re-emit so disk doesn't
                    // duplicate the tag.
                    LoadedProfile.Section priorSection = FindSection(priorLoaded, m.Id);
                    int knownTags = 1 + (adamPayload != null ? 1 : 0);
                    int unknownTagCount = 0;
                    if (priorSection != null && priorSection.UnknownTags != null)
                    {
                        for (int u = 0; u < priorSection.UnknownTags.Count; u++)
                        {
                            ushort uid = priorSection.UnknownTags[u].Key;
                            if (uid == TagId_Weights || uid == TagId_AdamState) continue;
                            unknownTagCount++;
                        }
                    }
                    ushort tagCount = (ushort)(knownTags + unknownTagCount);
                    bw.Write(tagCount);

                    int weightBytes = m.ParameterCount * 4;
                    bw.Write(TagId_Weights);
                    bw.Write((uint)weightBytes);
                    for (int j = 0; j < weights.Length; j++) bw.Write(weights[j]);

                    if (adamPayload != null)
                    {
                        bw.Write(TagId_AdamState);
                        bw.Write((uint)adamPayload.Length);
                        bw.Write(adamPayload, 0, adamPayload.Length);
                    }

                    if (unknownTagCount > 0)
                    {
                        for (int u = 0; u < priorSection.UnknownTags.Count; u++)
                        {
                            var kv = priorSection.UnknownTags[u];
                            if (kv.Key == TagId_Weights || kv.Key == TagId_AdamState) continue;
                            bw.Write(kv.Key);
                            bw.Write((uint)(kv.Value != null ? kv.Value.Length : 0));
                            if (kv.Value != null && kv.Value.Length > 0)
                                bw.Write(kv.Value, 0, kv.Value.Length);
                        }
                    }
                }
                bw.Flush();

                body = ms.ToArray();
                crc = Crc32(body, 0, body.Length);
            }

            // ---- Step 2: write tmp + fsync. If this throws, the backup ring is still
            // intact because we haven't touched it yet.
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs, Encoding.ASCII))
            {
                w.Write(body);
                w.Write(crc);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }

            // ---- Step 3: rotate backup ring NOW that tmp is committed.
            // L-015 / BUG-058: each step uses File.Move (atomic on NTFS) so a partial
            // failure leaves a coherent ring tier. current→previous copies via a
            // previousTmp sidecar so an aborted Copy never leaves a half-written
            // .previous. The Replace below needs the current file in place; once Replace
            // consumes tmp the current file is gone, so we snapshot first.
            try
            {
                if (File.Exists(previous))
                {
                    if (File.Exists(penultimate)) File.Delete(penultimate);
                    File.Move(previous, penultimate);
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
                    // BUG-058 hygiene: atomic-ish snapshot — Copy to previousTmp first,
                    // then File.Move into place so an aborted Copy doesn't leave a
                    // truncated .previous (which Load would CRC-fail-classify as corrupt).
                    string previousTmp = previous + ".tmp";
                    File.Copy(filePath, previousTmp, overwrite: true);
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(previousTmp, previous);
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.Learning.Save: current→previous snapshot failed: " + ex.Message);
            }

            // ---- Step 4: atomic swap tmp → current.
            // Phase 4 (FIX-PLAN.md) — File.Replace is atomic on NTFS; falls back to the
            // delete-then-move pattern on non-NTFS filesystems (degrades gracefully).
            if (File.Exists(filePath))
            {
                try
                {
                    File.Replace(tmp, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(filePath);
                    File.Move(tmp, filePath);
                }
            }
            else
            {
                File.Move(tmp, filePath);
            }
        }

        // BUG-023 helper: locate the prior-load section matching this model id so we can
        // re-emit its UnknownTags. Linear scan over <=4 sections — negligible.
        private static LoadedProfile.Section FindSection(LoadedProfile prior, ModelId id)
        {
            if (prior == null || prior.Sections == null) return null;
            for (int i = 0; i < prior.Sections.Length; i++)
            {
                var s = prior.Sections[i];
                if (s != null && s.Id == id) return s;
            }
            return null;
        }

        // BUG-046: per-model AdamState accessor. The four concrete model classes carry a
        // `private readonly AdamState _adam` field; the ILearnedModel interface does NOT
        // expose it (and is out of save-load-cluster ownership). Reflection-based access
        // is the conservative path: every model uses the same field name + AdamState type,
        // and the field is set once in the ctor (readonly), so we hand back a reference
        // the caller can read M/V/T from under model.SaveMutex. Cached at first call per
        // type so the reflection cost is paid exactly four times per process.
        private static readonly System.Collections.Generic.Dictionary<Type, System.Reflection.FieldInfo> _adamFieldCache
            = new System.Collections.Generic.Dictionary<Type, System.Reflection.FieldInfo>();
        private static AdamState GetAdamStateOrNull(ILearnedModel model)
        {
            if (model == null) return null;
            var t = model.GetType();
            System.Reflection.FieldInfo fi;
            lock (_adamFieldCache)
            {
                if (!_adamFieldCache.TryGetValue(t, out fi))
                {
                    fi = t.GetField("_adam",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic);
                    _adamFieldCache[t] = fi;   // null-cache too — don't re-search a missing field
                }
            }
            if (fi == null) return null;
            return fi.GetValue(model) as AdamState;
        }

        // BUG-046: serialize Adam state into a TLV 0x0002 payload. Layout:
        //   uint paramCount | long T | float LR | float[paramCount] M | float[paramCount] V
        // Returns null when the model has no Adam state (defensive — should be unreachable
        // for the four real models). Computed under model.SaveMutex.
        private static byte[] BuildAdamPayload(AdamState adam)
        {
            if (adam == null || adam.M == null || adam.V == null) return null;
            int n = adam.M.Length;
            if (adam.V.Length != n) return null;   // corrupted Adam state — drop tag
            int size = 4 + 8 + 4 + n * 4 + n * 4;
            using (var ms = new MemoryStream(size))
            using (var bw = new BinaryWriter(ms, Encoding.ASCII))
            {
                bw.Write((uint)n);
                bw.Write(adam.T);
                bw.Write(adam.LearningRate);
                for (int i = 0; i < n; i++) bw.Write(adam.M[i]);
                for (int i = 0; i < n; i++) bw.Write(adam.V[i]);
                bw.Flush();
                return ms.ToArray();
            }
        }

        // BUG-046: load Adam state from a TLV 0x0002 payload back into the model's
        // _adam field. Silent no-op on any inconsistency (paramCount mismatch, model has
        // no _adam, payload truncated) — the model just continues with its Glorot-init
        // Adam state, which is the documented fallback semantics ("training continues
        // across reloads" invariant becomes best-effort rather than guaranteed when
        // the saved state is incompatible with the live arch).
        // Caller MUST hold model.SaveMutex (we mutate _adam in place).
        public static void ApplyAdamStatePayload(ILearnedModel model, byte[] payload)
        {
            if (model == null || payload == null || payload.Length < 4 + 8 + 4) return;
            var adam = GetAdamStateOrNull(model);
            if (adam == null) return;
            using (var ms = new MemoryStream(payload, writable: false))
            using (var br = new BinaryReader(ms, Encoding.ASCII))
            {
                uint n = br.ReadUInt32();
                if (n != (uint)adam.M.Length) return;   // arch mismatch — keep fresh state
                if (payload.Length < 4 + 8 + 4 + n * 4 + n * 4) return;
                long t = br.ReadInt64();
                float lr = br.ReadSingle();
                for (int i = 0; i < n; i++) adam.M[i] = br.ReadSingle();
                for (int i = 0; i < n; i++) adam.V[i] = br.ReadSingle();
                adam.T = t;
                adam.LearningRate = lr;
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
                    };

                    // BUG-077: read sections until end-of-body rather than hardcoding 4.
                    // The stream is bounded to the body (CRC footer excluded by the
                    // MemoryStream length above), so PeekChar==-1 / Position>=Length is the
                    // section-loop terminator. Lets a future Save emit 3 sections (partial
                    // flush) or 5+ sections (added models, BaselineMetadata footer per
                    // BUG-064) without a hardcoded Load-side bump.
                    var sectionList = new System.Collections.Generic.List<LoadedProfile.Section>(4);
                    while (ms.Position < ms.Length)
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
                                else if (tagId == TagId_AdamState)
                                {
                                    // BUG-046: stash Adam state for ApplyProfile to reflect
                                    // into the model's _adam field. Treat as best-effort —
                                    // an arch mismatch or truncation just keeps the model's
                                    // fresh Adam state.
                                    section.AdamStatePayload = br.ReadBytes((int)bodyLen);
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
                        sectionList.Add(section);
                    }
                    p.Sections = sectionList.ToArray();

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

        /// <summary>
        /// Director rollback (T2 disk tier): load <paramref name="profilePath"/> (or its
        /// chosen rotation slot) and apply the section matching <paramref name="model"/>
        /// onto the live model under its SaveMutex. slot ∈ {"", ".previous", ".penultimate"}.
        /// Arch-version guard layers on top of the existing Load chain. Returns true if a
        /// section was applied. Reuses the existing byte format + Load fallback.
        /// </summary>
        public static bool RestoreFromCheckpoint(string profilePath, string slot, ILearnedModel model)
        {
            if (string.IsNullOrEmpty(profilePath) || model == null) return false;
            string targetPath = string.IsNullOrEmpty(slot) ? profilePath : profilePath + slot;
            try
            {
                LoadedProfile profile = Load(targetPath, baselineBytes: null);
                if (profile == null || profile.Sections == null) return false;
                for (int i = 0; i < profile.Sections.Length; i++)
                {
                    var section = profile.Sections[i];
                    if (section == null || section.Id != model.Id || section.Weights == null) continue;
                    if (section.ArchitectureVersion != model.ArchitectureVersion)
                    {
                        DebugTAC_AI.LogWarnFileOnly("director-restore-arch-" + model.Id,
                            "[DIRECTOR-ROLLBACK] restore-skip model=" + model.Id
                            + " disk=" + section.ArchitectureVersion + " live=" + model.ArchitectureVersion);
                        return false;
                    }
                    lock (model.SaveMutex)
                    {
                        model.LoadParameters(section.Weights);
                        if (section.AdamStatePayload != null)
                            ApplyAdamStatePayload(model, section.AdamStatePayload);
                    }
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-restore-throw-" + model.Id,
                    "[DIRECTOR-ROLLBACK] RestoreFromCheckpoint threw " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Director pre-action cleanup: enumerate pre-snapshot checkpoint directories under
        /// <paramref name="checkpointRoot"/> whose newest-file mtime is older than
        /// <paramref name="maxAge"/>. The Director prunes these on its tick.
        /// </summary>
        public static System.Collections.Generic.List<string> EnumerateExpiredPreSnapshots(string checkpointRoot, TimeSpan maxAge)
        {
            var expired = new System.Collections.Generic.List<string>();
            try
            {
                if (string.IsNullOrEmpty(checkpointRoot) || !Directory.Exists(checkpointRoot)) return expired;
                DateTime cutoff = DateTime.UtcNow - maxAge;
                string[] dirs = Directory.GetDirectories(checkpointRoot);
                for (int i = 0; i < dirs.Length; i++)
                {
                    try
                    {
                        DateTime newest = Directory.GetLastWriteTimeUtc(dirs[i]);
                        string[] files = Directory.GetFiles(dirs[i]);
                        for (int f = 0; f < files.Length; f++)
                        {
                            DateTime mt = File.GetLastWriteTimeUtc(files[f]);
                            if (mt > newest) newest = mt;
                        }
                        if (newest < cutoff) expired.Add(dirs[i]);
                    }
                    catch { /* skip unreadable dir */ }
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("director-enum-expired",
                    "[DIRECTOR-ROLLBACK] EnumerateExpiredPreSnapshots threw " + ex.GetType().Name + ": " + ex.Message);
            }
            return expired;
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
                // BUG-038: cold-start path never runs RunForward, so a ladder-vs-current
                // mismatch would stay dormant. Assert at static init that the registered
                // migration ladder ends EXACTLY at ProfilePersistence.CurrentSchemaVersion
                // — schema bumps must ship with a matching [SmartMigration] type, no
                // exceptions, no CI gap, no "future Wave" deferrals.
                if (maxFrom != ProfilePersistence.CurrentSchemaVersion)
                {
                    throw new InvalidOperationException(
                        "Smart.Learning: schema-migration ladder MaxSchemaVersion=" + maxFrom
                        + " does not match ProfilePersistence.CurrentSchemaVersion="
                        + ProfilePersistence.CurrentSchemaVersion
                        + ". Add or remove [SmartMigration] types so the highest fromVersion+1 equals the current schema.");
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
