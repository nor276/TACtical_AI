using System;
using System.Linq;
using System.Reflection;
using TerraTechETCUtil;
using TAC_AI.AI.Behaviors;

namespace TAC_AI.AI.Engine.Registry
{
    /// <summary>
    /// Boot-time type scan: registers every IBehavior in the assembly keyed by its AIBehaviorAttribute
    /// (vehicle class + category + id), falling back to the namespace for the vehicle class and the type
    /// name for the id. No central switch - "loaded by being in the folder for its vehicle type" is this
    /// reflection pass. Tolerant of partial type-load because optional deps (WeaponAimMod/TweakTech/
    /// Control_Block/WaterMod) are soft-referenced. Runs from InitAIModules() (pre-PatchMod); any behavior
    /// Initiate touching a Harmony-patched API runs later.
    /// </summary>
    public static class AIModuleLoader
    {
        public static void ScanAndRegister()
        {
            AIModuleRegistry.Clear();
            Type[] types;
            try
            {
                types = Assembly.GetExecutingAssembly().GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }

            int found = 0;
            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IBehavior).IsAssignableFrom(t)) continue;
                try
                {
                    var entry = Describe(t);
                    if (entry != null)
                    {
                        AIModuleRegistry.Add(entry);
                        found++;
                    }
                }
                catch (Exception ex)
                {
                    DebugTAC_AI.LogError("AIModuleLoader: failed to register " + t.FullName + " - " + ex.Message);
                }
            }
            DebugTAC_AI.Log("AIModuleLoader: registered " + found + " behavior module(s).");
        }

        private static ModuleEntry Describe(Type t)
        {
            var attr = (AIBehaviorAttribute)Attribute.GetCustomAttribute(t, typeof(AIBehaviorAttribute));
            if (attr != null)
            {
                string id = attr.Id ?? t.Name;
                return new ModuleEntry(t, attr.Category, attr.VehicleClass, id);
            }

            // No attribute: instantiate a throwaway to read the interface-declared keys.
            if (t.GetConstructor(Type.EmptyTypes) == null) return null;
            var probe = (IBehavior)Activator.CreateInstance(t);
            string pid = string.IsNullOrEmpty(probe.Id) ? t.Name : probe.Id;
            return new ModuleEntry(t, probe.Category, probe.VehicleClass, pid);
        }
    }
}
