extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Pipeline = enginehost::TiaMcp.FoundationHost.EngineHostPipeline;
using Xunit;

public sealed class B1OldReleaseBridgeTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19" }.Select(r => new object[] { r });
    private static RequestContext<CallToolRequestParams> Request(string tool, object args) => new(System.Reflection.DispatchProxy.Create<IMcpServer, ServerProxy>()) {
        Params = new CallToolRequestParams { Name = tool, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)) }
    };
    private static async Task<JsonObject> Invoke(IReadOnlyList<McpServerTool> tools, string name, object args)
    {
        var result = await tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(Request(name, args));
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)Assert.Single(result.Content)).Text)));
        return result.StructuredContent!.AsObject();
    }
    [Theory, MemberData(nameof(Releases))]
    public async Task BridgeUsesExactlyTheRegisteredCatalogAndReportsTargetFields(string release)
    {
        using var worker = new ForbiddenWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var names = tools.Select(t => t.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string name in PortedFamilies.All.Where(f => f.Name is "F01" or "F02" or "F03").SelectMany(f => f.Tools))
            Assert.Equal(PortedFamilies.Available(release, name), names.Contains(name));
        foreach (string name in new[] { "NoSuchB1Tool", "ListCommunicationConnections", "DecodePlcSimaticMl" })
        {
            var rejected = await Invoke(tools, "CallTool", new { name, arguments = new { } });
            Assert.Equal("TOOL_NOT_FOUND", (string?)rejected["error"]?["code"]);
        }
        var missing = await Invoke(tools, "CallTool", new { name = "AnalyzePlcReferences", arguments = new { } });
        Assert.Equal("directory", (string?)missing["error"]?["details"]?["parameter"]);
        var guidance = missing["meta"]!["warnings"]!.AsArray().Single(w => (string?)w!["code"] == "RECOVERY_GUIDANCE")!;
        Assert.True((string?)guidance["details"]?["getToolUsage"]?["toolName"] == "AnalyzePlcReferences", missing.ToJsonString());
        var preview = await Invoke(tools, "PreviewToolCall", new { name = "AnalyzePlcReferences", arguments = new { maxDepth = "bad" } });
        Assert.True((bool)preview["ok"]!);
        Assert.False((bool)preview["data"]!["ok"]!);
        Assert.Contains("directory", preview["data"]!["missing"]!.ToJsonString());
        Assert.Equal(2, preview["data"]!["validationErrors"]!.AsArray().Count);
        var gated = await Invoke(tools, "CallTool", new { name = "RenderPlcBlockDocument", arguments = new { softwarePath = "PLC_1", blockPath = "Main" } });
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)gated["error"]?["code"]);
        Assert.Equal("not-started", (string?)gated["meta"]?["execution"]);
        if (release is "14sp1" or "15.1" or "16")
        {
            var password = await Invoke(tools, "CallTool", new { name = "CompilePlcSoftware", arguments = new { softwarePath = "PLC_1", password = "fixture" } });
            Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)password["error"]?["code"]);
        }
        var categories = await Invoke(tools, "ListToolCategories", new { });
        Assert.Equal(names.Count, (int)categories["data"]!["toolCount"]!);
        Assert.Equal(0, worker.Calls);
    }
    [Theory, MemberData(nameof(Releases))]
    public async Task BridgedTargetKeepsItsFoundationApprovalBeforeDispatch(string release)
    {
        using var worker = new ApprovalWorker();
        var inner = new FoundationTool(FoundationTools.Definitions.Single(d => d.Name == "CreatePlcTag"), worker);
        int approvals = 0;
        var target = new FoundationV4Tool(inner, release, _ => TiaMcp.Logic.V4.BehaviorPolicy.Current, () => new(true, 1), (pending, _, _) => {
            approvals++;
            return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, false, "workbench-unavailable"));
        });
        using var pipeline = new Pipeline(release, new BoundWorker(), new[] { target });
        var rejected = await Invoke(pipeline.AllTools, "CallTool", new { name = "CreatePlcTag", arguments = new { plc = "PLC_1", table = "Tags", name = "Ready", dataType = "Bool", address = "%M0.0", dryRun = false, confirm = true, expectedProjectFile = "C:/Fixture.ap14" } });
        Assert.True((string?)rejected["error"]?["code"] == "CONFIRMATION_REQUIRED", rejected.ToJsonString());
        Assert.Equal("not-started", (string?)rejected["meta"]?["execution"]);
        Assert.Equal(1, approvals);
        Assert.Equal(0, worker.Writes);
    }
    [Theory, MemberData(nameof(Releases))]
    public async Task DoctorAcceptsExactReleaseKeysWithoutWorkerAccess(string release)
    {
        using var worker = new ForbiddenWorker();
        var result = await Invoke(LegacyHostToolRegistry.Create(worker, release, false), "GetEnvironmentDiagnostics", new { fix = false });
        Assert.True((bool)result["ok"]!);
        Assert.Equal("current", (string?)result["meta"]?["behaviorPolicy"]);
        Assert.Equal(0, worker.Calls);
    }
    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public async Task FullReleaseBridgeReportsTargetFieldsAndReturnsValidationPreview(string release)
    {
        using var pipeline = new Pipeline(release, new BoundWorker(), Array.Empty<McpServerTool>());
        var missing = await Invoke(pipeline.AllTools, "CallTool", new { name = "AnalyzePlcReferences", arguments = new { } });
        Assert.Equal("directory", (string?)missing["error"]?["details"]?["parameter"]);
        Assert.Equal("AnalyzePlcReferences", (string?)missing["meta"]?["tool"]);
        var preview = await Invoke(pipeline.AllTools, "PreviewToolCall", new { name = "AnalyzePlcReferences", arguments = new { maxDepth = "bad" } });
        Assert.True((bool)preview["ok"]!);
        Assert.False((bool)preview["data"]!["ok"]!);
        Assert.Equal(2, preview["data"]!["validationErrors"]!.AsArray().Count);
    }
    [Theory, MemberData(nameof(Releases))]
    public async Task PreviewTokenCanOnlyBeConsumedByItsOwningSessionOnce(string release)
    {
        var fixture = McpServerTool.Create((bool dryRun = true) => Host.McpServer.V4Result("SetDeviceItemAttribute",
            new JsonObject { ["dryRun"] = dryRun, ["value"] = "fixture" }), new() { Name = "SetDeviceItemAttribute" });
        using var first = new Pipeline(release, new BoundWorker(), new[] { fixture });
        using var second = new Pipeline(release, new BoundWorker(), new[] { fixture });
        var preview = await Invoke(first.AllTools, "PreviewToolBatch", new { expectedProject = "Fixture",
            operations = new[] { new { name = "SetDeviceItemAttribute", arguments = new { dryRun = true } } } });
        Assert.True((bool)preview["ok"]!);
        string token = (string)preview["data"]!["token"]!;
        using (second.EnterSession()) Assert.Equal("NOT_FOUND", (string?)Host.McpServer.ApplyToolBatch(token).StructuredContent?["error"]?["code"]);
        using (first.EnterSession())
        {
            _ = Host.McpServer.ApplyToolBatch(token);
            Assert.Equal("NOT_FOUND", (string?)Host.McpServer.ApplyToolBatch(token).StructuredContent?["error"]?["code"]);
        }
    }
    private static void SetWorkerField(WorkerClient worker, string name, object value)
        => typeof(WorkerClient).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(worker, value);
    [Theory, MemberData(nameof(Releases))]
    public void LegacyCachedStateNeedsTheSameObservedProcessForCompleteBatchIdentity(string release)
    {
        using var worker = new WorkerClient(release, "unused.exe", "unused-api", false);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var observedStart = current.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        SetWorkerField(worker, "attachedProcessId", current.Id);
        SetWorkerField(worker, "hostProcessStartUtc", observedStart);
        SetWorkerField(worker, "approvalIdentity", new JsonObject { ["BindingEpoch"] = 7, ["ProjectFile"] = "C:/Stale.ap14" });
        // Legacy ReadState has no start time; its current project must replace the earlier approval cache.
        SetWorkerField(worker, "hostState", new JsonObject { ["IsAttached"] = true, ["ProcessId"] = current.Id, ["ProjectFile"] = "C:/Fixture.ap14" });
        var snapshot = worker.HostSnapshot();
        Assert.Equal(observedStart, (string?)snapshot["binding"]?["identity"]?["processStartUtc"]);
        Assert.Equal("C:/Fixture.ap14", (string?)snapshot["binding"]?["identity"]?["projectPath"]);
        Assert.Equal(7, (int?)snapshot["binding"]?["identity"]?["generation"]);
        Assert.NotEmpty(TiaMcpServer.ModelContextProtocol.BatchPlanStore.BindingState(snapshot["binding"]!.AsObject()));
        SetWorkerField(worker, "hostProcessStartUtc", "2000-01-01T00:00:00.0000000Z");
        Assert.Null(worker.HostSnapshot()["binding"]?["identity"]?["processStartUtc"]);
        Assert.Throws<InvalidOperationException>(() => TiaMcpServer.ModelContextProtocol.BatchPlanStore.BindingState(worker.HostSnapshot()["binding"]!.AsObject()));
    }
    [Theory, MemberData(nameof(Releases))]
    public async Task IdleDisconnectInvalidatesHostSessionAndCachedBatchIdentity(string release)
    {
        using var worker = new WorkerClient(release, "unused.exe", "unused-api", false);
        SetWorkerField(worker, "attachedProcessId", Environment.ProcessId);
        SetWorkerField(worker, "hostState", new JsonObject { ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap14" });
        object session = worker.HostSessionKey;
        await worker.Call("Disconnect", new JsonObject(), CancellationToken.None);
        Assert.NotSame(session, worker.HostSessionKey);
        var snapshot = worker.HostSnapshot();
        Assert.False((bool)snapshot["session"]!["isConnected"]!);
        Assert.Null(snapshot["binding"]?["identity"]?["projectPath"]);
        Assert.Throws<InvalidOperationException>(() => TiaMcpServer.ModelContextProtocol.BatchPlanStore.BindingState(snapshot["binding"]!.AsObject()));
    }
    private sealed class ForbiddenWorker : IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        { Calls++; throw new InvalidOperationException("No worker work expected: " + operation); }
        public void Dispose() { }
    }
    private sealed class ApprovalWorker : IFoundationWorker
    {
        internal int Writes;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState" || (bool?)arguments["dryRun"] == true)
                return Task.FromResult<JsonNode?>(new JsonObject { ["Executed"] = false, ["ProjectFile"] = "C:/Fixture.ap14" });
            Writes++;
            throw new InvalidOperationException("No target dispatch expected: " + operation);
        }
        public void Dispose() { }
    }
    private sealed class BoundWorker : enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        public object SessionKey { get; } = new();
        public bool Faulted => false;
        public JsonNode? Binding => Snapshot()["binding"];
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true },
            ["session"] = new JsonObject { ["isConnected"] = true, ["project"] = "Fixture" },
            ["binding"] = new JsonObject { ["identity"] = new JsonObject { ["tiaMajorVersion"] = 14, ["processId"] = 123,
                ["processStartUtc"] = "2026-10-09T00:00:00Z", ["projectPath"] = "C:/Fixture.ap14", ["projectName"] = "Fixture", ["generation"] = 1 } } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => Task.FromResult(Snapshot());
        public Task<IDisposable> Acquire(CancellationToken token) => throw new InvalidOperationException("No worker lane expected.");
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine route expected.");
        public void Dispose() { }
    }
}
