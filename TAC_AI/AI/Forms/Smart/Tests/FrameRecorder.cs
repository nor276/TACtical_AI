using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Tests
{
    /// <summary>
    /// P10 Item 40 / P11 T8 Item 64: minimum-viable ControlFrame-output recorder.
    ///
    /// The full record-replay frame-equivalence harness (Plan T8 §"Full harness") is logged
    /// as out-of-scope per V0.2-PLAN-REV7.md — it requires a synthetic-Tank shim with five
    /// engine-surface mocks (boundsCentreWorldNoCheck / rbody / blockman / rootBlockTrans /
    /// control.m_Movement) plus a TacticalOptimizer test seam. That's ~1,050 LOC of testing
    /// infrastructure that isn't justifiable until joint-policy spawn-tests are routine.
    ///
    /// What ships here is the OUTPUT half of the harness: capture every Smart-driven tech's
    /// per-frame ControlProfile to a binary log so the operator can:
    ///   1. Record a baseline session with all policies OFF.
    ///   2. Toggle a policy + record a second session.
    ///   3. Run <c>smart.recording.diff &lt;baseline.bin&gt; &lt;policy.bin&gt;</c> from the
    ///      DevCommand console to dump a CSV of per-frame field drifts.
    ///
    /// The parity gates (P11 T1) cover subsystem-level drift detection on the live dispatch
    /// path; this recorder covers actuator-level drift, the layer downstream of all
    /// subsystems combined. Drift here that's NOT explained by a parity-gate emission means
    /// a cross-subsystem interaction the gates missed.
    ///
    /// Output file format (binary, little-endian):
    ///   Header: magic 0x53504D52 ("SPMR" little-endian: Smart ParityRecorder), schema=1
    ///   Per record: int techIdValue, long tickMono, float throttle, float steer,
    ///               float brake, byte fireAny, Vector3 aimPointWorld, float aimRadius
    ///
    /// SMART_DEV-gated: zero overhead in release builds (the whole class compiles out).
    /// </summary>
    public static class FrameRecorder
    {
        public const uint MagicHeader = 0x53504D52;   // 'S','P','M','R'
        public const int SchemaVersion = 1;
        // Per-record byte size: 4(int techId) + 8(long tickMono) + 4*3(throttle+steer+brake) + 1(fireAny) + 12(Vector3) + 4(aimRadius) = 41 bytes.
        public const int RecordSizeBytes = 41;

        /// <summary>Master gate. Default OFF; release builds compile out the writer entirely.</summary>
        public static bool Enabled = false;

        private static string _activeFilePath;
        private static BinaryWriter _writer;
        private static readonly object _writerLock = new object();

        /// <summary>
        /// Begin a new recording session. Subsequent <see cref="RecordControlFrame"/> calls
        /// write to <paramref name="fileNameNoExt"/>.bin under Mods/SmartAI/Recordings/.
        /// Closes any prior session first. Idempotent (calling twice with the same name
        /// rewinds the file).
        /// </summary>
        public static bool Begin(string fileNameNoExt)
        {
            if (string.IsNullOrEmpty(fileNameNoExt)) return false;
            try
            {
                Stop();
                string dir = Path.Combine(Environment.CurrentDirectory, "Mods", "SmartAI", "Recordings");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, Sanitize(fileNameNoExt) + ".bin");
                lock (_writerLock)
                {
                    _activeFilePath = path;
                    _writer = new BinaryWriter(File.Create(path));
                    _writer.Write(MagicHeader);
                    _writer.Write(SchemaVersion);
                    _writer.Write(MonoClock.Now());   // session start tick (long)
                }
                Enabled = true;
                DebugTAC_AI.Log("Smart.FrameRecorder: BEGIN '" + path + "'");
                return true;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.FrameRecorder.Begin: " + ex.Message);
                Enabled = false;
                return false;
            }
        }

        /// <summary>
        /// Close the active recording. No-op when not recording. Safe to call from
        /// SmartRuntime.Shutdown so a recording always flushes cleanly on session end.
        /// </summary>
        public static void Stop()
        {
            lock (_writerLock)
            {
                if (_writer == null) { Enabled = false; return; }
                try
                {
                    string path = _activeFilePath;
                    _writer.Flush();
                    _writer.Dispose();
                    _writer = null;
                    _activeFilePath = null;
                    Enabled = false;
                    DebugTAC_AI.Log("Smart.FrameRecorder: STOP '" + path + "'");
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarning("Smart.FrameRecorder.Stop: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Append one per-tech per-frame record. Called from
        /// <c>SmartForm.ControlFrame</c>'s exception-isolated try block after the
        /// controller writes to TankControl. No-op when <see cref="Enabled"/> is false.
        /// Single-writer (main-thread); the lock guards Begin/Stop racing with Record.
        /// </summary>
        public static void RecordControlFrame(int techIdValue, long tickMono,
            float throttle, float steer, float brake,
            bool fireAny, Vector3 aimPointWorld, float aimRadius)
        {
            if (!Enabled) return;
            lock (_writerLock)
            {
                if (_writer == null) return;
                try
                {
                    _writer.Write(techIdValue);
                    _writer.Write(tickMono);
                    _writer.Write(throttle);
                    _writer.Write(steer);
                    _writer.Write(brake);
                    _writer.Write(fireAny ? (byte)1 : (byte)0);
                    _writer.Write(aimPointWorld.x);
                    _writer.Write(aimPointWorld.y);
                    _writer.Write(aimPointWorld.z);
                    _writer.Write(aimRadius);
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogWarnFileOnly("frame-recorder-err",
                        "FrameRecorder.RecordControlFrame threw " + ex.GetType().Name + ": " + ex.Message);
                    // Disable to avoid log spam if the file went bad mid-session.
                    Enabled = false;
                }
            }
        }

        // ---- Reader / diff ----

        public sealed class Record
        {
            public int TechIdValue;
            public long TickMono;
            public float Throttle, Steer, Brake;
            public bool FireAny;
            public Vector3 AimPointWorld;
            public float AimRadius;
        }

        /// <summary>Load every record from a recording file. Throws on bad magic / schema.</summary>
        public static List<Record> Load(string path)
        {
            var records = new List<Record>(1024);
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                uint magic = br.ReadUInt32();
                if (magic != MagicHeader)
                    throw new InvalidDataException("FrameRecorder: bad magic header in " + path);
                int schema = br.ReadInt32();
                if (schema != SchemaVersion)
                    throw new InvalidDataException("FrameRecorder: schema=" + schema + " unsupported (expected " + SchemaVersion + ")");
                long _sessionStart = br.ReadInt64();
                while (br.BaseStream.Position + RecordSizeBytes <= br.BaseStream.Length)
                {
                    records.Add(new Record
                    {
                        TechIdValue = br.ReadInt32(),
                        TickMono = br.ReadInt64(),
                        Throttle = br.ReadSingle(),
                        Steer = br.ReadSingle(),
                        Brake = br.ReadSingle(),
                        FireAny = br.ReadByte() != 0,
                        AimPointWorld = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                        AimRadius = br.ReadSingle(),
                    });
                }
            }
            return records;
        }

        /// <summary>
        /// Diff two recordings keyed on (TechIdValue, TickMono). Emits CSV-style lines per
        /// frame whose any tracked field exceeds tolerance. Returns the diff line count
        /// (0 = bit-equivalent recordings).
        /// </summary>
        public static int DiffToFile(string baselinePath, string compareePath, string outputCsvPath)
        {
            var baseline = Load(baselinePath);
            var compare = Load(compareePath);
            var bIdx = new Dictionary<long, Record>(baseline.Count);
            for (int i = 0; i < baseline.Count; i++)
            {
                var r = baseline[i];
                long key = ((long)r.TechIdValue << 32) | (uint)(r.TickMono & 0xFFFFFFFF);
                bIdx[key] = r;
            }

            int driftCount = 0;
            const float DrivelineTol = 0.02f;
            const float AimTolM = 0.5f;
            using (var sw = new StreamWriter(outputCsvPath))
            {
                sw.WriteLine("tickMono,techId,field,baseline,comparee,delta");
                for (int i = 0; i < compare.Count; i++)
                {
                    var c = compare[i];
                    long key = ((long)c.TechIdValue << 32) | (uint)(c.TickMono & 0xFFFFFFFF);
                    if (!bIdx.TryGetValue(key, out var b)) continue;
                    driftCount += Emit(sw, b, c, "throttle", b.Throttle, c.Throttle, DrivelineTol);
                    driftCount += Emit(sw, b, c, "steer", b.Steer, c.Steer, DrivelineTol);
                    driftCount += Emit(sw, b, c, "brake", b.Brake, c.Brake, DrivelineTol);
                    if (b.FireAny != c.FireAny)
                    {
                        sw.WriteLine(c.TickMono + "," + c.TechIdValue + ",fireAny,"
                            + (b.FireAny ? 1 : 0) + "," + (c.FireAny ? 1 : 0) + ",bool-flip");
                        driftCount++;
                    }
                    float aimDelta = (b.AimPointWorld - c.AimPointWorld).magnitude;
                    if (aimDelta > AimTolM)
                    {
                        sw.WriteLine(c.TickMono + "," + c.TechIdValue + ",aimPointWorld,"
                            + b.AimPointWorld.ToString("F2") + "," + c.AimPointWorld.ToString("F2")
                            + "," + aimDelta.ToString("F3"));
                        driftCount++;
                    }
                }
                sw.WriteLine("# total drift lines: " + driftCount);
            }
            return driftCount;
        }

        private static int Emit(StreamWriter sw, Record b, Record c, string field, float bv, float cv, float tol)
        {
            float d = Mathf.Abs(bv - cv);
            if (d <= tol) return 0;
            sw.WriteLine(c.TickMono + "," + c.TechIdValue + "," + field + ","
                + bv.ToString("F4") + "," + cv.ToString("F4") + "," + d.ToString("F4"));
            return 1;
        }

        private static string Sanitize(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char ch = chars[i];
                bool ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')
                       || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
                if (!ok) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
