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

public sealed class PortedPortalAdmissionTests
{
    public static IEnumerable<object[]> Cases =>
        from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        from family in PortedToolContract.Families(release)
        from name in family.Tools
        where PortedFamilies.Available(release, name)
        from combined in release is "20" or "21" ? new[] { false, true } : new[] { false }
        select new object[] { release, name, combined };

    [Theory, MemberData(nameof(Cases))]
    public async Task Missing_portal_never_turns_an_unstarted_port_into_an_unknown_write(string release, string name, bool combined)
    {
        using var approval = new DisabledApproval();
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        using var lifetime = new Host.ImportStagingHostLifetime();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var catalog = new Catalog(release);
        var tool = tools.Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name, tool.ProtocolTool.InputSchema);
        var invoker = new Engine.WorkerToolInvoker(catalog, worker, lifetime, tools);
        using var context = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(worker) { Catalog = catalog, Invoker = invoker });
        Assert.Null(invoker.ValidateArguments(catalog.All[name], JsonSerializer.SerializeToElement(arguments), tool.ProtocolTool.InputSchema));
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
        var entry = combined ? Host.McpServer.WrapTools(new[] { invoker.CreateTool(catalog.All[name]) }).Single() : tool;
        var result = await entry.InvokeAsync(request);
        var body = result.StructuredContent!.AsObject();
        if (Environment.GetEnvironmentVariable("TIA_MCP_PORTAL_ADMISSION_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, release + "-" + name + "-" + combined + ".json"), body.ToJsonString());
        }
        Assert.NotEqual("unknown", (string?)body["meta"]?["outcome"]);
        Assert.False((bool)body["meta"]!["requiresSessionReset"]!);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        if (name is "CreatePlcBlockGroup" or "BuildAndImportPlcArtifact" or "CompileHmiDiagnostics")
        {
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        }
        if ((string?)body["error"]?["details"]?["condition"] == "ConnectPortal")
        {
            Assert.Equal("ConnectPortal", (string?)body["error"]?["details"]?["condition"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.Equal("none", (string?)body["meta"]?["completeness"]);
            var warnings = body["meta"]!["warnings"]!.AsArray().Select(w => (string?)w!["code"]).ToArray();
            Assert.Contains("UNVERIFIED_BEHAVIOR", warnings);
            Assert.Contains("RECOVERY_GUIDANCE", warnings);
        }
        if (name == "BuildAndImportPlcArtifact")
        {
            Assert.True((bool)body["data"]!["offlineBuildOk"]!);
            Assert.Single(body["data"]!["writtenFiles"]!.AsArray());
            Assert.Empty(body["data"]!["importedTagTables"]!.AsArray());
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "APPROVAL_DISABLED");
        }
        if (name == "CreatePlcBlockGroup")
        {
            Assert.Equal("McpException", (string?)body["data"]?["evidence"]?["exceptionType"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "APPROVAL_DISABLED");
        }
        Assert.True(worker.Calls <= (name == "SearchInstalledGsdDevices" ? 2 : 1), "A missing portal must not dispatch a native operation: " + name);
    }

    private sealed class DisabledApproval : IDisposable
    {
        private readonly TiaOpenness.Shared.ApprovalSettings previous = TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        internal DisabledApproval() => new TiaOpenness.Shared.ApprovalSettings(false, 1).Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        public void Dispose() => previous.Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Port_dispatch_takes_the_shared_worker_lane_before_journaling(bool cancel)
    {
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(contextWorker, "21");
        using var worker = new QueuedWorker();
        using var context = Engine.EngineHostConfiguration.Enter(new Engine.EngineSessionContext(worker) { Catalog = new Catalog("21") });
        using var held = await worker.Acquire(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = enginehost::TiaMcpServer.Dispatch.ToolDispatchLanes.Acquire("CreatePlcBlockGroup", cancellation.Token);
        Assert.False(pending.IsCompleted);
        Assert.Equal(2, worker.Acquires);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            held.Dispose();
            using var acquired = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            enginehost::TiaMcpServer.Dispatch.ToolDispatchLanes.Activate(acquired);
            Assert.Equal(1, worker.Activations);
        }
        using var local = await enginehost::TiaMcpServer.Dispatch.ToolDispatchLanes.Acquire("BuildDeviceAmlDocument", CancellationToken.None);
        Assert.Equal(2, worker.Acquires);
    }

    private sealed class QueuedWorker : Engine.IEngineWorker
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        internal int Acquires, Activations;
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No worker restart expected.");
        public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No worker dispatch expected.");
        public async Task<IDisposable> Acquire(CancellationToken token)
        {
            Acquires++;
            await serial.WaitAsync(token);
            return new Lane(this);
        }
        public void Dispose() => serial.Dispose();
        private sealed class Lane(QueuedWorker owner) : IDisposable, enginehost::TiaMcpServer.Dispatch.ToolDispatchLanes.IWorkerLane
        {
            private bool disposed;
            public void Activate() => owner.Activations++;
            public void Dispose() { if (!disposed) { disposed = true; owner.serial.Release(); } }
        }
    }

    private static JsonObject Arguments(string name, JsonElement schema)
    {
        var args = new JsonObject();
        if (schema.TryGetProperty("required", out var required))
            foreach (var key in required.EnumerateArray()) args[key.GetString()!] = Sample(schema.GetProperty("properties").GetProperty(key.GetString()!));
        var properties = schema.GetProperty("properties");
        foreach (var key in new[] { "dryRun", "confirm", "confirmProtectionChange" })
            if (properties.TryGetProperty(key, out _)) args[key] = key != "dryRun";
        foreach (var pair in Overrides(name)) args[pair.Key] = pair.Value?.DeepClone();
        return args;
    }

    private static JsonNode? Sample(JsonElement schema)
    {
        if (schema.TryGetProperty("enum", out var values)) return JsonNode.Parse(values[0].GetRawText());
        if (schema.TryGetProperty("anyOf", out var options)) return Sample(options[0]);
        if (!schema.TryGetProperty("type", out var type)) return new JsonObject();
        return type.GetString() switch {
            "string" => JsonValue.Create("Fixture"), "integer" => JsonValue.Create(0), "number" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(true), "array" => new JsonArray(Sample(schema.GetProperty("items"))),
            "object" => ObjectSample(schema), _ => null };
    }

    private static JsonObject ObjectSample(JsonElement schema)
    {
        var result = new JsonObject();
        if (schema.TryGetProperty("required", out var required))
            foreach (var key in required.EnumerateArray()) result[key.GetString()!] = Sample(schema.GetProperty("properties").GetProperty(key.GetString()!));
        return result;
    }

    private static JsonObject Overrides(string name) => JsonNode.Parse(name switch {
        "BuildAndImportPlcArtifact" => "{\"kind\":\"tagtable\",\"spec\":{\"tableName\":\"Tags\",\"tags\":[{\"name\":\"Ready\",\"dataTypeName\":\"Bool\",\"logicalAddress\":\"%M0.0\"}]}}",
        "PlanHardwareNetworkConfiguration" => "{\"plan\":{\"operations\":[]}}",
        "BuildDeviceAmlDocument" => "{\"spec\":{\"projectName\":\"Fixture\",\"devices\":[{\"name\":\"Fixture\",\"typeIdentifier\":\"OrderNumber:6ES7211-1BE40-0XB0/V4.7\",\"deviceItems\":[]}]},\"outputPath\":\"fixture.aml\"}",
        "SetPlcCpuSettings" => "{\"settings\":{\"exactAttributes\":{\"Name\":\"Fixture\"}}}",
        "SetDeviceAddress" or "SetDeviceItemIoAddress" => "{\"ioType\":\"Input\",\"startAddress\":0}",
        "ManagePlcUserGroup" => "{\"family\":\"blocks\",\"action\":\"rename\",\"newName\":\"Next\"}",
        "ManagePlcBlockProtection" => "{\"action\":\"read\"}",
        "ManagePlcExternalSources" => "{\"action\":\"list\"}",
        "ManageHardwareObject" => "{\"action\":\"deleteDevice\"}",
        "ManageDeviceServiceObjects" => "{\"family\":\"certificateServices\"}",
        "ExchangeSystemDiagnosticsSettings" => "{\"action\":\"export\"}",
        "ManageIoSystem" or "ManageNetworkDomain" or "ManageTransferArea" => "{\"action\":\"create\"}",
        "SetDeviceItemChannel" => "{\"channelType\":\"Digital\",\"channelIoType\":\"Input\",\"attributes\":{\"ChannelAddress\":0}}",
        "ManageCommunicationConnection" => "{\"action\":\"delete\"}",
        _ => "{}" })!.AsObject();

    private sealed class Catalog : Host.IToolCatalogView
    {
        public IReadOnlyDictionary<string, Host.ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, Host.ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<Host.ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Host.ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal Catalog(string release)
        {
            var descriptors = new Dictionary<string, Host.ToolDescriptor>();
            foreach (var method in Host.PortedToolDeclarations.Methods(release))
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                if (!PortedToolContract.Families(release).Any(f => f.Tools.Contains(name))) continue;
                Host.DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var d);
                descriptors.Add(name, new Host.ToolDescriptor(d.Name, d.RawDescription, d.Tool, d.Classification,
                    d.Signature, d.Parameters, d.DryRun, d.CandidateFamily, "foundation"));
            }
            All = descriptors;
        }
    }
}
