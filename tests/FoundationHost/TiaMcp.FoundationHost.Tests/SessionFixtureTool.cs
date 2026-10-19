extern alias foundationhost;
extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using View = enginehost::TiaMcpServer.ModelContextProtocol.IToolCatalogView;
using Descriptor = enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor;
using Parameter = enginehost::TiaMcpServer.ModelContextProtocol.ToolParameterDescriptor;

// This descriptor lives only in the test assembly. Production EngineCatalog rejects
// it; the internal test entry adds it after the real catalog has been validated.
internal static class SessionFixtureTool
{
    // Retain a registered name for generated V4 annotations; only the test seam
    // replaces its descriptor/schema/owner. File catalogs still require foundation.
    internal const string Name = "ListTestSuiteCases";
    internal static View Add(View source) => new Catalog(source);
    internal static void WriteCatalog(string path, string release, string worker)
        => File.WriteAllText(path, new JsonObject { ["formatVersion"] = 1, ["release"] = release,
            ["workerSha256"] = foundationhost::TiaMcp.FoundationHost.EngineWorkerClient.Hash(worker),
            ["behaviorCapabilities"] = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(TiaMcp.Logic.V4.BehaviorCapabilities).Assembly, release),
            ["tools"] = new JsonArray(), ["descriptors"] = new JsonArray(), ["liteTools"] = new JsonArray(),
            ["serverInstructions"] = "Offline session fixture." }.ToJsonString());

    internal static IReadOnlyList<global::ModelContextProtocol.Server.McpServerTool> PlcTools(
        foundationhost::TiaMcp.FoundationHost.IFoundationSessionWorker worker,
        IReadOnlyList<global::ModelContextProtocol.Server.McpServerTool> tools)
    {
        var descriptor = new Catalog(new EmptyView()).All[Name];
        var fixture = new PlcTool(worker, descriptor.Tool);
        var wrapped = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.WrapTools(new[] { fixture }).Single();
        return tools.Where(tool => tool.ProtocolTool.Name != Name).Append(wrapped).ToArray();
    }
    private sealed class EmptyView : View
    {
        public IReadOnlyDictionary<string, Descriptor> All { get; } = new Dictionary<string, Descriptor>();
        public IReadOnlyDictionary<string, Descriptor> IncludingUnavailable => All;
        public IReadOnlyList<Descriptor> Lite => [];
        public JsonArray BehaviorCapabilities { get; } = new();
        public Descriptor? Find(string name, bool includeUnavailable = false) => null;
    }
    private sealed class PlcTool(foundationhost::TiaMcp.FoundationHost.IFoundationSessionWorker worker, Tool tool)
        : global::ModelContextProtocol.Server.McpServerTool
    {
        public override Tool ProtocolTool => tool;
        public override async ValueTask<CallToolResult> InvokeAsync(
            global::ModelContextProtocol.Server.RequestContext<CallToolRequestParams> request, CancellationToken token = default)
        {
            var args = JsonSerializer.SerializeToNode(request.Params!.Arguments)?.AsObject() ?? new JsonObject();
            // Same request id as a served worker tool (WorkerToolInvoker) and the PlcWorker's
            // candidate-session invalidation below.
            string id = TiaOpenness.Shared.AuditInvocation.CurrentRequestId ?? enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId;
            var reply = await worker.Call("FixtureInvoke", new JsonObject { ["name"] = Name,
                ["requestId"] = id, ["arguments"] = args }, token);
            var result = JsonSerializer.Deserialize<CallToolResult>(reply!["result"]!.ToJsonString(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
            // A host tool that observes an unknown V4 outcome on the PlcWorker invalidates the
            // worker session exactly as the production candidate tools do.
            if ((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"] == true)
            {
                if (worker is foundationhost::TiaMcp.FoundationHost.WorkerClient native) native.InvalidateCandidateSession();
                else worker.MarkUncertain();
            }
            return result;
        }
    }

    private sealed class Catalog : View
    {
        public IReadOnlyDictionary<string, Descriptor> All { get; }
        public IReadOnlyDictionary<string, Descriptor> IncludingUnavailable { get; }
        public IReadOnlyList<Descriptor> Lite { get; }
        public JsonArray BehaviorCapabilities { get; }
        public Descriptor? Find(string name, bool includeUnavailable = false)
            => (includeUnavailable ? IncludingUnavailable : All).GetValueOrDefault(name);
        internal Catalog(View source)
        {
            var tool = new Tool { Name = Name, Description = "[L1][Diagnostics][READ] Offline fixture worker operation.",
                InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                    ["fault"] = new JsonObject { ["type"] = "boolean" }, ["hang"] = new JsonObject { ["type"] = "boolean" } },
                    ["additionalProperties"] = false }) };
            var descriptor = new Descriptor(Name, tool.Description, tool, new("L1", "Diagnostics", "READ", true, false), Name + "()",
                new[] { new Parameter("fault", "boolean", "System.Boolean", false, null, null, "", false, []),
                    new Parameter("hang", "boolean", "System.Boolean", false, null, null, "", false, []) }, new(false, false), null, "worker");
            var all = source.All.ToDictionary(p => p.Key, p => p.Value); all[Name] = descriptor; All = all;
            var including = source.IncludingUnavailable.ToDictionary(p => p.Key, p => p.Value); including[Name] = descriptor; IncludingUnavailable = including;
            Lite = source.Lite; BehaviorCapabilities = source.BehaviorCapabilities;
        }
    }
}
