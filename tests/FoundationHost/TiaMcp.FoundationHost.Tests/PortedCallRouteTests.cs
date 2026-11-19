extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Engine = toolpipeline::TiaMcp.FoundationHost;
using Xunit;

public sealed class PortedCallRouteTests
{
    [Theory, InlineData("20"), InlineData("21")]
    public void Registered_ports_share_the_foundation_boundary(string release)
    {
        using var worker = new Worker();
        var registry = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false).ToDictionary(t => t.ProtocolTool.Name);
        var ports = PortedToolContract.Create(worker, release).Where(t => PortedFamilies.Available(release, t.ProtocolTool.Name));
        int checkedPorts = 0;
        foreach (var port in ports)
        {
            var entry = registry[port.ProtocolTool.Name];
            if (entry is Engine.CanonicalParameterTool) entry = Inner(entry);
            if (entry is Host.AuditCallTool)
            {
                var forwarder = Inner(Assert.IsType<Host.VersionPolicyTool>(Inner(entry)));
                entry = forwarder switch
                {
                    CompatibilityTools.Forwarder => Inner(forwarder, "target"),
                    CompatibilityTools.ActionForwarder => Inner(forwarder, "original"),
                    CanonicalBatchTools.BatchTool => ((IReadOnlyDictionary<string, McpServerTool>)Field(forwarder, "roster"))[port.ProtocolTool.Name],
                    _ => throw new InvalidOperationException("Unexpected registered forwarder for " + port.ProtocolTool.Name)
                };
            }
            Assert.IsType<UsageHintTool>(entry);
            Assert.IsType<FoundationV4Tool>(Inner(entry));
            checkedPorts++;
        }
        Assert.True(checkedPorts > 0);
    }

    private static object Field(McpServerTool tool, string name)
        => tool.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tool)!;
    private static McpServerTool Inner(McpServerTool tool, string name = "inner") => (McpServerTool)Field(tool, name);

    [Theory, InlineData("14sp1"), InlineData("19"), InlineData("20"), InlineData("21")]
    public async Task CallTool_preserves_read_capability_and_denied_write_envelopes(string release)
    {
        using var worker = new Worker();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        using var pipeline = new Engine.EngineHostPipeline(release, contextWorker, tools);
        var bridge = pipeline.AllTools.Single(t => t.ProtocolTool.Name == "CallTool");
        var server = DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>();
        async Task<JsonNode> Call(string name, object arguments)
        {
            var request = new RequestContext<CallToolRequestParams>(server) { Params = new() { Name = "CallTool",
                Arguments = JsonSerializer.SerializeToElement(new { name, arguments }).Deserialize<Dictionary<string, JsonElement>>() } };
            using var current = Host.McpServer.UseProgressRequest(request, CancellationToken.None);
            var result = await bridge.InvokeAsync(request);
            var body = result.StructuredContent!;
            Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)Assert.Single(result.Content)).Text)));
            Assert.Equal(release, (string?)body["meta"]?["releaseKey"]);
            return body;
        }

        var read = await Call("ListDevices", new { });
        Assert.True((bool?)read["ok"], read.ToJsonString());
        Assert.Equal("succeeded", (string?)read["meta"]?["outcome"]);
        Assert.Equal("read-only", (string?)read["meta"]?["execution"]);
        string? previousLimit = Environment.GetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS", "256");
            var page = await Call("ListDevices", new { });
            Assert.True((bool?)page["ok"]);
            Assert.NotNull(page["data"]?["export"]?["id"]);
            Assert.NotNull(page["meta"]?["paging"]);
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS", previousLimit); }

        var invalid = await Call("ListDevices", new { unknownArgument = true });
        Assert.Equal("INVALID_ARGUMENT", (string?)invalid["error"]?["code"]);
        Assert.Equal(release is "20" or "21" ? "CallTool" : "ListDevices", (string?)invalid["meta"]?["tool"]);

        using (GenerationWriteFence.Acquire(contextWorker.SessionKey, "active-generation"))
        {
            var fenced = await Call("ManagePlcSimAdvancedInstance", new { instanceName = "Fixture", action = "stop", confirmInstanceChange = true,
                dryRun = false, apiPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll") });
            Assert.Equal("PRECONDITION_FAILED", (string?)fenced["error"]?["code"]);
            Assert.Equal("generation-run-active", (string?)fenced["error"]?["details"]?["condition"]);
            Assert.DoesNotContain("runtime-channels.ExecutePlcSimAdvanced", worker.Operations);
        }

        var unavailable = await Call("ManageTestSuiteCase", new { category = "system", name = "Case", action = "showInEditor" });
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)unavailable["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)unavailable["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)unavailable["meta"]?["execution"]);

        using var settings = new ApprovalEnabled();
        int waits = 0;
        var previousWait = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = (pending, _, _) =>
        {
            waits++;
            return Task.FromResult(new ApprovalOutcome(pending, false, "denied"));
        };
        try
        {
            var denied = await Call("AttachDeviceNodeToSubnet", new { deviceItemPath = "PLC/CPU", interfaceIndex = 0, subnetName = "PN", dryRun = false });
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)denied["error"]?["code"]);
            Assert.Equal("rejected-before-operation", (string?)denied["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)denied["meta"]?["execution"]);
            Assert.Equal(1, waits);
            Assert.DoesNotContain("hardware-network.HardwareAttachDeviceNodeToSubnet", worker.Operations);
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previousWait; }
    }

    [Theory, InlineData("14sp1"), InlineData("19")]
    public async Task Not_ready_openness_still_dispatches_plcsim_advanced_reads(string release)
    {
        using var worker = new BundledWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false,
            readinessProvider: () => new JsonObject { ["ready"] = false, ["cause"] = "Synthetic not-ready fixture" }, hostPorts: false);
        var read = tools.Single(t => t.ProtocolTool.Name == "ListPlcSimAdvancedInstances");
        var server = DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>();
        var request = new RequestContext<CallToolRequestParams>(server) { Params = new() { Name = read.ProtocolTool.Name } };
        var result = await read.InvokeAsync(request);
        var body = result.StructuredContent!;
        Assert.Equal(new[] { "runtime-channels.ExecutePlcSimAdvanced" }, worker.Operations);
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.Equal("read-only", (string?)body["meta"]?["execution"]);
    }

    [Theory, InlineData("14sp1"), InlineData("19")]
    public async Task Direct_and_forwarded_simulator_reads_preserve_file_validation_and_paging(string release)
    {
        using var worker = new Worker();
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        using var pipeline = new Engine.EngineHostPipeline(release, contextWorker, tools);
        var server = DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>();
        const string target = "ListPlcSimAdvancedInstances";
        async Task<JsonNode> Read(bool bridge, object arguments)
        {
            string name = bridge ? "CallTool" : target;
            var entry = pipeline.AllTools.Single(t => t.ProtocolTool.Name == name);
            var input = JsonSerializer.SerializeToElement(bridge ? new { name = target, arguments } : arguments);
            var request = new RequestContext<CallToolRequestParams>(server) { Params = new() {
                Name = name, Arguments = input.Deserialize<Dictionary<string, JsonElement>>() } };
            using var current = Host.McpServer.UseProgressRequest(request, CancellationToken.None);
            return (await entry.InvokeAsync(request)).StructuredContent!;
        }

        var missing = new { apiPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll") };
        var direct = await Read(false, missing);
        Assert.True((bool?)direct["ok"], direct.ToJsonString());
        Assert.Equal(new[] { "runtime-channels.ExecutePlcSimAdvanced" }, worker.Operations);
        var refused = await Read(true, missing);
        Assert.Equal("INVALID_ARGUMENT", (string?)refused["error"]?["code"]);
        Assert.Equal("/apiPath", (string?)refused["error"]?["details"]?["parameter"]);
        Assert.Equal("not-started", (string?)refused["meta"]?["execution"]);
        var unknown = await Read(true, new { unknownArgument = true });
        Assert.Equal("INVALID_ARGUMENT", (string?)unknown["error"]?["code"]);
        Assert.Equal("CallTool", (string?)unknown["meta"]?["tool"]);
        Assert.Single(worker.Operations);

        string? limit = Environment.GetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS", "256");
            var complete = await Read(false, new { });
            Assert.True((bool?)complete["ok"]);
            Assert.NotNull(complete["data"]?["instances"]);
            Assert.Null(complete["data"]?["export"]);
            var page = await Read(true, new { });
            Assert.NotNull(page["data"]?["export"]?["id"]);
            Assert.Equal("offset", (string?)page["meta"]?["paging"]?["mode"]);
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_MAX_RESPONSE_CHARS", limit); }
    }

    private sealed class BundledWorker : Worker, IFoundationSessionWorker
    {
        public bool Bundled => true;
        public bool SharedSession => false;
        public string ApprovalIdentity => "{}";
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => null;
        public Task<IDisposable?> AcquireLane(CancellationToken token) => Task.FromResult<IDisposable?>(null);
        public void ActivateLane(IDisposable? lane) { }
    }

    private class Worker : IFoundationWorker
    {
        internal List<string> Operations { get; } = new();
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Operations.Add(operation);
            return Task.FromResult<JsonNode?>(operation switch {
                "ReadState" => new JsonObject { ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap21" },
                "hardware-devices.HardwareDescribeDevices" or "hardware-devices.HardwareReadPlcNames" => new JsonArray(),
                "runtime-channels.ExecutePlcSimAdvanced" => new JsonObject {
                    ["Message"] = "Listed instances.", ["Ok"] = true, ["Data"] = new JsonObject { ["instances"] = new JsonArray() },
                    ["Meta"] = new JsonObject { ["success"] = true, ["operationSuccess"] = true } },
                _ => throw new InvalidOperationException("Unexpected worker operation " + operation)
            });
        }
        public void Dispose() { }
    }

    private sealed class ApprovalEnabled : IDisposable
    {
        private readonly ApprovalSettings previous = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
        internal ApprovalEnabled() => new ApprovalSettings(true, 1).Save(ApprovalSettings.SettingsPath);
        public void Dispose() => previous.Save(ApprovalSettings.SettingsPath);
    }
}
