extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using Xunit;

public sealed class FoundationV4ContractsTests
{
    [Theory]
    [InlineData("Main%20Screen", "main%20screen", true)]
    [InlineData("Main%20Screen", "Main Screen", false)]
    [InlineData("/Group/Main%20Screen", "main screen", true)]
    [InlineData("Overview", "overview", true)]
    public async Task Screen_design_name_matches_native_literal_and_case_rules(string target, string name, bool accepted)
    {
        foreach (string release in new[] { "19", "20", "21" })
        {
            using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
            using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
            var tool = DesignTool("ApplyUnifiedHmiScreenDesign", (_, _) => throw new InvalidOperationException("Preview must not dispatch."));
            var args = new JsonObject { ["hmiSoftwarePath"] = "HMI", ["screenName"] = target, ["dryRun"] = true,
                ["design"] = new JsonObject { ["screen"] = new JsonObject { ["name"] = name }, ["items"] = new JsonArray() } };
            var body = Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiScreenDesign", args.ToJsonString())));
            Assert.Equal(accepted, (bool?)body["ok"]);
            if (!accepted) Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        }
    }

    private static JsonObject UsageArguments(string tool, string profile)
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!
            ["profiles"]![profile]![tool]!["arguments"]!.DeepClone().AsObject();
    }

    private static McpServerTool DesignTool(string name, Func<string, JsonObject, JsonNode?> call)
    {
        var service = new enginehost::TiaMcpServer.Siemens.Services.UnifiedHmiService(call, () => "C:/Fixture/Project.ap21");
        var tools = new enginehost::TiaMcpServer.ModelContextProtocol.UnifiedHmiTools(service);
        return name switch {
            "ApplyUnifiedHmiTheme" => McpServerTool.Create(tools.ApplyUnifiedHmiThemeV4),
            "ApplyUnifiedHmiLayout" => McpServerTool.Create(tools.ApplyUnifiedHmiLayoutV4),
            _ => McpServerTool.Create(tools.ApplyUnifiedHmiScreenDesignJsonV4)
        };
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Failed_button_action_does_not_claim_it_was_applied(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        int calls = 0;
        var service = new enginehost::TiaMcpServer.Siemens.Services.UnifiedHmiService((_, _) => {
            calls++;
            return new JsonObject { ["Value"] = new JsonObject { ["Meta"] = new JsonObject {
                ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["status"] = "ReadOrWriteFailed",
                ["nativeMessage"] = "Event script write did not complete: Property Name field is not supported for event script action" } } };
        }, () => "C:/Fixture/Project.ap" + release);
        var tools = new enginehost::TiaMcpServer.ModelContextProtocol.UnifiedHmiTools(service);
        var tool = McpServerTool.Create(tools.EnsureUnifiedHmiButtonActionV4);
        var arguments = new JsonObject {
            ["hmiSoftwarePath"] = "HMI", ["screenName"] = "Overview", ["buttonName"] = "Start",
            ["eventType"] = "Down", ["actionKind"] = "set-bit", ["targetTag"] = "Run", ["dryRun"] = false };
        var body = Body(await tool.InvokeAsync(Request("EnsureUnifiedHmiButtonAction", arguments.ToJsonString())));
        Assert.Equal(1, calls);
        Assert.False((bool)body["ok"]!);
        Assert.DoesNotContain("applied via", (string)body["data"]!["summary"]!);
        Assert.Contains("was not applied", (string)body["data"]!["summary"]!);
        Assert.Contains("Property Name field", body.ToJsonString());
    }

    private static JsonObject DesignSuccess() => new() {
        ["Value"] = new JsonObject { ["Meta"] = new JsonObject { ["operationSuccess"] = true, ["mayHaveChanged"] = true } } };

    [Theory]
    [InlineData("19", "plc-foundation")] [InlineData("20", "plc-foundation")] [InlineData("21", "plc-foundation")]
    [InlineData("19", "full-engine")] [InlineData("20", "full-engine")] [InlineData("21", "full-engine")]
    public async Task ApplyUnifiedHmiTheme_example_previews_and_dispatches_only_screen_and_items(string release, string profile)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        int calls = 0;
        var tool = DesignTool("ApplyUnifiedHmiTheme", (operation, args) => {
            calls++;
            Assert.Equal("hmi-unified.ExecuteUnifiedHmi", operation);
            var request = args["request"]!.Deserialize<TiaMcp.Adapters.Contracts.UnifiedHmiRequest>()!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"screen\":{\"BackColor\":\"0xFFFFFFFF\"},\"items\":[]}"),
                JsonNode.Parse(request.DesignJson)));
            return DesignSuccess();
        });
        var arguments = UsageArguments("ApplyUnifiedHmiTheme", profile);
        Assert.True((bool)Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiTheme", arguments.ToJsonString())))["ok"]!);
        Assert.Equal(0, calls);
        arguments["dryRun"] = false;
        Assert.True((bool)Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiTheme", arguments.ToJsonString())))["ok"]!);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("19", "plc-foundation")] [InlineData("20", "plc-foundation")] [InlineData("21", "plc-foundation")]
    [InlineData("19", "full-engine")] [InlineData("20", "full-engine")] [InlineData("21", "full-engine")]
    public async Task ApplyUnifiedHmiLayout_with_screen_dispatches_without_Name(string release, string profile)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        int calls = 0;
        var tool = DesignTool("ApplyUnifiedHmiLayout", (_, args) => {
            calls++;
            var request = args["request"]!.Deserialize<TiaMcp.Adapters.Contracts.UnifiedHmiRequest>()!;
            var design = JsonNode.Parse(request.DesignJson)!.AsObject();
            Assert.Equal(new[] { "screen", "items" }, design.Select(p => p.Key));
            Assert.Null(design["screen"]!["Name"]);
            Assert.Equal(800, (int)design["screen"]!["Width"]!);
            return DesignSuccess();
        });
        var arguments = UsageArguments("ApplyUnifiedHmiLayout", profile);
        arguments["screenName"] = "/Group/Overview%20Screen";
        arguments["layout"]!["screen"] = new JsonObject { ["name"] = "Overview Screen", ["width"] = 800 };
        Assert.True((bool)Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiLayout", arguments.ToJsonString())))["ok"]!);
        Assert.Equal(0, calls);
        arguments["dryRun"] = false;
        Assert.True((bool)Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiLayout", arguments.ToJsonString())))["ok"]!);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("19", "plc-foundation")] [InlineData("20", "plc-foundation")] [InlineData("21", "plc-foundation")]
    [InlineData("19", "full-engine")] [InlineData("20", "full-engine")] [InlineData("21", "full-engine")]
    public async Task ApplyUnifiedHmiScreenDesign_usage_example_previews(string release, string profile)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        int calls = 0;
        var tool = DesignTool("ApplyUnifiedHmiScreenDesign", (_, args) => {
            calls++;
            var request = args["request"]!.Deserialize<TiaMcp.Adapters.Contracts.UnifiedHmiRequest>()!;
            Assert.Null(JsonNode.Parse(request.DesignJson)!["screen"]!["Name"]);
            return DesignSuccess();
        });
        var arguments = UsageArguments("ApplyUnifiedHmiScreenDesign", profile);
        Assert.True((bool)Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiScreenDesign", arguments.ToJsonString())))["ok"]!);
        Assert.Equal(0, calls);
        arguments["dryRun"] = false;
        Assert.True((bool)Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiScreenDesign", arguments.ToJsonString())))["ok"]!);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task Screen_design_naming_another_screen_is_INVALID_ARGUMENT(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        int calls = 0;
        var tool = DesignTool("ApplyUnifiedHmiScreenDesign", (_, _) => { calls++; return DesignSuccess(); });
        var arguments = UsageArguments("ApplyUnifiedHmiScreenDesign", "plc-foundation");
        arguments["design"]!["screen"]!["name"] = "Other";
        foreach (bool dryRun in new[] { true, false })
        {
            arguments["dryRun"] = dryRun;
            var body = Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiScreenDesign", arguments.ToJsonString())));
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
            Assert.Contains("renaming is refused", (string?)body["error"]!["message"]);
        }
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("19", false)] [InlineData("20", false)] [InlineData("21", false)]
    [InlineData("19", true)] [InlineData("20", true)] [InlineData("21", true)]
    [InlineData("19", null)] [InlineData("20", null)] [InlineData("21", null)]
    public async Task Worker_argument_refusal_with_no_change_is_INVALID_ARGUMENT(string release, bool? changed)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        int calls = 0;
        var tool = DesignTool("ApplyUnifiedHmiTheme", (_, _) => {
            calls++;
            return new JsonObject { ["MayHaveChanged"] = changed,
                ["Failure"] = new JsonObject { ["Code"] = "ArgumentException", ["Message"] = "Native property value refused." } };
        });
        var arguments = UsageArguments("ApplyUnifiedHmiTheme", "plc-foundation");
        arguments["dryRun"] = false;
        var body = Body(await tool.InvokeAsync(Request("ApplyUnifiedHmiTheme", arguments.ToJsonString())));
        Assert.Equal(changed == true ? "OUTCOME_UNKNOWN" : "INVALID_ARGUMENT", (string?)body["error"]!["code"]);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public void Unified_result_keeps_indented_text_fields(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        const string text = "first\n   second";
        const string source = "first\n   at legitimate text";
        var response = new { Data = new { text = source, ScriptCode = text, GlobalDefinitionAreaScriptCode = text },
            Meta = new { operationSuccess = true, nativeStackTrace = "hidden stack", password = "hidden secret" } };
        var body = Body(enginehost::TiaMcpServer.ModelContextProtocol.UnifiedHmiContract.Map("ReadUnifiedScriptModule", response, false, true));
        Assert.Equal(source, (string?)body["data"]!["text"]);
        Assert.Equal(text, (string?)body["data"]!["scriptCode"]);
        Assert.Equal(text, (string?)body["data"]!["globalDefinitionAreaScriptCode"]);
        Assert.Null(body["data"]!["evidence"]!["nativeStackTrace"]);
        Assert.Null(body["data"]!["evidence"]!["password"]);
    }

    private static readonly HashSet<string> LegacyNames = JsonNode.Parse(File.ReadAllText(Path.Combine(
        TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "reference/tool-catalog/v3-v4-renames.json")))!.AsObject()
        .Where(pair => pair.Key != pair.Value!.GetValue<string>()).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
    private static RequestContext<CallToolRequestParams> Request(string name, string json = "{}") =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } };
    private static JsonObject Body(CallToolResult result)
    {
        var text = ((TextContentBlock)result.Content.Single()).Text;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result.StructuredContent));
        var envelope = V4Json.Deserialize<Envelope>(text);
        Assert.Equal(!envelope.Ok, result.IsError);
        return JsonNode.Parse(text)!.AsObject();
    }
    private static McpServerTool Tool(string name, FakeWorker? worker = null, string release = "19") =>
        LegacyHostToolRegistry.Create(worker ?? new FakeWorker(), release, false).Single(t => t.ProtocolTool.Name == name);

    [Theory]
    [InlineData("{}", null, null)]
    [InlineData("{\"globalDefinitionAreaScriptCode\":\"\",\"async\":false}", "", false)]
    [InlineData("{\"globalDefinitionAreaScriptCode\":\"const keep = 2;\",\"async\":true}", "const keep = 2;", true)]
    [InlineData("{\"async\":false}", null, false)]
    [InlineData("{\"globalDefinitionAreaScriptCode\":\"\"}", "", null)]
    public async Task Script_options_keep_single_schema_types_and_preserve_omission(string options, string? globals, bool? async)
    {
        using var worker = new WritePreviewCoverageTests.PlanWorker();
        using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        TiaMcp.Adapters.Contracts.UnifiedHmiRequest? dispatched = null;
        int calls = 0;
        var service = new enginehost::TiaMcpServer.Siemens.Services.UnifiedHmiService((operation, workerArguments) =>
        {
            Assert.Equal("hmi-unified.ExecuteUnifiedHmi", operation);
            calls++;
            dispatched = workerArguments["request"]!.Deserialize<TiaMcp.Adapters.Contracts.UnifiedHmiRequest>();
            return new JsonObject { ["Value"] = new JsonObject { ["Meta"] = new JsonObject { ["operationSuccess"] = true } } };
        }, () => "C:/Fixture/Project.ap21");
        var tools = new enginehost::TiaMcpServer.ModelContextProtocol.UnifiedHmiTools(service);
        var tool = McpServerTool.Create(tools.SetUnifiedHmiButtonEventScriptCodeV4);
        var schema = tool.ProtocolTool.InputSchema;
        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("globalDefinitionAreaScriptCode").GetProperty("type").GetString());
        Assert.Equal("boolean", properties.GetProperty("async").GetProperty("type").GetString());
        Assert.False(properties.TryGetProperty("context", out _));
        var required = schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.DoesNotContain("globalDefinitionAreaScriptCode", required);
        Assert.DoesNotContain("async", required);
        var arguments = JsonNode.Parse(options)!.AsObject();
        arguments["hmiSoftwarePath"] = "HMI";
        arguments["screenName"] = "Main";
        arguments["buttonName"] = "Start";
        arguments["eventType"] = "Press";
        arguments["scriptCode"] = "new code";
        var preview = Body(await tool.InvokeAsync(Request("SetUnifiedHmiButtonEventScriptCode", arguments.ToJsonString())));
        arguments["dryRun"] = false;
        arguments["expectedPlanHash"] = preview["data"]!["planHash"]!.DeepClone();
        arguments["expectedProjectFile"] = preview["data"]!["targetIdentity"]!["identity"]!["projectPath"]!.DeepClone();
        Assert.True((bool)Body(await tool.InvokeAsync(Request("SetUnifiedHmiButtonEventScriptCode", arguments.ToJsonString())))["ok"]!);
        Assert.Equal(1, calls);
        Assert.NotNull(dispatched);
        Assert.Equal("new code", dispatched.ScriptCode);
        Assert.Equal(globals, dispatched.GlobalDefinitionAreaScriptCode);
        Assert.Equal(async, dispatched.Async);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Ported_usage_preserves_the_listed_schema_and_named_palette_keys(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        var target = roster.Single(t => t.ProtocolTool.Name == "ApplyUnifiedHmiTheme");
        var usage = roster.Single(t => t.ProtocolTool.Name == "GetToolUsage");
        var data = Body(await usage.InvokeAsync(Request("GetToolUsage", "{\"toolName\":\"ApplyUnifiedHmiTheme\"}")))["data"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(target.ProtocolTool.InputSchema.GetRawText()), data["inputSchema"]));
        Assert.NotNull(data["inputSchema"]!["properties"]!["theme"]!["properties"]!["palette"]!["properties"]!["Page"]);
    }

    [Theory]
    // P8-81b H-05: CreateGsdDevice is registered only on V18-V21 (register only available tools), one tool less on V14 SP1-V17.
    // P8-81e: plus two graphics tools on every release, minus DescribeHmiScreenItem before V19; P8-81f: plus ManageDcbLibraries
    // on V16/V17 (master 6cf6b132 roster; confirmed by the formal captures).
    // XREF adds the guarded offline AnalyzePlcCrossReferences tool on every release.
    // NO-LOCK-2 adds the host-only GetCallResult tool on every release.
    [InlineData("14sp1", 281)] [InlineData("15.1", 321)] [InlineData("16", 350)]
    [InlineData("17", 373)] [InlineData("18", 399)] [InlineData("19", 475)]
    public async Task EveryRegisteredEntryHasOneNameTypedSchemaAndV4Rejection(string release, int count)
    {
        var worker = new FakeWorker();
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        Assert.Equal(count, roster.Count);
        Assert.Equal(count, roster.Select(t => t.ProtocolTool.Name).Distinct().Count());
        Assert.DoesNotContain(roster, t => LegacyNames.Contains(t.ProtocolTool.Name));
        Assert.Contains(roster, t => t.ProtocolTool.Name == "CallTool");
        foreach (var tool in roster)
        {
            Assert.Equal(EnvelopeSchema.Value.GetRawText(), tool.ProtocolTool.OutputSchema!.Value.GetRawText());
            Assert.DoesNotContain(tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject(), p => p.Name.EndsWith("Json"));
            var result = Body(await tool.InvokeAsync(Request(tool.ProtocolTool.Name, "{\"__invalid\":true}")));
            Assert.Equal(4, (int)result["schemaVersion"]!);
            Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]!["code"]);
            Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
            Assert.Equal(release, (string?)result["meta"]!["releaseKey"]);
            Assert.Equal(tool.ProtocolTool.Name, (string?)result["meta"]!["tool"]);
        }
        Assert.Equal(0, worker.Calls);
    }


    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")]
    [InlineData("17")] [InlineData("18")] [InlineData("19")]
    public async Task RuntimeWorkerMoveRetainsNestedCallAdmissionEnvelope(string release)
    {
        var worker = new FakeWorker();
        var bridge = Tool("CallTool", worker, release);
        foreach (var name in new[] {
                "ListPlcSimAdvancedInstances", "ManagePlcSimAdvancedInstance", "GetPlcSimAdvancedTags", "WritePlcSimAdvancedTags",
                    "RunPlcSimAdvancedTestScenario"
            })
        {
            var json = JsonSerializer
                .Serialize(new { name, arguments = new Dictionary<string, bool> { ["SnapshotReject"] = true, ["snapshotReject"] = true } });
            var result = Body(await bridge.InvokeAsync(Request("CallTool", json)));
            Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]!["code"]);
            Assert.Equal("", (string?)result["error"]!["details"]!["parameter"]);
            Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
            Assert.Equal("CallTool", (string?)result["meta"]!["tool"]);
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("{\"udt\":\"{}\",\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udtJson\":\"{}\",\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udt\":null,\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udt\":{\"Name\":\"X\",\"members\":[]},\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udt\":{\"name\":\"X\",\"name\":\"Y\",\"members\":[]},\"outputReleaseKey\":\"19\"}")]
    [InlineData("{\"udt\":{\"name\":\"X\",\"members\":[],\"unknown\":true},\"outputReleaseKey\":\"19\"}")]
    public async Task TypedConstructionRejectsLegacyAndAmbiguousInputs(string json)
    {
        var worker = new FakeWorker();
        var result = Body(await Tool("BuildPlcUdt", worker).InvokeAsync(Request("BuildPlcUdt", json)));
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]!["code"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("A borrowed project/session is already open and bound; OpenProject cannot replace it.")]
    [InlineData("A project/session is already open and bound; close it explicitly before opening another.")]
    public void OpenProjectKeepsWorkerRefusalReason(string reason)
    {
        var result=Body(FoundationV4Result.Failure("18","OpenProject","fixture",true,true,null,
            new WorkerOperationException(reason,-32603,"rejected-before-operation",
                    "{\"exceptionType\":\"AdapterPreconditionException\",\"isArgument\":false}")));
        Assert.Equal("PRECONDITION_FAILED",(string?)result["error"]?["code"]);Assert.Equal(reason,(string?)result["error"]?["message"]);
        Assert.Equal("not-started",(string?)result["meta"]?["execution"]);Assert.False((bool?)result["meta"]?["requiresSessionReset"]);
    }
    [Fact]
    public void WorkerIOExceptionCarriesSanitizedBoundedMessage()
    {
        string text=@"Recovery export refused at C:\Users\Private\P.ap18; token=private-secret "+new string('x',6000);
        var result=Body(FoundationV4Result.Failure("18","ImportPlcBlocksFromDirectory","fixture",true,true,null,
            new WorkerOperationException(text,-32603,"unknown","{\"exceptionType\":\"IOException\"}")));
        string diagnostic=(string)result["error"]!["details"]!["evidence"]!["workerMessage"]!;
        Assert.Contains("Recovery export refused",diagnostic);Assert.Contains("<path>",diagnostic);Assert.True(diagnostic.Length<=4096);
        Assert.DoesNotContain("Private",diagnostic);Assert.DoesNotContain("private-secret",diagnostic);
        Assert.Equal("OUTCOME_UNKNOWN",(string?)result["error"]?["code"]);Assert.True((bool?)result["meta"]?["requiresSessionReset"]);
    }

    [Fact]
    public async Task CandidateKeepsOutputReleaseAndValidationEvidence()
    {
        var result = Body(await Tool("BuildPlcUdt").InvokeAsync(Request("BuildPlcUdt",
            "{\"udt\":{\"name\":\"UDT_Status\",\"members\":[{\"name\":\"Ready\",\"datatype\":\"Bool\"}]},\"outputReleaseKey\":\"14sp1\"}")));
        Assert.True((bool)result["ok"]!);
        Assert.Equal("14sp1", (string?)result["data"]!["outputReleaseKey"]);
        Assert.False((bool)result["data"]!["schemaValidated"]!);
        Assert.False((bool)result["data"]!["importValidated"]!);
        Assert.Contains(result["meta"]!["warnings"]!.AsArray(), w => (string?)w!["code"] == "CANDIDATE_ONLY");
    }

    [Fact]
    public async Task LimitsRemainEnforcedAndPlannerUsesClosedTypedRows()
    {
        var args = new JsonObject { ["udt"] = new JsonObject { ["name"] = new string('x', 4097), ["members"] = new JsonArray() }, ["outputReleaseKey"] = "19" };
        var result = Body(await Tool("BuildPlcUdt").InvokeAsync(Request("BuildPlcUdt", args.ToJsonString())));
        Assert.Equal("LIMIT_EXCEEDED", (string?)result["error"]!["code"]);
        foreach (var rows in new[] { "[{\"Id\":\"X\"}]", "[{\"id\":\"X\",\"priority\":1.5}]", "[{\"id\":\"X\",\"dependencies\":[\"X\"]}]" })
            Assert
                .False((bool)Body(await Tool("PlanArtifactImportOrder")
                        .InvokeAsync(Request("PlanArtifactImportOrder", "{\"artifacts\":" + rows + "}")))["ok"]!);
        result =
            Body(await Tool("PlanArtifactImportOrder")
                .InvokeAsync(Request("PlanArtifactImportOrder", "{\"artifacts\":[{\"id\":\"B\",\"dependencies\":[\"A\"]},{\"id\":\"A\"}]}")));
        Assert.Equal("[\"A\",\"B\"]", result["data"]!["order"]!.ToJsonString());
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")] [InlineData("18")] [InlineData("19")]
    public async Task MaintainedExamplesFitEveryAdvertisedSchema(string release)
    {
        var roster = LegacyHostToolRegistry.Create(new FakeWorker(), release, false);
        var guide = roster.Single(t => t.ProtocolTool.Name == "GetToolUsage");
        foreach (var tool in roster)
        {
            var usage = Body(await guide.InvokeAsync(Request("GetToolUsage", JsonSerializer.Serialize(new { toolName = tool.ProtocolTool.Name }))));
            Assert.True((bool)usage["ok"]!, tool.ProtocolTool.Name);
            var args = JsonSerializer.SerializeToElement(usage["data"]!["example"]!["request"]!["params"]!["arguments"]);
            var validation = new InputContract<JsonElement>(new InputSchema(tool.ProtocolTool.InputSchema), new InputBudget()).Read(args, "arguments");
            Assert.True(validation.IsValid, tool.ProtocolTool.Name + ": " + V4Json.Serialize(validation.Error));
            if (tool.ProtocolTool.Name == "BuildAndImportPlcArtifact")
                Assert
                    .Equal("UNSUPPORTED_CAPABILITY", (string?)Body(await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args.GetRawText())))["error"]
                        ?["code"]);
            else
                if (new[] {
                    "BuildPlcUdt", "BuildPlcGlobalDb", "BuildPlcTagTable", "BuildStructuredText", "BuildFlgNetCall", "BuildPlcFcBlock", "BuildPlcFbBlock",
                        "BuildPlcLadFcBlock"
                }.Contains(tool.ProtocolTool.Name))
                Assert.True((bool)Body(await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args.GetRawText())))["ok"]!, tool.ProtocolTool.Name);
        }
    }

    [Theory]
    [InlineData("outcome-unknown", false, true, "unknown", "unknown")]
    [InlineData("failed", false, false, "failed", "completed")]
    [InlineData("completed", true, false, "succeeded", "completed")]
    public void ExternalSourceEvidenceIsNeverPromoted(string status, bool executed, bool reset, string outcome, string execution)
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "GenerateBlocksFromExternalSource");
        var raw = new JsonObject { ["Status"] = status, ["Executed"] = executed, ["RequiresSessionReset"] = reset,
            ["ResultBasis"] = "inventory-observation-only; native API returns void", ["ObservedChanges"] = new JsonArray("observed"),
            ["Evidence"] = new JsonObject { ["ExactNativeKey"] = "retained" } };
        var result = Body(FoundationV4Result.Worker("14sp1", definition, "test-request", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal(outcome, (string?)result["meta"]!["outcome"]);
        Assert.Equal(execution, (string?)result["meta"]!["execution"]);
        Assert.Equal(reset, (bool)result["meta"]!["requiresSessionReset"]!);
        Assert.Null(result["data"]!["nativeResult"]);
        Assert.Equal("retained", (string?)result["data"]!["evidence"]!["ExactNativeKey"]);
        Assert.Equal("observed", (string?)result["data"]!["observation"]!["observedChanges"]![0]);
        Assert.Equal("current", (string?)result["meta"]!["behaviorPolicy"]);
    }

    [Fact]
    public void BatchRetainsKnownSuccessUnknownFailureAndNotExecutedInOrder()
    {
        var definition = FoundationTools.Definitions.Single(d => d.Name == "ImportPlcBlocksFromDirectory");
        var raw = JsonNode
            .Parse("{\"Executed\":true,\"RequiresSessionReset\":true,\"Items\":[{\"RelativePath\":\"a.xml\",\"Status\":\"imported\",\"Attempted\":true},{\"RelativePath\":\"b.xml\",\"Status\":\"failed\",\"Attempted\":true,\"Failure\":\"native-outcome-uncertain\"},{\"RelativePath\":\"c.xml\",\"Status\":\"not-attempted\"}]}")!;
        var result = Body(FoundationV4Result.Worker("19", definition, "batch", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        Assert
            .Equal(new[] { "succeeded", "unknown", "rejected-before-operation" }, result["data"]!["items"]!.AsArray()
                .Select(x => (string?)x!["result"]!["meta"]!["outcome"]));
        raw["Items"]![1]!["Attempted"] = false; raw["Items"]![1]!["Failure"] = "target-recheck-failed";
        result = Body(FoundationV4Result.Worker("19", definition, "batch", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal("partial", (string?)result["meta"]!["outcome"]);
        Assert.Equal("PARTIAL_FAILURE", (string?)result["error"]!["code"]);
        raw["Items"]!.AsArray().Add(JsonNode.Parse("{\"RelativePath\":\"d.xml\",\"Status\":\"not-attempted\"}"));
        result = Body(FoundationV4Result.Worker("19", definition, "batch", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal(1, (int)result["data"]!["items"]![3]!["result"]!["error"]!["details"]!["causeIndex"]!);
        raw["Items"]!.AsArray().RemoveAt(0);
        result = Body(FoundationV4Result.Worker("19", definition, "batch", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal("rejected-before-operation", (string?)result["meta"]!["outcome"]);
    }

    [Fact]
    public void MissingMutationResultAndNestedCompilerErrorsDoNotSucceed()
    {
        var result = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null));
        Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        var definition = FoundationTools.Definitions.Single(d => d.Name == "CompilePlcSoftware");
        var raw = JsonNode.Parse("{\"Executed\":true,\"ErrorCount\":0,\"State\":\"Success\",\"Errors\":[\"nested error\"]}")!;
        result = Body(FoundationV4Result.Worker("19", definition, "x", new JsonObject { ["dryRun"] = false }, raw, raw));
        Assert.Equal("failed", (string?)result["meta"]!["outcome"]);
        Assert.Equal("nested error", (string?)result["data"]!["errors"]![0]);
    }

    [Fact]
    public void WorkerFailuresRetainEvidenceAndDistinguishUnsentFromUnknown()
    {
        var error = new WorkerOperationException("private worker detail", -32603, "unknown", "{\"ExactNativeKey\":{\"nested\":[1,null]}}");
        var result = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null, error));
        Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        Assert.Equal("[1,null]", result["data"]!["evidence"]!["ExactNativeKey"]!["nested"]!.ToJsonString());
        Assert.Null(result["error"]!["details"]!["evidence"]!["workerMessage"]);
        Assert.DoesNotContain("private worker detail", result.ToJsonString());
        var read = Body(FoundationV4Result.Failure("19", "ListPlcBlocks", "x", true, false, null,
            new WorkerOperationException("Project tree is incomplete.", -32603, "read-failed")));
        Assert.Null(read["error"]!["details"]!["nativeMessage"]);
        var rejected = Body(FoundationV4Result.Failure("19", "ExportPlcBlocks", "x", true, true, null,
            new WorkerOperationException("Export directory must already exist.", -32602, "rejected-before-operation",
                    "{\"parameter\":\"exportPath\",\"isArgument\":true,\"exceptionType\":\"AdapterPreconditionException\"}")));
        Assert.Equal("rejected-before-operation", (string?)rejected["meta"]!["outcome"]);
        Assert.Equal("Export directory must already exist.", (string?)rejected["error"]!["message"]);
        Assert.Equal("/exportPath", (string?)rejected["error"]!["details"]!["parameter"]);
        Assert.Equal("not-started", (string?)rejected["meta"]!["execution"]);
        string privatePath = @"C:\Users\Private\project.ap19";
        string longMessage = "Failed at " + privatePath + "; token=private-secret " + new string('x', 6000);
        var unknown = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null,
            new WorkerOperationException(longMessage, -32603, "unknown")));
        Assert.Null(unknown["error"]!["details"]!["evidence"]!["workerMessage"]);
        string safeMessage = unknown.ToJsonString();
        Assert.DoesNotContain(privatePath, safeMessage);
        Assert.DoesNotContain("private-secret", safeMessage);
        error.Data["foundationRequestSent"] = false;
        result = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null, error));
        Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
        error.Data["foundationSessionPoisoned"] = true;
        result = Body(FoundationV4Result.Failure("19", "ImportPlcBlock", "x", true, true, null, error));
        Assert.Equal("rejected-before-operation", (string?)result["meta"]!["outcome"]);
        Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
        result = Body(FoundationV4Result.HostFailure("19", "GetToolUsage", "x"));
        Assert.Equal("INTERNAL_ERROR", (string?)result["error"]!["code"]);
        Assert.Equal("not-applicable", (string?)result["meta"]!["behaviorPolicy"]);
    }

    [Fact]
    public async Task UsagePagingAndFoundationSequenceUseV4Contracts()
    {
        var guide = Tool("GetToolUsage");
        var result = Body(await guide.InvokeAsync(Request("GetToolUsage", "{\"offset\":0,\"limit\":1}")));
        Assert.Equal(1, (int)result["meta"]!["paging"]!["nextOffset"]!);
        Assert.Null(result["data"]!["nextOffset"]);
        result = Body(await guide.InvokeAsync(Request("GetToolUsage", "{\"exampleKind\":\"sequence\"}")));
        Assert.Contains(result["data"]!["examples"]!.AsArray(), e => (string?)e!["id"] == "sequence/plc-scl-block-foundation" && (bool)e["available"]!);
        result = Body(await guide.InvokeAsync(Request("GetToolUsage", "{\"exampleId\":\"sequence/plc-scl-block-foundation\"}")));
        var steps = result["data"]!["examples"]![0]!["steps"]!.AsArray();
        Assert.Equal("GetSessionState", (string?)steps[0]!["tool"]);
        Assert.Contains(steps, s => (string?)s!["tool"] == "CompilePlcDiagnostics");
        Assert.DoesNotContain(steps, s => LegacyNames.Contains((string)s!["tool"]!));
    }
}
