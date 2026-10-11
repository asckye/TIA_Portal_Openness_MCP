extern alias enginehost;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Engine = enginehost::TiaMcp.FoundationHost;
using Services = enginehost::TiaMcpServer.Siemens.Services;
using Xunit;

public sealed class B6TransactionRoutingTests
{
    [Fact]
    public async Task Ported_PLC_hardware_and_engine_Unified_children_reuse_the_transaction_worker_lane()
    {
        using var worker = new Worker();
        using var lifetime = new Host.ImportStagingHostLifetime();
        var catalog = new Catalog();
        // This test isolates routing inside an admitted transaction; approval and portal
        // admission are exercised separately by PortedPortalAdmissionTests.
        var tools = Host.PortedToolDeclarations.Methods("21").Where(m => new[] { "CreatePlcTypeGroup", "ManageDeviceUserGroup" }.Contains(m.GetCustomAttribute<McpServerToolAttribute>()!.Name))
            .Select(m => new FoundationTool(worker, "21", m, m.GetCustomAttribute<McpServerToolAttribute>()!.Name!).ValidatedTool()).ToArray();
        var invoker = new Engine.WorkerToolInvoker(catalog, worker, lifetime, tools);
        using var context = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(worker) { Catalog = catalog, Invoker = invoker });
        using var release = Host.HardwareContract.UseRelease("21");
        using var progress = Host.McpServer.UseProgressRequest(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = "RunToolTransaction", Arguments = new Dictionary<string, JsonElement>() } }, CancellationToken.None);
        using var lane = await enginehost::TiaMcpServer.Dispatch.ToolDispatchLanes.Acquire("RunToolTransaction", CancellationToken.None);
        enginehost::TiaMcpServer.Dispatch.ToolDispatchLanes.Activate(lane);
        var service = new Services.EngineeringServicesPortService((op, args) => worker.Call(op, args, CancellationToken.None).GetAwaiter().GetResult(), () => "fixture.ap21");
        var calls = new[] {
            ("CreatePlcTypeGroup", "{\"softwarePath\":\"PLC\",\"groupPath\":\"Types\",\"dryRun\":false}"),
            ("ManageDeviceUserGroup", "{\"action\":\"create\",\"groupPath\":\"Devices\",\"dryRun\":false}"),
            ("ManageUnifiedHmiGroup", "{\"softwarePath\":\"HMI\",\"dryRun\":false}") };
        var meta = new JsonObject();
        Assert.True(Host.TransactionExecution.Run(calls.Length, () => service.BeginTransaction("Batch"), i => {
            Assert.Contains(calls[i].Item1, Host.TransactionExecution.SupportedTools);
            var result = invoker.Invoke(calls[i].Item1, new ToolArguments(JsonSerializer.Deserialize<JsonElement>(calls[i].Item2)), false).Result;
            Assert.True((bool)result.StructuredContent!["ok"]!, result.StructuredContent.ToJsonString() + " Operations: " + string.Join(",", worker.Operations));
            return true;
        }, meta));
        Assert.Equal(1, worker.Acquires);
        Assert.Equal(new[] { "CreatePlcTypeGroup", "ManageDeviceUserGroup", "ManageUnifiedHmiGroup" }, worker.Children);
        Assert.Equal(new[] { "foundation", "foundation", "worker" }, calls.Select(c => catalog.All[c.Item1].Execution));
        Assert.True(worker.DisposedTransaction);
    }

    [Fact]
    public void Unified_master_copy_import_crosses_a_typed_library_operation()
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var service = new Services.LibraryPortService((operation, args) => {
            Assert.Equal("libraries.ExecuteLibrary", operation);
            var request = JsonSerializer.Deserialize<TiaMcp.Adapters.Contracts.LibraryRequest>(args["request"]!.ToJsonString())!;
            Assert.Equal("ImportMasterCopyFromGlobalLibrary", request.Operation);
            Assert.Equal("HMI", request.HmiSoftwarePath);
            Assert.Equal("Main", request.ScreenName);
            Assert.Equal(10, request.Left); Assert.Equal(20, request.Top);
            Assert.False((bool)args["dryRun"]!); Assert.True((bool)args["confirm"]!);
            Assert.Equal("fixture.ap21", (string?)args["expectedProjectFile"]);
            return new JsonObject { ["Import"] = new JsonObject { ["Ok"] = true, ["ImportedItemName"] = "Button" } };
        }, () => "fixture.ap21");
        Assert.True(service.ImportMasterCopyFromGlobalLibrary("library.al21", "Button", "HMI", "Main", "Button", 10, 20).Ok);
    }

    private sealed class Catalog : Host.IToolCatalogView
    {
        public IReadOnlyDictionary<string, Host.ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, Host.ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<Host.ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Host.ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal Catalog()
        {
            var all = new Dictionary<string, Host.ToolDescriptor>();
            foreach (var method in Host.PortedToolDeclarations.Methods("21"))
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                Host.DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException(), out var d);
                all[name] = new(d.Name, d.RawDescription, d.Tool, d.Classification, d.Signature, d.Parameters, d.DryRun, d.CandidateFamily, "foundation");
            }
            Host.DeclaredToolMetadata.Create("ManageUnifiedHmiGroup", typeof(UnifiedFixture).GetMethod(nameof(UnifiedFixture.Edit))!, _ => throw new InvalidOperationException(), out var unified);
            all[unified.Name] = new(unified.Name, unified.RawDescription, unified.Tool, unified.Classification, unified.Signature, unified.Parameters, unified.DryRun, unified.CandidateFamily, "worker");
            All = all;
        }
    }
    private sealed class UnifiedFixture
    {
        [McpServerTool(Name = "ManageUnifiedHmiGroup"), Description("[L2][HMI-Unified][WRITE] Fixture worker declaration.")]
        public void Edit(string softwarePath, bool dryRun = true) => throw new InvalidOperationException("Engine dispatch must use IEngineWorker.");
    }
    private sealed class Worker : IFoundationWorker, Engine.IEngineWorker
    {
        internal int Acquires; internal bool Active, Requested, DisposedTransaction;
        internal List<string> Children = new(); internal List<string> Operations = new();
        public bool Faulted => false;
        public JsonNode? Binding => new JsonObject { ["projectPath"] = "fixture.ap21" };
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true }, ["binding"] = Binding?.DeepClone() };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException();
        public Task<IDisposable> Acquire(CancellationToken token) { Assert.False(Active); Acquires++; return Task.FromResult<IDisposable>(new Lane(this)); }
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            Assert.True(Active); Operations.Add(operation);
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["IsAttached"] = true, ["ProcessId"] = 123, ["ProjectFile"] = "fixture.ap21" });
            if (operation.EndsWith("BeginEngineeringTransaction")) return Task.FromResult<JsonNode?>(JsonValue.Create("token"));
            if (operation.EndsWith("ReadEngineeringTransaction")) return Task.FromResult<JsonNode?>(JsonValue.Create((string?)args["member"] switch { "CanCommit" => true, "CommitRequested" => Requested, _ => false }));
            if (operation.EndsWith("CommitEngineeringTransaction")) { Requested = true; return Task.FromResult<JsonNode?>(null); }
            if (operation.EndsWith("DisposeEngineeringTransaction")) { DisposedTransaction = true; return Task.FromResult<JsonNode?>(null); }
            Children.Add((string?)args["request"]?["Operation"] ?? "ManageDeviceUserGroup");
            var meta = new JsonObject { ["success"] = true, ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["mayHaveChanged"] = true, ["after"] = new JsonObject() };
            var step = new JsonObject { ["Message"] = "Fixture edit", ["Meta"] = meta };
            return Task.FromResult<JsonNode?>(operation.EndsWith("ExecutePlcOrganisation") ? new JsonObject { ["Data"] = meta.DeepClone() } : step);
        }
        public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject args, bool preview, CancellationToken token)
        {
            Assert.True(Active); Assert.False(preview); Assert.False((bool)args["dryRun"]!);
            Children.Add(name);
            var result = Host.SessionToolContract.Map(name, new Host.ResponseMessage { Message = "Fixture edit", Meta = new JsonObject { ["success"] = true, ["operationSuccess"] = true, ["mayHaveChanged"] = true } }, true, false);
            return Task.FromResult(new Engine.EngineReply(result, true, Binding, null, null));
        }
        public void Dispose() { }
        private sealed class Lane(Worker owner) : IDisposable, enginehost::TiaMcpServer.Dispatch.ToolDispatchLanes.IWorkerLane
        {
            public void Activate() => owner.Active = true;
            public void Dispose() => owner.Active = false;
        }
    }
}
