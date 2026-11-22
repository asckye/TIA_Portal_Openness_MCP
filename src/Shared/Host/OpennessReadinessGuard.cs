using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Runtime;

namespace TiaMcpServer.Dispatch
{
    internal static class OpennessReadinessGuard
    {
        internal static IList<McpServerTool> Wrap(IList<McpServerTool> tools)
            => tools.Select(tool => (McpServerTool)new GuardedTool(tool)).ToList();

        internal static bool RequiresOpenness(string name)
        {
#if TIA_ENGINE_HOST
            // The Foundation target checks readiness after capability admission.
            if (McpServer.CatalogView.Find(name)?.Execution == "foundation") return false;
            // Reviewed host operations with no Openness dependency. Keep their dispatch lanes
            // independent of installation readiness. New tools default to guarded.
            if (name is "BuildReleaseHandoffArtifacts" or "CheckProductUpdate" or "ExportPlcTestReport" or "GenerateErrorReport"
                or "ManagePlcTestLibrary" or "AuditEngineeringExports" or "ExtractPlcBlockMetrics" or "GeneratePlcDocumentation"
                or "InstantiatePlcTemplates" or "PatchPlcBlockDocument" or "ManageUnifiedCwcPackage" or "RunClassicHmiOfflineValidationSuite"
                or "RunClassicHmiTemporaryImportPreflight" or "RunHmiActionScriptRecipeSafetySelfTest" or "RunHmiTemplatePlcSyncPrecheckSuite"
                or "WriteClassicHmiMinimalPackageFiles" or "CallTool") return false;
            if (!McpServer.Worker.RequiresOpennessInstallation && TiaMcp.Adapters.Contracts.PortedFamilies.Contains(name)
                && TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(name).Name is "F01" or "F02" or "F03") return false;
#endif
            return !ToolTaxonomy.IsSafeWithoutTia(name);
        }

        private static JsonElement Field(JsonElement arguments, string key)
            => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out var value) ? value : default;

        private static string? OperationTarget(Func<string, JsonElement> field)
        {
            var name = field("name");
            if (name.ValueKind != JsonValueKind.String || name.GetString() == "CallTool") return null;
            var input = field("arguments");
            return NativeTarget(name.GetString()!, key => Field(input, key));
        }

        private static string? NativeTarget(string name, Func<string, JsonElement> field)
        {
#if TIA_ENGINE_HOST
            bool HasText(string key) => field(key) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString());
#endif
            if (name.Equals("CallTool", StringComparison.OrdinalIgnoreCase)) return OperationTarget(field);
            if (name is "RunToolTransaction" or "RunReadOnlyToolBatch")
            {
                var operations = field("operations");
                if (operations.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in operations.EnumerateArray())
                    if (OperationTarget(key => Field(item, key)) is string native) return native;
#if TIA_ENGINE_HOST
                if (name == "RunReadOnlyToolBatch" && HasText("expectedProject") && RequiresOpenness(name)) return name;
#endif
                return null;
            }
#if TIA_ENGINE_HOST
            // Only execution of a test library or export of a project block calls the worker.
            if (name == "RunPlcTestLibrary" && field("dryRun").ValueKind != JsonValueKind.False
                || name == "ComparePlcBlockDocuments" && !HasText("leftBlockPath") && !HasText("rightBlockPath")
                || name == "RenderPlcBlockDocument" && !HasText("blockPath")) return null;
#endif
            return RequiresOpenness(name) ? name : null;
        }

        private static CallToolResult? Check(string name, RequestContext<CallToolRequestParams> request)
        {
            var suppliedArguments = request.Params?.Arguments;
            var target = NativeTarget(name, key => suppliedArguments != null && suppliedArguments.TryGetValue(key, out var value) ? value : default);
            if (target == null || OpennessReadiness.Ready) return null;
            var arguments = JsonSerializer.SerializeToElement(suppliedArguments ?? new Dictionary<string, JsonElement>());
            var admission = McpServer.ValidateInfrastructureExample(name,
                JsonNode.Parse(arguments.GetRawText())!.AsObject());
            if (admission != null && name == "CallTool" && arguments.TryGetProperty("name", out var targetName)
                && targetName.ValueKind == JsonValueKind.String
                    && (!arguments.TryGetProperty("arguments", out var supplied) || supplied.ValueKind == JsonValueKind.Object))
                admission = McpServer.TargetInputError(targetName.GetString()!, new TiaMcp.Logic.V4.Inputs.ToolArguments(
                    arguments.TryGetProperty("arguments", out var targetArguments) ? targetArguments : JsonSerializer.SerializeToElement(new { })),
                        admission);
            if (admission != null)
                return McpServer.V4TargetReject(
                    name == "CallTool" && admission.Details is InvalidArgumentDetails { Parameter: not "" }
                        ? target : name, admission,
                    McpServer.CurrentBehaviorTargets(name, arguments));
            var cause = OpennessReadiness.Cause ?? "TIA Openness initialization has not completed.";
            var fix = OpennessReadiness.FixEn ?? "Run `tia doctor` to inspect the TIA Openness installation.";
            var fixZh = OpennessReadiness.FixZh ?? Runtime.EnvironmentDoctor.DefaultFixZh;
            var data = new JsonObject { ["environment"] = new JsonObject {
                ["ready"] = false, ["cause"] = cause, ["recommendedFix"] = fix, ["recommendedFixZh"] = fixZh } };
            var error = new Error(cause + " " + fix, new ResourceUnavailableDetails("tia-openness-environment"));
            // The surrounding serialized-call audit records this refusal (request and end, no start) and shares its requestId.
            var refusal = McpServer.V4Reject(name, error, data);
            return McpServer.DiscloseTargets(refusal, name, arguments);
        }

        private sealed class GuardedTool : McpServerTool
        {
            private readonly McpServerTool inner;
            internal GuardedTool(McpServerTool inner) => this.inner = inner;
            public override Tool ProtocolTool => inner.ProtocolTool;

            public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
                CancellationToken cancellationToken = default)
                => Check(ProtocolTool.Name, request)
                    ?? await inner.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
