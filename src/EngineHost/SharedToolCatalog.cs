using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcp.FoundationHost
{
    // Pure duplicate of ConnectPortal followed by AttachOpenProject.
    internal static class SharedToolPolicy
    {
        internal static readonly ISet<string> Removed = new HashSet<string>(StringComparer.Ordinal) {
            "ConnectProject"
        };
        internal static readonly string[] Bridge = { "FindTools", "CallTool", "PreviewToolCall", "ListToolCategories",
            "GetOpennessWorkerStatus", "RestartOpennessWorker" };
    }

    internal sealed class SharedToolCatalog : IToolCatalogView
    {
        public IReadOnlyDictionary<string, ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable { get; }
        public IReadOnlyList<ToolDescriptor> Lite { get; }
        public JsonArray BehaviorCapabilities { get; }
        public ToolDescriptor? Find(string name, bool includeUnavailable = false)
            => (includeUnavailable ? IncludingUnavailable : All).TryGetValue(name, out var tool) ? tool : null;

        internal SharedToolCatalog(IToolCatalogView engine, IReadOnlyList<McpServerTool> shared, ISet<string> essentials)
        {
            BehaviorCapabilities = engine.BehaviorCapabilities;
            var all = engine.All.Where(p => !SharedToolPolicy.Removed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var unavailable = engine.IncludingUnavailable.Where(p => !SharedToolPolicy.Removed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (var implementation in shared)
            {
                var tool = implementation.ProtocolTool;
                if (TiaMcp.Adapters.Contracts.PortedFamilies.IsMigratedDeclaration(tool.Name)
                    && !TiaMcp.Adapters.Contracts.PortedFamilies.UsesFoundationCandidate(tool.Name) && all.TryGetValue(tool.Name, out var declared))
                {
                    var ported = new ToolDescriptor(declared.Name, declared.RawDescription, tool, declared.Classification,
                        declared.Signature, declared.Parameters, declared.DryRun, declared.CandidateFamily,
                        declared.Execution == "host" ? "host" : "foundation");
                    all[tool.Name] = ported; unavailable[tool.Name] = ported;
                    continue;
                }
                var required = tool.InputSchema.TryGetProperty("required", out var requiredSchema)
                    ? requiredSchema.EnumerateArray().Select(n => n.GetString()!).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
                var parameters = tool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => {
                    string type = p.Value.TryGetProperty("type", out var t) ? t.GetString()! : "object";
                    string? fallback = p.Value.TryGetProperty("default", out var d) ? d.GetRawText() : null;
                    return new ToolParameterDescriptor(p.Name, type, type switch {
                        "string" => typeof(string).FullName!, "integer" => typeof(int).FullName!, "boolean" => typeof(bool).FullName!,
                        "array" => typeof(JsonElement).FullName!,
                        _ => typeof(TiaMcp.Logic.V4.Inputs.ToolArguments).FullName! }, required.Contains(p.Name), fallback,
                        fallback == null ? null : d.ToString(), p.Value.TryGetProperty("description", out var description) ? description.GetString()! : "", false,
                        p.Value.TryGetProperty("enum", out var values) ? values.EnumerateArray().Select(v => v.ToString()).ToArray() : Array.Empty<string>());
                }).ToArray();
                var descriptor = new ToolDescriptor(tool.Name, tool.Description ?? "", tool, SharedClassification(tool),
                    tool.Name + "(" + string.Join(", ", parameters.Select(p => p.Name + ": " + p.FriendlyType)) + ")", parameters,
                    new ToolDryRunDescriptor(parameters.Any(p => p.Name == "dryRun"), true), null, "foundation");
                all[tool.Name] = descriptor; unavailable[tool.Name] = descriptor;
            }
            All = all; IncludingUnavailable = unavailable;
            Lite = all.Values.Where(t => essentials.Contains(t.Name) || SharedToolPolicy.Bridge.Contains(t.Name, StringComparer.Ordinal)).ToArray();
        }

        // A shared implementation takes the reviewed classification of its tool, as a declaration does
        // (DeclaredToolMetadata): batch admission and the read/write outcome of a failure depend on it.
        // Only a tool without a ToolMetadata row keeps the operation-derived defaults.
        internal static ToolDescriptorClassification SharedClassification(global::ModelContextProtocol.Protocol.Tool tool)
        {
            var reviewed = ToolMetadata.Find(tool.Name);
            if (reviewed != null)
                return new ToolDescriptorClassification(reviewed.Layer, reviewed.Domain, reviewed.Operation, reviewed.BatchRead, reviewed.BatchWrite);
            var operation = ToolTaxonomy.OperationOf(tool.Name, tool.Description).Operation;
            return new ToolDescriptorClassification("L1", ToolTaxonomy.For(tool.Name).Domain == "Workbench" ? "Workbench" : "PLC", operation,
                operation is "READ" or "OFFLINE", operation is "WRITE" or "FILE");
        }
    }
}
