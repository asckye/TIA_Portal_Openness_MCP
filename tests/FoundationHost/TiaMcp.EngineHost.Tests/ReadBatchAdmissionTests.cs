extern alias foundationhost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using Xunit;
using Foundation = foundationhost::TiaMcp.FoundationHost;

// RunReadOnlyToolBatch admission on the host catalog of every release (P8-82): the 47 reads the batch review opened
// (bin-build/refactor/batch-flag-review.md section 3.1), the two admitted reads it closed (GetUnifiedCrossReferences,
// GetExportContent) and the four slow reads it keeps closed. Each call uses the tool's published GetToolUsage example,
// and a different expectedProject, so an admitted row stops at the batch identity check and nothing is dispatched.
public sealed class ReadBatchAdmissionTests
{
    private static readonly string[] Order = { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" };

    public static IEnumerable<object[]> Releases() => Order.Select(key => new object[] { key });

    // The first release that serves each opened tool (the review's "Served on" column); all of them are served on V21.
    private static readonly IReadOnlyDictionary<string, string> Opened = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GetProjectInfo"] = "14sp1", ["GetProjectTree"] = "14sp1", ["GetSoftwareInfo"] = "14sp1", ["GetSoftwareTree"] = "14sp1",
        ["ListPlcBlocks"] = "14sp1", ["GetPlcBlockHierarchy"] = "14sp1", ["GetPlcBlockInfo"] = "14sp1", ["ListPlcTypes"] = "14sp1",
        ["GetPlcTypeInfo"] = "14sp1", ["ListPlcTagTables"] = "14sp1", ["ListPlcTags"] = "14sp1", ["ListPlcUserConstants"] = "14sp1",
        ["ListPlcSystemConstants"] = "14sp1", ["ListPlcExternalSources"] = "14sp1", ["ListPlcWatchTables"] = "15.1",
        ["ListTechnologyObjects"] = "14sp1",
        ["ListDevices"] = "14sp1", ["GetDeviceInfo"] = "14sp1", ["GetDeviceItemInfo"] = "14sp1", ["GetDeviceItemTree"] = "14sp1",
        ["GetDevicePlugLocations"] = "14sp1", ["GetDeviceIpAddress"] = "14sp1", ["GetDeviceItemIoAddresses"] = "14sp1",
        ["GetDeviceItemNetworkInfo"] = "14sp1", ["GetProjectTopology"] = "14sp1", ["GetPlcPutGetAccess"] = "15.1",
        ["GetPlcOpcUaConfiguration"] = "15.1", ["ListPlcForceTables"] = "15.1", ["GetOnlineState"] = "14sp1",
        ["CheckDownloadReadiness"] = "15.1", ["GetPlcWatchTableCurrentValuesReadOnly"] = "15.1", ["ValidateAutomationContext"] = "14sp1",
        ["GetHmiProgramInfo"] = "14sp1", ["ListHmiScreens"] = "14sp1", ["ListHmiScreenPaths"] = "14sp1", ["ListHmiTagTables"] = "14sp1",
        ["ListHmiTags"] = "14sp1", ["ListHmiConnections"] = "14sp1",
        ["GetUnifiedHmiButtonEvent"] = "19", ["GetUnifiedHmiDynamization"] = "19", ["GetUnifiedHmiTexts"] = "19",
        ["ListUnifiedHmiApiTypes"] = "19",
        ["ListVersionControlWorkspaces"] = "16", ["ListStandardPackages"] = "14sp1", ["DescribeStandardPackage"] = "14sp1",
        ["GetGenerationRun"] = "14sp1", ["GetPortalConnectionReadiness"] = "14sp1",
    };

