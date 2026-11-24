extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.FoundationHost;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

// The fixture mutates process-wide release and approval state; run it alone.
[Collection(BundleSelectionCollection.Name)]
public sealed class UnifiedPortAdmissionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17")]
    [InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void Served_ported_operations_pass_offline_admission(string release)
    {
        var catalog = new TiaMcp.Engine.Tests.Shared.ServedToolOperations(release);
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var errors = new List<string>();
        var names = new List<string>();
        foreach (var tool in PortedToolContract.Create(worker, release).OfType<FoundationTool>())
        {
            string name = tool.ProtocolTool.Name;
            if (TiaOpenness.Shared.ToolUsageCatalog.ServedCall(name, release)?["operations"] is not JsonObject { Count: > 0 }) continue;
            names.Add(name);
            foreach (var (operation, usage) in catalog.For(name))
            {
                var example = usage["example"]!;
                var args = example["request"]!["params"]!["arguments"]!.Deserialize<Dictionary<string, JsonElement>>()!;
                try
                {
                    var refusal = tool.RefusePortedCapability(args);
                    if (example["releaseProblem"] == null)
                        Assert.True(refusal == null, refusal?.StructuredContent?.ToString());
                    if (PortedFamilies.ForTool(name).Name is "F27" or "F28")
                    {
                        var request = FoundationTool.BindUnifiedRequest(name, Method(name), args);
                        toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.DecodeInputs(request);
                        toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateInputs(request);
                    }
                }
                catch (Exception error) { errors.Add($"{release}/{name}/{operation}: {error.Message}"); }
            }
        }
        Assert.NotEmpty(names);
        output.WriteLine($"V{release} ported operation policies: {string.Join(", ", names.Order(StringComparer.Ordinal))}");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public async Task Runtime_settings_name_each_unknown_key_without_echoing_change_values()
    {
        const string toolName = "SetUnifiedRuntimeSettings";
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        var args = Arguments(toolName);
        args["changes"] = new JsonObject { ["Bad/Field"] = "private-one", ["StartScreen"] = "private-two", ["Bad~Name"] = "private-three" };
        var port = PortedToolContract.Create(worker, "21").OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == toolName);
        var capability = port.RefusePortedCapability(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString())!);
        var registered = LegacyHostToolRegistry.Create(worker, "21", false).Single(t => t.ProtocolTool.Name == toolName);
        var dispatched = await registered.InvokeAsync(Request(toolName, args));
        foreach (var result in new[] { capability!, dispatched })
        {
            var error = result.StructuredContent!["error"]!;
            Assert.Equal("Unknown runtime setting name.", (string?)error["message"]);
            Assert.Equal("/changes", (string?)error["details"]?["parameter"]);
            Assert.Equal(new[] { "/changes", "/changes/Bad~1Field", "/changes/Bad~0Name" },
                error["details"]!["parameters"]!.AsArray().Select(node => (string?)node));
            Assert.DoesNotContain("private-", result.StructuredContent.ToJsonString());
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("14sp1", false)]
    [InlineData("15.1", false)]
    [InlineData("16", false)]
    [InlineData("17", false)]
    [InlineData("18", false)]
    [InlineData("19", true)]
    [InlineData("20", true)]
    [InlineData("21", true)]
    public async Task Runtime_settings_sequence_matches_the_served_schema_on_all_eight_releases(string release, bool available)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var usage = tools.Single(t => t.ProtocolTool.Name == "GetToolUsage");
        var result = await usage.InvokeAsync(Request("GetToolUsage", new JsonObject {
            ["exampleId"] = "sequence/unified-runtime-settings", ["exampleKind"] = "all" }));
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent!.ToJsonString());
        var row = Assert.Single(result.StructuredContent!["data"]!["examples"]!.AsArray())!;
        Assert.Equal(available, (bool?)row["releaseMatches"]);
        Assert.Equal(available, (bool?)row["available"]);
        Assert.Equal("VM NOT RUN", (string?)row["validation"]);
        if (available)
        {
            Assert.True((bool?)row["profileMatches"]);
            var write = row["steps"]!.AsArray().Single(step => (string?)step!["tool"] == "SetUnifiedRuntimeSettings"
                && (bool?)step["arguments"]?["dryRun"] == false)!;
            Assert.NotNull(write["arguments"]?["previewToken"]);
            Assert.NotNull(write["arguments"]?["expectedPlanHash"]);
            Assert.NotNull(write["arguments"]?["expectedProjectFile"]);
        }
        else
            Assert.DoesNotContain(tools, tool => tool.ProtocolTool.Name is "GetUnifiedRuntimeSettings" or "SetUnifiedRuntimeSettings");
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19", "TelemetryRuntimeSettings.TelemetryActive")]
    [InlineData("20", "HmiUnifiedTagSettings.TagOptimizationActive")]
    public async Task Nested_runtime_fields_unavailable_on_the_release_refuse_before_worker_dispatch(string release, string field)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        const string name = "SetUnifiedRuntimeSettings";
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name); arguments["changes"] = new JsonObject { [field] = true };
        var result = await tool.InvokeAsync(Request(name, arguments));
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Contains("HmiRuntimeSetting." + field, (string)result.StructuredContent!["error"]!["message"]!);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("TelemetryRuntimeSettings.TelemetryActive")]
    [InlineData("HmiUnifiedTagSettings.TagOptimizationActive")]
    public void V21_admits_nested_runtime_fields(string field)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        const string name = "SetUnifiedRuntimeSettings";
        var tool = PortedToolContract.Create(worker, "21").OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name); arguments["changes"] = new JsonObject { [field] = true };
        Assert.Null(tool.RefusePortedCapability(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString())!));
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Generic_writes_refuse_the_whole_runtime_settings_subtree_before_dispatch(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        foreach (string name in new[] { "SetUnifiedObjectProperties", "SetUnifiedMultilingualProperty" })
        foreach (int length in new[] { 1, 2 })
        {
            var arguments = Arguments(name);
            arguments["objectPath"] = new JsonArray(new[] { new JsonObject { ["property"] = "RuntimeSettings" },
                new JsonObject { ["property"] = "OpcUaServerRuntimeSettings" } }.Take(length).Select(x => (JsonNode)x).ToArray());
            var result = await tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(Request(name, arguments));
            Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Contains("SetUnifiedRuntimeSettings", (string)result.StructuredContent!["error"]!["message"]!);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        }
        const string readName = "GetUnifiedObjectProperties";
        var read = PortedToolContract.Create(worker, release).OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == readName);
        var readArguments = Arguments(readName);
        readArguments["objectPath"] = new JsonArray(new JsonObject { ["property"] = "RuntimeSettings" },
            new JsonObject { ["property"] = "OpcUaServerRuntimeSettings" });
        Assert.Null(read.RefusePortedCapability(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(readArguments.ToJsonString())!));
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Runtime_settings_keep_the_twelve_field_limit_before_worker_dispatch(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        string[] fields = UnifiedRuntimeSettingsFields.Nested.Take(13).ToArray();
        foreach (string name in new[] { "GetUnifiedRuntimeSettings", "SetUnifiedRuntimeSettings" })
        {
            var arguments = Arguments(name);
            arguments[name.StartsWith("Get", StringComparison.Ordinal) ? "fields" : "changes"] = name.StartsWith("Get", StringComparison.Ordinal)
                ? JsonSerializer.SerializeToNode(fields) : JsonSerializer.SerializeToNode(fields.ToDictionary(f => f, _ => true, StringComparer.Ordinal));
            var result = await tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(Request(name, arguments));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("Too many runtime settings; select 1..12 fields.", (string?)result.StructuredContent?["error"]?["message"]);
            Assert.Equal(name.StartsWith("Get", StringComparison.Ordinal) ? "/fields" : "/changes",
                (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Unknown_and_duplicate_runtime_fields_are_invalid_arguments_with_parameter_pointers(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        foreach (string name in new[] { "GetUnifiedRuntimeSettings", "SetUnifiedRuntimeSettings" })
        {
            string parameter = name.StartsWith("Get", StringComparison.Ordinal) ? "fields" : "changes";
            var arguments = Arguments(name);
            arguments[parameter] = parameter == "fields" ? new JsonArray("MissingSetting") : new JsonObject { ["MissingSetting"] = true };
            var result = await tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(Request(name, arguments));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("/" + parameter, (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
            Assert.Equal("Unknown runtime setting name.", (string?)result.StructuredContent?["error"]?["message"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        }
        var duplicate = Arguments("GetUnifiedRuntimeSettings");
        duplicate["fields"] = new JsonArray("StartScreen", "StartScreen");
        var duplicateResult = await tools.Single(t => t.ProtocolTool.Name == "GetUnifiedRuntimeSettings")
            .InvokeAsync(Request("GetUnifiedRuntimeSettings", duplicate));
        Assert.Equal("INVALID_ARGUMENT", (string?)duplicateResult.StructuredContent?["error"]?["code"]);
        Assert.Equal("/fields", (string?)duplicateResult.StructuredContent?["error"]?["details"]?["parameter"]);
        Assert.Equal("Duplicate runtime setting name.", (string?)duplicateResult.StructuredContent?["error"]?["message"]);
        Assert.Equal("not-started", (string?)duplicateResult.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Runtime_execution_requires_plan_fields_before_readiness_or_dispatch(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        const string name = "SetUnifiedRuntimeSettings";
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        foreach (string missing in new[] { "expectedPlanHash", "expectedProjectFile" })
        {
            var arguments = Arguments(name); arguments["dryRun"] = false;
            arguments["expectedPlanHash"] = "hash"; arguments["expectedProjectFile"] = "C:/Fixture.ap21"; arguments.Remove(missing);
            var result = await tool.InvokeAsync(Request(name, arguments));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("14sp1", false)]
    [InlineData("15.1", false)]
    [InlineData("16", false)]
    [InlineData("17", false)]
    [InlineData("18", false)]
    [InlineData("19", true)]
    [InlineData("20", true)]
    [InlineData("21", true)]
    public void Script_create_sequence_has_exact_release_scope_and_valid_served_schemas(string release, bool available)
    {
        using var worker = new ApprovalWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false).ToDictionary(t => t.ProtocolTool.Name);
        var usage = TiaOpenness.Shared.ToolUsageCatalog.Examples(release, "plc-foundation", tools.Keys,
            exampleId: "sequence/unified-script-module-create", schemaFor: name => tools.TryGetValue(name, out var tool)
                ? JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject() : null);
        var sequence = Assert.Single(usage["examples"]!.AsArray())!;
        Assert.Equal(available, (bool?)sequence["releaseMatches"]);
        Assert.Equal(available, (bool?)sequence["available"]);
        Assert.Equal("VM NOT RUN", (string?)sequence["validation"]);
        if (available)
        {
            Assert.True((bool?)sequence["profileMatches"]);
            Assert.Equal(new[] { "update", "create" }, tools["SetUnifiedGlobalScript"].ProtocolTool.InputSchema
                .GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
            var steps = sequence["steps"]!.AsArray().ToArray();
            Assert.Equal(6, steps.Length);
            Assert.Equal("ListUnifiedGlobalScripts", (string?)steps[0]?["tool"]);
            Assert.Equal("create", (string?)steps[1]?["arguments"]?["action"]);
            Assert.True((bool?)steps[1]?["arguments"]?["dryRun"]);
            // After manual creation the four steps read, preview the update, apply it and read back.
            steps = steps[2..];
            Assert.Equal("GetUnifiedGlobalScript", (string?)steps[0]?["tool"]);
            Assert.Equal("update", (string?)steps[1]?["arguments"]?["action"]);
            Assert.True((bool?)steps[1]?["arguments"]?["dryRun"]);
            Assert.Equal("update", (string?)steps[2]?["arguments"]?["action"]);
            Assert.False((bool?)steps[2]?["arguments"]?["dryRun"]);
            Assert.Equal("GetUnifiedGlobalScript", (string?)steps[^1]?["tool"]);
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Script_create_is_admitted_and_preserves_action(string release)
    {
        using var worker = new ApprovalWorker(bound: true);
        using var releaseState = new ReleaseState(release);
        using var lifetime = new Host.ImportStagingHostLifetime();
        var catalog = new ApprovalCatalog(release);
        using var session = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog,
                Invoker = new toolpipeline::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, worker, lifetime) });
        const string name = "SetUnifiedGlobalScript";
        var method = Method(name);
        var arguments = Arguments(name); arguments["action"] = "create";
        var mapped = FoundationTool.BindUnifiedRequest(name, method,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString())!);
        Assert.Equal("create", mapped.Action);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var result = await tool.InvokeAsync(Request(name, arguments));
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent!.ToJsonString());
        Assert.NotEmpty((string)result.StructuredContent!["data"]!["planHash"]!);
        Assert.True(worker.Calls > 0);
        int callsAfterPreview = worker.Calls;
        arguments["dryRun"] = false;
        arguments["expectedPlanHash"] = result.StructuredContent["data"]!["planHash"]!.DeepClone();
        arguments["expectedProjectFile"] = "C:/fixture.ap21";
        arguments["previewToken"] = "preview-token";
        var refusal = (await tool.InvokeAsync(Request(name, arguments))).StructuredContent!;
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)refusal["error"]!["code"]);
        Assert.Contains("Create the module in TIA", (string?)refusal["error"]!["message"]);
        Assert.Contains("action=update", (string?)refusal["error"]!["message"]);
        Assert.Equal("rejected-before-operation", (string?)refusal["meta"]!["outcome"]);
        Assert.Equal("not-started", (string?)refusal["meta"]!["execution"]);
        Assert.Equal(callsAfterPreview, worker.Calls);
        Assert.Equal(0, worker.Applies);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    public void Script_create_is_not_registered_before_V19(string release)
    {
        using var worker = new ApprovalWorker();
        Assert.DoesNotContain(LegacyHostToolRegistry.Create(worker, release, false), t => t.ProtocolTool.Name == "SetUnifiedGlobalScript");
        Assert.False(PortedFamilies.Available(release, "SetUnifiedGlobalScript"));
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19", "delete", true)]
    [InlineData("20", "delete", true)]
    [InlineData("21", "delete", true)]
    [InlineData("19", "create", false)]
    [InlineData("20", "update", false)]
    [InlineData("21", "create", false)]
    public async Task Script_invalid_action_or_missing_plan_refuses_before_worker_dispatch(string release, string action, bool preview)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        const string name = "SetUnifiedGlobalScript";
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name); arguments["action"] = action; arguments["dryRun"] = preview;
        var result = await tool.InvokeAsync(Request(name, arguments));
        if (action == "create")
        {
            Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.Equal(0, worker.Calls);
            arguments["action"] = "update";
            result = await tool.InvokeAsync(Request(name, arguments));
        }
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("connections")]
    [InlineData("tagTables")]
    [InlineData("discreteAlarms")]
    public void Unified_extended_request_mapping_preserves_texts_and_plan_fields(string category)
    {
        var arguments = new JsonObject { ["softwarePath"] = "HMI_RT_1", ["category"] = category, ["name"] = "Alarm",
            ["action"] = "delete", ["texts"] = new JsonObject { ["EventText"] = new JsonObject { ["en-US"] = "Alarm", ["zh-CN"] = "报警" } },
            ["expectedPlanHash"] = "hash", ["expectedProjectFile"] = "C:/Fixture.ap21" };
        var request = FoundationTool.BindUnifiedRequest("ManageUnifiedEngineeringObject", Method("ManageUnifiedEngineeringObject"),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString())!);
        Assert.Equal(category, request.Category);
        Assert.Equal(arguments["texts"]!.ToJsonString(), request.TextsJson);
        Assert.Equal("hash", request.ExpectedPlanHash);
        Assert.Equal("C:/Fixture.ap21", request.ExpectedProjectFile);
        Assert.DoesNotContain(Method("ManageUnifiedEngineeringObject").GetParameters(), p => p.Name is "items" or "parameterTags");
    }

    [Theory]
    [InlineData("create", "", "")]
    [InlineData("update", "", "C:/Fixture.ap21")]
    [InlineData("delete", "hash", "")]
    public void Unified_execution_requires_both_plan_fields_before_service_dispatch(string action, string hash, string project)
    {
        using var release = new ReleaseState("21");
        using var worker = new ApprovalWorker(bound: true);
        using var context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = new ApprovalCatalog("21") });
        int dispatches = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.UnifiedEngineeringService(
            (_, _) => { dispatches++; throw new InvalidOperationException("No dispatch expected."); }, () => "C:/Fixture.ap21");
        var result = new Host.UnifiedEngineeringTools(service).ManageUnifiedEngineeringObjectV4("HMI_RT_1", "alarmClasses", "Alarm", action,
            dryRun: false, expectedPlanHash: hash, expectedProjectFile: project);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, dispatches);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Served_Unified_execution_without_plan_fields_refuses_before_readiness_or_preview_dispatch(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        const string name = "ManageUnifiedEngineeringObject";
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        foreach (var action in new[] { "create", "update", "delete" })
        foreach (var missing in new[] { "expectedPlanHash", "expectedProjectFile" })
        {
            var arguments = Arguments(name); arguments["action"] = action; arguments["dryRun"] = false;
            arguments["expectedPlanHash"] = "hash"; arguments["expectedProjectFile"] = "C:/Fixture.ap21"; arguments.Remove(missing);
            var result = await tool.InvokeAsync(Request(name, arguments));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        }
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public void Unified_native_preview_binds_texts_and_scalars_and_execution_uses_the_same_plan()
    {
        using var release = new ReleaseState("21");
        using var worker = new ApprovalWorker(bound: true);
        using var context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = new ApprovalCatalog("21") });
        var received = new List<UnifiedRequest>();
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.UnifiedEngineeringService((_, args) => {
            received.Add(args["request"]!.Deserialize<UnifiedRequest>()!);
            return new JsonObject { ["Value"] = new JsonObject { ["Message"] = "native preview", ["Meta"] = new JsonObject {
                ["success"] = true, ["operationSuccess"] = true, ["mayHaveChanged"] = !(bool)args["dryRun"]!, ["tagCount"] = 2 } } };
        }, () => "C:/Fixture.ap21");
        var tool = new Host.UnifiedEngineeringTools(service);
        var texts = TiaMcp.Logic.V4.V4Json.Deserialize<TiaMcp.Logic.V4.Inputs.AttributeMap<TiaMcp.Logic.V4.Inputs.AttributeMap<string>>>(
            "{\"EventText\":{\"en-US\":\"Alarm\",\"zh-CN\":\"报警\"}}");
        var preview = tool.ManageUnifiedEngineeringObjectV4("HMI_RT_1", "discreteAlarms", "Alarm", "create", texts: texts);
        Assert.True((bool)preview.StructuredContent!["ok"]!, preview.StructuredContent.ToJsonString());
        var hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        Assert.NotEmpty(hash);
        Assert.Equal(2, (int)preview.StructuredContent["data"]!["tagCount"]!);
        var request = Assert.Single(received);
        Assert.True(request.DryRun);
        Assert.Equal("报警", request.Inputs[request.TextsJson].Properties["EventText"].Properties["zh-CN"].Text);
        var stale = tool.ManageUnifiedEngineeringObjectV4("HMI_RT_1", "discreteAlarms", "Alarm", "create", dryRun: false,
            expectedPlanHash: hash, expectedProjectFile: "C:/Fixture.ap21");
        Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent?["error"]?["code"]);
        Assert.Single(received);
        var applied = tool.ManageUnifiedEngineeringObjectV4("HMI_RT_1", "discreteAlarms", "Alarm", "create", dryRun: false, texts: texts,
            expectedPlanHash: hash, expectedProjectFile: "C:/Fixture.ap21");
        Assert.True((bool)applied.StructuredContent!["ok"]!);
        Assert.Equal(2, received.Count);
        Assert.False(received[1].DryRun);
    }

    [Theory, InlineData(5, true), InlineData(6, false), InlineData(int.MaxValue, false)]
    public void Api_type_list_reports_truncation(int limit, bool truncated)
    {
        int requested = 0;
        using var release = Host.HardwareContract.UseRelease("21");
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.UnifiedHmiService((_, args) => {
            requested = (int)args["request"]!["Limit"]!;
            return new JsonObject { ["Value"] = new JsonArray(Enumerable.Range(0, Math.Min(requested, 6))
                .Select(n => (JsonNode)JsonValue.Create("class: Siemens.Engineering.HmiUnified.Type" + n)!).ToArray()) };
        }, () => "fixture");
        var result = new Host.UnifiedHmiTools(service).ListUnifiedHmiApiTypesV4("", limit).StructuredContent!;
        Assert.Equal(limit == int.MaxValue ? limit : limit + 1, requested);
        Assert.Equal(truncated, (bool?)result["data"]?["evidence"]?["truncated"]);
        Assert.Equal(truncated ? "partial" : "complete", (string?)result["meta"]?["completeness"]);
        Assert.Equal(Math.Min(limit, 6), result["data"]!["items"]!.AsArray().Count);
    }

    private static void AssertArgumentRefusal(Action action)
    {
        var refusal = Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(action);
        Assert.True(refusal.IsArgument);
        Assert.IsType<ArgumentException>(refusal.InnerException);
    }

    [Theory]
    [InlineData("GetUnifiedScreenBranch")]
    [InlineData("GetUnifiedGlobalScript")]
    [InlineData("GetUnifiedGraphicSelection")]
    [InlineData("GetUnifiedLibraryType")]
    [InlineData("ListUnifiedTagDefinitions")]
    [InlineData("ListUnifiedGlobalScripts")]
    [InlineData("ListUnifiedLibraryFolderEntries")]
    [InlineData("GetUnifiedFaceplateInstance")]
    public async Task Catalogue_reads_use_only_host_decoded_inputs(string name)
    {
        using var release = new ReleaseState("21");
        using var worker = new DecodedCatalogueReader();
        using var sessionWorker = new ApprovalWorker();
        using var lifetime = new Host.ImportStagingHostLifetime();
        var catalog = new ApprovalCatalog("21");
        using var context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(sessionWorker) { Catalog = catalog,
                Invoker = new toolpipeline::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, sessionWorker, lifetime) });
        var tool = new FoundationV4Tool(new FoundationTool(worker, "21", Method(name), name), "21");
        var arguments = Arguments(name);
        arguments["softwarePath"] = "HMI";
        arguments["expectedProject"] = "Project_A";
        if (arguments.ContainsKey("screenPath")) arguments["screenPath"] = "/Main";
        if (arguments.ContainsKey("moduleName")) arguments["moduleName"] = "Navigation";
        if (arguments.ContainsKey("cursor")) arguments["cursor"] = "";
        if (name == "GetUnifiedFaceplateInstance") arguments["typePath"] = "/FixtureType";
        if (name == "GetUnifiedScreenBranch") arguments["branch"] = new JsonArray(new JsonObject { ["property"] = "ScreenItems" });
        var raw = arguments.DeepClone().AsObject();
        raw["undecodedInput"] = DecodedCatalogueReader.Undecoded;
        var rejected = await tool.InvokeAsync(Request(name, raw));
        Assert.Equal("INVALID_ARGUMENT", (string?)rejected.StructuredContent?["error"]?["code"]);
        Assert.Empty(worker.Requests);

        var result = await tool.InvokeAsync(Request(name, arguments));
        var body = result.StructuredContent!.AsObject();
        Assert.DoesNotContain("Structured input was not decoded", body.ToJsonString(), StringComparison.Ordinal);
        Assert.True((bool?)body["ok"] == true, body.ToJsonString());
        Assert.NotNull(name == "GetUnifiedGraphicSelection" ? body["data"]?["page"]?["scope"] : body["data"]?["scope"]);
        Assert.NotEqual("unknown", (string?)body["meta"]!["outcome"]);
        var received = Assert.Single(worker.Requests);
        Assert.Equal(name, received.Operation);
        Assert.Equal("HMI", received.SoftwarePath);
        Assert.Equal("Project_A", received.ExpectedProject);
        Assert.DoesNotContain(DecodedCatalogueReader.Undecoded, JsonSerializer.Serialize(received), StringComparison.Ordinal);
        foreach (var property in typeof(UnifiedRequest).GetProperties().Where(p => p.Name.EndsWith("Json", StringComparison.Ordinal)))
        {
            var text = (string)property.GetValue(received)!;
            Assert.True(received.Inputs.ContainsKey(text), property.Name);
            if (text.Length == 0) Assert.NotNull(received.Inputs[text].Error);
            else Assert.Null(received.Inputs[text].Error);
        }
        if (name == "GetUnifiedScreenBranch")
        {
            var step = Assert.Single(received.Inputs[received.BranchJson].Items);
            Assert.Equal("ScreenItems", step.Properties["property"].Text);
        }
        if (name == "GetUnifiedGraphicSelection")
            Assert.Equal(arguments["itemNames"]!.AsArray().Select(item => (string)item!),
                received.Inputs[received.ItemNamesJson].Items.Select(item => item.Text));
    }

    // Keep the real host decoder and native cursor store on either side of this
    // worker boundary. The synthetic records require no Siemens object or engine fixture.
    private sealed class DecodedCatalogueReader : IFoundationWorker
    {
        internal const string Undecoded = "[{\"property\":\"NeverDecodedByHost\"}]";
        internal List<UnifiedRequest> Requests { get; } = new();
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState")
                return Task.FromResult(JsonSerializer.SerializeToNode(new { ProjectFile = "C:/Fixture.ap21", IsAttached = true }));
            var request = arguments["request"]!.Deserialize<UnifiedRequest>()!;
            Requests.Add(request);
            Assert.Equal(PortedFamilies.ForTool(request.Operation).OperationPrefix + ".ExecuteUnified", operation);
            var previousInputs = UnifiedRequestValues.Inputs;
            var previousDecoder = UnifiedRequestValues.Decoder;
            var previousRenderer = HmiValues.Renderer;
            try
            {
                UnifiedRequestValues.Decoder = null;
                UnifiedRequestValues.Inputs = request.Inputs.ToDictionary(p => p.Key, p => UnifiedRequestValues.Decode(p.Value));
                HmiValues.Renderer = value => JsonSerializer.Serialize(value);
                Assert.Throws<ArgumentException>(() => UnifiedRequestValues.Parse(Undecoded));
                var scope = new HmiObject { ["tool"] = request.Operation, ["softwarePath"] = request.SoftwarePath,
                    ["expectedProject"] = request.ExpectedProject };
                if (request.Operation == "GetUnifiedScreenBranch")
                    scope["branch"] = UnifiedRequestValues.Parse(request.BranchJson);
                if (request.Operation == "GetUnifiedGraphicSelection")
                    scope["itemNames"] = UnifiedRequestValues.Parse(request.ItemNamesJson);
                using var pages = new MigrationPages();
                var page = pages.Read(new object(), scope, request.Cursor, request.PageSize, request.BudgetMs,
                    () => new[] { new HmiObject { ["path"] = "/fixture", ["kind"] = "value", ["status"] = "ok" } });
                page["success"] = page["apiCallSuccess"];
                page["operationSuccess"] = page["apiCallSuccess"];
                return Task.FromResult(JsonSerializer.SerializeToNode(new HmiReply {
                    Value = new HmiMessage { Message = "Read-only fixture collection.", Meta = page } }));
            }
            finally
            {
                UnifiedRequestValues.Inputs = previousInputs;
                UnifiedRequestValues.Decoder = previousDecoder;
                HmiValues.Renderer = previousRenderer;
            }
        }
        public void Dispose() { }
    }

    [Fact]
    public void Refusal_examples_use_Unified_roots_and_keep_optional_domains_separate()
    {
        var path = JsonNode.Parse(EngineeringInputValidation.ObjectPathExample)!.AsArray();
        Assert.Equal("Screens", (string?)path[0]!["property"]);
        Assert.Equal("Overview", (string?)path[0]!["name"]);
        Assert.Equal("ScreenItems", (string?)path[1]!["property"]);
        Assert.Equal("StartButton", (string?)path[1]!["name"]);
        toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress(EngineeringInputValidation.ObjectPathExample);
        var unified = Assert.Throws<ArgumentException>(() => toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress("{}"));
        Assert.Contains(EngineeringInputValidation.ObjectPathExample, unified.Message);
        Assert.DoesNotContain("ScreenFolder", unified.Message);
        Assert.DoesNotContain("TagFolder", unified.Message);
        toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress(EngineeringInputValidation.OptionalObjectPathExample, optional: true);
        var optional = Assert.Throws<ArgumentException>(() => toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress("{}", optional: true));
        Assert.Contains("[] (the typed DCC/SiVArc root)", optional.Message);
        Assert.DoesNotContain("Screens", optional.Message);
    }

    [Fact]
    public void Malformed_branch_JSON_uses_the_branch_grammar_in_its_refusal_example()
    {
        toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress(EngineeringInputValidation.BranchPathExample, branch: true);
        var error = Assert.Throws<ArgumentException>(() => toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress("{", branch: true));
        Assert.Contains(EngineeringInputValidation.BranchPathExample, error.Message);
        Assert.DoesNotContain(EngineeringInputValidation.ObjectPathExample, error.Message);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Backslash_HMI_paths_are_rejected_before_any_worker_call(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        foreach (var name in new[] { "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag", "EnsureUnifiedHmiScreen" })
        foreach (var path in new[] { "Group\\Sub\\Name", "/Group\\Sub/Name" })
        {
            var arguments = Arguments(name);
            arguments[name == "EnsureUnifiedHmiScreen" ? "screenName" : "tagTableName"] = path;
            var result = await tools.Single(t => t.ProtocolTool.Name == name).InvokeAsync(Request(name, arguments));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.Contains("Corrected slash form: /Group/Sub/Name", (string)result.StructuredContent!["error"]!["message"]!);
        }
        Assert.Equal(0, worker.Calls);
    }

    private static readonly Type[] AddressServices = { typeof(toolpipeline::TiaMcpServer.Siemens.Services.UnifiedUiModelService), typeof(toolpipeline::TiaMcpServer.Siemens.Services.UnifiedObjectServicesService),
        typeof(toolpipeline::TiaMcpServer.Siemens.Services.UnifiedEventsService), typeof(toolpipeline::TiaMcpServer.Siemens.Services.MigrationReadService), typeof(toolpipeline::TiaMcpServer.Siemens.Services.DccService), typeof(toolpipeline::TiaMcpServer.Siemens.Services.OptionalEngineeringService) };
    private static readonly string[] AddressParameters = { "objectPathJson", "branchJson", "collectionPathJson", "tagPathJson", "durationPathJson" };
    public static IEnumerable<object[]> ServiceAddressCases => from type in AddressServices
        from method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        from parameter in method.GetParameters().Where(p => AddressParameters.Contains(p.Name))
        from raw in new[] { "[\"Screens\",\"Main\"]", "{}", "[{\"property\":\"Screens\",\"unknown\":true}]", "[{\"property\":\"Parent\"}]" }
        select new object[] { type, method.Name, parameter.Name!, raw };

    [Theory, MemberData(nameof(ServiceAddressCases))]
    public void Every_service_rejects_malformed_raw_addresses_before_worker_or_identity_lookup(Type type, string methodName, string parameter, string raw)
    {
        int calls = 0;
        var service = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { (Func<string, JsonObject, JsonNode?>)((_, _) => { calls++; throw new InvalidOperationException(); }),
                (Func<string>)(() => { calls++; throw new InvalidOperationException(); }) }, null)!;
        var method = type.GetMethod(methodName)!;
        var arguments = method.GetParameters().Select(p => p.Name == parameter ? raw
            : AddressParameters.Contains(p.Name) ? (object)"[]"
            : p.Name == "propertiesJson" || p.Name == "scriptPropertiesJson" ? "{}"
            : p.Name == "screenPath" ? "/Overview" : p.Name == "typePath" ? "/Faceplates/MotorFaceplate"
            : p.Name == "folderPath" ? "/"
            : p.Name == "action" ? "delete" : p.HasDefaultValue ? p.DefaultValue
            : p.ParameterType == typeof(string) ? "fixture" : Activator.CreateInstance(p.ParameterType)).ToArray();
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(service, arguments));
        var invalid = Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Contains("Expected an array", invalid.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"Width\":1,\"Width\":2}")]
    public void Properties_are_validated_before_dispatch_even_for_delete_preview(string properties)
    {
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.UnifiedUiModelService((_, _) => { calls++; throw new InvalidOperationException(); }, () => "fixture.ap21");
        Assert.Throws<ArgumentException>(() => service.ManageUnifiedObjectParts("HMI", "[]", "delete", propertiesJson: properties));
        Assert.Throws<ArgumentException>(() => service.ManageUnifiedDynamization("HMI", "[]", "Visible", "delete", propertiesJson: properties));
        Assert.Throws<ArgumentException>(() => service.ManageUnifiedScreenLayout("HMI", "[]", "delete", propertiesJson: properties));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("[{\"property\":\"Screens\",\"name\":\"Overview\"}]", false)]
    [InlineData("[{\"property\":\"ScreenItems\"},{\"index\":0},{\"attribute\":\"Display name\"}]", true)]
    [InlineData("[{\"name\":\"Overview\",\"key\":\"Name\"}]", true)]
    public void Address_preflight_preserves_the_existing_object_and_branch_grammars(string raw, bool branch)
        => toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService.ValidateAddress(raw, branch);


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_ensure_tools_dispatch_an_absolute_table_path_to_the_nested_collection(bool ensureTag)
    {
        var nested = new TagFolder();
        var root = new TagFolder { TagTableGroups = new[] { new TagFolder { Name = "Hmi Tags", Groups = new[] { nested } } } };
        nested.Name = "Diagnostic";
        int writes = 0, calls = 0;
        JsonNode? Call(string operation, JsonObject arguments)
        {
            calls++;
            var request = arguments["request"]!.Deserialize<UnifiedHmiRequest>()!;
            Assert.Equal(ensureTag ? "EnsureUnifiedHmiTag" : "EnsureUnifiedHmiTagTable", request.Operation);
            var table = HmiExchangePolicy.EnsureTagTable(root, request.TagTableName, () => writes++);
            if (ensureTag)
            {
                Assert.Equal("PLC_Source", request.PlcName);
                Assert.Equal("PlcReady", request.PlcTag);
                HmiExchangePolicy.EnsureTag(root, table, request.TagName, () => writes++);
            }
            return new JsonObject { ["Value"] = new JsonObject { ["Message"] = "fixture" } };
        }
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.UnifiedHmiService(Call, () => "fixture.ap21");
        for (int i = 0; i < 2; i++)
        {
            if (ensureTag) service.EnsureUnifiedHmiTag("HMI", "/Hmi%20Tags/Diagnostic/Diag%20Info", "Ready", plcName: "PLC_Source", plcTag: "PlcReady", dryRun: false);
            else service.EnsureUnifiedHmiTagTable("HMI", "/Hmi%20Tags/Diagnostic/Diag%20Info", dryRun: false);
        }
        Assert.Equal(2, calls);
        Assert.Equal(ensureTag ? 2 : 1, writes);
        Assert.Equal(new[] { "Diag Info" }, nested.TagTables.Calls);
        Assert.Empty(root.TagTables.Calls);
        Assert.Same(nested.TagTables.Single(), HmiExchangePolicy.TagTable(root, "/Hmi%20Tags/Diagnostic/Diag%20Info"));
    }

    [Theory]
    [InlineData("Group/Table")]
    [InlineData("/Group/Table/")]
    [InlineData("/Group//Table")]
    [InlineData("/")]
    public void Both_ensure_tools_refuse_invalid_paths_before_dispatch(string path)
    {
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.UnifiedHmiService((_, _) => { calls++; throw new InvalidOperationException(); },
            () => { calls++; throw new InvalidOperationException(); });
        AssertArgumentRefusal(() => service.EnsureUnifiedHmiTagTable("HMI", path));
        AssertArgumentRefusal(() => service.EnsureUnifiedHmiTag("HMI", path, "Ready", plcName: "PLC_Source", plcTag: "PlcReady"));
        Assert.Equal(0, calls);
    }

    public sealed class NamedTagTable { public string Name { get; set; } = ""; public object? Parent { get; set; } }
    public sealed class TagTables : List<NamedTagTable>
    {
        public List<string> Calls { get; } = new();
        public object? Owner { get; set; }
        public NamedTagTable Create(string name) { Calls.Add(name); var table = new NamedTagTable { Name = name, Parent = Owner }; Add(table); return table; }
    }
    public sealed class TagValue { public string Name { get; set; } = ""; public string TagTableName { get; set; } = ""; }
    public sealed class Tags : List<TagValue>
    {
        public TagValue Create(string name, string table) { var tag = new TagValue { Name = name, TagTableName = table }; Add(tag); return tag; }
    }
    public sealed class TagFolder
    {
        public string Name { get; set; } = "";
        public TagFolder[] TagTableGroups { get; set; } = Array.Empty<TagFolder>();
        public TagFolder[] Groups { get; set; } = Array.Empty<TagFolder>();
        public TagTables TagTables { get; } = new();
        public Tags Tags { get; } = new();
        public TagFolder() => TagTables.Owner = this;
    }

    private static MethodInfo Method(string name) => Host.PortedToolDeclarations.Methods("21")
        .Single(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name == name);
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments)
        => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
    private static JsonObject Arguments(string name, string? action = null)
    {
        var root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(Path.GetDirectoryName(typeof(UnifiedPortAdmissionTests).Assembly.Location)!);
        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!;
        var example = source["profiles"]!["full-engine"]![name]!;
        var arguments = example["arguments"]!.DeepClone().AsObject();
        if (action != null && name is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules"
            && example["operations"]?[action]?["arguments"] is JsonObject operation)
            foreach (var pair in operation) arguments[pair.Key] = pair.Value?.DeepClone();
        return arguments;
    }

    [Theory]
    [InlineData("EnsureUnifiedHmiTagTable")]
    [InlineData("EnsureUnifiedHmiTag")]
    public void Table_parameter_descriptions_and_examples_explain_the_absolute_form(string name)
    {
        var parameter = Method(name).GetParameters().Single(p => p.Name == "tagTableName");
        var description = parameter.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
        Assert.Contains("/Group/Sub/Table", description);
        Assert.Contains("must already exist", description);
        Assert.Equal("/HmiTags/Diagnostic/DiagInfo", (string?)Arguments(name)["tagTableName"]);
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Ensure_tools_reject_invalid_slash_names_before_readiness_or_worker_calls(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        foreach (var name in new[] { "EnsureUnifiedHmiTagTable", "EnsureUnifiedHmiTag", "EnsureUnifiedHmiScreen" })
        foreach (var path in new[] { "Group/Table", "/Group/Table/", "/Group//Table", "/" + string.Join("/", Enumerable.Repeat("G", 65)) + "/Table" })
        {
            var tool = tools.Single(t => t.ProtocolTool.Name == name);
            var arguments = Arguments(name);
            arguments[name == "EnsureUnifiedHmiScreen" ? "screenName" : "tagTableName"] = path;
            var result = await tool.InvokeAsync(Request(name, arguments));
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
            Assert.NotEqual("unknown", (string?)result.StructuredContent!["meta"]!["outcome"]);
            Assert.Contains("/Group/Sub/Table", (string)result.StructuredContent!["error"]!["message"]!);
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("19", "GetUnifiedAuditSettings", "alarmAuditClasses", "", "")]
    [InlineData("19", "ListUnifiedEngineeringObjects", "textLists", "", "")]
    [InlineData("19", "ManageUnifiedDynamization", "", "create", "Expression")]
    [InlineData("19", "ManageUnifiedDynamization", "", "update", "TagParameter")]
    [InlineData("20", "ExportUnifiedEngineeringList", "graphicLists", "", "")]
    [InlineData("21", "ManageUnifiedListEntries", "textLists", "create", "")]
    [InlineData("21", "ManageUnifiedEngineeringObject", "graphicLists", "create", "")]
    [InlineData("20", "ManageUnifiedEngineeringObject", "textLists", "create", "")]
    [InlineData("19", "ManageUnifiedEngineeringObject", "connections", "update", "")]
    [InlineData("21", "ManageUnifiedEngineeringObject", "tagTables", "create", "")]
    public async Task Missing_action_is_refused_by_the_served_host_before_any_worker_call(
        string release, string name, string category, string action, string kind)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name);
        if (category.Length != 0) arguments["category"] = category;
        if (action.Length != 0) arguments["action"] = action;
        if (kind.Length != 0) arguments["dynamizationKind"] = kind;
        var result = await tool.InvokeAsync(Request(name, arguments));
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }

    public static IEnumerable<object[]> MappingCases => from family in PortedFamilies.All.Where(f => f.Name is "F27" or "F28")
        from name in family.Tools select new object[] { name };

    public static IEnumerable<object[]> MalformedAddresses => ReleaseEnds.OldestAndNewest(
        AllMalformedAddresses(), row => (string)row[1] + ":" + row[2], row => (string)row[0]);

    private static IEnumerable<object[]> AllMalformedAddresses()
    {
        foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (var method in Host.PortedToolDeclarations.Methods(release))
        {
            var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            if (!PortedFamilies.Available(release, name)) continue;
            foreach (var parameter in method.GetParameters().Where(p => p.ParameterType.IsArray
                && p.Name is "objectPath" or "branch" or "collectionPath" or "tagPath" or "durationPath"))
            foreach (var path in new[] { "[\"Screens\",\"Main\"]", "{}", "[{\"property\":\"Screens\",\"unknown\":true}]", "[{\"property\":\"Parent\"}]" })
            foreach (bool encoded in new[] { false, true })
                yield return new object[] { release, name, parameter.Name!, path, encoded };
        }
    }

    [Theory, MemberData(nameof(MalformedAddresses))]
    public async Task Every_registered_address_tool_refuses_malformed_paths_before_any_worker_call(
        string release, string name, string parameter, string path, bool encoded)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name);
        if (name == "GetUnifiedPlantObject")
        {
            Assert.Equal("/<plant path segments from GetUnifiedPlantObject with empty plantPath>", (string?)arguments["plantPath"]);
            arguments["plantPath"] = "/FixturePlant";
        }
        arguments[parameter] = encoded ? JsonValue.Create(path) : JsonNode.Parse(path);
        var schema = tool.ProtocolTool.InputSchema.GetProperty("properties");
        if (schema.TryGetProperty("action", out _)) arguments["action"] = "delete";
        if (schema.TryGetProperty("dryRun", out _)) arguments["dryRun"] = true;
        if (schema.TryGetProperty("confirmDelete", out _)) arguments["confirmDelete"] = false;
        var result = await tool.InvokeAsync(Request(name, arguments));
        var body = result.StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("/" + parameter, (string?)body["error"]?["details"]?["parameter"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        var message = (string)body["error"]!["message"]!;
        Assert.Contains("Expected an array", message);
        Assert.Contains("\"property\"", message);
        if (path.StartsWith("[", StringComparison.Ordinal)) Assert.Contains("step 0", message);
        if (path.StartsWith("[\"", StringComparison.Ordinal)) Assert.Contains("is a string", message);
        Assert.Equal(0, worker.Calls);
    }
    [Theory]
    [InlineData("20", "GetUnifiedObjectEvents")]
    [InlineData("21", "GetUnifiedPlantObject")]
    [InlineData("20", "GetUnifiedScreenBranch")]
    [InlineData("21", "ValidateUnifiedObject")]
    public void Read_address_tools_do_not_require_approval(string release, string name)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        Assert.False(Host.McpServer.ApprovalWrite(name, Arguments(name).ToJsonString()));
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("20", "GetDccObject")]
    [InlineData("21", "GetDccObject")]
    [InlineData("20", "ListSivarcRules")]
    [InlineData("21", "ListSivarcRules")]
    public async Task Optional_root_addresses_keep_their_array_type_during_capability_admission(string release, string name)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = PortedToolContract.Create(worker, release).OfType<FoundationTool>().Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name); arguments["objectPath"] = new JsonArray();
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString())!;
        Assert.Null(tool.RefusePortedCapability(values));
        var result = await new FoundationV4Tool(tool, release).InvokeAsync(Request(name, arguments));
        Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(1, worker.Calls);
    }

    [Theory]
    [InlineData("19", "ManageUnifiedObjectParts", "objectPath")]
    [InlineData("20", "ManageUnifiedObjectParts", "objectPath")]
    [InlineData("21", "SetUnifiedPlantObject", "objectPath")]
    [InlineData("20", "GetUnifiedObjectEvents", "objectPath")]
    [InlineData("21", "GetUnifiedScreenBranch", "branch")]
    [InlineData("21", "ManageSivarcRule", "collectionPath")]
    public void Bridge_binding_refuses_malformed_addresses_before_approval_or_dispatch(string release, string name, string parameter)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var selected = new ReleaseState(release);
        using var lifetime = new Host.ImportStagingHostLifetime();
        var invoker = new toolpipeline::TiaMcp.FoundationHost.WorkerToolInvoker(new ApprovalCatalog(release), worker, lifetime);
        var arguments = Arguments(name);
        arguments[parameter] = JsonNode.Parse("[{\"property\":\"Parent\"}]");
        var error = invoker.Bind(name, new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(arguments)), out var call);
        Assert.NotNull(error);
        Assert.Equal(TiaMcp.Logic.V4.ErrorCode.InvalidArgument, error!.Code);
        Assert.Equal("/" + parameter, Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(error.Details).Parameter);
        Assert.Contains("Expected an array", error.Message);
        Assert.Null(call);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(MappingCases))]
    public void Every_served_parameter_has_an_explicit_request_mapping(string name)
    {
        var method = Method(name);
        foreach (var parameter in method.GetParameters())
        {
            JsonElement input = parameter.ParameterType == typeof(bool) ? JsonSerializer.SerializeToElement(false)
                : parameter.ParameterType == typeof(int) || parameter.ParameterType == typeof(uint) ? JsonSerializer.SerializeToElement(7)
                : parameter.ParameterType == typeof(string) ? JsonSerializer.SerializeToElement("fixture")
                : JsonSerializer.SerializeToElement(parameter.ParameterType.IsArray ? (object)Array.Empty<object>() : new Dictionary<string, object>());
            var request = FoundationTool.BindUnifiedRequest(name, method, new Dictionary<string, JsonElement> { [parameter.Name!] = input });
            var paged = name is "GetUnifiedGraphicSelection" or "ListUnifiedGlobalScripts" or "GetUnifiedGlobalScript"
                or "ListUnifiedTagDefinitions" or "GetUnifiedScreenBranch" or "GetUnifiedLibraryType" or "ListUnifiedLibraryFolderEntries" or "GetUnifiedFaceplateInstance";
            var expected = parameter.Name == "previewToken" ? "ExpectedToken" : parameter.Name == "limit" && paged ? "PageSize"
                : char.ToUpperInvariant(parameter.Name![0]) + parameter.Name[1..] + (parameter.ParameterType != typeof(string)
                    && parameter.ParameterType != typeof(bool) && parameter.ParameterType != typeof(int) && parameter.ParameterType != typeof(uint) ? "Json" : "");
            var value = typeof(UnifiedRequest).GetProperty(expected)!.GetValue(request);
            Assert.Equal(input.ValueKind == JsonValueKind.String ? input.GetString() : parameter.ParameterType == typeof(int) ? (object)7
                : parameter.ParameterType == typeof(uint) ? 7u : parameter.ParameterType == typeof(bool) ? false : input.GetRawText(), value);
        }
    }

    [Theory]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Duplicate_runtime_setting_keys_are_rejected_before_native_access(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = LegacyHostToolRegistry.Create(worker, release, false).Single(t => t.ProtocolTool.Name == "SetUnifiedRuntimeSettings");
        var request = Request("SetUnifiedRuntimeSettings", Arguments("SetUnifiedRuntimeSettings"));
        using var document = JsonDocument.Parse("{\"StartScreen\":\"A\",\"StartScreen\":\"B\"}");
        var arguments = new Dictionary<string, JsonElement>(request.Params!.Arguments!);
        arguments["changes"] = document.RootElement.Clone();
        var duplicateRequest = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>())
            { Params = new() { Name = "SetUnifiedRuntimeSettings", Arguments = arguments } };
        var result = await tool.InvokeAsync(duplicateRequest);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("/changes", (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
        Assert.Contains("Duplicate", (string)result.StructuredContent!["error"]!["message"]!);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(0, worker.Calls);
    }
    // Authored approval boundary: keep this independent of declaration markers.
    private static readonly HashSet<string> ApprovalGatedTools = new(StringComparer.Ordinal) {
        "SetUnifiedObjectProperties", "SetUnifiedMultilingualProperty", "ManageUnifiedLoggingTag",
        "SetUnifiedLogDuration", "ManageUnifiedOpcUaAlarmType", "ManageUnifiedPlantNode", "SetUnifiedPlantObject",
        "ManageUnifiedObjectParts", "ManageUnifiedDynamization", "ManageUnifiedScreenLayout", "ManageUnifiedListEntries",
        "ImportUnifiedEngineeringList", "ManageUnifiedEngineeringObject", "ManageUnifiedEvent",
        "ImportUnifiedOpcUaAlarms", "SetUnifiedGlobalScript", "SetUnifiedRuntimeSettings"
    };

    public static IEnumerable<object[]> ApprovalCases
    {
        get
        {
            foreach (var release in new[] { "19", "20", "21" })
            foreach (var family in PortedFamilies.All.Where(f => f.Name is "F27" or "F28"))
            foreach (var name in family.Tools.Where(n => PortedFamilies.Available(release, n)))
            {
                bool alwaysGated = ApprovalGatedTools.Contains(name);
                if (!alwaysGated && name is not ("ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" or "ExportUnifiedEngineeringList")) continue;
                foreach (var preview in new[] { true, false })
                foreach (var action in name is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" ? new[] { "import", "export" } : new[] { "read" })
                    yield return new object[] { release, name, preview, action, !preview && (alwaysGated || action == "import") };
            }
        }
    }

    [Theory, MemberData(nameof(ApprovalCases))]
    public async Task Foundation_unified_approval_follows_preview_and_native_effects(
        string release, string name, bool preview, string action, bool gated)
    {
        using var worker = new ApprovalWorker(); int waits = 0;
        using var releaseState = new ReleaseState(release);
        using var lifetime = new Host.ImportStagingHostLifetime();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var catalog = new ApprovalCatalog(release);
        var invoker = new toolpipeline::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, worker, lifetime, tools);
        using var context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog, Invoker = invoker });
        var previousWait = Host.McpServer.ApprovalWaitOverrideForTests;
        var previousSettings = TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        new TiaOpenness.Shared.ApprovalSettings(true, 1).Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        Task<TiaOpenness.Shared.ApprovalOutcome> Wait(TiaOpenness.Shared.PendingApproval pending,
            TiaOpenness.Shared.ApprovalSettings settings, CancellationToken token)
        {
            waits++;
            Assert.Equal(0, worker.Applies);
            return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, false, "denied"));
        }
        Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        // Foundation releases use the production Foundation wrapper; full-engine
        // releases use its shared serialized approval boundary.
        var tool = release == "19"
            ? new FoundationV4Tool(PortedToolContract.Create(worker, release).Single(t => t.ProtocolTool.Name == name),
                release, approvalWait: Wait)
            : tools.Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name, action); arguments["dryRun"] = preview;
        if (name is "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" or "ImportUnifiedOpcUaAlarms") arguments["action"] = action;
        if (release == "19" && name == "ManageUnifiedEngineeringObject") arguments["category"] = "alarmClasses";
        var directory = Path.Combine(AppContext.BaseDirectory, "unified-approval", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input.xml"); File.WriteAllText(input, "<Document />");
        foreach (var field in new[] { "filePath", "xmlPath" })
            if (arguments.ContainsKey(field)) arguments[field] = input;
        foreach (string parameter in new[] { "directory", "exportDirectory", "sourceDirectory" })
            if (arguments.ContainsKey(parameter)) arguments[parameter] = action == "export"
                ? TiaOpenness.Shared.DataLocations.Current.OutputRoot : directory;
        if (name == "ExchangeUnifiedScriptModules")
        {
            var script = Path.Combine(directory, arguments["fileName"]!.GetValue<string>());
            File.WriteAllText(script, "export function Fixture() { return 1; }");
            File.WriteAllText(Path.ChangeExtension(script, ".yml"), "Name: Status\n");
        }
        if (arguments.ContainsKey("destinationDirectory"))
            arguments["destinationDirectory"] = Path.Combine(TiaOpenness.Shared.DataLocations.Current.OutputRoot, "new-output");
        // Release 19 calls the raw Foundation tool, below the wrapper that adapts canonical names.
        if (release == "19") arguments = TiaMcp.Logic.V4.CanonicalToolParameters.LegacyArguments(name, arguments);
        try
        {
            if (name is "ManageUnifiedEngineeringObject" or "SetUnifiedGlobalScript" or "SetUnifiedRuntimeSettings" && !preview)
            {
                arguments["dryRun"] = true;
                var plan = await tool.InvokeAsync(Request(name, arguments));
                Assert.True((bool)plan.StructuredContent!["ok"]!, plan.StructuredContent.ToJsonString());
                arguments["expectedPlanHash"] = (string)plan.StructuredContent["data"]!["planHash"]!;
                arguments["expectedProjectFile"] = "C:/fixture.ap21";
                arguments["dryRun"] = false;
            }
            var result = await tool.InvokeAsync(Request(name, arguments));
            Assert.Equal(gated ? 1 : 0, waits);
            if (gated)
            {
                Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
                Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
                Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
                Assert.Equal(0, worker.Applies);
            }
            else
            {
                Assert.NotEqual("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
                Assert.True(worker.Calls > 0);
            }
        }
        finally
        {
            Host.McpServer.ApprovalWaitOverrideForTests = previousWait;
            previousSettings.Save(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
            Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, true);
        }
    }

    private sealed class ReleaseState : IDisposable
    {
        private readonly string previousRelease;
        internal ReleaseState(string release)
        {
            previousRelease = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
        }
        public void Dispose() => toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease;
    }

    private sealed class ApprovalCatalog : Host.IToolCatalogView
    {
        public IReadOnlyDictionary<string, Host.ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, Host.ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<Host.ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities => new();
        public Host.ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal ApprovalCatalog(string release)
        {
            var descriptors = new Dictionary<string, Host.ToolDescriptor>();
            foreach (var method in Host.PortedToolDeclarations.Methods(release))
            {
                var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
                if (!PortedToolContract.Families(release).Any(f => f.Tools.Contains(name))) continue;
                Host.DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
                descriptors.Add(name, new Host.ToolDescriptor(descriptor.Name, descriptor.RawDescription, descriptor.Tool, descriptor.Classification,
                    descriptor.Signature, descriptor.Parameters, descriptor.DryRun, descriptor.CandidateFamily, "foundation"));
            }
            All = descriptors;
        }
    }

    [Fact]
    public async Task Exception_after_script_import_keeps_worker_meta_in_the_unknown_envelope()
    {
        const string name = "SetUnifiedGlobalScript";
        using var worker = new ApprovalWorker(bound: true, failReadback: true);
        using var release = new ReleaseState("21");
        using var lifetime = new Host.ImportStagingHostLifetime();
        var catalog = new ApprovalCatalog("21");
        using var context = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog,
                Invoker = new toolpipeline::TiaMcp.FoundationHost.WorkerToolInvoker(catalog, worker, lifetime) });
        var tool = PortedToolContract.Create(worker, "21").Single(t => t.ProtocolTool.Name == name);
        var arguments = Arguments(name);
        var preview = (await tool.InvokeAsync(Request(name, arguments))).StructuredContent!;
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        arguments["dryRun"] = false;
        arguments["expectedPlanHash"] = preview["data"]!["planHash"]!.DeepClone();
        arguments["expectedProjectFile"] = "C:/fixture.ap21";
        var result = await tool.InvokeAsync(Request(name, arguments));
        var body = result.StructuredContent!;
        Assert.Equal(1, worker.Applies);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        var evidence = body["error"]!["details"]!["evidence"]!;
        Assert.True((bool?)evidence["importReturned"]);
        Assert.Equal("exportAfter", (string?)evidence["phase"]);
        Assert.Equal("System.IO.IOException", (string?)evidence["nativeExceptionType"]);
        Assert.Equal("candidate", (string?)evidence["candidateDirectoryRelativeToTemp"]);
        Assert.Null(evidence["error"]);
        Assert.Equal(4, (int?)evidence["scriptCountAfterImport"]);
        Assert.False((bool?)evidence["moduleExistsAfterImport"]);
        Assert.Equal("<path>", (string?)evidence["candidateDirectory"]);
        Assert.DoesNotContain("PRIVATE_FIXTURE", body.ToJsonString(), StringComparison.Ordinal);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain("PRIVATE_FIXTURE", text, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(text)));
    }

    [Fact]
    public void Returned_script_write_with_unknown_outcome_exposes_only_import_facts()
    {
        var response = ScriptReadbackFailure();
        response.Meta["candidateDirectoryRelativeToTemp"] = response.Meta["candidateDirectory"];
        var body = Host.HmiInspectionContract.Map("SetUnifiedGlobalScript", response, writes: true, current: true).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]!["code"]);
        var evidence = body["error"]!["details"]!["evidence"]!;
        Assert.True((bool?)evidence["importReturned"]);
        Assert.Equal("exportAfter", (string?)evidence["phase"]);
        Assert.Equal("<path>", (string?)evidence["candidateDirectoryRelativeToTemp"]);
        Assert.DoesNotContain("PRIVATE_FIXTURE", body.ToJsonString(), StringComparison.Ordinal);
    }

    private static HmiMessage ScriptReadbackFailure() => new() {
        Message = "PRIVATE_FIXTURE worker summary",
        Meta = new() {
            ["importReturned"] = true, ["phase"] = "exportAfter", ["operationSuccess"] = false, ["mayHaveChanged"] = true,
            ["writeOutcomeUnknown"] = true, ["scriptCountAfterImport"] = 4, ["moduleExistsAfterImport"] = false,
            ["candidateDirectory"] = "C:/PRIVATE_FIXTURE/candidate", ["candidateDirectoryRelativeToTemp"] = "candidate",
            ["nativeExceptionType"] = typeof(IOException).FullName, ["error"] = "PRIVATE_FIXTURE exception stack",
            ["nativeMessage"] = "PRIVATE_FIXTURE native reason", ["workerMessage"] = "PRIVATE_FIXTURE worker reason",
            ["readError"] = "PRIVATE_FIXTURE read error", ["failures"] = new[] { new { reason = "PRIVATE_FIXTURE nested reason" } }
        }
    };

    [Theory, InlineData(false), InlineData(true)]
    public void Unknown_script_import_filters_worker_and_native_text_on_the_HMI_and_ported_failure_paths(bool priorMutation)
    {
        const string tool = "SetUnifiedGlobalScript";
        var reply = JsonSerializer.SerializeToNode(new HmiReply { Value = ScriptReadbackFailure() })!.AsObject();
        reply["nativeMessage"] = "PRIVATE_FIXTURE outer native diagnostic";
        reply["Failure"] = new JsonObject { ["Message"] = "PRIVATE_FIXTURE worker failure",
            ["Evidence"] = new JsonObject { ["nativeMessage"] = "PRIVATE_FIXTURE failure detail" } };
        var exception = new WorkerOperationException("PRIVATE_FIXTURE worker exception", -32603, "unknown", reply.ToJsonString());
        exception.Data["workerRecoveryMessage"] = "PRIVATE_FIXTURE recovery diagnostic";
        if (priorMutation) exception.Data["foundationPriorMutation"] = true;
        var hmi = Host.HmiInspectionContract.Run(tool, true, true, () => throw exception);
        var caught = FoundationV4Result.Failure("21", tool, "fixture", true, true, reply, exception);
        if (priorMutation) Assert.True((bool?)caught.StructuredContent?["data"]?["earlierMutationMayHaveChanged"]);
        var reconciled = FoundationV4Result.ReconcileWorker(caught, FoundationV4Result.NativeDiagnostics(reply), tool, exception);
        foreach (var result in new[] { hmi, caught, reconciled })
        {
            var body = result.StructuredContent!;
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            var facts = body["error"]!["details"]!["evidence"]!;
            Assert.True((bool?)facts["importReturned"]);
            Assert.Equal("exportAfter", (string?)facts["phase"]);
            Assert.DoesNotContain("PRIVATE_FIXTURE", body.ToJsonString(), StringComparison.Ordinal);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.DoesNotContain("PRIVATE_FIXTURE", text, StringComparison.Ordinal);
            Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(text)));
        }
    }

    [Fact]
    public void Unknown_script_import_without_worker_evidence_drops_a_native_exception_message()
    {
        const string tool = "SetUnifiedGlobalScript";
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, "21");
        var cause = new IOException("PRIVATE_FIXTURE native exception without worker evidence");
        cause.Data["recoveryWarning"] = "PRIVATE_FIXTURE recovery warning";
        foreach (var result in new[] { FoundationV4Result.Failure("21", tool, "fixture", true, true, null, cause),
            Host.McpServer.TargetFailure(tool, cause, true, """{"dryRun":false}""") })
        {
            var body = result.StructuredContent!;
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Empty(body["error"]!["details"]!["evidence"]!.AsObject());
            Assert.DoesNotContain("PRIVATE_FIXTURE", body.ToJsonString(), StringComparison.Ordinal);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(text)));
        }
    }

    private sealed class ApprovalWorker(bool bound = false, bool failReadback = false) : IFoundationWorker, toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        internal int Calls, Applies;
        public bool Faulted => false;
        public JsonNode? Binding => bound ? new JsonObject { ["projectFile"] = "C:/Fixture.ap21" } : null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<toolpipeline::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine dispatch expected.");
        private sealed class Lane : IDisposable { public void Dispose() { } }
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["ProjectFile"] = "C:/fixture.ap21", ["IsAttached"] = true });
            if ((bool?)arguments["dryRun"] == false)
            {
                Applies++;
                if (failReadback)
                {
                    // WorkerSessionRecovery sends the entire HmiReply as ChannelFailureException evidence.
                    var reply = new HmiReply { OutcomeUnknown = true, MayHaveChanged = true, Value = ScriptReadbackFailure() };
                    throw new WorkerOperationException("The write outcome is unknown.", -32603, "unknown", JsonSerializer.Serialize(reply));
                }
            }
            return Task.FromResult<JsonNode?>(new JsonObject { ["Value"] = new JsonObject { ["Message"] = "fixture", ["Meta"] = new JsonObject {
                ["success"] = true, ["operationSuccess"] = true, ["apiCallSuccess"] = true,
                ["dataComplete"] = true, ["verificationSuccess"] = true, ["mayHaveChanged"] = false } } });
        }
        public void Dispose() { }
    }
}
