using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

public sealed class ServedExampleRowsTests
{
    private static string Root => TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
    private static JsonObject Calls => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "reference/tool-examples/calls.json")))!.AsObject();
    private static readonly string[] VerifiedTools = {
        "ListIoSystems", "EnsureSubnet", "ManageUnifiedEngineeringObject", "ExchangeSystemDiagnosticsSettings", "ExportPlcType",
        "UploadStationFromPlc", "ScanAccessibleDevices", "ManageCommunicationConnection", "ManageTeamcenterDataset",
        "ImportPlcTagTablesFromDirectory", "WritePlcSclSourceFile", "GetObjectIdentifier", "GetLibraryType", "CompareLibraries",
        "RenderPlcBlockDocument", "DescribeService", "InstantiatePlcTemplates", "PlanProjectGeneration", "ApplyGenerationPlan"
    };

    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();
    public static IEnumerable<object[]> VerifiedRows() => from release in Releases()
        from entry in ToolUsageCatalog.ProfileEntries((string)release[0])
        let name = (string)entry!["currentName"]!
        where VerifiedTools.Contains(name)
        select new object[] { release[0], name };
    public static IEnumerable<object[]> EvidenceRows() => from profile in new[] { "plc-foundation", "full-engine" }
        from name in VerifiedTools.Except(new[] { "ApplyGenerationPlan" })
        where Calls["profiles"]?[profile]?[name] != null
        select new object[] { profile, name };

    [Theory, MemberData(nameof(Releases))]
    public void Served_examples_use_canonical_parameters(string release)
    {
        foreach (var entry in ToolUsageCatalog.ProfileEntries(release))
        {
            string name = (string)entry!["currentName"]!;
            var args = ToolUsageCatalog.ServedCall(name, release)!["arguments"]!.AsObject();
            foreach (var alias in CanonicalToolParameters.Aliases(name))
                Assert.False(args.ContainsKey(alias.Legacy), release + "/" + name + "/" + alias.Legacy);
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Served_examples_match_the_maintained_row(string release)
    {
        var calls = Calls;
        foreach (var entry in ToolUsageCatalog.ProfileEntries(release))
        {
            string name = (string)entry!["currentName"]!;
            var args = calls["profiles"]![ToolUsageCatalog.ExampleSource(name, release)]![name]!["arguments"]!.DeepClone().AsObject();
            if ((bool?)args["dryRun"] == false)
            {
                args["dryRun"] = true;
                args.Remove("expectedPlanHash"); args.Remove("expectedProjectFile");
            }
            if ((bool?)args["confirm"] == true) args["confirm"] = false;
            var expected = JsonNode.Parse(args.ToJsonString().Replace("{extension}", (string)calls["releaseExtensions"]![release]!)
                .Replace("{release}", release).Replace("{major}", release == "14sp1" ? "14" : release == "15.1" ? "15" : release));
            Assert.True(JsonNode.DeepEquals(expected, ToolUsageCatalog.ServedCall(name, release)!["arguments"]), release + "/" + name);
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Served_examples_are_previews(string release)
    {
        var calls = Calls;
        foreach (var entry in ToolUsageCatalog.ProfileEntries(release))
        {
            string name = (string)entry!["currentName"]!;
            var args = ToolUsageCatalog.ServedCall(name, release)!["arguments"]!.AsObject();
            Assert.False((bool?)args["dryRun"] == false, release + "/" + name);
            Assert.False((bool?)args["confirm"] == true, release + "/" + name);
            // ApplyGenerationPlan already has dryRun=true and requires its identity and hash even in a preview.
            if ((bool?)calls["profiles"]![ToolUsageCatalog.ExampleSource(name, release)]![name]!["arguments"]!["dryRun"] != false) continue;
            Assert.False(args.ContainsKey("expectedPlanHash"), release + "/" + name);
            Assert.False(args.ContainsKey("expectedProjectFile"), release + "/" + name);
        }
        var apply = ToolUsageCatalog.ServedCall("ApplyGenerationPlan", release)!["arguments"]!.AsObject();
        Assert.True((bool?)apply["dryRun"]);
        Assert.True(apply.ContainsKey("expectedPlanHash")); Assert.True(apply.ContainsKey("expectedProjectFile"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Recovery_schema_keeps_canonical_parameters(string release)
    {
        const string name = "ImportPlcBlocksFromDirectory";
        var properties = new JsonObject();
        foreach (string key in new[] { "softwarePath", "groupPath", "regexName", "dir" })
            properties[key] = new JsonObject { ["type"] = "string" };
        foreach (string key in new[] { "dryRun", "confirm", "overwrite" }) properties[key] = new JsonObject { ["type"] = "boolean" };
        var input = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("dir") };
        var schema = FoundationV4Tool.RecoverySchema(name, JsonSerializer.SerializeToElement(input));
        var usage = ToolUsageCatalog.Describe(name, release, "plc-foundation", "", schema);
        var example = usage["example"]!;
        var args = example["request"]!["params"]!["arguments"]!.AsObject();
        Assert.True(args.ContainsKey("sourceDirectory"));
        Assert.False(args.ContainsKey("dir"));
        Assert.DoesNotContain("<dir:", example.ToJsonString(new JsonSerializerOptions {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Theory, MemberData(nameof(VerifiedRows))]
    public void Verified_rows_hold_the_verified_values(string release, string name)
    {
        var call = ToolUsageCatalog.ServedCall(name, release)!;
        var args = call["arguments"]!.AsObject();
        switch (name)
        {
            case "ListIoSystems":
                Assert.Equal("<exact device name from GetProjectTree>", (string?)args["devicePath"]?[0]);
                Assert.Equal(2, args["itemPath"]!.AsArray().Count); break;
            case "EnsureSubnet": Assert.Equal("PROFINET", (string?)args["subnetType"]); break;
            case "ManageUnifiedEngineeringObject": Assert.Equal("alarmClasses", (string?)args["category"]); break;
            case "ExchangeSystemDiagnosticsSettings": Assert.EndsWith(".dat", (string?)args["filePath"]); break;
            case "ExportPlcType": Assert.Equal(@"C:\Examples\exports", (string?)args["exportDirectory"]); break;
            case "UploadStationFromPlc":
                Assert.Equal("192.0.2.10", (string?)args["targetIpAddress"]);
                goto case "ScanAccessibleDevices";
            case "ScanAccessibleDevices":
                Assert.Equal("<exact PG/PC interface name from ListTransferRoutes>", (string?)args["pgPcInterface"]); break;
            case "ManageCommunicationConnection":
                Assert.Equal("21", release); Assert.Equal("create", (string?)args["action"]);
                foreach (string key in new[] { "localInterfaceItemPath", "partnerDevicePath", "partnerItemPath", "partnerInterfaceItemPath" })
                    Assert.NotEmpty(args[key]!.AsArray());
                Assert.Equal("ExampleConnection", (string?)args["connectionName"]);
                Assert.Equal("S7Connection", (string?)args["connectionType"]); break;
            case "ManageTeamcenterDataset":
                Assert.Equal("search", (string?)args["action"]); Assert.Equal("Project", (string?)args["itemType"]);
                Assert.Equal("<Teamcenter item id>", (string?)args["itemId"]); Assert.True((bool?)args["dryRun"]); break;
            case "ImportPlcTagTablesFromDirectory": Assert.True(args.ContainsKey("sourceDirectory")); break;
            case "WritePlcSclSourceFile": Assert.True(args.ContainsKey("exportFile")); break;
            case "GetObjectIdentifier":
                Assert.Equal("device", (string?)args["kind"]);
                Assert.Equal("<exact device name from GetProjectTree>", (string?)args["devicePath"]?[0]); break;
            case "GetLibraryType":
                Assert.Equal("<exact library type path from GetLibraryOverview>", (string?)args["typePath"]); break;
            case "CompareLibraries": Assert.Equal("<open global library name>", (string?)args["rightLibraryName"]); break;
            case "RenderPlcBlockDocument": Assert.Equal(@"C:\Examples\exports\Main.xml", (string?)args["filePath"]); break;
            case "DescribeService":
                Assert.Equal("<exact service type suffix from the Siemens API reference>", (string?)args["serviceTypeSuffix"]); break;
            case "InstantiatePlcTemplates": Assert.Contains("{{Name}}", (string?)call["note"]); break;
            case "PlanProjectGeneration":
                Assert.Equal(release, (string?)args["machine"]?["target"]?["release"]);
                Assert.Equal(@"C:\Projects\Machine.ap" + (string)Calls["releaseExtensions"]![release]!,
                    (string?)args["machine"]?["target"]?["project"]?["projectIdentity"]); break;
            case "ApplyGenerationPlan":
                Assert.Equal(@"C:\Projects\Machine.ap" + (string)Calls["releaseExtensions"]![release]!, (string?)args["expectedProjectFile"]); break;
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task List_io_systems_example_passes_the_scope_rule(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        var empty = await fixture.Call("ListIoSystems", new JsonObject());
        Assert.Equal("INVALID_ARGUMENT", (string?)empty["error"]?["code"]);
        var served = await fixture.Call("ListIoSystems", ToolUsageCatalog.ServedCall("ListIoSystems", release)!["arguments"]!);
        Assert.NotEqual("INVALID_ARGUMENT", (string?)served["error"]?["code"]);
        Assert.Equal("PROJECT_NOT_BOUND", (string?)served["error"]?["code"]);
    }

    [Theory, MemberData(nameof(EvidenceRows))]
    public void Real_machine_evidence_names_its_release(string profile, string name)
    {
        string validation = (string)Calls["profiles"]![profile]![name]!["validation"]!;
        if (name == "ManageTeamcenterDataset") Assert.DoesNotContain("Real-machine evidence:", validation);
        else
        {
            Assert.StartsWith("Real-machine evidence:", validation);
            Assert.Contains("V21", validation);
            if (name == "ManageCommunicationConnection") Assert.Contains("registered on V21 only", validation);
            else Assert.Contains("V21 only", validation);
        }
        Assert.Contains("NOT RUN", validation);
    }

    [Fact]
    public void Example_library_holds_no_test_run_values()
    {
        string directory = Path.Combine(Root, "reference/tool-examples");
        var files = Directory.GetFiles(directory, "*.json").Concat(Directory.GetFiles(Path.Combine(directory, "languages"), "*"));
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string forbidden in new[] { "MCP_B4_", "MCP_B3_", "+S1-K", "192.168.86.", "TIA_MCP_Delivery",
                @"SIEMENS\\Desktop", @"C:\\Users\\" }) Assert.DoesNotContain(forbidden, text);
            if (Path.GetExtension(file) == ".json") CheckText(JsonNode.Parse(text));
        }
        static void CheckText(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj)
                {
                    if (pair.Key is "validation" or "note" && pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        Assert.False(Regex.IsMatch(text, @"\b[0-9a-f]{8}([0-9a-f]{24})?\b"), text);
                    CheckText(pair.Value);
                }
            else if (node is JsonArray array) foreach (var item in array) CheckText(item);
        }
    }
}
