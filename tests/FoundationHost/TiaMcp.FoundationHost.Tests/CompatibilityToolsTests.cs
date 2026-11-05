extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class CompatibilityToolsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() {
            Name = name, Arguments = arguments.Deserialize<Dictionary<string, JsonElement>>() } };

    [Theory]
    [InlineData("CreateDevice", "{\"orderNumber\":\"6ES7 512-1DK01-0AB0\",\"version\":\"V2.9\",\"deviceName\":\"CPU\"}")]
    [InlineData("CreateHardwareCatalogDevice",
                "{\"keyword\":\"OrderNumber:6ES7 512-1DK01-0AB0/V2.9\",\"deviceName\":\"CPU\",\"preferredText\":\"ignored ranking\"}")]
    [InlineData("CreatePlcBlockGroup", "{\"softwarePath\":\"PLC\",\"groupPath\":\"A/B\"}")]
    [InlineData("CreatePlcTypeGroup", "{\"softwarePath\":\"PLC\",\"groupPath\":\"A/B\"}")]
    [InlineData("DeleteEmptyPlcBlockGroup", "{\"softwarePath\":\"PLC\",\"groupPath\":\"A/B\"}")]
    public async Task Compatibility_dispatches_once_and_returns_the_exact_canonical_result(string name, string json)
    {
        var mapping = CompatibilityTools.Mappings[name];
        Assert.Equal(PortedFamilies.CompatibilityTargets[name], mapping.Target);
        var canonical = new SpyTool(mapping.Target);
        var forward = new CompatibilityTools.Forwarder(new Tool { Name = name }, canonical, mapping, "21");
        var input = JsonNode.Parse(json)!.AsObject();
        input["dryRun"] = false; input["confirm"] = true; input["expectedProjectFile"] = "C:/Project.ap21";
        input["expectedPlanHash"] = new string('a', 64);
        var request = Request(name, input); var original = request.Params;
        using var cancellation = new CancellationTokenSource();
        var result = await forward.InvokeAsync(request, cancellation.Token);
        Assert.Same(canonical.Result, result);
        Assert.Same(original, request.Params);
        Assert.Equal(1, canonical.Calls);
        Assert.Equal(cancellation.Token, canonical.Token);
        var expected = input.DeepClone().AsObject(); expected.Remove("preferredText");
        foreach (var pair in mapping.Parameters) { var value = expected[pair.Key]!.DeepClone(); expected.Remove(pair.Key); expected[pair.Value] = value; }
        foreach (var pair in mapping.Fixed) expected[pair.Key] = pair.Value;
        Assert.True(JsonNode.DeepEquals(expected, canonical.Arguments));
        Assert.Equal(mapping.Target, canonical.Name);
    }

    [Theory]
    [InlineData("CreateDevice")]
    [InlineData("CreateHardwareCatalogDevice")]
    [InlineData("CreatePlcBlockGroup")]
    [InlineData("CreatePlcTypeGroup")]
    [InlineData("DeleteEmptyPlcBlockGroup")]
    public async Task Invalid_alias_arguments_never_reach_the_canonical_tool(string name)
    {
        var mapping = CompatibilityTools.Mappings[name]; var canonical = new SpyTool(mapping.Target);
        var forward = new CompatibilityTools.Forwarder(new Tool { Name = name }, canonical, mapping, "21");
        var response = await forward.InvokeAsync(Request(name, new JsonObject { ["unexpected"] = true }));
        Assert.True(response.IsError); Assert.Equal(0, canonical.Calls);
        Assert.True(forward.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("dryRun").GetProperty("default").GetBoolean());
    }

    [Fact]
    public async Task Cancellation_or_failure_restores_the_original_request_without_retry()
    {
        var mapping = CompatibilityTools.Mappings["CreatePlcBlockGroup"]; var target = new SpyTool(mapping.Target) { Failure = new OperationCanceledException()
        };
        var forward = new CompatibilityTools.Forwarder(new Tool { Name = "CreatePlcBlockGroup" }, target, mapping, "21");
        var request = Request("CreatePlcBlockGroup", new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "A/B" }); var original = request.Params;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await forward.InvokeAsync(request));
        Assert.Same(original, request.Params); Assert.Equal(1, target.Calls);
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
    public void Retained_batches_and_every_available_alias_have_one_target(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var roster = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false).Select(t => t.ProtocolTool).ToDictionary(t => t.Name);
        foreach (var batch in CanonicalBatchTools.Names)
        {
            Assert.Equal(PortedFamilies.Available(release, batch), roster.ContainsKey(batch));
            Assert.DoesNotContain(batch, enginehost::TiaMcp.FoundationHost.SharedToolPolicy.Removed);
            if (roster.TryGetValue(batch, out var declaration)) Assert
                .True(declaration.InputSchema.GetProperty("properties").GetProperty("dryRun").GetProperty("default").GetBoolean());
        }
        foreach (var pair in CompatibilityTools.Mappings)
        {
            if (!PortedFamilies.Available(release, pair.Key)) { Assert.DoesNotContain(pair.Key, roster.Keys); continue; }
            Assert.Contains(pair.Key, roster.Keys);
            if (pair.Key == "CreateDevice" && release is "14sp1" or "15.1" or "16" or "17")
                Assert.DoesNotContain(pair.Value.Target, roster.Keys);
            else Assert.Contains(pair.Value.Target, roster.Keys);
            var properties = roster[pair.Key].InputSchema.GetProperty("properties");
            Assert.True(properties.GetProperty("dryRun").GetProperty("default").GetBoolean());
            foreach (var fixedParameter in pair.Value.Fixed.Keys) Assert.False(properties.TryGetProperty(fixedParameter, out _));
        }
    }

    [Theory]
    [InlineData("tag", "CreatePlcTag", "address")]
    [InlineData("constant", "CreatePlcUserConstant", "value")]
    public async Task Definition_creation_uses_the_canonical_declaration_tool(string kind, string canonical, string valueField)
    {
        var source = new ActionSpy("ManagePlcTagDefinition"); var target = new ActionSpy(canonical);
        var forward = new CompatibilityTools.ActionForwarder(source, new Dictionary<string, McpServerTool> { [canonical] = target }, "21");
        var request = Request(source.ProtocolTool.Name, new JsonObject { ["action"] = "create", ["kind"] = kind, ["softwarePath"] = "PLC",
            ["tablePath"] = "Group/Tags", ["name"] = "Ready", ["dataType"] = "Bool", ["addressOrValue"] = "%M0.0", ["dryRun"] = true });
        Assert.Same(target.Result, await forward.InvokeAsync(request));
        Assert.Equal(0, source.Calls); Assert.Equal(1, target.Calls);
        Assert.Equal("PLC", (string?)target.Arguments!["plc"]); Assert.Equal("Group/Tags", (string?)target.Arguments["table"]);
        Assert.Equal("%M0.0", (string?)target.Arguments[valueField]);
    }

    [Fact]
    public async Task Extra_creation_properties_refuse_before_any_dispatch_and_update_keeps_its_route()
    {
        var source = new ActionSpy("ManagePlcTagDefinition");
        var forward = new CompatibilityTools.ActionForwarder(source, new Dictionary<string, McpServerTool>(), "21");
        var input = new JsonObject { ["action"] = "create", ["properties"] = new JsonObject { ["Comment"] = "Keep" } };
        Assert.True((await forward.InvokeAsync(Request(source.ProtocolTool.Name, input))).IsError);
        Assert.Equal(0, source.Calls);
        input["action"] = "update";
        Assert.Same(source.Result, await forward.InvokeAsync(Request(source.ProtocolTool.Name, input)));
        Assert.Equal(1, source.Calls); Assert.Equal("Keep", (string?)source.Arguments!["properties"]!["Comment"]);
    }

    [Theory]
    [InlineData("createFromFile", "ImportPlcExternalSource")]
    [InlineData("delete", "DeletePlcExternalSource")]
    [InlineData("generateBlocks", "GenerateBlocksFromExternalSource")]
    [InlineData("createGroup", "ManagePlcUserGroup")]
    [InlineData("renameGroup", "ManagePlcUserGroup")]
    [InlineData("deleteGroup", "ManagePlcUserGroup")]
    public async Task Ordinary_external_source_actions_dispatch_once_through_foundation(string action, string canonical)
    {
        var source = new ActionSpy("ManagePlcExternalSources"); var target = new ActionSpy(canonical);
        var forward = new CompatibilityTools.ActionForwarder(source, new Dictionary<string, McpServerTool> { [canonical] = target }, "21");
        var input = new JsonObject { ["action"] = action, ["softwarePath"] = "PLC", ["name"] = action == "createFromFile" ? "" : "Source.scl",
            ["filePath"] = "C:/Sources/Source.scl", ["dryRun"] = false, ["confirmDelete"] = true, ["confirm"] = true,
            ["expectedPlanHash"] = new string('b', 64), ["expectedProjectFile"] = "C:/Project.ap21" };
        Assert.Same(target.Result, await forward.InvokeAsync(Request(source.ProtocolTool.Name, input)));
        Assert.Equal(0, source.Calls); Assert.Equal(1, target.Calls);
        Assert.Equal(false, (bool?)target.Arguments!["dryRun"]); Assert.Equal("PLC", (string?)target.Arguments["softwarePath"]);
    }

    [Theory]
    [InlineData("unitName", "UnitA")]
    [InlineData("targetKind", "block")]
    [InlineData("generateOption", "KeepOnError")]
    public async Task Distinct_scoped_source_operations_keep_the_existing_official_api_route(string field, string value)
    {
        var source = new ActionSpy("ManagePlcExternalSources");
        var forward = new CompatibilityTools.ActionForwarder(source, new Dictionary<string, McpServerTool>(), "21");
        Assert
            .Same(source.Result, await forward
                .InvokeAsync(Request(source.ProtocolTool.Name, new JsonObject { ["action"] = "generateBlocks", [field] = value })));
        Assert.Equal(1, source.Calls); Assert.Equal(value, (string?)source.Arguments![field]);
    }

    [Fact]
    public void A_lost_move_worker_keeps_the_original_without_claiming_a_recovery_copy()
    {
        string? attempted = null;
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, arguments) => {
            Assert.True((bool?)arguments["dryRun"]);
            Assert.True((bool?)arguments["request"]!["DryRun"]);
            attempted = (string?)arguments["request"]!["FilePath"];
            throw new IOException("Fixture worker unavailable");
        }, () => true, () => "C:/Project.ap21");
        var result = new enginehost::TiaMcpServer.ModelContextProtocol.PlcOrganisationTools(service).MoveBlockToGroupV4("PLC", "Source/Motor", "Target");
        Assert.True(result.IsError); Assert.NotNull(attempted);
        Assert.Contains("plc-move-", attempted, StringComparison.Ordinal);
        Assert.Null(result.StructuredContent?["data"]?["evidence"]?["recoveryExportPath"]);
        Assert.Equal("unconfirmed", (string?)result.StructuredContent?["data"]?["evidence"]?["recoveryExportStatus"]);
        Assert.False((bool?)result.StructuredContent?["data"]?["evidence"]?["nativeMutationIssued"]);
        Assert.NotEqual("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
    }

    [Theory]
    [InlineData("import", true)][InlineData("verify", true)][InlineData("delete-source", false)]
    public void Move_failure_after_import_retains_the_worker_recovery_and_source_evidence(string stage, bool retained)
    {
        const string path = "C:/recovery/original.xml";
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, arguments) =>
            new JsonObject { ["MayHaveChanged"] = true, ["Failure"] = new JsonObject { ["Code"] = "IOException", ["Message"] = "fixture failure",
                ["Evidence"] = new JsonObject {
                    ["stage"] = stage, ["recoveryExportPath"] = path, ["originalRetained"] = retained, ["nativeMutationIssued"] = true } } },
            () => true, () => "C:/Project.ap21");
        var result = new enginehost::TiaMcpServer.ModelContextProtocol.PlcOrganisationTools(service)
            .MoveBlockToGroupV4("PLC", "Motor", "Target", true, false, true, new string('a', 64), "C:/Project.ap21");
        Assert.True(result.IsError); Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal(path, (string?)result.StructuredContent?["data"]?["evidence"]?["recoveryExportPath"]);
        Assert.Equal(retained, (bool?)result.StructuredContent?["data"]?["evidence"]?["originalRetained"]);
        Assert.Equal(stage, (string?)result.StructuredContent?["data"]?["evidence"]?["stage"]);
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public void Move_restoration_results_and_the_only_recovery_copy_cross_the_worker_boundary(bool restored)
    {
        const string path = "C:/recovery/original.xml";
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, arguments) =>
            new JsonObject { ["MayHaveChanged"] = true, ["Failure"] = new JsonObject { ["Code"] = "IOException", ["Message"] = "target import failed",
                ["Evidence"] = new JsonObject { ["stage"] = "import", ["recoveryExportPath"] = path, ["mayHaveChanged"] = true,
                    ["restoreAttempted"] = true, ["restored"] = restored, ["recoveryOnlyCopy"] = !restored, ["nativeMutationIssued"] = true } } },
            () => true, () => "C:/Project.ap21");
        var result = new enginehost::TiaMcpServer.ModelContextProtocol.PlcOrganisationTools(service)
            .MoveBlockToGroupV4("PLC", "Motor", "Target", true, false, true, new string('a', 64), "C:/Project.ap21");
        Assert.True(result.IsError); Assert.Equal(restored, (bool?)result.StructuredContent?["data"]?["evidence"]?["restored"]);
        Assert.True((bool?)result.StructuredContent?["data"]?["evidence"]?["mayHaveChanged"]);
        Assert.Equal(path, (string?)result.StructuredContent?["data"]?["evidence"]?["recoveryExportPath"]);
        if (!restored) {
            Assert.Contains(path, (string?)result.StructuredContent?["error"]?["message"], StringComparison.Ordinal); Assert
                .Contains("only verified recovery copy", (string?)result.StructuredContent?["error"]?["message"], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Move_admission_refusals_keep_their_typed_precondition()
    {
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((operation, arguments) =>
            new JsonObject {
                ["MayHaveChanged"] = false, ["Failure"] = new JsonObject {
                    ["Code"] = "AdapterPreconditionException", ["Message"] = "The block is inconsistent." }
            },
            () => true, () => "C:/Project.ap21");
        var result = new enginehost::TiaMcpServer.ModelContextProtocol.PlcOrganisationTools(service).MoveBlockToGroupV4("PLC", "Motor", "Target");
        Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
    }

    [Theory]
    [InlineData("AdapterPreconditionException", "Move plan changed; preview again.", "PRECONDITION_FAILED")]
    [InlineData("AdapterPreconditionException", "Target group does not exist.", "PRECONDITION_FAILED")]
    [InlineData("AdapterPreconditionException", "The block is know-how protected.", "PRECONDITION_FAILED")]
    [InlineData("NotSupportedOnVersion", "Moving organization blocks is disabled.", "UNSUPPORTED_CAPABILITY")]
    public void Move_apply_refusals_without_an_approval_precheck_do_not_lock_the_session(string code, string message, string expected)
    {
        int calls = 0;
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, arguments) => {
            calls++; Assert.False((bool?)arguments["dryRun"]);
            return new JsonObject { ["MayHaveChanged"] = false, ["Failure"] = new JsonObject {
                ["Code"] = code, ["Message"] = message, ["Evidence"] = new JsonObject {
                    ["nativeExceptionType"] = code, ["nativeMessage"] = message } } };
        }, () => true, () => "C:/Project.ap21");
        // Direct apply models the disabled-approval path: there is no preview/precheck invocation.
        var result = new enginehost::TiaMcpServer.ModelContextProtocol.PlcOrganisationTools(service)
            .MoveBlockToGroupV4("PLC", "Motor", "Target", false, false, true, new string('a', 64), "C:/Project.ap21");
        Assert.Equal(1, calls);
        Assert.Equal(expected, (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.NotEqual(true, (bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.Null(result.StructuredContent?["data"]?["evidence"]?["recoveryExportPath"]);
    }

    [Theory]
    [InlineData("AdapterPreconditionException", "Move plan changed; preview again.", "PRECONDITION_FAILED")]
    [InlineData("AdapterPreconditionException", "Target group does not exist.", "PRECONDITION_FAILED")]
    [InlineData("AdapterPreconditionException", "The block is know-how protected.", "PRECONDITION_FAILED")]
    [InlineData("NotSupportedOnVersion", "Moving organization blocks is disabled.", "UNSUPPORTED_CAPABILITY")]
    [InlineData("ArgumentException", "Invalid group path.", "INVALID_ARGUMENT")]
    [InlineData("InvalidOperationException", "Exclusive access is unavailable.", "PRECONDITION_FAILED")]
    public async Task Disabled_approval_move_apply_keeps_the_registered_session_usable(string code, string message, string expected)
    {
        using var worker = new MoveRefusalWorker(code, message);
        var method = typeof(enginehost::TiaMcpServer.ModelContextProtocol.PlcOrganisationTools).GetMethod("MoveBlockToGroupV4")!;
        var tool = new FoundationV4Tool(new FoundationTool(worker, "21", method, "MovePlcBlockToGroup"), "21",
            () => new(false, 1), (pending, settings, _) => {
                Assert.False(settings.Enabled); return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(pending, true, null)); });
        var args = new JsonObject { ["softwarePath"] = "PLC", ["blockName"] = "Motor", ["targetGroupPath"] = "Target",
            ["autoCreateGroup"] = false, ["dryRun"] = false, ["confirm"] = true,
            ["expectedPlanHash"] = new string('a', 64), ["expectedProjectFile"] = "C:/Project.ap21" };
        var result = await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args));
        Assert.Equal(expected, (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.Contains(result.StructuredContent!["meta"]!["warnings"]!.AsArray(),
            warning => (string?)warning?["code"] == "APPROVAL_DISABLED");
        Assert.Equal(1, worker.MoveCalls);
        args["dryRun"] = true;
        Assert.True((bool?)(await tool.InvokeAsync(Request(tool.ProtocolTool.Name, args))).StructuredContent?["ok"]);
        Assert.Equal(2, worker.MoveCalls);
    }

    private sealed class MoveRefusalWorker(string code, string message) : IFoundationWorker
    {
        internal int MoveCalls;
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProjectFile"] = "C:/Project.ap21", ["ProcessId"] = 42, ["IsAttached"] = true });
            Assert.EndsWith(".ExecutePlcOrganisation", operation, StringComparison.Ordinal);
            MoveCalls++;
            if ((bool?)args["dryRun"] == true) return Task.FromResult<JsonNode?>(new JsonObject { ["Message"] = "preview",
                ["Meta"] = new JsonObject { ["operationSuccess"] = true, ["mayHaveChanged"] = false, ["planHash"] = new string('a', 64) } });
            return Task.FromResult<JsonNode?>(new JsonObject { ["MayHaveChanged"] = false, ["RequiresSessionReset"] = false,
                ["Failure"] = new JsonObject { ["Code"] = code, ["Message"] = message,
                    ["Evidence"] = new JsonObject { ["nativeExceptionType"] = code, ["nativeMessage"] = message } } });
        }
        public void Dispose() { }
    }

    [Fact]
    public void Lost_apply_worker_keeps_the_attempted_recovery_path_for_a_possible_delete()
    {
        string? attempted = null;
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, args) => {
            attempted = (string?)args["request"]?["FilePath"];
            throw new IOException("Worker disconnected after request dispatch.");
        }, () => true, () => "C:/Project.ap21");
        var result = new enginehost::TiaMcpServer.ModelContextProtocol.PlcOrganisationTools(service)
            .MoveBlockToGroupV4("PLC", "Motor", "Target", true, false, true, new string('a', 64), "C:/Project.ap21");
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.True((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.NotNull(attempted);
        Assert.Equal(attempted, (string?)result.StructuredContent?["data"]?["evidence"]?["recoveryExportPath"]);
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")]
    [InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public void Native_technology_batch_forwards_preview_and_reviewed_apply_without_static_session_state(string release)
    {
        using var selectedRelease = enginehost::TiaMcpServer.ModelContextProtocol.HardwareContract.UseRelease(release);
        const string project = "C:/Project.ap21";
        string hash = new string('c', 64); var calls = new List<JsonObject>();
        var service = new enginehost::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, arguments) => {
            calls.Add(arguments.DeepClone().AsObject());
            return new JsonObject { ["Batch"] = new JsonObject { ["Imported"] = new JsonArray(), ["Failed"] = new JsonArray(),
                ["Meta"] = new JsonObject { ["operationSuccess"] = true, ["planHash"] = hash, ["mayHaveChanged"] = false } } };
        }, () => true, () => project);
        var tools = new enginehost::TiaMcpServer.ModelContextProtocol.PlcTechnologyTools(service);
        Assert.True((bool?)tools.ExportTechnologyObjectsToDirectoryV4("PLC", "C:/Exports").StructuredContent?["ok"]);
        Assert.True((bool?)calls[0]["request"]?["DryRun"]);
        Assert.True((bool?)tools.ExportTechnologyObjectsToDirectoryV4("PLC", "C:/Exports", dryRun: false,
            confirm: true, expectedPlanHash: hash, expectedProjectFile: project).StructuredContent?["ok"]);
        Assert.False((bool?)calls[1]["request"]?["DryRun"]);
        Assert.Equal(hash, (string?)calls[1]["request"]?["ExpectedPlanHash"]);
        Assert.Equal(project, (string?)calls[1]["expectedProjectFile"]);
        var wrong = tools.ExportTechnologyObjectsToDirectoryV4("PLC", "C:/Exports", dryRun: false,
            confirm: true, expectedPlanHash: hash, expectedProjectFile: "C:/Other.ap21");
        Assert.Equal("rejected-before-operation", (string?)wrong.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal(2, calls.Count);
    }

    [Theory]
    [InlineData("14sp1", "hardware-devices.HardwareCreateDevice")]
    [InlineData("15.1", "hardware-devices.HardwareCreateDevice")]
    [InlineData("16", "hardware-devices.HardwareCreateDevice")]
    [InlineData("17", "hardware-devices.HardwareCreateDevice")]
    [InlineData("18", "AddDeviceWithFallback")]
    [InlineData("19", "AddDeviceWithFallback")]
    [InlineData("20", "AddDeviceWithFallback")]
    [InlineData("21", "AddDeviceWithFallback")]
    public void Device_diagnostics_name_the_actual_release_operation(string release, string expected)
    {
        var method = typeof(PortedToolContract).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Single(m => m.Name == "WiredOperations");
        var parameters = method.GetParameters().Length == 1 ? new object[] { "CreateDevice" } : new object[] { "CreateDevice", release };
        Assert.Equal(expected, ((string[])method.Invoke(null, parameters)!)[0]);
    }

    [Fact]
    public async Task Forwarding_cost_is_reported_without_changing_any_timing_gate()
    {
        const int calls = 5000;
        var canonical = new SpyTool("ManagePlcUserGroup");
        var mapping = CompatibilityTools.Mappings["CreatePlcBlockGroup"];
        var source = new SpyTool("CreatePlcBlockGroup");
        var forward = new CompatibilityTools.Forwarder(source.ProtocolTool, canonical, mapping, "21");
        var aliasRequest = Request("CreatePlcBlockGroup", new JsonObject { ["softwarePath"] = "PLC", ["groupPath"] = "A/B" });
        var directRequest =
            Request("ManagePlcUserGroup", new JsonObject {
                ["softwarePath"] = "PLC", ["groupPath"] = "A/B", ["family"] = "blocks", ["action"] = "create", ["newName"] = "" });
        for (int i = 0; i < 100; i++) { await canonical.InvokeAsync(directRequest); await forward.InvokeAsync(aliasRequest); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < calls; i++) await canonical.InvokeAsync(directRequest);
        double before = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        for (int i = 0; i < calls; i++) await forward.InvokeAsync(aliasRequest);
        double after = watch.Elapsed.TotalMilliseconds;
        output
            .WriteLine($"Synthetic canonical call before={before / calls * 1000:F3} us; "
                + $"compatibility call after={after / calls * 1000:F3} us; n={calls}. No Siemens calls or timing assertion.");
        var raw = new McpServerTool[] { source, canonical };
        const int sessions = 200;
        watch.Restart();
        for (int i = 0; i < sessions; i++) _ = new List<McpServerTool>(raw);
        double rosterBefore = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        for (int i = 0; i < sessions; i++) { var roster = new List<McpServerTool>(raw); CompatibilityTools.Apply(roster, "21"); }
        output
            .WriteLine($"Synthetic roster before={rosterBefore / sessions * 1000:F3} us; "
                + $"with forwarding after={watch.Elapsed.TotalMilliseconds / sessions * 1000:F3} us; n={sessions}. Native time excluded.");
    }

    [Theory]
    [InlineData("createFromFile", "generateOption", "KeepOnError")]
    [InlineData("delete", "targetKind", "block")]
    [InlineData("delete", "confirm", "false")]
    public async Task Irrelevant_options_and_conflicting_confirmation_cannot_bypass_the_canonical_route(string action, string field, string value)
    {
        var source = new ActionSpy("ManagePlcExternalSources");
        var forward = new CompatibilityTools.ActionForwarder(source, new Dictionary<string, McpServerTool>(), "21");
        var arguments = new JsonObject { ["action"] = action, ["confirmDelete"] = true };
        arguments[field] = field == "confirm" ? JsonValue.Create(false) : JsonValue.Create(value);
        Assert.True((await forward.InvokeAsync(Request(source.ProtocolTool.Name, arguments))).IsError);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("CreateDevice")]
    [InlineData("CreateHardwareCatalogDevice")]
    [InlineData("CreatePlcBlockGroup")]
    [InlineData("CreatePlcTypeGroup")]
    [InlineData("DeleteEmptyPlcBlockGroup")]
    [InlineData("ManagePlcTagDefinition")]
    [InlineData("ManagePlcExternalSources")]
    public async Task Registered_forwarders_keep_recovery_guidance_on_admission_refusal(string name)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var shared = LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false);
        var services = FoundationHostPorts.CreateServices(worker, "21", () => new JsonObject(), null);
        using var pipeline = new enginehost::TiaMcp.FoundationHost.EngineHostPipeline("21", services, shared);
        var tool = pipeline.AllTools.Single(candidate => candidate.ProtocolTool.Name == name);
        var result = await tool.InvokeAsync(Request(name, new JsonObject { ["SnapshotReject"] = true, ["snapshotReject"] = true }));
        Assert.True(result.IsError);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Single(result.StructuredContent!["meta"]!["warnings"]!.AsArray(), warning => (string?)warning?["code"] == "RECOVERY_GUIDANCE");
    }

    private sealed class ActionSpy(string name) : McpServerTool
    {
        internal int Calls;
        internal JsonNode? Arguments;
        internal readonly CallToolResult Result = new();
        public override Tool ProtocolTool { get; } = new() {
            Name = name, InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
            ["action"] = new JsonObject { ["type"] = "string" }, ["kind"] = new JsonObject { ["type"] = "string" },
            ["plc"] = new JsonObject { ["type"] = "string" }, ["table"] = new JsonObject { ["type"] = "string" },
            ["softwarePath"] = new JsonObject { ["type"] = "string" }, ["tablePath"] = new JsonObject { ["type"] = "string" },
            ["name"] = new JsonObject { ["type"] = "string" }, ["externalSourceName"] = new JsonObject { ["type"] = "string" },
            ["dataType"] = new JsonObject { ["type"] = "string" }, ["address"] = new JsonObject { ["type"] = "string" }, ["value"] = new JsonObject {
                ["type"] = "string" },
            ["addressOrValue"] = new JsonObject { ["type"] = "string" }, ["properties"] = new JsonObject { ["type"] = "object" },
            ["dryRun"] = new JsonObject { ["type"] = "boolean" }, ["confirmDelete"] = new JsonObject { ["type"] = "boolean" },
            ["unitName"] = new JsonObject { ["type"] = "string" }, ["targetKind"] = new JsonObject { ["type"] = "string" },
            ["generateOption"] = new JsonObject { ["type"] = "string" }, ["filePath"] = new JsonObject { ["type"] = "string" },
            ["family"] = new JsonObject { ["type"] = "string" }, ["groupPath"] = new JsonObject { ["type"] = "string" },
            ["confirm"] = new JsonObject { ["type"] = "boolean" }, ["expectedPlanHash"] = new JsonObject { ["type"] = "string" }, ["expectedProjectFile"] =
                new JsonObject { ["type"] = "string" }
        } }) };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        { Calls++; Arguments = JsonSerializer.SerializeToNode(request.Params!.Arguments); return ValueTask.FromResult(Result); }
    }

    private sealed class SpyTool : McpServerTool
    {
        private readonly Tool tool;
        internal int Calls; internal string? Name; internal JsonNode? Arguments; internal CancellationToken Token; internal Exception? Failure;
        internal readonly CallToolResult Result = new() { IsError = true, StructuredContent = new JsonObject { ["error"] = "unchanged native diagnostics" } };
        internal SpyTool(string name)
        {
            var properties = new JsonObject();
            foreach (var parameter in new[] {
                    "preferredMlfb", "preferredVersion", "deviceName", "softwarePath", "groupPath", "family", "action", "newName", "expectedProjectFile",
                        "expectedPlanHash"
                })
                properties[parameter] = new JsonObject { ["type"] = "string" };
            properties["dryRun"] = new JsonObject { ["type"] = "boolean", ["default"] = true };
            properties["confirm"] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            tool = new Tool {
                Name = name, InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = properties, ["required"] = name == "CreateHardwareDevice" ? new JsonArray("preferredMlfb", "preferredVersion", "deviceName")
                    : new JsonArray("softwarePath", "groupPath") }) };
        }
        public override Tool ProtocolTool => tool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            Calls++; Name = request.Params!.Name; Arguments = JsonSerializer.SerializeToNode(request.Params.Arguments); Token = cancellationToken;
            if (Failure != null) throw Failure;
            return ValueTask.FromResult(Result);
        }
    }
}
