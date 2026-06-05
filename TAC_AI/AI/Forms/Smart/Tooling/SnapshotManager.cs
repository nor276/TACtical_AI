using System;
using System.Collections.Generic;
using System.IO;
using TAC_AI.AI.Forms.Smart.Learning;

namespace TAC_AI.AI.Forms.Smart.Tooling
{
    /// <summary>
    /// P10 Item 33: named-snapshot management for the learning profile.
    ///
    /// Save copies the live profile to <c>SmartAI/Profiles/snapshots/{name}.bin</c>.
    /// Restore copies the named snapshot back over the live profile slot.
    /// List enumerates snapshot names (without extension).
    /// ResetThreat / ResetAll / ResetTunables clear specific subsystems.
    ///
    /// Console-command surface in <see cref="SmartConsoleCommands"/> exposes these as
    /// <c>smart.snapshot save</c> / <c>smart.snapshot restore</c> / etc.
    /// </summary>
    public static class SnapshotManager
    {
        public const string SnapshotSubpath = "SmartAI/Profiles/snapshots";

        private static string SnapshotDir(string modDir)
            => Path.Combine(modDir ?? ".", SnapshotSubpath);

        /// <summary>Resolve mod-dir-relative path; defaults to <c>{cwd}/Mods</c> matching SmartRuntime.Init.</summary>
        private static string ModDir()
            => Path.Combine(Environment.CurrentDirectory, "Mods");

        /// <summary>Save the current learning profile under <paramref name="name"/>. Returns false on error.</summary>
        public static bool Save(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!LearningService.IsRunning) return false;
            try
            {
                string dir = SnapshotDir(ModDir());
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, SanitizeName(name) + ".bin");
                var models = new ILearnedModel[]
                {
                    LearningService.Intent, LearningService.ActionValue,
                    LearningService.Residual, LearningService.Threat,
                };
                ProfilePersistence.Save(path, models);
                DebugTAC_AI.Log("Smart.SnapshotManager: saved snapshot '" + name + "' → " + path);
                return true;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.SnapshotManager.Save('" + name + "'): " + ex.Message);
                return false;
            }
        }

        /// <summary>Restore the named snapshot over the live profile. Returns false on error.</summary>
        public static bool Restore(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!LearningService.IsRunning) return false;
            try
            {
                string dir = SnapshotDir(ModDir());
                string path = Path.Combine(dir, SanitizeName(name) + ".bin");
                if (!File.Exists(path))
                {
                    DebugTAC_AI.LogWarning("Smart.SnapshotManager.Restore: snapshot '" + name + "' not found at " + path);
                    return false;
                }
                var profile = ProfilePersistence.Load(path, baselineBytes: null);
                if (profile == null)
                {
                    DebugTAC_AI.LogWarning("Smart.SnapshotManager.Restore: profile load returned null for '" + name + "'");
                    return false;
                }
                // ProfilePersistence applies the sections to the live models — same path
                // LearningService.LoadProfile uses internally. We re-call SaveProfile after
                // so the live local.bin gets the restored bytes.
                ApplyToLiveModels(profile);
                LearningService.SaveProfile(LearningService.CurrentPlayerId);
                DebugTAC_AI.Log("Smart.SnapshotManager: restored snapshot '" + name + "' from " + path);
                return true;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.SnapshotManager.Restore('" + name + "'): " + ex.Message);
                return false;
            }
        }

        /// <summary>List snapshot names (without .bin extension). Empty list on error.</summary>
        public static IReadOnlyList<string> List()
        {
            var result = new List<string>();
            try
            {
                string dir = SnapshotDir(ModDir());
                if (!Directory.Exists(dir)) return result;
                var files = Directory.GetFiles(dir, "*.bin");
                for (int i = 0; i < files.Length; i++)
                    result.Add(Path.GetFileNameWithoutExtension(files[i]));
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarning("Smart.SnapshotManager.List: " + ex.Message);
            }
            return result;
        }

        /// <summary>Reset the Threat model parameters (Glorot re-init). Other models unchanged.</summary>
        public static void ResetThreat()
        {
            if (!LearningService.IsRunning || LearningService.Threat == null) return;
            ReinitModel(LearningService.Threat);
            DebugTAC_AI.Log("Smart.SnapshotManager: Threat model reset to Glorot init.");
        }

        /// <summary>Reset all 4 model parameters (Glorot re-init).</summary>
        public static void ResetAll()
        {
            if (!LearningService.IsRunning) return;
            ReinitModel(LearningService.Intent);
            ReinitModel(LearningService.ActionValue);
            ReinitModel(LearningService.Residual);
            ReinitModel(LearningService.Threat);
            DebugTAC_AI.Log("Smart.SnapshotManager: all 4 models reset to Glorot init.");
        }

        /// <summary>
        /// Reset every Smart-keyed tunable to its registered default. Walks the central
        /// TunableRegistry via TunableMenuBridge filter; resets by re-binding.
        /// </summary>
        public static void ResetTunables()
        {
            int count = 0;
            foreach (var t in TunableMenuBridge.SmartEntries)
            {
                if (t == null) continue;
                try
                {
                    switch (t.Kind)
                    {
                        case Tunables.TunableKind.Float: Tunables.TunableRegistry.SetFloat(t.Key, t.DefaultFloat); break;
                        case Tunables.TunableKind.Int:   Tunables.TunableRegistry.SetInt(t.Key, t.DefaultInt); break;
                        case Tunables.TunableKind.Bool:  Tunables.TunableRegistry.SetBool(t.Key, t.DefaultBool); break;
                    }
                    count++;
                }
                catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.SnapshotManager.ResetTunables[" + t.Key + "]: " + ex.Message); }
            }
            DebugTAC_AI.Log("Smart.SnapshotManager: reset " + count + " Smart-keyed tunables to defaults.");
        }

        // ---- helpers ----

        private static void ApplyToLiveModels(LoadedProfile profile)
        {
            // Mirror LearningService.ApplyProfile's switch — duplicated rather than exposed
            // because LearningService keeps it private (per-model section IDs are stable).
            for (int i = 0; i < profile.Sections.Length; i++)
            {
                var section = profile.Sections[i];
                if (section == null) continue;
                try
                {
                    switch (section.Id)
                    {
                        case ModelId.Intent:            LearningService.Intent?.LoadParameters(section.Weights); break;
                        case ModelId.ActionValue:       LearningService.ActionValue?.LoadParameters(section.Weights); break;
                        case ModelId.TrajectoryResidual: LearningService.Residual?.LoadParameters(section.Weights); break;
                        case ModelId.ThreatAssessment:  LearningService.Threat?.LoadParameters(section.Weights); break;
                    }
                }
                catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.SnapshotManager.Apply[" + section.Id + "]: " + ex.Message); }
            }
        }

        private static void ReinitModel(ILearnedModel m)
        {
            if (m == null) return;
            // P11 T3 Item 53: real Glorot/orthogonal reset via ILearnedModel.Reset(seed).
            // Replaces the v0.2-ship zero-fill placeholder. Seed = TickCount XORed with the
            // model id inside Reset() so two models reset in sequence don't share state.
            // For reproducible tests, pass a fixed seed via a future overload — current
            // console commands intentionally take the non-deterministic path.
            try { m.Reset(Environment.TickCount); }
            catch (Exception ex) { DebugTAC_AI.LogWarning("Smart.SnapshotManager.Reinit[" + m.Id + "]: " + ex.Message); }
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
