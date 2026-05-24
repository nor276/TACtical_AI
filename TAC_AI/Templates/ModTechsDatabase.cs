using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using TerraTechETCUtil;
using System.IO;
using System.Collections;

namespace TAC_AI.Templates
{
    internal class ModTechsDatabase
    {
        internal class PreloadWrapper : IModPreloadable
        {
            internal PreloadWrapper(string subject, IEnumerator bypass)
            {
                Name = subject;
                enumerator = bypass;
            }
            public PreloadWrapper(string subject, Dictionary<SpawnBaseTypes, RawTechTemplate> preCompile,
                Dictionary<SpawnBaseTypes, RawTech> target)
            {
                Name = subject;
                enumerator = ValidateAndAdd(preCompile, target);
            }
            public PreloadWrapper(string subject, List<KeyValuePair<SpawnBaseTypes, RawTechTemplate>> preCompile,
                Dictionary<SpawnBaseTypes, RawTech> target)
            {
                Name = subject;
                enumerator = ValidateAndAdd(preCompile, target);
            }
            public PreloadWrapper(string subject, List<KeyValuePair<SpawnBaseTypes, RawTechTemplate>> preCompile,
                List<KeyValuePair<SpawnBaseTypes, RawTech>> target)
            {
                Name = subject;
                enumerator = ValidateAndAdd(preCompile, target);
            }

            ModDataHandle IModPreloadable.ModHandle => KickStartTAC_AI.oInst;
            bool IModPreloadable.ChainFail => false;
            void IModPreloadable.OnFail() { }
            private string Name = null;
            public IEnumerator enumerator;
            string IModPreloadable.Subject
            {
                get
                {
                    if (Name != null)
                        return Name;
                    return Subject;
                }
            }
            string IModPreloadable.InProgress => InProgress;
            float IModPreloadable.EstPercentDone => EstPercentDone;
            int IModPreloadable.EstNumSteps => EstNumSteps;
            int IModPreloadable.EstNumStepsIterator => EstNumStepsIterator;
            IEnumerator IModPreloadable.GetEnumerator() => enumerator;
        }

        // --------------------  RawTech Lookups  --------------------
        // ~4s worth of items at 60fps. Was Mathf.RoundToInt(4 / Time.deltaTime): at static-init
        // Time.deltaTime is 0 -> +Infinity -> clamped to 1000 (4x too coarse, stalls progress UI).
        internal static int IterateExtraRate = 240;

        internal static string Subject = null;
        internal static string InProgress = null;
        internal static float EstPercentDone = 1f;
        internal static int EstNumSteps = 1;
        internal static int EstNumStepsIterator = 0;

        public const int MinimumLocalTechsToTriggerWarning = 32;
        private static int lastExtLocalCount = 0;
        private static int lastExtModCount = 0;

        public static int ExtPopTechsAllCount() => ExtPopTechsAll.Count;
        public static RawTech ExtPopTechsAllLookup(int index)
        {
            if (ExtPopTechsAll.Count <= index)
            {
                DebugTAC_AI.Exception("Our external population composed of Techs that player and external mods have added only amounts to " +
                   ExtPopTechsAll.Count + ", but the final template lookup ID is " + index + " which is outside our range of [0-"
                    + (ExtPopTechsAll.Count - 1) + "].  How is this possible?  Was our collection changed DURING our search???");
            }
            if (index < 0)
            {
                DebugTAC_AI.Exception("We tried to pick a negative index of (" + index + ")???  But that shouldn't happen normally...");
            }
            return ExtPopTechsAll[index];
        }
        public static RawTech ExtPopTechsAllFindByName(string name)
        {
            if (name == null)
                throw new ArgumentNullException("name");
            return ExtPopTechsAll.Find(delegate (RawTech cand) { return cand.techName == name; });
        }

        private static void InsureFactionLevel(RawTech tech)
        {
            if (tech.factionLim == FactionLevel.NULL)
            {
                foreach (var item in tech.savedTech)
                {
                    if (BlockIndexer.StringToBlockType(item.t, out var BT))
                    {
                        var corp = ManSpawn.inst.GetCorporation(BT);
                        if (corp != FactionSubTypes.NULL)
                        {
                            var level = RawTechUtil.GetFactionLevel(corp);
                            if (tech.factionLim <= level)
                                tech.factionLim = level;
                        }
                    }
                }
#if DEBUG
                DebugTAC_AI.Log(KickStart.ModID + ": (RawTechs) The tech \"" + tech.techName +
                    "\" was not assigned a proper factionLim, so it was given " + tech.factionLim);
#endif
            }
        }

