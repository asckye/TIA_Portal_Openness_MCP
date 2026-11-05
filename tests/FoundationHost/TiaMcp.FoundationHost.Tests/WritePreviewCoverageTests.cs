extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using Xunit;
using Metadata = TiaMcpServer.ModelContextProtocol.ToolMetadata;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;

[Collection(BundleSelectionCollection.Name)]
public sealed class WritePreviewCoverageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Plan_hash_matches_normalized_arguments_and_rejects_changed_arguments_or_target_before_execution()
    {
        using var worker = new PlanWorker();
        using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("EnsureSubnet", new JsonObject(), completed: true); }
        var args = new JsonObject { ["subnetName"] = "PN", ["anchorDeviceItemPath"] = "PLC/CPU" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, Execute);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        var reordered = new JsonObject { ["anchorDeviceItemPath"] = "PLC/CPU", ["subnetName"] = "PN" };
        Assert.Equal(hash, (string?)Host.RunWritePreview("EnsureSubnet", true, reordered, Execute).StructuredContent!["data"]!["planHash"]);
        args["subnetName"] = "changed";
        Assert
            .Equal("PLAN_STALE", (string?)Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "C:/Projects/original.ap21")
                .StructuredContent!["error"]!["code"]);
        Assert.Equal(0, calls);
        args["subnetName"] = "PN"; worker.Identity["identity"]!["projectPath"] = "C:/Projects/other.ap21";
        Assert
            .Equal("PLAN_STALE", (string?)Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "C:/Projects/original.ap21")
                .StructuredContent!["error"]!["code"]);
        Assert.Equal(0, calls);
        worker.Identity["identity"]!["projectPath"] = "C:/Projects/original.ap21";
        Assert.True((bool?)Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "C:/Projects/original.ap21").StructuredContent!["ok"]);
        Assert.Equal(1, calls);
        output.WriteLine("Generic preview uses the existing canonical SHA-256 plan hash and cached target identity.");
    }

    [Fact]
    public async Task Async_plan_hash_rejects_a_changed_target_before_dispatch()
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        Task<CallToolResult> Execute() { calls++; return Task.FromResult(Host.V4Result("DownloadPlc", new JsonObject(), completed: true)); }
        var preview = await Host.RunWritePreviewAsync("DownloadPlc", true, new { softwarePath = "PLC/CPU" }, Execute);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        worker.Identity["identity"]!["processId"] = 456;
        var rejected = await Host.RunWritePreviewAsync("DownloadPlc", false, new { softwarePath = "PLC/CPU" }, Execute, hash, "C:/Projects/original.ap21");
        Assert.Equal("PLAN_STALE", (string?)rejected.StructuredContent!["error"]!["code"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Published_write_wrapper_forwards_the_expected_plan_hash_before_service_access()
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        var tools = new enginehost::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools(null!);
        var preview = tools.SetDeviceItemAttributeV4("PLC/CPU", "Name", "CPU_2");
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        var stale = tools.SetDeviceItemAttributeV4("PLC/CPU", "Name", "CPU_3", false, hash, "C:/Projects/original.ap21");
        Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent!["error"]!["code"]);
        Assert.Equal("not-started", (string?)stale.StructuredContent["meta"]!["execution"]);
        var wrongProject = tools.SetDeviceItemAttributeV4("PLC/CPU", "Name", "CPU_2", false, hash, "C:/Projects/other.ap21");
        Assert.Equal("IDENTITY_MISMATCH", (string?)wrongProject.StructuredContent!["error"]!["code"]);
    }

    [Theory]
    [InlineData("C:/Projects/other.ap21", "IDENTITY_MISMATCH")]
    [InlineData("relative.ap21", "INVALID_ARGUMENT")]
    [InlineData("C:", "INVALID_ARGUMENT")]
    [InlineData("", "INVALID_ARGUMENT")]
    public async Task Execution_requires_the_absolute_bound_project_before_sync_or_async_dispatch(string project, string code)
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("EnsureSubnet", new JsonObject(), completed: true); }
        var args = new { subnetName = "PN" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, Execute);
        Assert.True((bool?)preview.StructuredContent!["ok"]);
        string hash = (string)preview.StructuredContent["data"]!["planHash"]!;
        var sync = Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, project);
        var asyncResult = await Host.RunWritePreviewAsync("EnsureSubnet", false, args, () => Task.FromResult(Execute()), hash, project);
        Assert.Equal(code, (string?)sync.StructuredContent!["error"]!["code"]);
        Assert.Equal(code, (string?)asyncResult.StructuredContent!["error"]!["code"]);
        Assert.Equal("not-started", (string?)sync.StructuredContent["meta"]!["execution"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Execution_requires_a_reviewed_hash_and_accepts_the_canonical_bound_project()
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("EnsureSubnet", new JsonObject(), completed: true); }
        var args = new { subnetName = "PN" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, Execute);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        var rejected = Host.RunWritePreview("EnsureSubnet", false, args, Execute, "", "C:/Projects/original.ap21");
        Assert.Equal("INVALID_ARGUMENT", (string?)rejected.StructuredContent!["error"]!["code"]);
        Assert.Equal("expectedPlanHash", (string?)rejected.StructuredContent["error"]!["details"]!["parameter"]);
        Assert.Equal(0, calls);
        Assert.True((bool?)Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "c:/PROJECTS/original.ap21").StructuredContent!["ok"]);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("InvokeObject")]
    [InlineData("InvokeService")]
    [InlineData("ManageOnlineDriveFunctions")]
    public void Read_modes_keep_their_existing_execution_without_project_or_hash_inputs(string tool)
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        var result = Host.RunWritePreview(tool, false, new { action = "read" }, () => {
            calls++; return Host.V4Result(tool, new JsonObject());
        }, writing: false);
        Assert.True((bool?)result.StructuredContent!["ok"]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void In_memory_export_handle_operations_execute_directly_without_a_preview()
    {
        using var worker = new PlanWorker();
        using var session = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(new enginehost::TiaMcp.FoundationHost.EngineSessionContext(worker));
        var tools = new enginehost::TiaMcpServer.ModelContextProtocol.ExportTools();
        string id = enginehost::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "A", "retained", 4).id;
        Assert.True((bool?)tools.DeleteExport(id).StructuredContent!["ok"]);
        Assert.Null(enginehost::TiaMcp.FoundationHost.SessionExportStore.Get(id));
        enginehost::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "B", "retained", 4);
        Assert.Equal(1, (int?)tools.ClearExports(0).StructuredContent!["data"]!["deleted"]);
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
    public void Clearing_parked_responses_only_deletes_the_calling_sessions_handles(string release)
    {
        using var worker = new PlanWorker();
        var catalog = new HmiTagPathContractTests.DeleteCatalog(release);
        var current = new enginehost::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog };
        var other = new enginehost::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog };
        string previous = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        string currentId = "", otherId = "";
        try
        {
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            string Park(string target)
            {
                var response = Host.V4Result("GetSessionState", new JsonObject { ["value"] = target });
                var parked = enginehost::TiaMcpServer.ModelContextProtocol.ResponseGuardTool.Shrink(
                    response, "GetSessionState", target, force: true);
                return (string)parked.StructuredContent!["data"]!["export"]!["id"]!;
            }
            using (enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(other)) otherId = Park("other");
            using (enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(current))
            {
                currentId = Park("current");
                var tools = new enginehost::TiaMcpServer.ModelContextProtocol.ExportTools();
                Assert.Equal(1, (int?)tools.ClearExports(0).StructuredContent!["data"]!["deleted"]);
                Assert.Equal(0, (int?)tools.ClearExports(0).StructuredContent!["data"]!["deleted"]);
                Assert.Null(enginehost::TiaMcp.FoundationHost.SessionExportStore.Get(currentId));
                Assert.True(tools.GetExport(otherId).IsError);
            }
            using (enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(other))
            {
                Assert.NotNull(enginehost::TiaMcp.FoundationHost.SessionExportStore.Get(otherId));
                Assert.False(new enginehost::TiaMcpServer.ModelContextProtocol.ExportTools().GetExport(otherId).IsError ?? false);
            }
        }
        finally
        {
            current.Exports.Dispose(); other.Exports.Dispose();
            TiaMcpServer.ModelContextProtocol.ExportStore.Delete(currentId);
            TiaMcpServer.ModelContextProtocol.ExportStore.Delete(otherId);
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
        }
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
    public void Every_registered_write_has_a_default_preview_or_an_explicit_plan(string release)
    {
        using var worker = new FakeWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var writes = tools.Where(t => Metadata.Find(t.ProtocolTool.Name)?.Operation is "WRITE" or "ONLINE-WRITE").ToArray();
        Assert.NotEmpty(writes);
        foreach (var implementation in writes)
        {
            var tool = implementation.ProtocolTool;
            var properties = tool.InputSchema.GetProperty("properties");
            bool preview = properties.TryGetProperty("dryRun", out var dryRun)
                && dryRun.GetProperty("type").GetString() == "boolean"
                && dryRun.GetProperty("default").GetBoolean();
            bool planned = properties.TryGetProperty("mode", out var mode)
                && mode.TryGetProperty("default", out var value) && value.GetString() == "preview";
            bool previewedBatch = tool.Name == "ApplyToolBatch" && properties.TryGetProperty("previewToken", out _);
            bool restartPreview = tool.Name == "RestartOpennessWorker"
                && !properties.GetProperty("confirmRestart").GetProperty("default").GetBoolean();
            bool memoryOnly = tool.Name is "DeleteExportHandle" or "ClearExportHandles"; // in-memory handle only, no project change
            Assert.True(preview || planned || previewedBatch || restartPreview || memoryOnly, release + ": " + tool.Name);
        }
    }

    [Fact]
    public void Planned_arguments_are_identical_on_preview_and_execution_and_secrets_are_redacted()
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        var arguments = new { devicePath = "PLC/CPU", attributeName = "Name", value = "CPU_2", password = "private-value" };
        CallToolResult Execute() { calls++; return Host.V4Result("SetDeviceItemAttribute", new JsonObject(), completed: true); }
        var preview = Host.RunWritePreview("SetDeviceItemAttribute", true, arguments, Execute);
        Assert.Equal(0, calls);
        var applied = Host
            .RunWritePreview("SetDeviceItemAttribute", false, arguments, Execute, (string)preview.StructuredContent!["data"]!["planHash"]!,
                "C:/Projects/original.ap21");
        Assert.Equal(1, calls);
        Assert.True(JsonNode.DeepEquals(preview.StructuredContent!["data"]!["plannedChanges"], applied.StructuredContent!["data"]!["plannedChanges"]));
        Assert.DoesNotContain("private-value", preview.StructuredContent.ToJsonString());
        Assert.Equal("read-only", (string?)preview.StructuredContent["meta"]?["execution"]);
        Assert.Equal("completed", (string?)applied.StructuredContent["meta"]?["execution"]);
        Assert.Equal(applied.StructuredContent.ToJsonString(), JsonNode.Parse(((TextContentBlock)applied.Content.Single()).Text)!.ToJsonString());
    }

    [Theory]
    [InlineData("SetDeviceItemAttribute", "{\"deviceItemPath\":\"PLC/CPU\",\"attributeName\":\"Name\",\"value\":\"CPU_2\"}")]
    [InlineData("SetPlcCpuSettings", "{\"cpuPath\":\"PLC/CPU\",\"settings\":{\"exactAttributes\":{\"Name\":\"CPU_2\"}}}")]
    [InlineData("DownloadPlc", "{\"softwarePath\":\"PLC\",\"password\":\"private-value\"}")]
    [InlineData("SetUnifiedHmiButtonEventScriptCode",
                "{\"hmiSoftwarePath\":\"HMI\",\"screenName\":\"/Overview\",\"buttonName\":\"Start\"," +
                "\"eventType\":\"Tapped\",\"scriptCode\":\"HMIRuntime.Tags(\\\"Start\\\").Write(true);\"}")]
    public async Task Default_preview_does_not_dispatch_a_native_operation(string name, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var shared = LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        var tool = pipeline.AllTools.Single(t => t.ProtocolTool.Name == name);
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }
        });
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.True((bool?)result.StructuredContent?["data"]?["dryRun"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)result.StructuredContent!["data"]!["planHash"]!);
        Assert.DoesNotContain("private-value", result.StructuredContent!.ToJsonString());
        Assert.InRange(worker.Calls, 0, 1); // The fixture refuses every operation except managed ReadState.
    }

    [Theory]
    [InlineData("AttachDeviceNodeToSubnet")]
    [InlineData("ConnectDeviceNodesToProfinetSubnet")]
    [InlineData("CreateGsdDevice")]
    [InlineData("CreateVersionControlWorkspace")]
    [InlineData("DownloadPlc")]
    [InlineData("EnsureSubnet")]
    [InlineData("ImportAlarmClasses")]
    [InlineData("ImportAlarmTextLists")]
    [InlineData("ImportHmiConnection")]
    [InlineData("ImportHmiScreen")]
    [InlineData("ImportHmiScreensFromDirectory")]
    [InlineData("ImportHmiTagTable")]
    [InlineData("ImportHmiTagTablesFromDirectory")]
    [InlineData("ImportMasterCopyFromGlobalLibrary")]
    [InlineData("ImportOpcUaInterface")]
    [InlineData("ImportTechnologyObject")]
    [InlineData("ImportTechnologyObjectsFromDirectory")]
    [InlineData("InvokeObject")]
    [InlineData("InvokeService")]
    [InlineData("ManageOnlineDriveFunctions")]
    [InlineData("SeedProjectFromReference")]
    [InlineData("SetDeviceItemAttribute")]
    [InlineData("SetOpcUaInterfaceEnabled")]
    [InlineData("SetPlcCpuSettings")]
    [InlineData("SetPlcWatchTableModifyValue")]
    [InlineData("SetUnifiedHmiButtonEventScriptCode")]
    [InlineData("SetUnifiedHmiRuntimeState")]
    public async Task Maintained_write_examples_preview_without_native_dispatch(string name)
    {
        using var worker = new PlanWorker();
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var method = typeof(Host).Assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Single(candidate => candidate.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == "McpServerToolAttribute"
                && attribute.NamedArguments.Any(argument => argument.MemberName == "Name" && (string?)argument.TypedValue.Value == name)));
        // Unconfigured services make any accidental service access fail; published admission is covered separately.
        var constructor = method.DeclaringType!.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var target = constructor.Invoke(new object?[constructor.GetParameters().Length]);
        var tool = McpServerTool.Create(method, target);
        var path = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "reference", "tool-examples", "calls.json");
        var arguments = JsonNode.Parse(File.ReadAllText(path))!["profiles"]!["plc-foundation"]![name]!["arguments"]!.DeepClone().AsObject();
        arguments["dryRun"] = true;
        arguments.Remove("expectedPlanHash");
        arguments.Remove("expectedProjectFile");
        if (name is "InvokeObject" or "InvokeService") arguments["allowWrite"] = true;
        if (name == "ManageOnlineDriveFunctions") arguments["action"] = "performRamToRomCopy";
        foreach (string field in new[] { "dryRun", "expectedPlanHash", "expectedProjectFile" })
            Assert.Equal(CompatibilityTools.PreviewParameterDescriptions[field],
                tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty(field).GetProperty("description").GetString());
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(
            DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
                Params = new CallToolRequestParams { Name = name,
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) }
            });
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.True((bool?)result.StructuredContent?["data"]?["dryRun"]);
        Assert.Equal("read-only", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)result.StructuredContent!["data"]!["planHash"]!);
        Assert.Equal(0, worker.Calls);
    }

    internal sealed class PlanWorker : enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal int Calls;
        internal JsonObject Identity = new() { ["identity"] = new JsonObject { ["processId"] = 123, ["projectPath"] = "C:/Projects/original.ap21" } };
        public bool Faulted => false;
        public JsonNode? Binding => Identity;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<enginehost::TiaMcp.FoundationHost
            .EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        { Calls++; throw new NotSupportedException(); }
        public void Dispose() { }
    }

}
