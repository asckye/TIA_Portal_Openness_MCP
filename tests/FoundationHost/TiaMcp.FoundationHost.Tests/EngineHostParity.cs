extern alias toolpipeline;
extern alias enginefixture;
using System.Reflection;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using BatchFixture = TiaMcp.Engine.Tests.BatchReplacementPolicyTests.AdmissionFixture;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.ModelContextProtocol;
using TiaOpenness.Shared;
using HostMcp = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using Descriptor = toolpipeline::TiaMcpServer.ModelContextProtocol.ToolDescriptor;
using CatalogView = toolpipeline::TiaMcpServer.ModelContextProtocol.IToolCatalogView;
using Invoker = toolpipeline::TiaMcp.FoundationHost.WorkerToolInvoker;
using IEngineWorker = toolpipeline::TiaMcp.FoundationHost.IEngineWorker;
using EngineReply = toolpipeline::TiaMcp.FoundationHost.EngineReply;
using EngineFixture = enginefixture::TiaMcp.Engine.Tests.BehaviorParityEngine;

internal sealed class EngineHostParity
{
    private readonly FoundationFixture? port;
    private EngineHostParity(FoundationFixture? port = null) => this.port = port;

    internal static IDisposable EnterScope() => new Scope(false, true);
    internal static string Run(string scenario, bool plcWorker = false)
    {
        using var port = plcWorker ? new FoundationFixture(scenario) : null;
        var result = new EngineHostParity(port).Execute(() => EngineFixture.Run(scenario), true, plcWorker, readUnknownResult: scenario == "unknown");
        return port?.Measure(result) ?? result;
    }
    internal static string RunPrecondition(string tool, string message, string? parameter, bool argument, bool wrapped, bool typed, string arguments)
        => new EngineHostParity().Execute(() => EngineFixture.RunPrecondition(tool, message, parameter, argument, wrapped, typed, arguments), false);
    private string Execute(Func<string> run, bool approvalContext, bool plcWorker = false, bool readUnknownResult = false)
    {
        EngineFixture.ExternalHost = () => new Scope(approvalContext, port: port);
        EngineFixture.ExternalCall = (name, args) =>
        {
            // The shared-adapter route dispatches through the MCP request that carries the
            // call, as every served request does; the engine-host route does not use it.
            args = port?.Arguments(name, args) ?? args;
            using var request = plcWorker ? HostMcp.UseProgressRequest(new global::ModelContextProtocol.Server.RequestContext<CallToolRequestParams>(
                DispatchProxy.Create<global::ModelContextProtocol.Server.IMcpServer, WorkerRobustnessTests.ServerProxy>())
                { Params = new CallToolRequestParams { Name = name } }, CancellationToken.None) : null;
            var body = HostMcp.ResultBody(HostMcp.CallTool(name,
                new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(args))))!;
            if (readUnknownResult && (string?)body["meta"]?["outcome"] == "unknown")
            {
                var read = HostMcp.GetCallResult((string)body["meta"]!["requestId"]!);
                Xunit.Assert.Equal("OUTCOME_UNKNOWN", (string?)read.StructuredContent?["error"]?["code"]);
            }
            return body;
        };
        try { return run(); }
        finally { EngineFixture.ExternalHost = null; EngineFixture.ExternalCall = null; }
    }

    private sealed class Catalog : CatalogView
    {
        public IReadOnlyDictionary<string, Descriptor> All { get; }
        public IReadOnlyDictionary<string, Descriptor> IncludingUnavailable => All;
        public IReadOnlyList<Descriptor> Lite => All.Values.ToArray();
        public Descriptor? Find(string name, bool includeUnavailable = false) => All.TryGetValue(name, out var value) ? value : null;
        public JsonArray BehaviorCapabilities { get; }
        internal Catalog(string release, bool complete = false)
        {
            var all = JsonSerializer.Deserialize<Descriptor[]>(EngineFixture.CatalogJson(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!
                .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
            if (complete)
                foreach (var method in toolpipeline::TiaMcpServer.ModelContextProtocol.PortedToolDeclarations.Methods(release))
                {
                    var name = method.GetCustomAttribute<global::ModelContextProtocol.Server.McpServerToolAttribute>()!.Name!;
                    if (all.ContainsKey(name)) continue;
                    toolpipeline::TiaMcpServer.ModelContextProtocol.DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
                    all.Add(name, descriptor);
                }
            All = all;
            BehaviorCapabilities = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(Catalog).Assembly, release);
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly toolpipeline::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        private readonly bool context;
        private readonly bool approvalContext;
        internal Scope(bool approvalContext, bool complete = false, FoundationFixture? port = null)
        {
            var type = typeof(EngineFixture).Assembly.GetType("TiaMcpServer.ModelContextProtocol.McpServer")!;
            string release = (string)type.GetProperty("ReleaseKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            var worker = new Worker(port?.Worker);
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Worker = worker;
            var declared = new Catalog(release, complete);
            var shared = port?.Tools(release, declared) ?? Array.Empty<McpServerTool>();
            CatalogView catalog = port == null ? declared : new toolpipeline::TiaMcp.FoundationHost.SharedToolCatalog(declared, shared, new HashSet<string>());
            var invoker = new Invoker(catalog, worker, lifetime, shared);
            if (EngineFixture.ExternalStagingStore is ImportStagingStore store)
                invoker.SetLocalTarget(typeof(toolpipeline::TiaMcpServer.ModelContextProtocol.ImportStagingTools),
                    new toolpipeline::TiaMcpServer.ModelContextProtocol.ImportStagingTools(store));
            HostMcp.ConfigureToolBridge(catalog, invoker, () => false, new HashSet<string>());
            HostMcp.ApprovalWaitOverrideForTests = (pending, settings, token) =>
                ((Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>)type.GetProperty("ApprovalWaitOverrideForTests", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)(pending, settings, token);
            this.approvalContext = approvalContext;
            if (approvalContext) context = HostMcp.EnterMcpApprovalContext();
        }
        public void Dispose()
        {
            if (approvalContext) HostMcp.LeaveMcpApprovalContext(context);
            HostMcp.ApprovalWaitOverrideForTests = null;
            lifetime.Dispose();
        }
    }

    // Drive the same scenarios through CallTool and the actual Foundation boundary.
    // Only the Siemens worker and the Workbench decision are supplied by the fixture.
    private sealed class FoundationFixture : IDisposable
    {
        private readonly string scenario;
        private readonly BehaviorParityTests.Worker native;
        private readonly BatchFixture? batch;
        internal IFoundationWorker Worker { get; }

        internal FoundationFixture(string scenario)
        {
            this.scenario = scenario.EndsWith("-disabled", StringComparison.Ordinal) ? scenario[..^9] : scenario;
            native = new BehaviorParityTests.Worker(this.scenario);
            if (scenario.StartsWith("disabled-", StringComparison.Ordinal))
            {
                batch = new BatchFixture(scenario[(scenario.StartsWith("disabled-program-", StringComparison.Ordinal) ? 17 : 15)..],
                    scenario.StartsWith("disabled-program-", StringComparison.Ordinal));
            }
            Worker = batch == null ? native : new BehaviorParityTests.LocalBatchWorker(batch);
        }

        internal McpServerTool[] Tools(string release, Catalog declared) => FoundationTools.Definitions
            .Where(d => declared.Find(d.Name)?.Execution == "worker")
            .Select(d => (McpServerTool)new UsageHintTool(new FoundationV4Tool(new FoundationTool(d, Worker), release, sharedSession: true))).ToArray();

        internal JsonObject Arguments(string name, JsonObject args)
        {
            if (batch != null && name is "ImportPlcProgramFromDirectory" or "ImportPlcBlocksFromDirectory") return batch.Arguments();
            if (name == "ListPlcTags" && (scenario.StartsWith("blocked-export", StringComparison.Ordinal) || scenario == "typed-export-refusal"))
                native.Followup = true;
            if (name is "ImportPlcBlock" or "ImportPlcType" or "ImportPlcTagTable" or "CompilePlcSoftware")
            {
                args["dryRun"] = false;
                args["confirm"] = true;
                args["expectedProjectFile"] = "C:/fixture.ap19";
            }
            return args;
        }

        internal string Measure(string report)
        {
            var result = JsonNode.Parse(report)!.AsObject();
            if (batch != null) result["calls"] = JsonSerializer.SerializeToNode(batch.Calls);
            else if (!scenario.StartsWith("staging-", StringComparison.Ordinal))
            {
                result["writes"] = native.Writes;
                result["previews"] = native.Previews;
            }
            return result.ToJsonString();
        }

        public void Dispose() { Worker.Dispose(); batch?.Dispose(); }
    }

    private sealed class Worker(IFoundationWorker? foundation = null) : IEngineWorker
    {
        public toolpipeline::TiaMcp.FoundationHost.UnsettledCalls Unsettled
            => toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For((object?)foundation ?? this);
        public toolpipeline::TiaMcp.FoundationHost.LateCalls LateResults => toolpipeline::TiaMcp.FoundationHost.LateCalls.For((object?)foundation ?? this);
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => Task.FromResult(Snapshot());
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        {
            var value = EngineFixture.InvokeBody(id, name, arguments.ToJsonString(), preview);
            var result = JsonSerializer.Deserialize<CallToolResult>(value.Result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;
            return Task.FromResult(new EngineReply(result, value.NativeCallIssued, null, null, null));
        }
        public void Dispose() { }
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }
}
