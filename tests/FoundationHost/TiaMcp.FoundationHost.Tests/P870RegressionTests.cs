extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;

namespace TiaMcp.FoundationHost.Tests;

public sealed class P870RegressionTests
{
    public static IEnumerable<object[]> Releases => PlcDeclarationXmlFormat.ReleaseKeys.Select(key => new object[] { key });
    public static IEnumerable<object[]> Builders => from row in Releases
        from kind in new[] { "udt", "tagTable", "globalDb" }
        select new object[] { row[0], kind };

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };

    [Theory, MemberData(nameof(Builders))]
    public async Task Public_declaration_builder_defaults_to_serving_release(string release, string kind)
    {
        var (name, parameter, json) = kind switch
        {
            "udt" => ("BuildPlcUdt", "udt", """{"name":"UDT_Status","members":[{"name":"Ready","datatype":"Bool"}]}"""),
            "tagTable" => ("BuildPlcTagTable", "tagTable", """{"tableName":"Tags","tags":[{"name":"Ready","dataTypeName":"Bool","logicalAddress":"%M0.0"}]}"""),
            _ => ("BuildPlcGlobalDb", "globalDb", """{"dbName":"DB_Status","dbNumber":1,"staticMembers":[{"name":"Ready","datatype":"Bool"}]}""")
        };
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        if (PortedFamilies.Contains(name) && !PortedFamilies.Available(release, name))
        {
            Assert.DoesNotContain(roster, tool => tool.ProtocolTool.Name == name);
            Assert.Equal(0, worker.Calls);
            return;
        }
        var tool = roster.Single(tool => tool.ProtocolTool.Name == name);
        var result = await tool.InvokeAsync(Request(name, new JsonObject { [parameter] = JsonNode.Parse(json) }));
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        var body = result.StructuredContent!;
        var data = body["data"]!;
        Assert.Equal(release, (string?)data["outputReleaseKey"]);
        var document = XDocument.Parse((string)data["xml"]!);
        var format = PlcDeclarationXmlFormat.ForRelease(release);
        Assert.Equal(format.EngineeringVersion,
            (string?)document.Root!.Element("Engineering")!.Attribute("version"));
        if (kind != "tagTable")
            Assert.Contains(document.Descendants(), element => element.Name == format.InterfaceNamespace + "Sections");
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Explicit_tag_table_output_keeps_existing_supported_format_gate(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var roster = LegacyHostToolRegistry.Create(worker, release, false);
        if (!PortedFamilies.Available(release, "BuildPlcTagTable"))
        {
            Assert.DoesNotContain(roster, tool => tool.ProtocolTool.Name == "BuildPlcTagTable");
            Assert.Equal(0, worker.Calls);
            return;
        }
        var tool = roster.Single(tool => tool.ProtocolTool.Name == "BuildPlcTagTable");
        var args = new JsonObject { ["tagTable"] = JsonNode.Parse("""{"tableName":"Tags","tags":[{"name":"Ready","dataTypeName":"Bool","logicalAddress":"%M0.0"}]}"""),
            ["outputReleaseKey"] = "18" };
        var rejected = await tool.InvokeAsync(Request("BuildPlcTagTable", args));
        Assert.Equal("INVALID_ARGUMENT", (string?)rejected.StructuredContent?["error"]?["code"]);
        args["outputReleaseKey"] = "21";
        var accepted = await tool.InvokeAsync(Request("BuildPlcTagTable", args));
        Assert.True((bool?)accepted.StructuredContent?["ok"]);
        Assert.Equal("21", (string?)accepted.StructuredContent?["data"]?["outputReleaseKey"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Builders))]
    public void Build_and_import_candidate_records_its_actual_release(string release, string kind)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var json = kind switch
        {
            "udt" => """{"name":"UDT_Status","members":[{"name":"Ready","datatype":"Bool"}]}""",
            "tagTable" => """{"tableName":"Tags","tags":[{"name":"Ready","dataTypeName":"Bool","logicalAddress":"%M0.0"}]}""",
            _ => """{"dbName":"DB_Status","dbNumber":1,"staticMembers":[{"name":"Ready","datatype":"Bool"}]}"""
        };
        var builder = typeof(Host.PlcBuildTools).GetMethod("BuildPlcArtifact", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (JsonObject)builder.Invoke(null, new object[] { kind.ToLowerInvariant(), json })!;
        Assert.True((bool?)result["ok"]);
        Assert.Equal(release, (string?)result["outputReleaseKey"]);
        Assert.Equal(PlcDeclarationXmlFormat.ForRelease(release).EngineeringVersion,
            (string?)XDocument.Parse((string)result["xml"]!).Root!.Element("Engineering")!.Attribute("version"));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Watch_table_header_uses_the_declaration_mapping(string release)
    {
        var document = WatchTableEntryXml.NewTable("Watch", release);
        Assert.Equal(PlcDeclarationXmlFormat.ForRelease(release).EngineeringVersion,
            (string?)document.Root!.Element("Engineering")!.Attribute("version"));
    }

    [Theory]
    [InlineData("20", false)]
    [InlineData("21", true)]
    public void Access_control_registration_follows_the_available_API(string release, bool available)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        // The 20/21 version policy reads the process-wide engine release; earlier tests in the same shard may leave another one.
        string previousRelease = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        string[] names;
        try
        {
            enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            names = LegacyHostToolRegistry.Create(worker, release, false).Select(tool => tool.ProtocolTool.Name).ToArray();
        }
        finally { enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previousRelease; }
        foreach (var name in new[] { "GetOpcUaAccessControl", "ManageOpcUaAccessControl" })
        {
            Assert.Equal(available, PortedFamilies.Available(release, name));
            Assert.Equal(available, names.Contains(name));
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("18")]
    [InlineData("19")]
    public void Teamcenter_workflow_reaches_worker_on_registered_old_release(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        using var selected = Host.HardwareContract.UseRelease(release);
        var calls = new List<string>();
        var service = new Services.TeamcenterPortService((operation, arguments) =>
        {
            calls.Add(operation);
            Assert.Equal("ManageTeamcenterWorkflow", (string?)arguments["request"]?["Operation"]);
            return new JsonObject { ["Message"] = "Fake workflow result", ["Meta"] = new JsonObject { ["success"] = true } };
        }, () => "C:/Fixture/Project.ap" + release);
        var result = new Host.TeamcenterTools(service).ManageTeamcenterWorkflow("readCustomAttributes", itemType: "T4TiaProject");
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.Equal(new[] { "version-control.ExecuteTeamcenter" }, calls);
    }

    public static IEnumerable<object[]> Documents => from release in new[] { "20", "21" }
        from name in new[] { "ExportPlcBlockDocuments", "ExportPlcBlocksDocuments", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments" }
        select new object[] { release, name };

    [Theory, MemberData(nameof(Documents))]
    public async Task Host_document_entry_points_reach_fake_worker(string release, string name)
    {
        using var selected = Host.HardwareContract.UseRelease(release);
        var directory = Path.Combine(Path.GetFullPath("bin-build"), "p870-documents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Block.s7dcl"), "FUNCTION Block : Void\nEND_FUNCTION");
            var calls = new List<string>();
            var service = new Services.DocumentsService((operation, arguments) =>
            {
                Assert.Equal(PortedFamilies.ForTool(name).OperationPrefix + ".ExecutePlcB5", operation);
                var request = arguments["request"]!;
                calls.Add((string)request["Operation"]! + ":" + (string?)request["Action"]);
                return new JsonObject { ["Value"] = true, ["Count"] = 1, ["Blocks"] = new JsonArray(new JsonObject { ["Name"] = "Block" }),
                    ["ExportFailures"] = new JsonArray(), ["ImportFailures"] = new JsonArray(), ["ImportedNames"] = new JsonArray("Block"),
                    ["Scanned"] = 1, ["Selected"] = 1, ["Attempted"] = 1, ["Succeeded"] = 1, ["Stopped"] = false };
            }, () => "C:/Fixture/Project.ap" + release);
            var tools = new Host.DocumentsTools(service);
            var request = Request(name, new JsonObject());
            switch (name)
            {
                case "ExportPlcBlockDocuments": tools.ExportAsDocuments("PLC", "Block", directory); break;
                case "ExportPlcBlocksDocuments": await tools.ExportBlocksAsDocuments(request.Server, request, "PLC", directory); break;
                case "ImportPlcBlockDocuments":
                    var result = tools.ImportFromDocuments("PLC", "", directory, "Block");
                    Assert.Equal(int.Parse(release, System.Globalization.CultureInfo.InvariantCulture),
                        (int?)result.Meta?["formatPreflight"]?["major"]);
                    break;
                default: await tools.ImportBlocksFromDocuments(request.Server, request, "PLC", "", directory); break;
            }
            Assert.Contains(name + ":", calls);
            if (name == "ExportPlcBlocksDocuments") Assert.Contains(name + ":count", calls);
            if (name == "ImportPlcBlockDocuments") Assert.Contains(name + ":verify", calls);
        }
        finally { Directory.Delete(directory, true); }
    }
}
