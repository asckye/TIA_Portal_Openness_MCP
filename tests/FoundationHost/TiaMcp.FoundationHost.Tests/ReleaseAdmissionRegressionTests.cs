extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class ReleaseAdmissionRegressionTests
{
    [Fact]
    public void Every_description_names_only_tools_registered_on_its_release()
    {
        var catalogs = new Dictionary<string, Dictionary<string, ModelContextProtocol.Protocol.Tool>>();
        foreach (var version in B3HostPortsTests.Releases)
        {
            string release = (string)version[0];
            using var fixture = new DiscoveryFixture(release);
            catalogs.Add(release, fixture.Tools.ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool));
        }
        var names = catalogs.Values.SelectMany(tools => tools.Keys).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var (release, tools) in catalogs)
        foreach (var (name, tool) in tools)
        foreach (var (location, description) in SchemaDescriptions(tool.InputSchema, name).Prepend((name, tool.Description)))
        {
            Assert.DoesNotMatch(@"\[\[[A-Za-z0-9,]+\|", description ?? "");
            var missing = Regex.Matches(description ?? "", @"\b[A-Za-z][A-Za-z0-9]*\b").Select(m => m.Value)
                .Where(name => names.Contains(name) && !tools.ContainsKey(name)).Distinct();
            failures.AddRange(missing.Select(reference => $"{release}/{location} references {reference}"));
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<(string, string?)> SchemaDescriptions(JsonElement node, string path)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
            foreach (var field in SchemaDescriptions(child, path)) yield return field;
        }
        if (node.ValueKind != JsonValueKind.Object) yield break;
        foreach (var member in node.EnumerateObject())
        {
            if (member.Name == "description" && member.Value.ValueKind == JsonValueKind.String)
                yield return (path, member.Value.GetString());
            else if (member.Name is "properties" or "$defs" or "patternProperties" or "dependentSchemas")
            {
                foreach (var property in member.Value.EnumerateObject())
                foreach (var field in SchemaDescriptions(property.Value, path + "/" + member.Name + "/" + property.Name)) yield return field;
            }
            else if (member.Name is not ("examples" or "default" or "enum" or "const"))
                foreach (var field in SchemaDescriptions(member.Value, path + "/" + member.Name)) yield return field;
        }
    }

    [Theory]
    [InlineData("14sp1", false, false, false)]
    [InlineData("18", true, false, false)]
    [InlineData("19", true, true, false)]
    [InlineData("20", true, true, true)]
    public void Scaffold_and_faceplate_guidance_follow_the_release(string release, bool catalog, bool unified, bool documents)
    {
        using var fixture = new DiscoveryFixture(release);
        string description = fixture.Tools.Single(tool => tool.ProtocolTool.Name == "BuildProjectScaffold").ProtocolTool.Description!;
        Assert.Equal(catalog, description.Contains("V18+"));
        string Parameter(string tool, string field) => fixture.Tools.Single(t => t.ProtocolTool.Name == tool).ProtocolTool.InputSchema
            .GetProperty("properties").GetProperty(field).GetProperty("description").GetString()!;
        Assert.Equal(catalog, Parameter("ManageHardwareUtilities", "typeIdentifier").Contains("SearchHardwareCatalog"));
        Assert.Equal(catalog, Parameter("ManagePlcSimAdvancedInstance", "cpuType").Contains("SearchHardwareCatalog"));
        Assert.Contains("only to action=delete", Parameter("ManageTechnologyObject", "confirm"));
        Assert.Contains("only to action=delete", Parameter("ManageTechnologyObject", "expectedProjectFile"));
        Assert.Equal(unified, description.Contains("Unified HMI"));
        Assert.Equal(documents, description.Contains("LAD S7DCL"));
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = false }, null);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, services,
            Array.Empty<ModelContextProtocol.Server.McpServerTool>());
        using var context = pipeline.EnterSession();
        var compatibility = new Host.EngineeringDiagnosticsTools().GetOpennessCompatibilityV4().StructuredContent!;
        Assert.True((bool?)compatibility["ok"], compatibility.ToJsonString());
        string guidance = (string)compatibility["data"]!["evidence"]!["faceplateTypeInternalAuthoring"]!;
        Assert.Equal(!unified, guidance.Contains("Unified HMI is unavailable"));
    }

    [Theory]
    [InlineData("ImportHmiScreensFromDirectory")]
    [InlineData("ImportHmiTagTablesFromDirectory")]
    public async Task Directory_import_refusal_uses_the_public_directory_parameter(string name)
    {
        using var fixture = new DiscoveryFixture("14sp1");
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var body = await fixture.Call(name, new { softwarePath = "HMI", sourceDirectory = missing, folderPath = "", dryRun = true });
        Assert.Equal("/sourceDirectory", (string?)body["error"]?["details"]?["parameter"]);
        Assert.Contains("directory does not exist", (string?)body["error"]?["message"]);
    }

    [Theory]
    [InlineData("contextLines", 21, "0 and 20")]
    [InlineData("limit", 501, "1 and 500")]
    public async Task Comparison_rejects_an_out_of_range_field(string parameter, int value, string range)
    {
        using var fixture = new DiscoveryFixture("14sp1");
        var body = await fixture.Call("ComparePlcBlockDocuments", new JsonObject { [parameter] = value });
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("/" + parameter, (string?)body["error"]?["details"]?["parameter"]);
        Assert.Contains(range, (string?)body["error"]?["message"]);
    }

    [Fact]
    public async Task Recovery_uses_the_registered_inner_tool_and_its_example()
    {
        using var fixture = new DiscoveryFixture("14sp1");
        const string name = "AnalyzePlcReferences";
        var body = await fixture.Call("CallTool", new { name, arguments = new { sourceDirectory = @"\\localhost\fixture" } });
        var hint = body["meta"]!["warnings"]!.AsArray().Single(row => (string?)row!["code"] == "RECOVERY_GUIDANCE")!["details"]!;
        Assert.Equal(name, (string?)hint["getToolUsage"]?["toolName"]);
        Assert.NotNull(hint["exampleArguments"]?["sourceDirectory"]);
    }

    [Theory]
    [InlineData(false, "", "CONFIRMATION_REQUIRED")]
    [InlineData(true, "", "INVALID_ARGUMENT")]
    [InlineData(true, "C:/Other.ap15_1", "IDENTITY_MISMATCH")]
    [InlineData(true, "C:/Bound.ap15_1", null)]
    public void Technology_delete_binds_the_reviewed_project_and_is_destructive(bool confirm, string expected, string? error)
    {
        using var fixture = new HardwareAddressingAdmissionTests.SessionFixture(new HardwareAddressingAdmissionTests.UnboundWorker(), "15.1");
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, input) =>
        {
            Assert.True((bool?)input["confirm"]);
            Assert.Equal(expected, (string?)input["expectedProjectFile"]);
            calls++;
            return new JsonObject { ["Message"] = "Deleted", ["Meta"] = new JsonObject { ["success"] = true, ["verifiedAbsent"] = true } };
        }, () => true, () => "C:/Bound.ap15_1");
        var result = new Host.PlcTechnologyTools(service).ManageTechnologyObjectV4("PLC", "Axis", "delete", dryRun: false,
            confirm: confirm, expectedProjectFile: expected);
        Assert.Equal(error, (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(error == null ? 1 : 0, calls);
        foreach (string action in new[] { "delete", "create", "setParameter", "read" })
        {
            var card = TiaOpenness.Shared.PendingApproval.Create("foundation", "15.1", "ManageTechnologyObject",
                JsonSerializer.Serialize(new { action, dryRun = false }), null, 10);
            Assert.Equal(action == "delete" ? "destructive" : "write", card.Risk);
        }
    }

    [Fact]
    public async Task Technology_export_example_uses_the_full_software_path()
    {
        using var fixture = new DiscoveryFixture("14sp1");
        var usage = await fixture.Call("GetToolUsage", new { toolName = "ExportTechnologyObjectsToDirectory" });
        Assert.Equal("devices/PLC_1/PLC_1", (string?)usage["data"]?["example"]?["request"]?["params"]?["arguments"]?["softwarePath"]);
    }

    [Fact]
    public void Reuse_plan_reflects_the_analyzer_verdict_for_an_existing_nonlibrary_directory()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        var plan = new Host.HmiOfflineTools().PlanGlobalLibraryTemplateReuse(fixture.Root).Data!;
        Assert.False((bool?)plan["analysis"]?["ok"]);
        Assert.Equal((bool?)plan["analysis"]?["ok"], (bool?)plan["offlineAnalysisOk"]);
    }

    [Fact]
    public async Task Unknown_argument_lists_only_the_declared_names_without_disclosing_the_key()
    {
        using var fixture = new DiscoveryFixture("14sp1");
        const string name = "ListPlcBlocks";
        var schema = fixture.Tools.Single(tool => tool.ProtocolTool.Name == name).ProtocolTool.InputSchema;
        var declared = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order();
        var body = await fixture.Call(name, new { softwarePath = "PLC", privateKey = "privateValue" });
        var details = body["error"]!["details"]!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
        Assert.Equal("", (string?)details["parameter"]);
        Assert.Equal(declared, details["allowedValues"]!.AsArray().Select(value => (string)value!).Order());
        Assert.DoesNotContain("privateKey", body.ToJsonString());
        Assert.DoesNotContain("privateValue", body.ToJsonString());
    }

    [Theory]
    [InlineData("PreviewToolCall")]
    [InlineData("CallTool")]
    [InlineData("FindTools")]
    public async Task Unknown_key_before_invalid_limit_reports_the_declared_parameter(string entry)
    {
        using var fixture = new DiscoveryFixture("14sp1");
        var arguments = new { privateKey = 1, limit = 0 };
        var body = await fixture.Call(entry, entry == "FindTools" ? arguments : (object)new { name = "FindTools", arguments });
        var error = entry == "PreviewToolCall" ? body["data"]?["validationError"] : body["error"];
        Assert.Equal("/limit", (string?)error?["details"]?["parameter"]);
        Assert.Equal("integer", (string?)error?["details"]?["expected"]);
        var pointers = error!["details"]!["parameters"]!.AsArray().Select(value => (string?)value).ToArray();
        Assert.Contains("/limit", pointers);
        Assert.Contains("", pointers);
        Assert.DoesNotContain("privateKey", body.ToJsonString());
    }

    [Fact]
    public void Preview_identifies_the_declared_spelling_and_lists_accepted_names_for_an_unknown_parameter()
    {
        using var fixture = new PreviewHostFixture("14sp1");
        using var session = fixture.Pipeline.EnterSession();
        var args = new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(new { softwarepath = "PLC", privateKey = 1 }));
        var data = Host.McpServer.PreviewToolCall("ListPlcBlocks", args).StructuredContent!["data"]!;
        Assert.Equal("/softwarePath", (string?)data["validationError"]?["details"]?["parameter"]);
        var unknown = data["validationErrors"]!.AsArray().Single(e => (string?)e!["details"]?["parameter"] == "");
        Assert.Contains("softwarePath", unknown!["details"]!["allowedValues"]!.AsArray().Select(v => (string?)v));
        Assert.DoesNotContain("privateKey", data.ToJsonString());
    }
}
