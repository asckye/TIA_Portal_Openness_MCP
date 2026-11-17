extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using WatchTools = toolpipeline::TiaMcpServer.ModelContextProtocol.PlcWatchTools;
using WatchService = toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService;

[Collection(BundleSelectionCollection.Name)]
public sealed class B4HostPortsTests
{
    [Fact]
    public void Source_catalog_registers_every_served_table_parameter_and_current_binding_description()
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var registrations = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference", "tool-catalog", "engine-source.json")))!["registrations"]!;
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var tools = LegacyHostToolRegistry.Create(worker, "21", false).ToDictionary(tool => tool.ProtocolTool.Name);
        foreach (string name in new[] { "ListPlcForceTables", "ManagePlcTableEntries", "ImportPlcWatchTableOffline", "ExportPlcWatchTablesToDirectory" })
        {
            var registration = registrations[name]!;
            var parameters = registration["parameters"]!.AsObject();
            foreach (var parameter in tools[name].ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
                Assert.True(parameters.ContainsKey(parameter.Name), name + "/" + parameter.Name);
            foreach (var parameter in parameters.Where(parameter => parameter.Key != "exportDirectory"))
                Assert.Contains(parameter.Key, (string)registration["signature"]!);
        }
        Assert.Contains("createTable", (string?)registrations["ManagePlcTableEntries"]!["parameters"]!["action"]!["description"]);
        foreach (string name in new[] { "ImportPlcWatchTableOffline", "ExportPlcWatchTablesToDirectory" })
        {
            Assert.Equal("watch", (string?)registrations[name]!["parameters"]!["tableKind"]!["value"]);
            Assert.Contains("force", (string?)registrations[name]!["parameters"]!["tableKind"]!["description"]);
        }
        foreach (string name in new[] { "ImportPlcWatchTableOffline", "ManagePlcTableEntries" })
            foreach (string parameter in new[] { "expectedPlanHash", "expectedProjectFile" })
            {
                Assert.Equal("", (string?)registrations[name]!["parameters"]![parameter]!["value"]);
                Assert.False(string.IsNullOrWhiteSpace((string?)registrations[name]!["parameters"]![parameter]!["description"]));
            }
    }

