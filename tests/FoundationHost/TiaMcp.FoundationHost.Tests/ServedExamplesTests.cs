extern alias toolpipeline;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using Examples = toolpipeline::TiaMcpServer.ModelContextProtocol.ToolExamples;
using Preflight = TiaMcpServer.ModelContextProtocol.PreflightLogic;
using Xunit;

[Collection(BundleSelectionCollection.Name)]
public sealed class ServedExamplesTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public static IEnumerable<object[]> Releases => B3HostPortsTests.Releases;

    public static IEnumerable<object[]> LargeSchemas => from version in Releases
        let release = (string)version[0]
        from name in new[] { "CompareUnifiedGraphicSelections", "BuildAndImportPlcArtifact" }
        where TiaOpenness.Shared.ToolUsageCatalog.IsRegistered(name, release)
        select new object[] { release, name };

    [Theory, MemberData(nameof(Releases))]
    public async Task Every_registered_operation_serves_a_call_example_unless_refused_by_the_release_guard(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var failures = new List<string>();
        int checkedOperations = 0;
        int unavailable = 0;
        foreach (var tool in fixture.Tools)
        {
            string name = tool.ProtocolTool.Name;
            var schema = tool.ProtocolTool.InputSchema;
            var properties = schema.GetProperty("properties");
            var usage = await fixture.Call("GetToolUsage", new { toolName = name });
            if (usage["data"]?["operations"] is not JsonArray operations)
            {
                failures.Add($"{release}/{name}: missing operations list. {usage["error"]}");
                continue;
            }
            string[] listed = operations.Select(row => (string)row!["operation"]!).ToArray();
            string? selector = properties.TryGetProperty("action", out var field) ? "action"
                : name != "GetToolUsage" && properties.TryGetProperty("operation", out field) ? "operation" : null;
            var choices = listed;
            if (selector != null)
            {
                var advertised = field.TryGetProperty("enum", out var alternatives)
                    ? alternatives.EnumerateArray().Select(value => value.GetString()!).Where(value => value.Length > 0).ToArray()
                    : Array.Empty<string>();
                if (advertised.Length == 0 || !listed.SequenceEqual(advertised))
                    failures.Add($"{release}/{name}: operation list differs from {selector}.");
                choices = advertised.Union(listed, StringComparer.Ordinal).ToArray();
            }
            var validator = new InputSchema(schema);
            foreach (string operation in choices)
            {
                checkedOperations++;
                var selected = await fixture.Call("GetToolUsage", new { toolName = name, operation });
                var example = selected["data"]?["example"];
                var request = example?["request"];
                var args = request?["params"]?["arguments"] as JsonObject;
                var error = args == null ? "Missing arguments." : validator.Validate(JsonSerializer.SerializeToElement(args), "arguments")?.Message;
                string? kind = (string?)example?["kind"];
                var option = operations.SingleOrDefault(row => (string?)row!["operation"] == operation);
                string? selectedField = selector ?? (string?)option?["selector"];
                if ((string?)request?["method"] != "tools/call" || (string?)request?["params"]?["name"] != name)
                    error = $"Expected a tools/call request for {name}. {error}";
                if (selectedField != null && (string?)args?[selectedField] != operation)
                    error = $"Expected {selectedField}={operation}. {error}";
                bool refused = false;
                bool hasGuard = option?["refusedByReleaseGuard"] is JsonValue guard && guard.TryGetValue(out refused);
                bool guardedExample = kind == "unavailable" && refused && !string.IsNullOrWhiteSpace((string?)example?["releaseProblem"]);
                bool callExample = kind == "parameterized-call-example" && example?["releaseProblem"] == null;
                if (guardedExample) unavailable++;
                if (!hasGuard || (!callExample && !guardedExample) || error != null)
                    failures.Add($"{release}/{name}/{operation}: {kind ?? "missing example"}, refusedByReleaseGuard={option?["refusedByReleaseGuard"]}. " +
                        $"{example?["releaseProblem"] ?? selected["error"]} {error}");
            }
        }
        output.WriteLine($"{release}: {fixture.Tools.Count} tools, {checkedOperations} operations, {unavailable} unavailable under the release guard.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("19", "ManageUmcUsers", "createOffline", true)]
    [InlineData("20", "ManageUmcUsers", "createOffline", false)]
    [InlineData("18", "ManagePlcUserGroup", "rename", true)]
    [InlineData("19", "ManagePlcUserGroup", "rename", false)]
    [InlineData("20", "ManageDeviceServiceObjects", "setDefault", true)]
    [InlineData("20", "ManageDeviceServiceObjects", "export", false)]
    [InlineData("16", "ManagePlcCertificate", "export", false)]
    [InlineData("19", "ManageSyslogServers", "read", false)]
    [InlineData("21", "ManageTechnologyObject", "create", false)]
    [InlineData("21", "ExchangeCfcCharts", "selectiveExport", true)]
    [InlineData("21", "ExchangeCfcCharts", "import", false)]
    public void Operation_guard_is_independent_of_a_rejected_example(string release, string name, string operation, bool refused)
    {
        using var fixture = new DiscoveryFixture(release);
        var schema = fixture.Tools.Single(tool => tool.ProtocolTool.Name == name).ProtocolTool.InputSchema;
        var usage = TiaOpenness.Shared.ToolUsageCatalog.Describe(name, release, "plc-foundation", "", JsonNode.Parse(schema.GetRawText())!.AsObject(),
            operation: operation, callProblem: _ => "This example's arguments were refused.");
        Assert.Equal(refused ? "unavailable" : "parameterized-call-example", (string?)usage["example"]?["kind"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)usage["example"]?["releaseProblem"]));
        var choice = usage["operations"]!.AsArray().Single(row => (string?)row!["operation"] == operation);
        Assert.Equal(refused, (bool?)choice!["refusedByReleaseGuard"]);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Application_test_set_import_example_passes_exchange_validation(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var usage = await fixture.Call("GetToolUsage", new { toolName = "ExchangeTestSuiteCase", operation = "importTestSets" });
        var args = usage["data"]!["example"]!["request"]!["params"]!["arguments"]!;
        var plan = TiaMcpServer.Siemens.TestSuiteLogic.ValidateExchangeRequest((string)args["category"]!, (string)args["action"]!,
            (string)args["name"]!, (string)args["filePath"]!, (string?)args["importOptions"] ?? "None", (string?)args["loadOptions"] ?? "",
            (string)args["kind"]!, (bool)args["dryRun"]!);
        Assert.Equal("testSet", plan.Kind);
        Assert.False(plan.Writes);
    }

    [Theory, MemberData(nameof(LargeSchemas))]
    public async Task Large_usage_schemas_preserve_the_registered_schema(string release, string name)
    {
        using var fixture = new DiscoveryFixture(release);
        var result = await fixture.Call("GetToolUsage", new { toolName = name });
        Assert.True((bool?)result["ok"], result.ToJsonString());
        var tool = fixture.Tools.Single(tool => tool.ProtocolTool.Name == name);
        var expected = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText());
        var schema = result["data"]!["inputSchema"]!;
        Assert.True(JsonNode.DeepEquals(expected, schema));
        output.WriteLine($"{release}/{name}: {result.ToJsonString().Length} characters");
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Discovery_and_preview_serve_schema_valid_release_examples(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var tools = fixture.Tools;
        var lookup = tools.ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
        var found = await fixture.Call("FindTools", new { limit = int.MaxValue });
        var lines = found["data"]!["items"]!.AsArray();
        Assert.Equal(tools.Count * 3, lines.Count);
        for (int i = 0; i < lines.Count; i += 3)
        {
            string signature = (string)lines[i]!;
            string name = signature[..signature.IndexOf('(')];
            string line = (string)lines[i + 2]!;
            var args = ParseExample(line);
            var error = new InputSchema(lookup[name].ProtocolTool.InputSchema).Validate(args, "arguments");
            Assert.True(error == null, release + "/" + name + ": " + error?.Message + " " + args);
            if (release != "21") Assert.DoesNotContain(".ap21", args.GetRawText(), StringComparison.OrdinalIgnoreCase);
            var preview = await fixture.Call("PreviewToolCall", new { name, arguments = args });
            var example = preview["data"]?["example"];
            Assert.True(example != null, release + "/" + name + ": " + preview.ToJsonString());
            Assert.Null(new InputSchema(lookup[name].ProtocolTool.InputSchema).Validate(JsonSerializer.SerializeToElement(example), "arguments"));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(args.GetRawText()), example), release + "/" + name);
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Handover_is_explicit_and_online_target_examples_require_binding(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var connect = (await fixture.Call("GetToolUsage", new { toolName = "ConnectPortal" }))["data"]!;
        Assert.False((bool?)connect["parameterSources"]?["takeOver"]?["exampleValue"]);
        Assert.Contains("idle", (string?)connect["inputSchema"]?["properties"]?["takeOver"]?["description"]);
        foreach (var (name, address) in new[] { ("ConnectOnlinePlc", "ipAddress"), ("DownloadPlc", "targetIpAddress") })
        {
            if (!fixture.Tools.Any(tool => tool.ProtocolTool.Name == name)) continue;
            var data = (await fixture.Call("GetToolUsage", new { toolName = name }))["data"]!;
            foreach (string field in new[] { address, "pgPcInterface" })
            {
                Assert.True((bool?)data["parameterSources"]?[field]?["requiresBinding"]);
                Assert.StartsWith("<", (string?)data["example"]?["request"]?["params"]?["arguments"]?[field]);
            }
        }
    }

    [Fact]
    public async Task V19_library_updates_inherit_release_arguments_and_unavailable_actions_explain_the_gap()
    {
        using var fixture = new DiscoveryFixture("19");
        var update = await fixture.Call("GetToolUsage", new { toolName = "ManageLibraryType", operation = "update" });
        var args = update["data"]!["example"]!["request"]!["params"]!["arguments"]!;
        Assert.NotNull(args["properties"]?["Name"]);
        var offline = await fixture.Call("GetToolUsage", new { toolName = "ManageUmcUsers", operation = "createOffline" });
        var example = offline["data"]!["example"]!;
        Assert.Equal("unavailable", (string?)example["kind"]);
        Assert.Equal("ManageUmcUsers", (string?)example["request"]?["params"]?["name"]);
        Assert.Equal("createOffline", (string?)example["request"]?["params"]?["arguments"]?["action"]);
        Assert.Contains("CreateOfflineUmcUser", (string?)example["validation"]);
    }

    [Fact]
    public async Task V16_safety_examples_select_the_available_setters()
    {
        using var fixture = new DiscoveryFixture("16");
        var data = (await fixture.Call("GetToolUsage", new { toolName = "ManageSafetyGlobalSettings" }))["data"]!;
        Assert.Equal("update", (string?)data["example"]?["request"]?["params"]?["arguments"]?["action"]);
        var read = data["operations"]!.AsArray().Single(row => (string?)row!["operation"] == "read");
        Assert.Contains("getters", (string?)read!["variants"]![0]!["reason"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Compile_examples_follow_the_unit_boundary_without_inventing_an_action(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        foreach (string name in new[] { "CompilePlcSoftware", "CompilePlcDiagnostics" })
        {
            var usage = await fixture.Call("GetToolUsage", new { toolName = name });
            var data = usage["data"]!;
            Assert.Empty(data["operations"]!.AsArray());
            var names = data["namedExamples"]!.AsArray().Select(node => (string)node!).ToArray();
            Assert.Contains("block", names);
            Assert.Equal(release is not ("14sp1" or "15.1"), names.Contains("unit"));
            Assert.Equal(names.Contains("unit"), names.Contains("blockInUnit"));
            var unknown = await fixture.Call("GetToolUsage", new { toolName = name, operation = "missing" });
            Assert.Equal("INVALID_ARGUMENT", (string?)unknown["error"]?["code"]);
            Assert.Contains(string.Join(", ", names), (string?)unknown["error"]?["message"]);
        }
    }

    public static IEnumerable<object[]> ApplyExamples => from version in Releases
        let release = (string)version[0]
        from example in new[] { ("ManageOnlineDriveFunctions", "performRamToRomCopy", "expectedPlanHash"),
            ("ManageSivarcScreenLayout", "import", "allowRemoveScreenItems") }
        where TiaOpenness.Shared.ToolUsageCatalog.IsRegistered(example.Item1, release)
        select new object[] { release, example.Item1, example.Item2, example.Item3 };

    [Theory, MemberData(nameof(ApplyExamples))]
    public async Task Preview_and_apply_arguments_are_served_for_the_same_operation(string release, string toolName, string operation, string applyKey)
    {
        using var fixture = new DiscoveryFixture(release);
        var usage = await fixture.Call("GetToolUsage", new { toolName, operation });
        var example = usage["data"]!["example"]!;
        var preview = example["request"]!["params"]!["arguments"]!.AsObject();
        Assert.Equal(operation, (string?)preview["action"]);
        Assert.True((bool?)preview["dryRun"]);
        Assert.False(preview.ContainsKey(applyKey));
        var patch = example["execution"]!["arguments"]!.AsObject();
        Assert.False((bool?)patch["dryRun"]);
        Assert.True(patch.ContainsKey(applyKey));
        var apply = preview.DeepClone().AsObject();
        foreach (var field in patch) apply[field.Key] = field.Value?.DeepClone();
        var schema = fixture.Tools.Single(tool => tool.ProtocolTool.Name == toolName).ProtocolTool.InputSchema;
        Assert.Null(new InputSchema(schema).Validate(JsonSerializer.SerializeToElement(apply), "arguments"));
    }

    [Theory, InlineData("update"), InlineData("setServiceGroupName"), InlineData("createService"), InlineData("deleteService")]
    public async Task Device_service_operations_keep_the_family_that_accepts_the_example(string operation)
    {
        using var fixture = new DiscoveryFixture("21");
        var usage = await fixture.Call("GetToolUsage", new { toolName = "ManageDeviceServiceObjects", operation });
        var args = usage["data"]!["example"]!["request"]!["params"]!["arguments"]!;
        Assert.Equal("certificateServices", (string?)args["family"]);
        toolpipeline::TiaMcpServer.Siemens.DeviceServiceObjectRules.ValidateServiceObjectRequest(
            (string)args["family"]!, operation, (string?)args["name"] ?? "", args["properties"]?.AsObject() ?? new JsonObject(),
            (string?)args["filePath"] ?? "", (bool)args["confirmChange"]!, (bool)args["dryRun"]!);
        if (operation == "update") Assert.Equal(30, (int?)args["properties"]?["RemainingCertificateLifetime"]);
        else Assert.False(string.IsNullOrEmpty((string?)args["name"]));
    }

    [Fact]
    public async Task Guidance_paging_counts_every_served_document_and_line()
    {
        using var fixture = new DiscoveryFixture("21");
        var listing = await fixture.Call("GetOpennessGuidance", new { limit = 1 });
        var data = listing["data"]!;
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        int total = Directory.GetFiles(Path.Combine(root, "reference/siemens-openness/skills"), "*.md", SearchOption.AllDirectories).Length;
        Assert.Equal(total, (int?)data["total"]);
        Assert.False((bool?)data["dataComplete"]);
        var last = await fixture.Call("GetOpennessGuidance", new { offset = total - 1, limit = 1 });
        Assert.True((bool?)last["data"]?["dataComplete"]);
        string document = (string)data["documents"]![0]!["id"]!;
        var lines = await fixture.Call("GetOpennessGuidance", new { document, limit = 1 });
        Assert.False((bool?)lines["data"]?["dataComplete"]);
        var end = await fixture.Call("GetOpennessGuidance", new { document, offset = (int)lines["data"]!["total"]! - 1, limit = 1 });
        Assert.True((bool?)end["data"]?["dataComplete"]);
    }

    private static JsonElement ParseExample(string line)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line[line.IndexOf('{')..]));
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Generation_machine_documents_follow_the_selected_release(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        foreach (string name in new[] { "PlanProjectGeneration", "ManageMachineDescription" })
        {
            var found = await fixture.Call("FindTools", new { query = name, limit = 1 });
            var arguments = ParseExample((string)found["data"]!["items"]![2]!);
            if (name == "PlanProjectGeneration")
                Assert.Equal(release, arguments.GetProperty("machine").GetProperty("target").GetProperty("release").GetString());
            else
            {
                Assert.Equal("validate", arguments.GetProperty("action").GetString());
                Assert.True(arguments.TryGetProperty("machine", out var machine));
                Assert.True(arguments.GetProperty("dryRun").GetBoolean());
                AssertMinimalMachine(JsonNode.Parse(machine.GetRawText())!.AsObject(), release);
                var validation = await fixture.Call(name, arguments);
                Assert.True((bool?)validation["ok"], validation.ToJsonString());
                Assert.True((bool?)validation["data"]?["valid"]);
                Assert.False((bool?)validation["data"]?["executed"]);
                Assert.False(string.IsNullOrEmpty((string?)validation["data"]?["machineHash"]));
            }
            var preview = await fixture.Call("PreviewToolCall", new { name, arguments });
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(arguments.GetRawText()), preview["data"]?["example"]));
            if (name == "PlanProjectGeneration") Assert.Equal(release, (string?)preview["data"]?["example"]?["machine"]?["target"]?["release"]);
        }
        foreach (string operation in new[] { "validate", "export" })
        {
            var usage = await fixture.Call("GetToolUsage", new { toolName = "ManageMachineDescription", operation });
            var arguments = usage["data"]!["example"]!["request"]!["params"]!["arguments"]!;
            Assert.Equal(release, (string?)arguments["machine"]?["target"]?["release"]);
            Assert.Equal(21, arguments["machine"]!["devices"]!.AsArray().Count);
        }
    }

    private static void AssertMinimalMachine(JsonObject machine, string release)
    {
        Assert.Equal(8, machine.Count);
        Assert.Equal("tiamcp.machine/1", (string?)machine["schema"]);
        Assert.Equal(2, machine["machine"]!.AsObject().Count);
        Assert.Single(machine["machine"]!["name"]!.AsObject());
        Assert.Equal(2, machine["standard"]!.AsObject().Count);
        Assert.Equal(2, machine["target"]!.AsObject().Count);
        Assert.Equal(release, (string?)machine["target"]!["release"]);
        Assert.Equal("new", (string?)machine["target"]!["project"]!["mode"]);
        Assert.Equal(3, machine["target"]!["project"]!.AsObject().Count);
        var station = Assert.Single(machine["stations"]!.AsArray());
        Assert.Equal(2, station!.AsObject().Count);
        Assert.Empty(machine["topology"]!.AsArray());
        Assert.Empty(machine["devices"]!.AsArray());
        Assert.Single(machine["options"]!.AsObject());
        Assert.Single(machine["options"]!["languages"]!.AsArray());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Complete_machine_operations_do_not_inherit_template_arguments(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        const string name = "ManageMachineDescription";
        var maintained = TiaOpenness.Shared.ToolUsageCatalog.ServedCall(name, release)!;
        var schema = new InputSchema(fixture.Tools.Single(t => t.ProtocolTool.Name == name).ProtocolTool.InputSchema);
        foreach (var operation in maintained["operations"]!.AsObject())
        {
            Assert.Equal("complete", (string?)operation.Value!["argumentsMode"]);
            var maintainedArguments = operation.Value["arguments"]!.AsObject();
            Assert.Null(schema.Validate(JsonSerializer.SerializeToElement(maintainedArguments), "arguments"));
            Assert.Null(TiaMcp.Logic.V4.CanonicalToolParameters.ValidateAliases(name, maintainedArguments));
            // importJson labels a format variant of action=import, not an additional action selector.
            string action = (string)maintainedArguments["action"]!;
            var usage = await fixture.Call("GetToolUsage", new { toolName = name, operation = action });
            var arguments = usage["data"]!["example"]!["request"]!["params"]!["arguments"]!.AsObject();
            Assert.True(JsonNode.DeepEquals(maintained["operations"]![action]!["arguments"], arguments), release + "/" + operation.Key);
            Assert.Null(schema.Validate(JsonSerializer.SerializeToElement(arguments), "arguments"));
            Assert.Null(TiaMcp.Logic.V4.CanonicalToolParameters.ValidateAliases(name, arguments));
            if (operation.Key is "import" or "importJson" or "validate") Assert.False(arguments.ContainsKey("exportDirectory"));
        }
    }

    [Theory, InlineData("array", JsonValueKind.Array), InlineData("object", JsonValueKind.Object)]
    public void Placeholder_keeps_the_declared_shape(string kind, JsonValueKind expected)
    {
        var example = Examples.Derive("UnknownTool", new[] { new Preflight.ParameterSpec("value", kind, true, null, "") });
        using var doc = JsonDocument.Parse(example.ArgumentsJson);
        Assert.Equal(expected, doc.RootElement.GetProperty("value").ValueKind);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Maintained_data_corrects_the_reported_bindings(string release)
    {
        JsonObject Args(string name) => TiaOpenness.Shared.ToolUsageCatalog.ServedCall(name, release)!["arguments"]!.AsObject();
        Assert.EndsWith("SystemDiagnostics.dat", (string?)Args("ExchangeSystemDiagnosticsSettings")["filePath"]);
        Assert.Equal(@"C:\Examples\exports", (string?)Args("ExportPlcType")["exportDirectory"]);
        foreach (string name in new[] { "UploadStationFromPlc", "ScanAccessibleDevices" })
            Assert.Equal("<exact PG/PC interface name from ListTransferRoutes>", (string?)Args(name)["pgPcInterface"]);
        Assert.Equal(release is "20" or "21" ? "read" : "write", (string?)Args("GetDriveParameters")["source"]);
        Assert.Equal("Digital", (string?)Args("SetDeviceItemChannel")["channelType"]);
        Assert.Equal("Input", (string?)Args("SetDeviceItemChannel")["channelIoType"]);
        Assert.Equal("PROFINET", (string?)Args("EnsureSubnet")["subnetType"]);
        Assert.Equal("DB_Flags", (string?)Args("ImportPlcBlockDocuments")["fileNameWithoutExtension"]);
        Assert.False((bool?)Args("ImportPlcBlockDocuments")["overwrite"]);
        Assert.Contains("intended LAD or DB blocks", (string?)Args("ExportPlcBlocksDocuments")["groupPath"]);
        foreach (string name in new[] { "GetClassicHmiScreenTree", "ManageClassicHmiScreenObject", "ManageClassicHmiFolder",
            "ListClassicHmiScripts", "ManageClassicHmiScript", "ManageClassicHmiCycle", "ManageClassicHmiTextGraphicList", "ManageClassicHmiGraphic" })
            Assert.Equal("<exact Classic HMI softwarePath from GetProjectTree>", (string?)Args(name)["softwarePath"]);
    }

    [Fact]
    public void Both_maintained_profiles_keep_the_corrected_sample_values()
    {
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var profiles = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-examples/calls.json")))!["profiles"]!.AsObject();
        foreach (var profile in profiles)
        {
            JsonObject Args(string name) => profile.Value![name]!["arguments"]!.AsObject();
            var machineDescription = profile.Value!["ManageMachineDescription"]!;
            Assert.Equal("validate", (string?)Args("ManageMachineDescription")["action"]);
            Assert.True((bool?)Args("ManageMachineDescription")["dryRun"]);
            AssertMinimalMachine(Args("ManageMachineDescription")["machine"]!.AsObject(), "{release}");
            Assert.Equal("{release}", (string?)Args("PlanProjectGeneration")["machine"]?["target"]?["release"]);
            foreach (string operation in new[] { "validate", "export" })
            {
                var machine = machineDescription["operations"]![operation]!["arguments"]!["machine"]!;
                Assert.Equal("{release}", (string?)machine["target"]?["release"]);
                Assert.Equal(21, machine["devices"]!.AsArray().Count);
            }
            var diagnostics = Args("ExchangeSystemDiagnosticsSettings");
            Assert.EndsWith("SystemDiagnostics.dat", (string?)diagnostics["filePath"]);
            foreach (var operation in profile.Value!["ExchangeSystemDiagnosticsSettings"]!["operations"]!.AsObject())
                Assert.EndsWith("SystemDiagnostics.dat", (string?)(operation.Value?["arguments"]?["filePath"] ?? diagnostics["filePath"]));
            Assert.Equal(@"C:\Examples\exports", (string?)Args("ExportPlcType")["exportDirectory"]);
            foreach (string name in new[] { "UploadStationFromPlc", "ScanAccessibleDevices" })
                Assert.Equal("<exact PG/PC interface name from ListTransferRoutes>", (string?)Args(name)["pgPcInterface"]);
            Assert.Equal("read", (string?)Args("GetDriveParameters")["source"]);
            Assert.Equal("Digital", (string?)Args("SetDeviceItemChannel")["channelType"]);
            Assert.Equal("Input", (string?)Args("SetDeviceItemChannel")["channelIoType"]);
            Assert.Equal("PROFINET", (string?)Args("EnsureSubnet")["subnetType"]);
            Assert.Equal("DB_Flags", (string?)Args("ImportPlcBlockDocuments")["fileNameWithoutExtension"]);
            Assert.False((bool?)Args("ImportPlcBlockDocuments")["overwrite"]);
            Assert.Contains("intended LAD or DB blocks", (string?)Args("ExportPlcBlocksDocuments")["groupPath"]);
            foreach (string name in new[] { "GetClassicHmiScreenTree", "ManageClassicHmiScreenObject", "ManageClassicHmiFolder",
                "ListClassicHmiScripts", "ManageClassicHmiScript", "ManageClassicHmiCycle", "ManageClassicHmiTextGraphicList", "ManageClassicHmiGraphic" })
                Assert.Equal("<exact Classic HMI softwarePath from GetProjectTree>", (string?)Args(name)["softwarePath"]);
        }
    }
}
