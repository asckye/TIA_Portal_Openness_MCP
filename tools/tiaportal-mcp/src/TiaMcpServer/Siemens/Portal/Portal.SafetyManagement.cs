using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        public ResponseMessage ManagePlcSafety(string softwarePath, string action = "read", string runtimeGroup = "", string propertiesJson = "{}", bool dryRun = true)
            => RunHmiStepTool("ManagePlcSafety", meta => {
                if (!new[] { "read", "createRuntimeGroup", "deleteRuntimeGroup", "updateRuntimeGroup", "updateSettings" }.Contains(action)) throw new ArgumentException("Invalid Safety action.");
                bool writing = action != "read" && !dryRun;
                using var access = writing ? AcquireHmiEditAccess() : null;
                var plc = ExactPlcForEngineering(softwarePath, writing);
                Assembly assembly;
                try { assembly = Assembly.Load("Siemens.Engineering.Safety"); }
                catch (System.IO.FileNotFoundException) { assembly = typeof(IEngineeringServiceProvider).Assembly; }
                var type = assembly.GetType("Siemens.Engineering.Safety.SafetyAdministration", true)!;
                var service = typeof(Portal).GetMethod("ResolvePlcService", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(type).Invoke(this, new object[] { softwarePath, plc })
                    ?? throw new NotSupportedException("SafetyAdministration unavailable for this PLC/API.");
                meta["action"] = action; meta["dryRun"] = dryRun; meta["mayHaveChanged"] = false;
                meta["administration"] = EngineeringScalarProperties.Read(service);
                var groups = EngineeringGroupOperations.Get(service, "RuntimeGroups");
                if (action == "read")
                {
                    meta["settings"] = EngineeringScalarProperties.Read(EngineeringGroupOperations.Get(service, "Settings"));
                    meta["runtimeGroups"] = new JsonArray(EngineeringGroupOperations.Items(groups).Select(g => (JsonNode)EngineeringScalarProperties.Read(g)).ToArray());
                    var signatures = EngineeringGroupOperations.Get(service, "ProgramSignatures");
                    meta["programSignatures"] = signatures is IEnumerable ? new JsonArray(EngineeringGroupOperations.Items(signatures).Select(s => (JsonNode)EngineeringScalarProperties.Read(s)).ToArray()) : EngineeringScalarProperties.Read(signatures);
                    return "Safety metadata read. No login, compile, save or download.";
                }
                if (writing && (bool)EngineeringGroupOperations.Get(service, "IsSafetyOfflineProgramPasswordSet") && !(bool)EngineeringGroupOperations.Get(service, "IsLoggedOnToSafetyOfflineProgram"))
                    throw new InvalidOperationException("Safety offline program is protected and not logged on. Authenticate in TIA; credentials are not handled by this tool.");
                object? target = null;
                if (action != "updateSettings")
                {
                    if (string.IsNullOrWhiteSpace(runtimeGroup)) throw new ArgumentException("Exact runtimeGroup required.");
                    target = EngineeringGroupOperations.Find(groups, runtimeGroup);
                    if (action == "createRuntimeGroup" && target != null) throw new InvalidOperationException("Runtime group exists.");
                    if (action != "createRuntimeGroup" && target == null) throw new InvalidOperationException("Runtime group not found.");
                }
                else target = EngineeringGroupOperations.Get(service, "Settings");
                if (action == "createRuntimeGroup")
                {
                    if (writing) { meta["mayHaveChanged"] = true; target = EngineeringGroupOperations.Call(groups, "Create", new[] { typeof(string) }, runtimeGroup); meta["after"] = EngineeringScalarProperties.Read(target); }
                }
                else if (action == "deleteRuntimeGroup")
                {
                    meta["before"] = EngineeringScalarProperties.Read(target!);
                    if (writing) { meta["mayHaveChanged"] = true; EngineeringGroupOperations.Call(target!, "Delete", Type.EmptyTypes); if (EngineeringGroupOperations.Find(groups, runtimeGroup) != null) throw new InvalidOperationException("Runtime group remains after Delete."); meta["verifiedAbsent"] = true; }
                }
                else
                {
                    var changes = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object.");
                    if (changes.ContainsKey("Name") || changes.ContainsKey("SafetyModeCanBeDisabled")) throw new ArgumentException("Renaming and disabling safety mode are outside this adapter.");
                    var prepared = EngineeringScalarProperties.Prepare(target!.GetType(), changes);
                    meta["before"] = EngineeringScalarProperties.Read(target); meta["requestedProperties"] = changes.DeepClone();
                    if (writing) { EngineeringScalarProperties.Apply(target, prepared, meta); meta["after"] = EngineeringScalarProperties.Read(target); }
                }
                return writing ? "Offline Safety engineering edit completed; no compile, validation, save or download. Safety acceptance is still required." : "Safety edit preview; no modification.";
            });
    }
}
