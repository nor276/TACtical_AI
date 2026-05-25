using System.Collections.Generic;
using TAC_AI.AI.Behaviors;

namespace TAC_AI.AI.Engine.Registry
{
    /// <summary>One discovered behavior module: the concrete IBehavior type + its discovery keys.</summary>
    public sealed class ModuleEntry
    {
        public readonly System.Type Type;
        public readonly AIBehaviorCategory Category;
        public readonly VehicleClass VehicleClass;
        public readonly string Id;

        public ModuleEntry(System.Type type, AIBehaviorCategory category, VehicleClass vehicleClass, string id)
        {
            Type = type;
            Category = category;
            VehicleClass = vehicleClass;
            Id = id;
        }

        /// <summary>Fresh instance. Behaviors are stateless-per-class (per-tech state lives in the helper bus).</summary>
        public IBehavior Create() => (IBehavior)System.Activator.CreateInstance(Type);
    }

    /// <summary>
    /// Discovered behaviors keyed by (VehicleClass, Category) and by global Id. Populated once by
    /// AIModuleLoader at boot; there is no central switch - a module is registered simply by existing
    /// (being an IBehavior in the assembly with the right attribute/namespace). 4.6.1: no ValueTuple keys.
    /// </summary>
    public static class AIModuleRegistry
    {
        // class:category -> entries (composite string key avoids System.ValueTuple on 4.6.1)
        private static readonly Dictionary<string, List<ModuleEntry>> byClassCategory = new Dictionary<string, List<ModuleEntry>>();
        private static readonly Dictionary<string, ModuleEntry> byId = new Dictionary<string, ModuleEntry>();

        private static string Key(VehicleClass vc, AIBehaviorCategory cat) => ((int)vc) + ":" + ((int)cat);

        public static void Clear()
        {
            byClassCategory.Clear();
            byId.Clear();
        }

        public static void Add(ModuleEntry entry)
        {
            if (entry == null) return;
            string k = Key(entry.VehicleClass, entry.Category);
            if (!byClassCategory.TryGetValue(k, out var list))
            {
                list = new List<ModuleEntry>();
                byClassCategory.Add(k, list);
            }
            list.Add(entry);
            byId[entry.Id] = entry;   // last writer wins; ids are expected unique
        }

        /// <summary>Behaviors available for a vehicle class + category. Shared (loco-agnostic) modules fold in too.</summary>
        public static IEnumerable<ModuleEntry> ForClassCategory(VehicleClass vc, AIBehaviorCategory cat)
        {
            if (byClassCategory.TryGetValue(Key(vc, cat), out var list))
                foreach (var e in list) yield return e;
            if (vc != VehicleClass.Shared && byClassCategory.TryGetValue(Key(VehicleClass.Shared, cat), out var shared))
                foreach (var e in shared) yield return e;
        }

        public static bool TryGet(string id, out ModuleEntry entry) => byId.TryGetValue(id, out entry);

        public static IReadOnlyDictionary<string, ModuleEntry> AllById => byId;
    }
}