    [Fact]
    public async Task Force_directory_batch_reaches_the_watch_port_with_native_item_hash_and_confirmation()
    {
        using var worker = new WatchPlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        string directory = Path.Combine(root, "bin-build", "force-export-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(directory, "Group", "Force.xml");
        var requests = new List<JsonObject>();
        var tools = new WatchTools(new WatchService((operation, args) => {
            Assert.Equal("plc-watch.ExecutePlcOrganisation", operation);
            var request = args["request"]!.AsObject(); requests.Add(request.DeepClone().AsObject());
            if ((string?)request["Operation"] == "ListPlcForceTables")
                return new JsonObject { ["Names"] = new JsonArray("Group/Force") };
            Assert.Equal("ExportPlcWatchTablesToDirectory", (string?)request["Operation"]);
            Assert.Equal("force", (string?)request["Kind"]);
            Assert.Equal("\\AGroup/Force\\z", (string?)request["RegexName"]);
            bool dry = (bool)request["DryRun"]!;
            if (!dry)
            {
                Assert.True((bool?)args["confirm"]); Assert.Equal(WatchPlanWorker.Project, (string?)args["expectedProjectFile"]);
                Assert.Equal(new string('f', 64), (string?)request["ExpectedPlanHash"]);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, "<Document><SW.WatchAndForceTables.PlcForceTable ID=\"0\"><AttributeList><Name>Force</Name>" +
                    "</AttributeList></SW.WatchAndForceTables.PlcForceTable></Document>");
            }
            return new JsonObject { ["Batch"] = new JsonObject {
                ["Imported"] = dry ? new JsonArray() : new JsonArray(output), ["Failed"] = new JsonArray(),
                ["Meta"] = new JsonObject { ["operationSuccess"] = true, ["dryRun"] = dry, ["planHash"] = new string('f', 64),
                    ["tableKind"] = "force", ["items"] = new JsonArray(new JsonObject { ["objectPath"] = "Group/Force", ["outputFile"] = output }) }
            } };
        }, () => true, () => WatchPlanWorker.Project));
        var native = McpServerTool.Create(tools.ExportPlcWatchTablesToDirectoryV4);
        var reader = McpServerTool.Create(tools.GetPlcForceTablesV4);
        var batch = new CanonicalBatchTools.BatchTool(native.ProtocolTool, new Dictionary<string, McpServerTool> {
            [native.ProtocolTool.Name] = native, [reader.ProtocolTool.Name] = reader
        }, "21", () => new JsonObject { ["projectPath"] = WatchPlanWorker.Project, ["processId"] = 123 }) {
            CompleteApproval = (_, _) => Task.CompletedTask };
        int approvals = 0;
        batch.Approve = (pending, _, _) => { approvals++; return Task.FromResult(new ApprovalOutcome(pending, false, null)); };
        var values = new JsonObject { ["softwarePath"] = "PLC", ["dir"] = directory, ["tableKind"] = "force" };
        RequestContext<CallToolRequestParams> Request() => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = native.ProtocolTool.Name, Arguments = values.Deserialize<Dictionary<string, JsonElement>>() } };
        try
        {
            var preview = await batch.InvokeAsync(Request());
            Assert.True((bool?)preview.StructuredContent?["ok"], preview.StructuredContent?.ToJsonString());
            Assert.False(Directory.Exists(directory)); Assert.Equal(0, approvals); Assert.Equal(2, requests.Count);
            Assert.Equal(new string('f', 64), (string?)preview.StructuredContent!["data"]!["items"]![0]!["canonicalPlanHash"]);
            values["dryRun"] = false; values["confirm"] = true;
            values["expectedPlanHash"] = preview.StructuredContent["data"]!["planHash"]!.DeepClone();
            values["expectedProjectFile"] = WatchPlanWorker.Project;
            var result = await batch.InvokeAsync(Request());
            Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
            Assert.Equal(1, approvals); Assert.Equal(7, requests.Count); Assert.True(File.Exists(output));
            Assert.Equal(1, WatchTableImportValidation.Validate(output, "force"));
            Assert.Single(requests.Where(request => (string?)request["Operation"] == native.ProtocolTool.Name && (bool?)request["DryRun"] == false));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task TableUsageRetrievalFollowsTheEightReleaseRegistrations(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        var usage = roster.Single(t => t.ProtocolTool.Name == "GetToolUsage");
        foreach (string name in new[] { "ManagePlcTableEntries", "ImportPlcWatchTableOffline", "ExportPlcWatchTablesToDirectory" })
        {
            var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = "GetToolUsage", Arguments = new Dictionary<string, JsonElement> {
                    ["toolName"] = JsonSerializer.SerializeToElement(name) } } };
            var result = await usage.InvokeAsync(request);
            var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!;
            Assert.Equal(release != "14sp1", (bool?)body["ok"]);
            if (release == "14sp1") Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
            else Assert.Contains(name == "ManagePlcTableEntries" ? "createTable" : "force", body.ToJsonString());
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("15.1")]
    [InlineData("21")]
    public void WatchSchemasExposeTableKindAndWholeToolPlanBinding(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false).ToDictionary(t => t.ProtocolTool.Name);
        foreach (string name in new[] { "ImportPlcWatchTableOffline", "ExportPlcWatchTablesToDirectory" })
        {
            var kind = tools[name].ProtocolTool.InputSchema.GetProperty("properties").GetProperty("tableKind");
            Assert.Equal("watch", kind.GetProperty("default").GetString());
            Assert.Equal(new[] { "watch", "force" }, kind.GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        }
        foreach (string name in new[] { "ImportPlcWatchTableOffline", "ManagePlcTableEntries" })
        {
            var fields = tools[name].ProtocolTool.InputSchema.GetProperty("properties");
            Assert.Equal("", fields.GetProperty("expectedPlanHash").GetProperty("default").GetString());
            Assert.Equal("", fields.GetProperty("expectedProjectFile").GetProperty("default").GetString());
        }
        var actions = tools["ManagePlcTableEntries"].ProtocolTool.InputSchema.GetProperty("properties").GetProperty("action").GetProperty("enum");
        Assert.Contains("createTable", actions.EnumerateArray().Select(v => v.GetString()));
        var exportFields = tools["ExportPlcWatchTablesToDirectory"].ProtocolTool.InputSchema.GetProperty("properties");
        Assert.True(exportFields.GetProperty("dryRun").GetProperty("default").GetBoolean());
        Assert.False(exportFields.GetProperty("confirm").GetProperty("default").GetBoolean());
        Assert.Equal("", exportFields.GetProperty("expectedPlanHash").GetProperty("default").GetString());
        Assert.Equal("", exportFields.GetProperty("expectedProjectFile").GetProperty("default").GetString());
        var oldTools = LegacyHostToolRegistry.Create(worker, "14sp1", false);
        foreach (string name in new[] { "ImportPlcWatchTableOffline", "ManagePlcTableEntries", "ExportPlcWatchTablesToDirectory" })
            Assert.DoesNotContain(oldTools, t => t.ProtocolTool.Name == name);
    }

    [Theory]
    [InlineData("createTable")]
    [InlineData("createComment")]
    [InlineData("deleteEntry")]
    [InlineData("deleteTable")]
    public void EveryWatchMutationRequiresTheNativePreviewPlanAndProject(string action)
    {
        using var worker = new WatchPlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var requests = new List<JsonObject>();
        var tools = Tools(requests);
        int index = action == "deleteEntry" ? 0 : -1;
        CallToolResult Call(bool dry, string hash = "", string project = "") => tools.ManagePlcTableEntriesV4(
            "PLC", "watch", "Group/Table%2FName", action, index, true, dry, expectedPlanHash: hash, expectedProjectFile: project);
        var preview = Call(true);
        Assert.True((bool?)preview.StructuredContent!["ok"], preview.StructuredContent.ToJsonString());
        var request = Assert.Single(requests);
        Assert.Equal(action, (string?)request["Action"]);
        Assert.Equal("watch", (string?)request["Kind"]);
        Assert.Equal("Group/Table%2FName", (string?)request["Path"]);
        Assert.True((bool?)request["DryRun"]);
        string hash = (string)preview.StructuredContent["data"]!["planHash"]!;
        Assert.Matches("^[0-9a-f]{64}$", hash);
        foreach (var (plan, project, code) in new[] {
            ("", WatchPlanWorker.Project, "INVALID_ARGUMENT"), (hash, "", "INVALID_ARGUMENT"),
            (new string('0', 64), WatchPlanWorker.Project, "PLAN_STALE"), (hash, "C:/Other.ap21", "IDENTITY_MISMATCH") })
        {
            var refused = Call(false, plan, project);
            Assert.Equal(code, (string?)refused.StructuredContent!["error"]!["code"]);
            Assert.Single(requests);
        }
        var applied = Call(false, hash, WatchPlanWorker.Project);
        Assert.True((bool?)applied.StructuredContent!["ok"], applied.StructuredContent.ToJsonString());
        Assert.Equal(2, requests.Count);
        Assert.False((bool?)requests[1]["DryRun"]);
        var force = tools.ManagePlcTableEntriesV4("PLC", "force", "Table", "createTable");
        Assert.Equal("INVALID_ARGUMENT", (string?)force.StructuredContent!["error"]!["code"]);
        Assert.Equal(2, requests.Count);
        var read = tools.ManagePlcTableEntriesV4("PLC", "force", "Table", dryRun: false);
        Assert.True((bool?)read.StructuredContent!["ok"]);
        Assert.Equal("read", (string?)requests[2]["Action"]);
    }

    [Fact]
    public void ForceImportBindsFileKindAndGroupAndDirectoryExportMapsTheNativePreview()
    {
        using var worker = new WatchPlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var requests = new List<JsonObject>();
        var tools = Tools(requests);
        string file = Path.Combine(Path.GetTempPath(), "tia-force-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(file, "<Document><SW.WatchAndForceTables.PlcForceTable ID=\"0\"><AttributeList><Name>Force</Name></AttributeList>" +
                "</SW.WatchAndForceTables.PlcForceTable></Document>");
            var preview = tools.ImportPlcWatchTableOfflineV4("PLC", file, "Group%2FName", tableKind: "force");
            Assert.True((bool?)preview.StructuredContent!["ok"], preview.StructuredContent.ToJsonString());
            var request = Assert.Single(requests);
            Assert.Equal("force", (string?)request["Kind"]);
            Assert.Equal("Group%2FName", (string?)request["GroupPath"]);
            Assert.Equal(file, (string?)request["FilePath"]);
            string hash = (string)preview.StructuredContent["data"]!["planHash"]!;
            foreach (var (plan, project) in new[] { ("", WatchPlanWorker.Project), (hash, "") })
            {
                var refused = tools.ImportPlcWatchTableOfflineV4("PLC", file, "Group%2FName", false, "force", plan, project);
                Assert.Equal("INVALID_ARGUMENT", (string?)refused.StructuredContent!["error"]!["code"]);
                Assert.Single(requests);
            }
            File.AppendAllText(file, "\n");
            var changed = tools.ImportPlcWatchTableOfflineV4("PLC", file, "Group%2FName", false, "force", hash, WatchPlanWorker.Project);
            Assert.Equal("PLAN_STALE", (string?)changed.StructuredContent!["error"]!["code"]);
            Assert.Single(requests);
            preview = tools.ImportPlcWatchTableOfflineV4("PLC", file, "Group%2FName", tableKind: "force");
            hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
            var applied = tools.ImportPlcWatchTableOfflineV4("PLC", file, "Group%2FName", false, "force", hash, WatchPlanWorker.Project);
            Assert.True((bool?)applied.StructuredContent!["ok"]);
            Assert.Equal(3, requests.Count);
            Assert.False((bool?)requests[2]["DryRun"]);
            tools.ExportPlcWatchTablesToDirectory("PLC", Path.GetTempPath(), tableKind: "force");
            Assert.Equal("force", (string?)requests[3]["Kind"]);
            Assert.Equal("ExportPlcWatchTablesToDirectory", (string?)requests[3]["Operation"]);
            Assert.True((bool?)requests[3]["DryRun"]);
        }
        finally { File.Delete(file); }
    }

    private static WatchTools Tools(List<JsonObject> requests) => new(new WatchService((operation, args) => {
        Assert.Equal("plc-watch.ExecutePlcOrganisation", operation);
        requests.Add(args["request"]!.AsObject().DeepClone().AsObject());
        if ((string?)args["request"]!["Operation"] == "ExportPlcWatchTablesToDirectory")
            return new JsonObject { ["Batch"] = new JsonObject { ["Imported"] = new JsonArray(), ["Failed"] = new JsonArray() } };
        return new JsonObject { ["Message"] = "native fixture readback", ["Meta"] = new JsonObject { ["operationSuccess"] = true } };
    }, () => true, () => WatchPlanWorker.Project));

    private sealed class WatchPlanWorker : toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        internal const string Project = "C:/Projects/watch.ap21";
        public bool Faulted => false;
        public JsonNode? Binding => new JsonObject { ["identity"] = new JsonObject { ["processId"] = 123, ["projectPath"] = Project } };
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new InvalidOperationException();
        public Task<toolpipeline::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview,
            CancellationToken token) => throw new InvalidOperationException("No native dispatch expected.");
        public void Dispose() { }
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void RegistryIncludesExactlyTheUsableB4Tools(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var names = LegacyHostToolRegistry.Create(worker, release, false).Select(tool => tool.ProtocolTool.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("16", "ManagePlcTableEntries", "{\"softwarePath\":\"PLC\",\"tableKind\":\"watch\",\"tablePath\":\"Watch\",\"action\":\"read\"}")]
    [InlineData("20", "ManageMotionAxis", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"createMapping\"}")]
    [InlineData("20", "ManageMotionAxis", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"connectIdent\"}")]
    [InlineData("15.1", "ManageMotionAxis", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"action\":\"addMasterValue\",\"aspect\":\"conveyorLeading\",\"name\":\"Master\"}")]
    [InlineData("14sp1", "ConfigureMotionHardwareConnection", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Axis\",\"interfaceKind\":\"torque\",\"action\":\"read\"}")]
    [InlineData("18", "GetMotionAxisConfiguration", "{\"softwarePath\":\"PLC\",\"objectPath\":\"Group/Axis\"}")]
    [InlineData("18", "GetTechnologyObjectTree", "{\"softwarePath\":\"PLC\",\"groupPath\":\"Group\"}")]
    [InlineData("17", "ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"read\",\"name\":\"Unit\",\"unitKind\":\"safety\"}")]
    [InlineData("17", "ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"update\",\"name\":\"Unit\",\"properties\":{\"NamespacePreset\":\"Ns\"}}")]
    [InlineData("19", "ManagePlcSoftwareUnit", "{\"softwarePath\":\"PLC\",\"action\":\"createFromMasterCopy\",\"name\":\"Unit\",\"libraryName\":\"Library\",\"masterCopyPath\":\"Master\",\"copyMode\":\"Replace\"}")]
    public async Task UnavailableActionsRefuseBeforeWorkerStateAndNativeLookup(string release, string name, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(tool => tool.ProtocolTool.Name == name);
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
        var result = await tool.InvokeAsync(request);
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, worker.Calls);
    }
}
