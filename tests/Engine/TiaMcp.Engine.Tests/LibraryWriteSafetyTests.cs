using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcpServer.ModelContextProtocol;

// Metadata-compatible fake, with no Siemens binary or native TIA access.
namespace Siemens.Engineering.HmiUnified.Library
{
    internal partial class ScriptModuleType
    {
        public int Reads, Writes;
        public string Name { get { Reads++; return "old"; } set { Writes++; } }
        public bool DoNotUse { get { Reads++; return false; } set { Writes++; } }
    }
}
namespace TiaMcp.Engine.Tests
{
    internal static class LibraryWriteSafetyTests
    {
        private sealed class DerivedModule : global::Siemens.Engineering.HmiUnified.Library.ScriptModuleType { }
        private sealed class OtherType { public string Name { get; set; } = "old"; }
        private static bool Blocked(Action action)
        {
            try { action(); return false; }
            catch (PortalException ex) { return ex.Code == PortalErrorCode.NativeCrashRiskBlocked && !HmiReadSafety.ConnectionUnavailable(ex) && !PortalFailureClassifier.IsPortalProcessLost(ex); }
        }
        internal static void Run(Action<bool, string> check)
        {
            int previous = Engineering.TiaMajorVersion;
            try
            {
                Engineering.TiaMajorVersion = 21;
                var fake = new global::Siemens.Engineering.HmiUnified.Library.ScriptModuleType();
                var type = fake.GetType();
                var changes = new JsonObject { ["DoNotUse"] = true, ["Name"] = "LGen_LibraryScripts" };
                check(Blocked(() => EngineeringScalarProperties.Prepare(type, changes)), "library safety: V21 script-module type rename preview refuses known crash path");
                check(fake.Reads == 0 && fake.Writes == 0, "library safety: guard uses metadata, no native getter or setter");
                var prepared = new List<(PropertyInfo Property, object? Value)> {
                    (type.GetProperty("DoNotUse")!, true), (type.GetProperty("Name")!, "LGen_LibraryScripts") };
                var meta = new JsonObject { ["mayHaveChanged"] = false };
                check(Blocked(() => EngineeringScalarProperties.Apply(fake, prepared, meta)), "library safety: prebuilt batch cannot bypass rename protection");
                check(fake.Reads == 0 && fake.Writes == 0 && meta["appliedProperties"]!.AsArray().Count == 0 && !meta["mayHaveChanged"]!.GetValue<bool>(), "library safety: later blocked property prevents every earlier write in the batch");
                check(Blocked(() => EngineeringScalarProperties.Prepare(typeof(DerivedModule), changes)), "library safety: derived proxy type retains protection");
                check(EngineeringScalarProperties.Prepare(type, new JsonObject { ["DoNotUse"] = true }).Count == 1, "library safety: unrelated script-module type property remains available");
                var other = new OtherType();
                EngineeringScalarProperties.Apply(other, EngineeringScalarProperties.Prepare(typeof(OtherType), new JsonObject { ["Name"] = "new" }), new JsonObject());
                check(other.Name == "new", "library safety: unrelated object rename remains available");
                Engineering.TiaMajorVersion = 20;
                check(EngineeringScalarProperties.Prepare(type, changes).Count == 2, "library safety: V21 incident guard is not generalized to V20");
                var portal = HmiToolFixture.Session;
                portal.FixtureResetReadHealth();
                var failure = new JsonObject { ["tool"] = "ManageLibraryType", ["action"] = "update", ["typePath"] = "LGen/Types_HMI/Scripts/LSicar_LibraryScripts", ["libraryName"] = "", ["lastAttemptedProperty"] = "Name", ["appliedProperties"] = new JsonArray(), ["mayHaveChanged"] = true };
                portal.RecordHmiReadFault(failure);
                var retained = portal.GetHmiReadHealth()["lastFailure"]!;
                check(retained["tool"]!.ToString() == "ManageLibraryType" && retained["lastAttemptedProperty"]!.ToString() == "Name" && retained["appliedProperties"]!.AsArray().Count == 0 && retained["mayHaveChanged"]!.GetValue<bool>(), "library safety: fault health retains property, tool and uncertainty evidence");
                portal.FixtureResetReadHealth();
            }
            finally { Engineering.TiaMajorVersion = previous; }
        }
    }
}
