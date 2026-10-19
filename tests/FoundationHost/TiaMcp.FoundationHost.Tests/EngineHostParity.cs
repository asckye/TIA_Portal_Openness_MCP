extern alias enginehost;
extern alias enginefixture;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.ModelContextProtocol;
using TiaOpenness.Shared;
using HostMcp = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Descriptor = enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor;
using CatalogView = enginehost::TiaMcpServer.ModelContextProtocol.IToolCatalogView;
using Invoker = enginehost::TiaMcp.FoundationHost.WorkerToolInvoker;
using IEngineWorker = enginehost::TiaMcp.FoundationHost.IEngineWorker;
using EngineReply = enginehost::TiaMcp.FoundationHost.EngineReply;
using EngineFixture = enginefixture::TiaMcp.Engine.Tests.BehaviorParityEngine;

internal static class EngineHostParity
{
    internal static IDisposable EnterScope() => new Scope(false, true);
    internal static string Run(string scenario, bool plcWorker = false) => Execute(() => EngineFixture.Run(scenario), true, plcWorker);
    internal static string RunPrecondition(string tool, string message, string? parameter, bool argument, bool wrapped, bool typed, string arguments)
        => Execute(() => EngineFixture.RunPrecondition(tool, message, parameter, argument, wrapped, typed, arguments), false);
    private static string Execute(Func<string> run, bool approvalContext, bool plcWorker = false)
    {
        EngineFixture.ExternalHost = () => new Scope(approvalContext, plcWorker: plcWorker);
        EngineFixture.ExternalCall = (name, args) =>
        {
            // The shared-adapter route dispatches through the MCP request that carries the
            // call, as every served request does; the engine-host route does not use it.
            using var request = plcWorker ? HostMcp.UseProgressRequest(new global::ModelContextProtocol.Server.RequestContext<CallToolRequestParams>(
                DispatchProxy.Create<global::ModelContextProtocol.Server.IMcpServer, WorkerRobustnessTests.ServerProxy>())
                { Params = new CallToolRequestParams { Name = name } }, CancellationToken.None) : null;
            return HostMcp.ResultBody(HostMcp.CallTool(name, new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(args))))!;
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
        internal Catalog(string release, bool complete = false, bool plcWorker = false)
        {
            var all = JsonSerializer.Deserialize<Descriptor[]>(EngineFixture.CatalogJson(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!
                .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
            if (complete)
                foreach (var method in enginehost::TiaMcpServer.ModelContextProtocol.PortedToolDeclarations.Methods(release))
                {
                    var name = method.GetCustomAttribute<global::ModelContextProtocol.Server.McpServerToolAttribute>()!.Name!;
                    if (all.ContainsKey(name)) continue;
                    enginehost::TiaMcpServer.ModelContextProtocol.DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
                    all.Add(name, descriptor);
                }
            if (plcWorker)
                all = all.ToDictionary(pair => pair.Key, pair => pair.Value.Execution == "worker"
                    ? new Descriptor(pair.Value.Name, pair.Value.RawDescription, pair.Value.Tool, pair.Value.Classification,
                        pair.Value.Signature, pair.Value.Parameters, pair.Value.DryRun, pair.Value.CandidateFamily, "foundation")
                    : pair.Value, StringComparer.OrdinalIgnoreCase);
            All = all;
            BehaviorCapabilities = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(Catalog).Assembly, release);
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        private readonly bool context;
        private readonly bool approvalContext;
        internal Scope(bool approvalContext, bool complete = false, bool plcWorker = false)
        {
            var type = typeof(EngineFixture).Assembly.GetType("TiaMcpServer.ModelContextProtocol.McpServer")!;
            string release = (string)type.GetProperty("ReleaseKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Worker = new Worker();
            var catalog = new Catalog(release, complete, plcWorker);
            // Shared-adapter route: each probe is wrapped exactly like a V20/V21 ported shared tool
            // (PortedToolContract.Wrap: McpServer.WrapTools over the validated declaration).
            var invoker = new Invoker(catalog, enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Worker, lifetime,
                plcWorker ? catalog.All.Values.Where(tool => tool.Execution == "foundation").Select(tool => HostMcp.WrapTools(new global::ModelContextProtocol.Server.McpServerTool[] {
                    new enginehost::TiaMcpServer.ModelContextProtocol.InfrastructureInputTool(new SharedProbe(tool), tool) }).Single()).ToArray() : null);
            if (EngineFixture.ExternalStagingStore is ImportStagingStore store)
                invoker.SetLocalTarget(typeof(enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingTools),
                    new enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingTools(store));
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

    private sealed class SharedProbe(Descriptor descriptor) : global::ModelContextProtocol.Server.McpServerTool
    {
        public override Tool ProtocolTool => descriptor.Tool;
        public override ValueTask<CallToolResult> InvokeAsync(global::ModelContextProtocol.Server.RequestContext<CallToolRequestParams> request, CancellationToken token = default)
        {
            var args = JsonSerializer.SerializeToNode(request.Params!.Arguments)!.AsObject();
            var reply = EngineFixture.InvokeBody("fixture", descriptor.Name, args.ToJsonString(), HostMcp.IsReadOnlyApprovalPreview);
            if (reply.NativeCallIssued) enginehost::TiaMcpServer.ModelContextProtocol.InvocationJournal.NativeCallStarted();
            return ValueTask.FromResult(JsonSerializer.Deserialize<CallToolResult>(reply.Result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!);
        }
    }

    private sealed class Worker : IEngineWorker
    {
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
