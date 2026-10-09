using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        internal static bool IsSafeWithoutTia(string name)
        {
#if TIA_ENGINE_HOST
            if (!TiaMcp.Versioning.TiaVersionCatalog.Get(McpServer.ReleaseKey).IsFullEngine
                && TiaMcp.Adapters.Contracts.PortedFamilies.Contains(name)
                && TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(name).Name is "F01" or "F02" or "F03") return true;
#endif
            return ToolTaxonomy.IsSafeWithoutTia(name);
        }

        private static bool RequiresOpenness(string name)
        {
#if TIA_ENGINE_HOST
            // Foundation targets retain their own release readiness and approval boundary.
            if (!TiaMcp.Versioning.TiaVersionCatalog.Get(McpServer.ReleaseKey).IsFullEngine
                && McpServer.CatalogView.Find(name)?.Execution == "foundation") return false;
#endif
            return !IsSafeWithoutTia(name);
        }

        private static string? NativeTarget(string name, IReadOnlyDictionary<string, JsonElement>? arguments)
        {
            if (arguments == null) return RequiresOpenness(name) ? name : null;
            if (name.Equals("CallTool", StringComparison.OrdinalIgnoreCase))
            {
                if (!arguments.TryGetValue("name", out var target) || target.ValueKind != JsonValueKind.String) return null;
                return RequiresOpenness(target.GetString() ?? "") ? target.GetString() : null;
            }
            if (name is "RunToolTransaction" or "RunReadOnlyToolBatch" or "ApplyToolBatch")
            {
                if (!arguments.TryGetValue("operations", out var operations) || operations.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in operations.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var target)
                        && target.ValueKind == JsonValueKind.String && RequiresOpenness(target.GetString() ?? "")) return target.GetString();
                return null;
            }
            return RequiresOpenness(name) ? name : null;
        }

        private sealed class GuardedTool : McpServerTool
        {
            private readonly McpServerTool inner;
            internal GuardedTool(McpServerTool inner) => this.inner = inner;
            public override Tool ProtocolTool => inner.ProtocolTool;

            public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            {
                var target = NativeTarget(ProtocolTool.Name, request.Params?.Arguments);
                if (target == null || OpennessReadiness.Ready) return inner.InvokeAsync(request, cancellationToken);
                var arguments = JsonSerializer.SerializeToElement(request.Params?.Arguments
                    ?? new Dictionary<string, JsonElement>());
                var admission = McpServer.ValidateInfrastructureExample(ProtocolTool.Name,
                    JsonNode.Parse(arguments.GetRawText())!.AsObject());
                if (admission != null && ProtocolTool.Name == "CallTool" && arguments.TryGetProperty("name", out var targetName)
                    && targetName.ValueKind == JsonValueKind.String && (!arguments.TryGetProperty("arguments", out var supplied) || supplied.ValueKind == JsonValueKind.Object))
                    admission = McpServer.TargetInputError(targetName.GetString()!, new TiaMcp.Logic.V4.Inputs.ToolArguments(
                        arguments.TryGetProperty("arguments", out var targetArguments) ? targetArguments : JsonSerializer.SerializeToElement(new { })), admission);
                if (admission != null)
                    return new ValueTask<CallToolResult>(McpServer.V4TargetReject(
                        ProtocolTool.Name == "CallTool" && admission.Details is InvalidArgumentDetails { Parameter: not "arguments" }
                            ? target : ProtocolTool.Name, admission,
                        McpServer.CurrentBehaviorTargets(ProtocolTool.Name, arguments)));
                var cause = OpennessReadiness.Cause ?? "TIA Openness initialization has not completed.";
                var fix = OpennessReadiness.FixEn ?? "Run `tia doctor` to inspect the TIA Openness installation.";
                var fixZh = OpennessReadiness.FixZh ?? Runtime.EnvironmentDoctor.DefaultFixZh;
                var data = new JsonObject { ["environment"] = new JsonObject {
                    ["ready"] = false, ["cause"] = cause, ["recommendedFix"] = fix, ["recommendedFixZh"] = fixZh } };
                var error = new Error(cause + " " + fix, new ResourceUnavailableDetails("tia-openness-environment"));
                // The surrounding serialized-call audit records this refusal (request and end, no start) and shares its requestId.
                var refusal = McpServer.V4Reject(ProtocolTool.Name, error, data);
                return new ValueTask<CallToolResult>(McpServer.DiscloseTargets(refusal, ProtocolTool.Name, arguments));
            }
        }
    }
}