    // Closed by the review (native cross-reference query; a result parked inside a parked batch) and kept closed
    // (a row over the 120 s worker budget faults the worker), with the first release that serves each.
    private static readonly IReadOnlyDictionary<string, string> Closed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GetUnifiedCrossReferences"] = "19", ["GetExportContent"] = "14sp1",
        ["DescribePlcBlockLogic"] = "14sp1", ["GetVersionControlStatus"] = "16", ["SearchHardwareCatalog"] = "18",
        ["SearchInstalledGsdDevices"] = "14sp1",
    };

    private static bool Serves(string first, string release) => Array.IndexOf(Order, first) <= Array.IndexOf(Order, release);

    [Fact]
    public void The_reviewed_lists_match_the_reviewed_rows()
    {
        Assert.Equal(47, Opened.Count);
        foreach (var name in Opened.Keys)
            Assert.True(ToolMetadata.Find(name) is { Operation: "READ", BatchRead: true }, name + " is not an admitted read");
        foreach (var name in Closed.Keys)
            Assert.True(ToolMetadata.Find(name) is { Operation: "READ", BatchRead: false }, name + " is not a closed read");
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Read_batch_admits_every_opened_read_the_release_serves(string release)
    {
        using var fixture = new Fixture(release);
        var expected = Opened.Where(pair => Serves(pair.Value, release)).Select(pair => pair.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, Opened.Keys.Where(fixture.Serves).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        if (release == "21") Assert.Equal(47, expected.Length);
        var operations = new JsonArray();
        foreach (var name in expected)
        {
            var operation = await fixture.Operation(name);
            var batch = await fixture.ReadBatch(new JsonArray(operation.DeepClone()));
            fixture.AssertAdmitted(batch, name);
            operations.Add(operation);
        }
        // All of them in one batch, in input order.
        var combined = await fixture.ReadBatch(operations);
        var items = combined["data"]?["items"] as JsonArray;
        Assert.True(items != null && items.Count == expected.Length, combined.ToJsonString());
        for (int i = 0; i < expected.Length; i++) fixture.AssertRow(items![i]!, i, expected[i]);
        fixture.AssertNothingDispatched();
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Read_batch_refuses_the_closed_and_the_slow_reads(string release)
    {
        using var fixture = new Fixture(release);
        var refused = Closed.Where(pair => Serves(pair.Value, release)).Select(pair => pair.Key).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(refused, Closed.Keys.Where(fixture.Serves).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        var admitted = await fixture.Operation("GetProjectInfo");
        foreach (var name in refused)
        {
            var operation = await fixture.Operation(name);
            fixture.AssertRefused(await fixture.ReadBatch(new JsonArray(operation.DeepClone())), name);
            // A refused read cannot ride along with an admitted one.
            fixture.AssertRefused(await fixture.ReadBatch(new JsonArray(admitted.DeepClone(), operation.DeepClone())), name);
        }
        fixture.AssertNothingDispatched();
    }

    private sealed class Fixture : IDisposable
    {
        private const string OtherProject = "OtherProject";
        private readonly string previous = EngineHostConfiguration.ReleaseKey;
        private readonly CountingFoundationWorker foundation = new();
        private readonly BoundEngineWorker engine = new();
        private readonly EngineHostPipeline pipeline;
        private readonly Dictionary<string, McpServerTool> tools;

        // Registered as EngineReleaseHost registers it: shared implementations without host ports, and GetToolUsage
        // answering for the complete published roster.
        internal Fixture(string release)
        {
            EngineHostConfiguration.ReleaseKey = release;
            IReadOnlyList<McpServerTool>? roster = null;
            var shared = Foundation.LegacyHostToolRegistry.Create(foundation, release, false, usageRoster: () => roster!, hostPorts: false);
            pipeline = new EngineHostPipeline(release, engine, shared);
            roster = pipeline.AllTools;
            tools = pipeline.AllTools.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
        }

        internal bool Serves(string name) => tools.ContainsKey(name);

        internal async Task<JsonObject> Call(string name, JsonObject arguments)
        {
            var result = await tools[name].InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, PipelineTests.ServerProxy>())
                { Params = new CallToolRequestParams { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } });
            return result.StructuredContent!.AsObject();
        }

        // The tool's published example call, as an agent reads it from GetToolUsage before building a batch.
        internal async Task<JsonObject> Operation(string name)
        {
            var usage = await Call("GetToolUsage", new JsonObject { ["toolName"] = name });
            var arguments = usage["data"]?["example"]?["request"]?["params"]?["arguments"] as JsonObject;
            Assert.True(arguments != null, name + " has no usage example: " + usage.ToJsonString());
            return new JsonObject { ["name"] = name, ["arguments"] = arguments!.DeepClone() };
        }

        internal Task<JsonObject> ReadBatch(JsonArray operations)
            => Call("RunReadOnlyToolBatch", new JsonObject { ["operations"] = operations, ["expectedProject"] = OtherProject });

        internal void AssertAdmitted(JsonObject batch, string name)
        {
            var items = batch["data"]?["items"] as JsonArray;
            Assert.True(items is { Count: 1 }, name + " was not admitted: " + batch.ToJsonString());
            AssertRow(items![0]!, 0, name);
        }

        // An admitted row reaches the per-row identity check, which refuses it before dispatch.
        internal void AssertRow(JsonNode row, int index, string name)
        {
            Assert.Equal(index, (int?)row["index"]);
            Assert.Equal(name, (string?)row["target"]);
            var result = row["result"]!;
            Assert.True((string?)result["error"]?["code"] == "PRECONDITION_FAILED"
                && (string?)result["error"]?["details"]?["target"] == OtherProject, name + ": " + result.ToJsonString()
                + " worker calls: " + string.Join(",", foundation.Calls.Concat(engine.Calls)));
            Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
        }

        // The batch's existing refusal: the whole batch is rejected at operations before any row.
        internal void AssertRefused(JsonObject batch, string name)
        {
            Assert.True((string?)batch["error"]?["code"] == "INVALID_ARGUMENT" && (string?)batch["error"]?["details"]?["parameter"] == "operations"
                && batch["data"] == null, name + " was not refused: " + batch.ToJsonString());
            Assert.Equal("RunReadOnlyToolBatch", (string?)batch["meta"]?["tool"]);
            Assert.Equal("rejected-before-operation", (string?)batch["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)batch["meta"]?["execution"]);
        }

        internal void AssertNothingDispatched()
        {
            Assert.Empty(foundation.Calls);
            Assert.Empty(engine.Calls);
        }

        public void Dispose() { pipeline.Dispose(); EngineHostConfiguration.ReleaseKey = previous; }
    }

    private sealed class CountingFoundationWorker : Foundation.IFoundationWorker
    {
        internal readonly List<string> Calls = new();
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            lock (Calls) Calls.Add(operation);
            throw new InvalidOperationException("No worker dispatch expected: " + operation);
        }
        public void Dispose() { }
    }

    private sealed class BoundEngineWorker : IEngineWorker
    {
        internal readonly List<string> Calls = new();
        public bool Faulted => false;
        public object SessionKey { get; } = new();
        public JsonNode? Binding => Snapshot()["binding"];
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true },
            ["session"] = new JsonObject { ["isConnected"] = true, ["project"] = "Fixture" },
            ["binding"] = new JsonObject { ["identity"] = new JsonObject { ["tiaMajorVersion"] = 21, ["processId"] = 123,
                ["processStartUtc"] = "2026-10-20T00:00:00Z", ["projectPath"] = "C:/Fixture.ap21", ["projectName"] = "Fixture", ["generation"] = 1 } } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No worker restart expected.");
        // The Openness lane is entered before the per-row identity check; a lease is not a dispatch.
        internal int Leases;
        public Task<IDisposable> Acquire(CancellationToken token)
        {
            Interlocked.Increment(ref Leases);
            return Task.FromResult<IDisposable>(new Lease());
        }
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        {
            lock (Calls) Calls.Add(name);
            throw new InvalidOperationException("No engine dispatch expected: " + name);
        }
        public void Dispose() { }
    }
}
