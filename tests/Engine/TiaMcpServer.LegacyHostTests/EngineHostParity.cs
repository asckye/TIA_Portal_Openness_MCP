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
using Invoker = enginehost::TiaMcp.LegacyHost.WorkerToolInvoker;
using IEngineWorker = enginehost::TiaMcp.LegacyHost.IEngineWorker;
using EngineReply = enginehost::TiaMcp.LegacyHost.EngineReply;
using EngineFixture = enginefixture::TiaMcpServer.Tests.BehaviorParityEngine;

internal static class EngineHostParity
{
    internal static string Run(string scenario) => Execute(() => EngineFixture.Run(scenario), true);
    internal static string RunPrecondition(string tool, string message, string? parameter, bool argument, bool wrapped, bool typed, string arguments)
        => Execute(() => EngineFixture.RunPrecondition(tool, message, parameter, argument, wrapped, typed, arguments), false);
    private static string Execute(Func<string> run, bool approvalContext)
    {
        EngineFixture.ExternalHost = () => new Scope(approvalContext);
        EngineFixture.ExternalCall = (name, args) => HostMcp.ResultBody(HostMcp.CallTool(name,
            new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(args))))!;
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
        internal Catalog(string release)
        {
            All = JsonSerializer.Deserialize<Descriptor[]>(EngineFixture.CatalogJson(), global::ModelContextProtocol.McpJsonUtilities.DefaultOptions)!
                .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
            BehaviorCapabilities = TiaMcp.Logic.V4.BehaviorCapabilities.Table(typeof(Catalog).Assembly, release);
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        private readonly bool context;
        private readonly bool approvalContext;
        internal Scope(bool approvalContext)
        {
            var type = typeof(EngineFixture).Assembly.GetType("TiaMcpServer.ModelContextProtocol.McpServer")!;
            string release = (string)type.GetProperty("ReleaseKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            enginehost::TiaMcp.LegacyHost.EngineHostConfiguration.ReleaseKey = release;
            enginehost::TiaMcp.LegacyHost.EngineHostConfiguration.Worker = new Worker();
            var catalog = new Catalog(release);
            var invoker = new Invoker(catalog, enginehost::TiaMcp.LegacyHost.EngineHostConfiguration.Worker, lifetime);
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
