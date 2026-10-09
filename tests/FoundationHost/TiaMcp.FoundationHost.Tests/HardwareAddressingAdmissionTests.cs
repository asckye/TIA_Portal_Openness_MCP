extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Xunit;
using AddressesTools = enginehost::TiaMcpServer.ModelContextProtocol.AddressesTools;
using AddressService = enginehost::TiaMcpServer.ModelContextProtocol.IHardwareAddressingService;
using HostContract = enginehost::TiaMcpServer.ModelContextProtocol.HardwareContract;
using HostResponse = enginehost::TiaMcpServer.ModelContextProtocol.ResponseMessage;

public sealed class HardwareAddressingAdmissionTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });
    public static IEnumerable<object[]> InvalidMaps => from release in Releases
        from parameter in new[] { "properties", "attributes" }
        from value in new[] { "null", "\"{}\"", "[]", "{\"Name\":{}}" }
        select new object[] { release[0], parameter, value };
    public static IEnumerable<object[]> Refusals => from release in Releases
        from scenario in new[] { "empty-device", "blank-item", "long-path", "negative-address", "fractional-property", "overflow-property", "unknown-property" }
        select new object[] { release[0], scenario };

    private static JsonObject Arguments() => new() { ["devicePath"] = new JsonArray("PLC_1"),
        ["itemPath"] = new JsonArray("CPU"), ["ioType"] = "Input", ["startAddress"] = 0 };
    private static RequestContext<CallToolRequestParams> Request(JsonObject args) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = "SetDeviceAddress",
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };
    private static JsonObject Body(CallToolResult result)
    {
        var body = result.StructuredContent!.AsObject();
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        Assert.Equal(!(bool)body["ok"]!, result.IsError);
        return body;
    }

    [Theory, MemberData(nameof(InvalidMaps))]
    public async Task Invalid_scalar_maps_are_rejected_before_worker_dispatch(string release, string parameter, string value)
    {
        using var worker = new UnboundWorker();
        using var session = new SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == "SetDeviceAddress");
        var args = Arguments(); args[parameter] = JsonNode.Parse(value);
        var body = Body(await tool.InvokeAsync(Request(args)));
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(release, (string?)body["meta"]?["releaseKey"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Omitted_optional_maps_are_admitted_without_native_calls(string release)
    {
        using var worker = new UnboundWorker();
        using var session = new SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == "SetDeviceAddress");
        var body = Body(await tool.InvokeAsync(Request(Arguments())));
        Assert.Equal("PROJECT_NOT_BOUND", (string?)body["error"]?["code"]);
        Assert.Equal(1, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Foundation_owns_five_typed_declarations_and_preserves_schema_defaults(string release)
    {
        using var worker = new UnboundWorker();
        using var session = new SessionFixture(worker, release);
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        var family = PortedFamilies.ForTool("GetDeviceAddressing");
        Assert.Equal(5, family.Tools.Length);
        foreach (var name in family.Tools) Assert.Single(roster, tool => tool.ProtocolTool.Name == name);
        var properties = roster.Single(t => t.ProtocolTool.Name == "SetDeviceAddress").ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var name in new[] { "properties", "attributes" }) Assert.False(properties.GetProperty(name).TryGetProperty("default", out _));
        Assert.True(properties.GetProperty("dryRun").GetProperty("default").GetBoolean());
        var read = typeof(AddressesTools).GetMethod("GetDeviceAddressingV4")!;
        var write = typeof(AddressesTools).GetMethod("SetDeviceAddressV4")!;
        Assert.Equal(typeof(string[]), read.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(string[]), read.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(string[]), write.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(string[]), write.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(AttributeMap<Scalar>), write.GetParameters()[4].ParameterType);
        Assert.Equal(typeof(AttributeMap<Scalar>), write.GetParameters()[5].ParameterType);
        foreach (var method in new[] { "GetDeviceItemIoAddressesV4", "SetDeviceItemIoAddressV4", "GetDeviceIpAddressV4" })
            Assert.Equal(typeof(string), typeof(AddressesTools).GetMethod(method)!.GetParameters()[0].ParameterType);
        foreach (var method in new[] { read, write, typeof(AddressesTools).GetMethod("GetDeviceItemIoAddressesV4")!,
            typeof(AddressesTools).GetMethod("SetDeviceItemIoAddressV4")!, typeof(AddressesTools).GetMethod("GetDeviceIpAddressV4")! })
            Assert.Equal(typeof(CallToolResult), method.ReturnType);
    }

    [Theory, MemberData(nameof(Refusals))]
    public void Typed_domain_refusals_precede_project_and_native_lookup(string release, string scenario)
    {
        using var selected = HostContract.UseRelease(release);
        var service = new ForbiddenAddressService(); var tool = new AddressesTools(service);
        var result = scenario switch
        {
            "empty-device" => tool.GetDeviceAddressingV4(Array.Empty<string>()),
            "blank-item" => tool.GetDeviceAddressingV4(new[] { "PLC/one,exact" }, new[] { " " }),
            "long-path" => tool.GetDeviceAddressingV4(Enumerable.Repeat("x", 65).ToArray()),
            "negative-address" => tool.SetDeviceItemIoAddressV4("CPU", "Input", -1, false),
            _ => tool.SetDeviceAddressV4(new[] { "PLC_1" }, new[] { "CPU" }, "Input", 0,
                V4Json.Deserialize<AttributeMap<Scalar>>(scenario switch {
                    "fractional-property" => "{\"StartAddress\":1.5}",
                    "overflow-property" => "{\"Length\":2147483648}",
                    _ => "{\"Name\":\"x\"}" }), dryRun: false)
        };
        var body = Body(result);
        Assert.Equal(scenario == "long-path" ? "LIMIT_EXCEEDED" : "INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(release, (string?)body["meta"]?["releaseKey"]);
        Assert.Equal(0, service.Calls);
    }

    internal sealed class SessionFixture : IDisposable
    {
        private readonly enginehost::TiaMcpServer.ModelContextProtocol.ImportStagingHostLifetime lifetime = new();
        private readonly IDisposable scope;
        private readonly string previousRelease;
        internal SessionFixture(UnboundWorker worker, string release)
        {
            previousRelease = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            var catalog = new AddressCatalog();
            var context = new enginehost::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog,
                Invoker = new enginehost::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, worker, lifetime) };
            scope = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(context);
        }
        public void Dispose()
        {
            scope.Dispose(); lifetime.Dispose();
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;
        }
    }
    private sealed class AddressCatalog : enginehost::TiaMcpServer.ModelContextProtocol.IToolCatalogView
    {
        public IReadOnlyDictionary<string, enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal AddressCatalog()
        {
            var descriptors = new Dictionary<string, enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor>();
            foreach (var name in PortedFamilies.ForTool("GetDeviceAddressing").Tools)
            {
                enginehost::TiaMcpServer.ModelContextProtocol.DeclaredToolMetadata.Create(name,
                    typeof(AddressesTools).GetMethod(name + "V4")!, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
                descriptors.Add(name, descriptor);
            }
            All = descriptors;
        }
    }
    internal sealed class UnboundWorker : IFoundationWorker, enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal int Calls;
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No engine restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine tool dispatch expected.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++; Assert.Equal("ReadState", operation);
            return Task.FromResult<JsonNode?>(new JsonObject());
        }
        public void Dispose() { }
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }
    private sealed class ForbiddenAddressService : AddressService
    {
        internal int Calls;
        private Exception Unexpected() { Calls++; return new InvalidOperationException("Native lookup must not run."); }
        public bool HasProject => throw Unexpected();
        public IReadOnlyList<IoAddressInfo>? GetDeviceItemAddresses(string path) => throw Unexpected();
        public List<string> DescribeChildItemsWithAddresses(string path) => throw Unexpected();
        public (bool ok, string message, IoAddressInfo? before, IoAddressInfo? after) SetDeviceItemStartAddress(string path, string ioType, int startAddress) => throw Unexpected();
        public HostResponse ReadDeviceAddressing(string devicePathJson, string itemPathJson, int offset, int limit) => throw Unexpected();
        public HostResponse UpdateDeviceAddress(string devicePathJson, string itemPathJson, string ioType, int startAddress, string propertiesJson, string attributesJson, string softwarePath, string processImageObName, bool dryRun) => throw Unexpected();
        public JsonObject GetDeviceIpAddress(string path) => throw Unexpected();
    }
}