        // --------------------  MAIN RawTech Validation  --------------------
        private static List<RawTech> ExtPopTechsAll = new List<RawTech>();
        public static void ValidateAllStringTechs()
        {
            ValidateAndAddAllInternalTechs();
            ValidateAndAddAllExternalTechs();
        }
        public static bool DoWeNotHaveEnoughLocalTechs()
        {
            return ExtPopTechsAll.Count < MinimumLocalTechsToTriggerWarning;
        }

        internal static void EnqueueToDo(IModPreloadable preloadable)
        {
            DebugTAC_AI.Log(KickStart.ModID + ": EnqueueToDo()");
            if (ManGameMode.inst.GetModePhase() != ManGameMode.GameState.InGame)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": PreloadThis()");
                InvokeHelper.PreloadThis(preloadable);
            }
            else
            {
                DebugTAC_AI.Log(KickStart.ModID + ": InvokeCoroutine()");
                InvokeHelper.InvokeCoroutine(preloadable.GetEnumerator());
            }
        }

        // --------------------  Internal RawTech Validation  --------------------
        public static Dictionary<SpawnBaseTypes, RawTech> InternalPopTechs;
        public static HashSet<SpawnBaseTypes> InternalHasModBlocks = new HashSet<SpawnBaseTypes>();
        public static bool SkipUnmodded = false;
        public static void ValidateAndAddAllInternalTechs(bool reloadPublic = true)
        {
            EnqueueToDo(new PreloadWrapper("Loading global techs - ", AsyncValidateAndAddAllInternalTechs(reloadPublic)));
        }
        private static float startTimeInt;
        private static IEnumerator AsyncValidateAndAddAllInternalTechs(bool reloadPublic = true)
        {
            DebugTAC_AI.Log(KickStart.ModID + ": Starting load for internal RawTechs...");
            if (InternalPopTechs == null)
                InternalPopTechs = new Dictionary<SpawnBaseTypes, RawTech>();
            InternalPopTechs.Clear();
            InternalHasModBlocks.Clear();
            var enumerator = AsyncValidateAndAddInternalTechs();
            while (enumerator.MoveNext())
                yield return enumerator.Current;
            SkipUnmodded = true;
        }
        private static IEnumerator AsyncValidateAndAddInternalTechs(bool reloadPublic = true)
        {
            startTimeInt = Time.realtimeSinceStartup;
#if !DEBUG
            if (!KickStart.TryForceOnlyPlayerSpawns)
            {
                var baseGameSpawns = SpecialAISpawner.ReturnAllBaseGameSpawns();
                var enumerator = ValidateAndAdd(baseGameSpawns, InternalPopTechs);
                while (enumerator.MoveNext())
                    yield return enumerator.Current;
            }
#endif
            foreach (var step in TempStorage.GetAllTemp())
                yield return step;
            AddInternalTechs(TempStorage.techBasesPrefab);

            foreach (var step in CommunityStorage.GenerateCommunityStored())
                yield return step;
            AddInternalTechs(CommunityStorage.CommunityStored);

            if (reloadPublic)
            {
                foreach (var step in CommunityStorage.GenerateCommunityClustered())
                    yield return step;
                AddInternalTechs(CommunityStorage.CommunityClustered);
            }

            InProgress = "Cleaning up...";
            EstNumSteps = 1;
            EstNumStepsIterator = 0;
            EstPercentDone = 0;
            yield return new WaitForEndOfFrame();
            try
            {
                CommunityCluster.Organize(ref InternalPopTechs);
                InvokeHelper.Invoke(DelayedValidateAndAddBaseGameTechs, 3);
            }
            catch (InsufficientMemoryException)
            {
                GC.Collect(); // Do it NOW IT'S AN EMERGENCY
                DebugTAC_AI.FatalError("Advanced AI ran COMPLETELY OUT of memory when loading enemy spawns." +
                    " Some enemies might be corrupted!");
            }
            catch (OutOfMemoryException)
            {
                GC.Collect(); // Do it NOW IT'S AN EMERGENCY
                DebugTAC_AI.FatalError("Advanced AI ran out of memory when loading enemy spawns." +
                    " Some enemies might be corrupted!");
            }
            EstNumStepsIterator = 3;
            EstPercentDone = 1f;
            yield return new WaitForEndOfFrame();
            DebugTAC_AI.Log(KickStart.ModID + ": Finished in " + (Time.realtimeSinceStartup - startTimeInt).ToString("F") + " seconds");
        }

        public static void DelayedValidateAndAddBaseGameTechs()
        {
            /*
            if (ManPresetFilter.inst.IsSettingUp || ManPop.inst.IsSettingUp)
                InvokeHelper.Invoke(DelayedValidateAndAddBaseGameTechs, 0.25f);
            else
                ValidateAndAddTechs(SpecialAISpawner.ReturnAllBaseGameSpawns());
            */
        }
        private static void AddInternalTechs(IEnumerable<KeyValuePair<SpawnBaseTypes, RawTech>> compile)
        {
            foreach (KeyValuePair<SpawnBaseTypes, RawTech> pair in compile)
            {
                InsureFactionLevel(pair.Value);
                InternalPopTechs.Add(pair.Key, pair.Value);
            }
        }

        private static IEnumerator ValidateAndAdd(Dictionary<SpawnBaseTypes, RawTechTemplate> preCompile, Dictionary<SpawnBaseTypes, RawTech> target)
        {
            EstNumSteps = preCompile.Count;
            EstNumStepsIterator = 0;
            int iterateExtra = 0;
            foreach (KeyValuePair<SpawnBaseTypes, RawTechTemplate> pair in preCompile)
            {
                if (SkipUnmodded && !InternalHasModBlocks.Contains(pair.Key))
                    continue;
                InProgress = pair.Key.ToString();
                iterateExtra++;
                if (iterateExtra >= IterateExtraRate)
                {
                    iterateExtra = 0;
                    EstPercentDone = EstNumStepsIterator / (float)EstNumSteps;
                    yield return new WaitForEndOfFrame();
                }
                RawTech inst = pair.Value.ToActive();
                try
                {
                    var status = inst.ValidateBlocksInTech(false, true);
                    if (status.CanLoadAnyBlocks())
                    {
                        if (!SkipUnmodded && status.HasModded())
                            InternalHasModBlocks.Add(pair.Key);
                        InsureFactionLevel(inst);
                        target.Add(pair.Key, inst);
                    }
                    else
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": (Prefabs) Could not load " + pair.Value.techName +
                            " as the load operation encountered an error");
                    }
                }
                catch (Exception e)
                {
                    DebugTAC_AI.LogLoad(KickStart.ModID + ": (Prefabs) Could not load " + pair.Value.techName +
                        " as the load operation encountered a serious error - " + e);
                }
                EstNumStepsIterator++;
            }
            InProgress = "Cleaning up...";
            EstPercentDone = 1f;
            yield return new WaitForEndOfFrame();
            preCompile.Clear(); // GC, do your duty
            yield return new WaitForEndOfFrame();
            EstPercentDone = 1f;
            EstNumSteps = 0;
            EstNumStepsIterator = 0;
        }
        private static IEnumerator ValidateAndAdd(List<KeyValuePair<SpawnBaseTypes, RawTechTemplate>> preCompile, List<KeyValuePair<SpawnBaseTypes, RawTech>> target)
        {
            EstNumSteps = preCompile.Count;
            EstNumStepsIterator = 0;
            int iterateExtra = 0;
            foreach (KeyValuePair<SpawnBaseTypes, RawTechTemplate> pair in preCompile)
            {
                if (SkipUnmodded && !InternalHasModBlocks.Contains(pair.Key))
                    continue;
                InProgress = pair.Key.ToString();
                iterateExtra++;
                if (iterateExtra >= IterateExtraRate)
                {
                    iterateExtra = 0;
                    EstPercentDone = EstNumStepsIterator / (float)EstNumSteps;
                    yield return new WaitForEndOfFrame();
                }
                try
                {
                    RawTech inst = pair.Value.ToActive();
                    try
                    {
                        var status = inst.ValidateBlocksInTech(true, false);
                        if (status.CanLoadAnyBlocks())
                        {
                            if (!SkipUnmodded && status.HasModded())
                                InternalHasModBlocks.Add(pair.Key);
                            InsureFactionLevel(inst);
                            target.Add(new KeyValuePair<SpawnBaseTypes, RawTech>(pair.Key, inst));
                        }
                        else
                        {
                            DebugTAC_AI.Log(KickStart.ModID + ": (CommunityInternal) Could not load " + pair.Value.techName +
                                " as the load operation encountered an error");
                        }
                    }
                    catch (Exception e)
                    {
                        DebugTAC_AI.LogLoad(KickStart.ModID + ": (CommunityInternal) Could not load " + pair.Value.techName +
                            " as the load operation encountered a serious error - " + e);
                    }
                }
                catch
                {
                    DebugTAC_AI.LogLoad(KickStart.ModID + ": (CommunityInternal) Could not load " + pair.Value.techName +
                        " as it was completely corrupted");
                }
                EstNumStepsIterator++;
            }

            InProgress = "Cleaning up...";
            EstPercentDone = 1f;
            yield return new WaitForEndOfFrame();
            preCompile.Clear(); // GC, do your duty
            yield return new WaitForEndOfFrame();
            EstPercentDone = 1f;
            EstNumSteps = 0;
            EstNumStepsIterator = 0;
        }
        private static IEnumerator ValidateAndAdd(List<KeyValuePair<SpawnBaseTypes, RawTechTemplate>> preCompile, Dictionary<SpawnBaseTypes, RawTech> target)
        {
            EstNumSteps = preCompile.Count;
            EstNumStepsIterator = 0;
            int iterateExtra = 0;
            foreach (KeyValuePair<SpawnBaseTypes, RawTechTemplate> pair in preCompile)
            {
                if (SkipUnmodded && !InternalHasModBlocks.Contains(pair.Key))
                    continue;
                InProgress = pair.Key.ToString();
                iterateExtra++;
                if (iterateExtra >= IterateExtraRate)
                {
                    iterateExtra = 0;
                    EstPercentDone = EstNumStepsIterator / (float)EstNumSteps;
                    yield return new WaitForEndOfFrame();
                }
                try
                {
                    RawTech inst = pair.Value.ToActive();
                    var status = inst.ValidateBlocksInTech(true, false);
                    if (status.CanLoadAnyBlocks())
                    {
                        if (!SkipUnmodded && status.HasModded())
                            InternalHasModBlocks.Add(pair.Key);
                        InsureFactionLevel(inst);
                        target.Add(pair.Key, inst);
                    }
                    else
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": (VanillaInternal) Could not load " + pair.Value.techName + " as the load operation encountered an error");
                    }
                }
                catch { }
                EstNumStepsIterator++;
            }
            InProgress = "Cleaning up...";
            EstPercentDone = 1f;
            yield return new WaitForEndOfFrame();
            preCompile.Clear(); // GC, do your duty
            yield return new WaitForEndOfFrame();
            EstPercentDone = 1f;
            EstNumSteps = 0;
            EstNumStepsIterator = 0;
        }

        // --------------------  External RawTech Validation  --------------------
        internal static List<RawTech> ExtPopTechsLocal = new List<RawTech>();
        internal static List<RawTech> ExtPopTechsMods = new List<RawTech>();

        public static void ValidateAndAddAllExternalTechs(bool force = false)
        {
            EnqueueToDo(new PreloadWrapper(null, DoValidateAndAddAllExternalTechsAsync(force)));
        }
        public static IEnumerator DoValidateAndAddAllExternalTechsAsync(bool force)
        {
            DebugTAC_AI.Log(KickStart.ModID + ": DoValidateAndAddAllExternalTechsAsync()");
            foreach (var item in DoValidateClientTechs(force))
                yield return item;
            foreach (var item in DoValidateModdedTechs(force))
                yield return item;
            foreach (var item in CombineExtTechPools())
                yield return item;
        }
        public static IEnumerator DoValidateAndAddClientTechsAsync(bool force)
        {
            DebugTAC_AI.Log(KickStart.ModID + ": DoValidateAndAddClientTechsAsync()");
            foreach (var item in DoValidateClientTechs(force))
                yield return item;
            foreach (var item in CombineExtTechPools())
                yield return item;
        }
        public static IEnumerator DoValidateAndAddModdedTechsAsync(bool force)
        {
            DebugTAC_AI.Log(KickStart.ModID + ": DoValidateAndAddModdedTechsAsync()");
            foreach (var item in DoValidateModdedTechs(force))
                yield return item;
            foreach (var item in CombineExtTechPools())
                yield return item;
        }

        private static float startTimeExtClient;
        private static IEnumerable DoValidateClientTechs(bool force)
        {
            DebugTAC_AI.Log(KickStart.ModID + ": Starting load for external RawTechs...");
            startTimeExtClient = Time.realtimeSinceStartup;
            Subject = "Loading Local Techs - ";
            EstNumSteps = 0;
            EstNumStepsIterator = 0;
            EstPercentDone = 0;
            RawTechExporter.ValidateEnemyFolder();
            int tCount = RawTechExporter.GetTechCounts();
            if (tCount != lastExtLocalCount || force)
            {
                ExtPopTechsLocal.Clear();
                List<RawTechTemplate> ExternalTechsRaw = RawTechExporter.LoadAllEnemyTechs();
                EstNumSteps = ExternalTechsRaw.Count;
                yield return new WaitForEndOfFrame();
                int iterateExtra = 0;
                foreach (RawTechTemplate raw in ExternalTechsRaw)
                {
                    InProgress = raw.techName.NullOrEmpty() ? "<NULL>" : raw.techName;
                    iterateExtra++;
                    if (iterateExtra >= IterateExtraRate)
                    {
                        iterateExtra = 0;
                        yield return new WaitForEndOfFrame();
                    }
                    RawTech inst = raw.ToActive();
                    var status = inst.ValidateBlocksInTech(true, false);
                    if (status.CanLoadAnyBlocks())
                        ExtPopTechsLocal.Add(inst);
                    else
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Could not load local RawTech " + inst.techName + " as the load operation encountered an error");
                    }
                    EstNumStepsIterator++;
                    EstPercentDone = EstNumStepsIterator / (float)EstNumSteps;
                    yield return null;
                }
                DebugTAC_AI.Log(KickStart.ModID + ": Pushed " + ExtPopTechsLocal.Count + " RawTechs from the Local pool");
                lastExtLocalCount = tCount;
            }
            EstPercentDone = 1f;
            EstNumSteps = 0;
            EstNumStepsIterator = 0;
            yield return new WaitForEndOfFrame();
            DebugTAC_AI.Log(KickStart.ModID + ": Finished in " + (Time.realtimeSinceStartup - startTimeExtClient).ToString("F") + " seconds");
        }
        private static float startTimeExtModded;
        private static IEnumerable DoValidateModdedTechs(bool force)
        {
            DebugTAC_AI.Log(KickStart.ModID + ": Starting load for external RawTechs...");
            startTimeExtModded = Time.realtimeSinceStartup;
            Subject = "Loading Other Mod Techs - ";
            EstNumSteps = 0;
            EstNumStepsIterator = 0;
            EstPercentDone = 0;
            int tMCount = RawTechExporter.GetRawTechsCountExternalMods();
            if (lastExtModCount != tMCount || force)
            {
                ExtPopTechsMods.Clear();
                List<RawTechTemplate> ExternalTechsRaw = RawTechExporter.LoadAllEnemyTechsExternalMods();
                EstNumSteps = ExternalTechsRaw.Count;
                yield return new WaitForEndOfFrame();
                int iterateExtra = 0;
                foreach (RawTechTemplate raw in ExternalTechsRaw)
                {
                    InProgress = raw.techName.NullOrEmpty() ? "<NULL>" : raw.techName;
                    iterateExtra++;
                    if (iterateExtra >= IterateExtraRate)
                    {
                        iterateExtra = 0;
                        yield return new WaitForEndOfFrame();
                    }
                    RawTech inst = raw.ToActive();
                    var status = inst.ValidateBlocksInTech(true, false);
                    if (status.CanLoadAnyBlocks())
                    {
                        if (inst.purposes == null)
                            inst.purposes = new HashSet<BasePurpose>();
                        if (inst.techName == null)
                            inst.techName = "<NULL>";
                        ExtPopTechsMods.Add(inst);
                    }
                    else
                    {
                        DebugTAC_AI.Log(KickStart.ModID + ": Could not load ModBundle RawTech " + raw.techName + " as the load operation encountered an error");
                    }
                    EstNumStepsIterator++;
                    EstPercentDone = EstNumStepsIterator / (float)EstNumSteps;
                    yield return new WaitForEndOfFrame();
                }
                DebugTAC_AI.Log(KickStart.ModID + ": Pushed " + ExtPopTechsMods.Count + " RawTechs from the Mod pool");
                lastExtModCount = tMCount;

            }
            EstPercentDone = 1f;
            EstNumSteps = 0;
            EstNumStepsIterator = 0;
            yield return new WaitForEndOfFrame();
            DebugTAC_AI.Log(KickStart.ModID + ": Finished in " + (Time.realtimeSinceStartup - startTimeExtModded).ToString("F") + " seconds");
        }

        private static IEnumerable CombineExtTechPools()
        {
            Subject = "Combining External Techs - ";
            EstNumSteps = 3;
            EstNumStepsIterator = 0;
            EstPercentDone = 0;
            yield return new WaitForEndOfFrame();

            ExtPopTechsAll.Clear();
            ExtPopTechsAll.AddRange(ExtPopTechsLocal);
            ExtPopTechsAll.AddRange(ExtPopTechsMods);
            EstNumStepsIterator = 1;
            EstPercentDone = 1f / 3f;
            yield return new WaitForEndOfFrame();
            DebugTAC_AI.Log(KickStart.ModID + ": Pushed a total of " + ExtPopTechsAll.Count + " to the external tech pool.");
            DebugRawTechSpawner.Organize(ref ExtPopTechsLocal);
            EstNumStepsIterator = 2;
            EstPercentDone = 2f / 3f;
            yield return new WaitForEndOfFrame();
            DebugRawTechSpawner.Organize(ref ExtPopTechsMods);
            EstNumStepsIterator = 3;
            EstPercentDone = 1f;
            yield return new WaitForEndOfFrame();

            InProgress = "Cleaning up...";
            EstNumSteps = 0;
            EstNumStepsIterator = 0;
            EstPercentDone = 1f;
            yield return new WaitForEndOfFrame();
            GC.Collect(); // GC, do your duty
            yield return new WaitForEndOfFrame();
            EstPercentDone = 1f;
            yield return new WaitForEndOfFrame();
        }

        public static void ValidateAndAddAllExternalTechsIMMEDEATELY(bool force = false)
        {
            try
            {
                var iterator = DoValidateAndAddAllExternalTechsAsync(force);
                while (iterator.MoveNext()) { }
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log("CRASH on ValidateAndAddAllExternalTechs - " + e);
            }
        }

        // --------------------  RawTech Utilities  --------------------
        public static bool ValidateBlocksInTechAndPurgeIfNeeded(List<RawBlockMem> toScreen)
        {
            try
            {
                if (toScreen.Count == 0)
                {
                    DebugTAC_AI.Log(KickStart.ModID + ": ValidateBlocksInTechAndPurgeIfNeeded - FAILED as no blocks were present!");
                    return false;
                }
                bool valid = true;
                for (int step = toScreen.Count - 1; step >= 0; step--)
                {
                    RawBlockMem bloc = toScreen[step];
                    BlockTypes type = BlockIndexer.StringToBlockType(bloc.t);
                    if (!Singleton.Manager<ManSpawn>.inst.IsBlockAllowedInCurrentGameMode(type))
                    {
                        valid = false;
                        toScreen.RemoveAt(step);
                        continue;
                    }
                    bloc.t = Singleton.Manager<ManSpawn>.inst.GetBlockPrefab(type).name;
                }
                return valid;
            }
            catch (Exception e)
            {
                DebugTAC_AI.Log(KickStart.ModID + ": ValidateBlocksInTech - Tech was corrupted via unexpected mod changes! - " + e);
                return false; 
            }
        }

    }
}
