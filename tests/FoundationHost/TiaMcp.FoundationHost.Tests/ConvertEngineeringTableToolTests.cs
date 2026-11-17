extern alias toolpipeline;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.Exchange;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class ConvertEngineeringTableToolTests
{
    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();

    [Theory, MemberData(nameof(Releases))]
    public async Task Discovery_and_usage_expose_the_table_contract_on_every_release(string release)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        IReadOnlyList<McpServerTool>? published = null;
        var registry = LegacyHostToolRegistry.Create(worker, release, false, usageRoster: () => published!, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, registry);
        published = pipeline.AllTools;
        using var session = pipeline.EnterSession();
        async Task<CallToolResult> Call(string name, object args) => await published.Single(t => t.ProtocolTool.Name == name).InvokeAsync(
            new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, VersionControlAdmissionTests.ServerProxy>()) {
                Params = new() { Name = name, Arguments = JsonSerializer.SerializeToElement(args).Deserialize<Dictionary<string, JsonElement>>() } });
        var found = Body(await Call("FindTools", new { query = "ConvertEngineeringTable", limit = 1 }));
        Assert.True((bool?)found["ok"], found.ToJsonString()); Assert.Contains("ConvertEngineeringTable", found.ToJsonString());
        var descriptor = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.AllToolDescriptors()["ConvertEngineeringTable"];
        var described = TiaOpenness.Shared.ToolUsageCatalog.Describe("ConvertEngineeringTable", release, "full-engine", descriptor.Tool.Description!,
            JsonNode.Parse(descriptor.Tool.InputSchema.GetRawText())!.AsObject(), operation: "fromTable");
        Assert.Equal("fromTable", (string?)described["example"]?["request"]?["params"]?["arguments"]?["direction"]);
        var usage = Body(await Call("GetToolUsage", new { toolName = "ConvertEngineeringTable", operation = "fromTable" }));
        Assert.True((bool?)usage["ok"], usage.ToJsonString());
        Assert.Equal("fromTable", (string?)usage["data"]?["example"]?["request"]?["params"]?["arguments"]?["direction"]);
        Assert.Contains("10000", usage.ToJsonString()); Assert.Contains("SystemConstant", usage.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Registered_file_tool_previews_then_writes_exact_target_and_refuses_overwrite(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        Assert.Contains("ConvertEngineeringTable", PortedFamilies.ToolReleases.Where(p => p.Value.Contains(release)).Select(p => p.Key));
        Assert.Equal("FILE", ToolMetadata.Find("ConvertEngineeringTable")!.Operation);
        string file = Path.Combine(fixture.Root, "rows.xlsx");
        var args = new JsonObject { ["kind"] = "rows", ["direction"] = "toTable", ["exportFile"] = file,
            ["rows"] = new JsonArray(new JsonObject { ["Name"] = "电机/1", ["Address"] = "%M0.0", ["Value"] = "0010",
                ["Enabled"] = true, ["Number"] = -1 }) };
        var preview = Body(await fixture.Call("ConvertEngineeringTable", args));
        Assert.True((bool?)preview["ok"], preview.ToJsonString()); Assert.False(File.Exists(file));
        Assert.True((bool?)preview["data"]?["dryRun"]); Assert.Equal(file, (string?)preview["data"]?["exportFile"]);
        args["dryRun"] = false;
        var executed = Body(await fixture.Call("ConvertEngineeringTable", args));
        Assert.True((bool?)executed["ok"], executed.ToJsonString());
        Assert.Equal(new[] { file }, Directory.GetFiles(fixture.Root));
        Assert.Equal(file, (string?)executed["data"]?["output"]?["path"]);
        Assert.Equal(new FileInfo(file).Length, (long)executed["data"]!["output"]!["bytes"]!);
        Assert.Equal(64, ((string)executed["data"]!["output"]!["sha256"]!).Length);
        byte[] before = File.ReadAllBytes(file);
        var refused = Body(await fixture.Call("ConvertEngineeringTable", args));
        Assert.False((bool?)refused["ok"]); Assert.Equal(before, File.ReadAllBytes(file));
        var read = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "fromTable", inputPath = file }));
        Assert.True((bool?)read["ok"], read.ToJsonString());
        Assert.Equal("电机/1", (string?)read["data"]?["rows"]?[0]?["Name"]);
        Assert.Equal("0010", (string?)read["data"]?["rows"]?[0]?["Value"]);
        Assert.Equal("true", (string?)read["data"]?["rows"]?[0]?["Enabled"]);
        Assert.Equal("-1", (string?)read["data"]?["rows"]?[0]?["Number"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Rows_from_table_pages_fifty_without_an_output_file(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        string file = Path.Combine(fixture.Root, "input.csv");
        File.WriteAllBytes(file, TableFile.WriteCsv(new TableData(new[] { "Name" }, Enumerable.Range(0, 51).Select(i => new[] { i.ToString() }))));
        var first = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "fromTable", inputPath = file }));
        Assert.True((bool?)first["ok"], first.ToJsonString());
        Assert.Equal(50, first["data"]!["rows"]!.AsArray().Count); Assert.Equal(51, (int)first["data"]!["total"]!);
        Assert.Equal(50, (int)first["meta"]!["paging"]!["nextOffset"]!);
        var last = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "fromTable", inputPath = file,
            offset = 50, dryRun = false }));
        Assert.True((bool?)last["ok"], last.ToJsonString()); Assert.Single(last["data"]!["rows"]!.AsArray());
        Assert.True((bool?)last["meta"]?["paging"]?["complete"]); Assert.Null(last["data"]?["exportFile"]);
        Assert.Equal(new[] { file }, Directory.GetFiles(fixture.Root));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Default_target_uses_session_directory_and_correct_extension(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        var preview = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "toTable", format = "csv",
            rows = new[] { new { Name = "x" } } }));
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        string path = (string)preview["data"]!["exportFile"]!;
        Assert.StartsWith("ConvertEngineeringTable.Rows.", Path.GetFileName(path));
        Assert.EndsWith(".csv", path);
        Assert.StartsWith(TiaOpenness.Shared.DataLocations.Current.OutputRoot, path);
        Assert.False(File.Exists(path));
        var second = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "toTable", format = "csv",
            rows = new[] { new { Name = "x" } } }));
        Assert.True((bool?)second["ok"], second.ToJsonString());
        Assert.NotEqual(path, (string?)second["data"]?["exportFile"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Default_table_outputs_include_encoded_names_and_never_collide_in_one_session(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int i = 0;
        foreach (string name in new[] { "表/一", "表/二", "表/一" })
        {
            string source = Path.Combine(fixture.Root, "source" + i++ + ".xml");
            File.WriteAllText(source, "<Document><Engineering version=\"V21\"/><SW.Tags.PlcTagTable ID=\"0\"><AttributeList><Name>"
                + name + "</Name></AttributeList><ObjectList/></SW.Tags.PlcTagTable></Document>");
            var result = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "plcTagTable", direction = "toTable", inputPath = source,
                format = "csv", dryRun = false }));
            Assert.True((bool?)result["ok"], result.ToJsonString());
            string path = (string)result["data"]!["exportFile"]!;
            Assert.StartsWith("ConvertEngineeringTable." + ObjectFileNameEncoding.Encode(name) + ".", Path.GetFileName(path));
            Assert.True(outputs.Add(path)); Assert.True(File.Exists(path));
        }
        Assert.Equal(3, outputs.Count);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Outside_output_root_is_denied_before_conversion_writes(string release)
    {
        string settingsPath = TiaOpenness.Shared.ApprovalSettings.SettingsPath;
        byte[]? settings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var previous = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests;
        int cards = 0;
        toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = (card, _, _) => {
            cards++; Assert.Equal("ConvertEngineeringTable", card.Tool);
            return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(card, false, "denied"));
        };
        try
        {
            new TiaOpenness.Shared.ApprovalSettings(true, 1).Save(settingsPath);
            using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
            string target = Path.Combine(Path.GetDirectoryName(TiaOpenness.Shared.DataLocations.Current.OutputRoot)!,
                "outside-conversion-" + Guid.NewGuid().ToString("N") + ".csv");
            var result = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "toTable", format = "csv",
                rows = new[] { new { Name = "x" } }, exportFile = target, dryRun = false }));
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)result["error"]?["code"]);
            Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
            Assert.Equal(1, cards); Assert.False(File.Exists(target));
        }
        finally
        {
            toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer.ApprovalWaitOverrideForTests = previous;
            if (settings == null) File.Delete(settingsPath); else File.WriteAllBytes(settingsPath, settings);
        }
    }

    [Theory]
    [InlineData("csv", "bad.xlsx")]
    [InlineData("xlsx", "bad.csv")]
    public async Task Wrong_extension_is_refused_before_writing(string format, string name)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        var result = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "rows", direction = "toTable", format,
            rows = new[] { new { Name = "x" } }, exportFile = Path.Combine(fixture.Root, name), dryRun = false }));
        Assert.False((bool?)result["ok"]); Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Plc_tag_table_conversion_previews_merge_and_preserves_template(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        string xml = Path.Combine(fixture.Root, "source.xml"), csv = Path.Combine(fixture.Root, "edit.csv"), output = Path.Combine(fixture.Root, "out.xml");
        File.WriteAllText(xml, "<Document><Engineering version=\"V21\"/><DocumentInfo/><SW.Tags.PlcTagTable ID=\"0\">"
            + "<AttributeList><Name>Tags</Name></AttributeList><ObjectList><SW.Tags.PlcTag ID=\"1\" CompositionName=\"Tags\">"
            + "<AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>Run</Name></AttributeList>"
            + "</SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>", new UTF8Encoding(false));
        var toTable = Body(await fixture.Call("ConvertEngineeringTable", new { kind = "plcTagTable", direction = "toTable", inputPath = xml,
            format = "csv", exportFile = csv, dryRun = false }));
        Assert.True((bool?)toTable["ok"], toTable.ToJsonString());
        var table = TableFile.ReadCsv(File.ReadAllBytes(csv)); table.Rows[0][3] = "%M1.0"; File.WriteAllBytes(csv, TableFile.WriteCsv(table));
        var args = new JsonObject { ["kind"] = "plcTagTable", ["direction"] = "fromTable", ["inputPath"] = csv,
            ["templatePath"] = xml, ["exportFile"] = output };
        var preview = Body(await fixture.Call("ConvertEngineeringTable", args));
        Assert.True((bool?)preview["ok"], preview.ToJsonString()); Assert.False(File.Exists(output));
        Assert.Equal(1, (int)preview["data"]!["changes"]!["updated"]!);
        Assert.Equal("ImportPlcTagTable", (string?)preview["data"]?["nextStep"]?["tool"]);
        args["dryRun"] = false;
        var applied = Body(await fixture.Call("ConvertEngineeringTable", args));
        Assert.True((bool?)applied["ok"], applied.ToJsonString()); Assert.Contains("%M1.0", File.ReadAllText(output));
        Assert.Contains("%M0.0", File.ReadAllText(xml));
    }

    private static JsonObject Body(ModelContextProtocol.Protocol.CallToolResult result) => result.StructuredContent!.AsObject();
}
