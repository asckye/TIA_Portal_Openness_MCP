extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Engine = enginehost::TiaMcp.FoundationHost;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class HmiTagPathContractTests : IClassFixture<HmiTagPathContractTests.RegistryFixture>
{
    private readonly RegistryFixture registry;
    public HmiTagPathContractTests(RegistryFixture registry) => this.registry = registry;
    public static IEnumerable<object[]> Cases => from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        from path in new[] { "", "/Table", "/Group/Sub/Table", "/分组/变量表" }
        select new object[] { release, path };

    [Theory, MemberData(nameof(Cases))]
    public async Task Documented_paths_pass_the_served_schema_and_binding(string release, string path)
    {
        var (worker, catalog, tool) = registry.ForRelease(release);
        int before = worker.Requests.Count;
        using var session = new Session(worker, release, catalog);
        var schema = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("tagTablePath");
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("enum", out _));
        Assert.Matches(schema.GetProperty("pattern").GetString()!, path);
        var args = new JsonObject { ["softwarePath"] = "HMI", ["tagTablePath"] = path, ["tagName"] = "Tag" };
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
        {
            Params = new() { Name = "DeleteHmiTag", Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) },
        });
        Assert.True((bool)result.StructuredContent!["ok"]!, result.StructuredContent.ToJsonString());
        Assert.Equal(before + 1, worker.Requests.Count);
        var bound = worker.Requests.Last();
        Assert.Equal("DeleteHmiTag", bound.Operation);
        Assert.Equal("HMI", bound.SoftwarePath);
        Assert.Equal(path, bound.TagTablePath);
        Assert.Equal("Tag", bound.TagName);
        Assert.True(bound.DryRun);
        Assert.False(bound.ConfirmDelete);
    }

    public sealed class RegistryFixture : IDisposable
    {
        private readonly Dictionary<string, (PreviewWorker Worker, DeleteCatalog Catalog, McpServerTool Tool)> tools = new();
        internal (PreviewWorker Worker, DeleteCatalog Catalog, McpServerTool Tool) ForRelease(string release)
        {
            if (!tools.TryGetValue(release, out var entry))
            {
                Assert.Contains(PortedToolContract.Families(release), f => f.Tools.Contains("DeleteHmiTag"));
                var worker = new PreviewWorker();
                var catalog = new DeleteCatalog(release);
                using var session = new Session(worker, release, catalog);
                var tool = PortedToolContract.Wrap(new FoundationTool(worker, release, catalog.Method, "DeleteHmiTag"));
                entry = (worker, catalog, tool);
                tools.Add(release, entry);
            }
            return entry;
        }
        public void Dispose()
        {
            foreach (var entry in tools.Values) entry.Worker.Dispose();
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly Host.ImportStagingHostLifetime lifetime = new();
        private readonly IDisposable scope;
        private readonly string previousRelease;
        internal Session(PreviewWorker worker, string release, DeleteCatalog catalog)
        {
            previousRelease = Engine.EngineHostConfiguration.ReleaseKey;
            Engine.EngineHostConfiguration.ReleaseKey = release;
            var context = new Engine.EngineSessionContext(worker) { Catalog = catalog,
                Invoker = new Engine.WorkerToolInvoker(catalog, worker, lifetime) };
            scope = Engine.EngineHostConfiguration.Enter(context);
        }
        public void Dispose()
        {
            scope.Dispose(); lifetime.Dispose();
            Engine.EngineHostConfiguration.ReleaseKey = previousRelease;
        }
    }

    internal sealed class DeleteCatalog : Host.IToolCatalogView
    {
        internal MethodInfo Method { get; }
        public IReadOnlyDictionary<string, Host.ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, Host.ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<Host.ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Host.ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal DeleteCatalog(string release)
        {
            Method = Host.PortedToolDeclarations.Methods(release).Single(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name == "DeleteHmiTag");
            Host.DeclaredToolMetadata.Create("DeleteHmiTag", Method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
            var current = new Host.ToolDescriptor(descriptor.Name, descriptor.RawDescription, descriptor.Tool, descriptor.Classification,
                descriptor.Signature, descriptor.Parameters, descriptor.DryRun, descriptor.CandidateFamily, "foundation");
            All = new Dictionary<string, Host.ToolDescriptor> { [current.Name] = current };
        }
    }

    internal sealed class PreviewWorker : IFoundationWorker, Engine.IEngineWorker
    {
        internal List<HmiRequest> Requests { get; } = new();
        public bool Faulted => false;
        public JsonNode Binding => new JsonObject { ["projectPath"] = "C:/fixture.ap21" };
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine dispatch expected.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["ProjectFile"] = "C:/fixture.ap21", ["IsAttached"] = true });
            Assert.Equal(PortedFamilies.ForTool("DeleteHmiTag").OperationPrefix + ".ExecuteHmi", operation);
            var request = JsonSerializer.Deserialize<HmiRequest>(arguments["request"]!.ToJsonString())!;
            Requests.Add(request);
            return Task.FromResult<JsonNode?>(new JsonObject { ["Value"] = new JsonObject { ["Message"] = "preview", ["Meta"] = new JsonObject {
                ["success"] = true, ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["dataComplete"] = true,
                ["dryRun"] = true, ["mayHaveChanged"] = false, ["exists"] = true, ["referencesChecked"] = false } } });
        }
        public void Dispose() { }
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }
}
