using System;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    public sealed class ImportOrderTools
    {
        [McpServerTool(Name = "PlanArtifactImportOrder"), Description("[L2][Validation][READ] Offline dependency-first import order for 1..256 PLC/HMI artifacts, shared across all release profiles. Input is a JSON array of Id, Target, Priority and Dependencies. Explicit dependencies override numeric priority (lower first). Reports missing dependencies and cycles with no executable order. Does not infer dependencies from source, inspect TIA, validate native import formats or import files. Reuses the MIT EidoTiaWorkbench planner.")]
        public ResponseMessage PlanArtifactImportOrder(
            [Description("JSON array, e.g. [{\"Id\":\"UDT_A\"},{\"Id\":\"FB_A\",\"Dependencies\":[\"UDT_A\"]}]. IDs are unique ignoring case; dependency IDs must be included. Optional Target and integer Priority order independent artifacts.")] string artifactsJson)
            => OfflineToolExecution.RunOfflineAnalysisTool("PlanArtifactImportOrder", meta => {
                if (artifactsJson == null || artifactsJson.Length > 1024 * 1024) throw new ArgumentException("Provide at most one MiB of JSON.");
                var plan = ImportDependencyPlanner.Build(JsonSerializer.Deserialize<ImportOrderItem[]>(artifactsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
                meta["plan"] = JsonSerializer.SerializeToNode(plan);
                return plan.Valid ? "Dependency order calculated; native import remains separate." : "Import order unavailable: resolve the reported missing or cyclic dependencies.";
            });
    }
}
