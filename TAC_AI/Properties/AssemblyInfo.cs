using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("TAC_AI")]
[assembly: AssemblyDescription("Advanced AI for TerraTech game.  Overhauls A.I. with many fun new abilities.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("TAC_AI")]
[assembly: AssemblyCopyright("Copyright ©  2021")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("651a2212-7f7d-4183-abee-ee2d6b2559b1")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Phase 2.2 (FIX-PLAN.md): expose internal types in TAC_AI to sibling Smart-Diagnostics
// and Smart-Tests assemblies so a developer can read internal SmartRuntime / TeamRuntime /
// SmartPerTechState / PathingService / LearningService state without making the public API
// surface load-bearing. Without these the only observation channel is DebugTAC_AI.Log
// lines — see AUDIT-R2 §2.R2.J (Theme H, "no way to inspect anything").
[assembly: InternalsVisibleTo("Smart.Diagnostics")]
[assembly: InternalsVisibleTo("Smart.Tests")]
