extern alias toolpipeline;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;
using Editing = TiaMcpServer.ModelContextProtocol.PlcDocumentEditing;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;

[Collection(BundleSelectionCollection.Name)]
public sealed class OfflineDocumentOutcomeTests
{
    private const string Document = """
        <Document><Engineering version="V15.1"/><SW.Blocks.GlobalDB ID="0"><AttributeList><Name>Fixture</Name>
        <ProgrammingLanguage>DB</ProgrammingLanguage><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v3">
        <Section Name="Static"><Member Name="Motor" Datatype="&quot;Drive&quot;"><Sections><Section Name="None">
        <Member Name="Speed" Datatype="Real"><StartValue>1.5</StartValue></Member></Section></Sections></Member>
        <Member Name="Grp" Datatype="Struct"><Member Name="Inner" Datatype="&quot;Drive&quot;"><Sections><Section Name="None">
        <Member Name="Speed" Datatype="Real"><StartValue>2.5</StartValue></Member></Section></Sections></Member></Member>
        </Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>
        """;

    [Theory, InlineData("18"), InlineData("19")]
    public void Patch_preview_names_an_occupied_export_file(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        string input = Path.Combine(fixture.Root, "input.xml"), output = Path.Combine(fixture.Root, "edited.xml");
        File.WriteAllText(input, Document);
        File.WriteAllText(output, "edited outside TIA");
        var edit = new TiaMcp.Logic.V4.Domain.MemberStartValueEdit(System.Text.Json.JsonSerializer.SerializeToElement(new {
            action = "setMemberStartValue", section = "None", memberPath = "Motor/Speed", expectedValue = "1.5", value = "3.5" }));
        var tool = new Host.PlcOfflineTools();
        var body = tool.PatchPlcBlockDocumentV4(input, new[] { edit }, (string)Editing.Inspect(Document)["documentFingerprint"]!, output).StructuredContent!;
        Assert.True((bool?)body["data"]?["applyBlocked"]);
        Assert.Contains("exportFile already exists", (string?)body["data"]?["applyBlockedReason"]);
        Assert.Equal("edited outside TIA", File.ReadAllText(output));
    }

    // SW.InterfaceSections_v5 Section_T contains the inherited Sections followed by the declared members.
    private const string InheritedInterface = """
        <Document><Engineering version="V17"/><SW.Blocks.GlobalDB ID="0"><AttributeList><Name>DriveDB</Name>
        <ProgrammingLanguage>DB</ProgrammingLanguage><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
        <Section Name="Static"><Member Name="Pump" Datatype="&quot;Drive&quot;"><Sections>
        <Section Name="Base"><Sections Datatype="&quot;BaseDrive&quot;"><Section Name="None"><Member Name="Speed" Datatype="Int"/>
        </Section></Sections></Section><Section Name="None"><Member Name="Speed" Datatype="Real"/></Section>
        </Sections></Member></Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>
        """;

    [Theory, MemberData(nameof(VerifiedImportOutcomeTests.Releases), MemberType = typeof(VerifiedImportOutcomeTests))]
    public async Task Inherited_and_declared_members_with_the_same_name_keep_distinct_paths(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        string leftFilePath = Path.Combine(fixture.Root, "left.xml"), rightFilePath = Path.Combine(fixture.Root, "right.xml");
        File.WriteAllText(leftFilePath, InheritedInterface);
        File.WriteAllText(rightFilePath, InheritedInterface);
        async Task<JsonNode> Compare() => (await fixture.Call("ComparePlcBlockDocuments", new { leftFilePath, rightFilePath })).StructuredContent!;
        var equal = await Compare();
        Assert.True((bool?)equal["data"]?["identicalAfterNormalization"], equal.ToJsonString());
        File.WriteAllText(rightFilePath, InheritedInterface.Replace("Datatype=\"Int\"", "Datatype=\"DInt\""));
        var changed = await Compare();
        Assert.False((bool?)changed["data"]?["identicalAfterNormalization"], changed.ToJsonString());
        var difference = Assert.Single(changed["data"]!["structural"]!["interface"]!["typeChanged"]!.AsArray());
        Assert.Equal("Static", (string?)difference!["section"]);
        Assert.Equal("Pump.Base.Speed", (string?)difference["path"]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Inherited_speed_is_distinct_from_a_declared_Base_member(bool topLevel)
    {
        var document = XDocument.Parse(InheritedInterface);
        var pump = document.Descendants().Single(e => (string?)e.Attribute("Name") == "Pump");
        var ns = pump.Name.Namespace;
        var sections = pump.Element(ns + "Sections")!;
        var declared = sections.Elements().Last().Element(ns + "Member")!;
        declared.ReplaceWith(new XElement(ns + "Member", new XAttribute("Name", "Base"), new XAttribute("Datatype", "Struct"), new XElement(declared)));
        if (topLevel)
            pump.ReplaceWith(sections.Elements().First().Element(ns + "Sections"), sections.Elements().Last().Elements(ns + "Member"));
        string xml = document.ToString();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("17");
        using var session = fixture.EnterSession();
        string leftFilePath = Path.Combine(fixture.Root, "left.xml"), rightFilePath = Path.Combine(fixture.Root, "right.xml");
        File.WriteAllText(leftFilePath, xml);
        File.WriteAllText(rightFilePath, xml.Replace("Datatype=\"Int\"", "Datatype=\"DInt\""));
        var body = (await fixture.Call("ComparePlcBlockDocuments", new { leftFilePath, rightFilePath })).StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        var changed = Assert.Single(body["data"]!["structural"]!["interface"]!["typeChanged"]!.AsArray());
        Assert.Equal("Static", (string?)changed!["section"]);
        Assert.Equal(topLevel ? "Base.Speed" : "Pump.Base.Speed", (string?)changed["path"]);
        File.WriteAllText(rightFilePath, xml.Replace("Datatype=\"Real\"", "Datatype=\"LReal\""));
        var declaredDiff = (await fixture.Call("ComparePlcBlockDocuments", new { leftFilePath, rightFilePath })).StructuredContent!;
        var literal = Assert.Single(declaredDiff["data"]!["structural"]!["interface"]!["typeChanged"]!.AsArray());
        Assert.Equal(topLevel ? "\"Base\".Speed" : "Pump.\"Base\".Speed", (string?)literal!["path"]);
    }

    [Fact]
    public async Task Metrics_and_documentation_include_inherited_members_of_one_expanded_instance()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("17");
        using var session = fixture.EnterSession();
        string directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "exports")).FullName;
        string path = Path.Combine(directory, "DriveDB.xml"), outputPath = Path.Combine(fixture.Root, "handbook.md");
        File.WriteAllText(path, InheritedInterface);
        var metrics = (await fixture.Call("ExtractPlcBlockMetrics", new { path })).StructuredContent!;
        Assert.True((bool?)metrics["ok"], metrics.ToJsonString());
        var counts = Assert.Single(metrics["data"]!["rows"]!.AsArray())!["interfaceMembers"]!;
        Assert.Equal(3, (int?)counts["Static"]);
        Assert.Equal(3, (int?)counts["total"]);
        Assert.Null(counts["None"]);
        var handbook = (await fixture.Call("GeneratePlcDocumentation", new { directory, outputPath })).StructuredContent!;
        Assert.True((bool?)handbook["ok"], handbook.ToJsonString());
        var markdown = File.ReadAllText(outputPath);
        Assert.Contains("| Static | `Pump.Base.Speed` | Int |", markdown);
        Assert.Contains("| Static | `Pump.Speed` | Real |", markdown);
    }

    [Theory]
    [InlineData("2.5", true)]
    [InlineData("stale", false)]
    public async Task Patch_uses_the_advertised_nested_member_and_settles_the_result(string expected, bool succeeds)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        string filePath = Path.Combine(fixture.Root, "source.xml"), outputPath = Path.Combine(fixture.Root, "patched.xml");
        File.WriteAllText(filePath, Document);
        var inspection = (await fixture.Call("GetPlcBlockEditCapabilities", new { filePath })).StructuredContent!;
        var data = inspection["data"]!["evidence"]!["data"]!;
        var member = data["members"]!.AsArray().Single(m => (string?)m!["memberPath"] == "Grp/Inner/Speed")!;
        var change = new { action = "setMemberStartValue", section = (string)member["section"]!, memberPath = (string)member["memberPath"]!,
            expectedValue = expected, value = "3.5" };
        var result = (await fixture.Call("PatchPlcBlockDocument", new { filePath, expectedDocumentHash = (string)data["documentFingerprint"]!,
            exportFile = outputPath, dryRun = false, changes = new[] { change } })).StructuredContent!;
        Assert.Equal(succeeds, (bool?)result["ok"]);
        Assert.Empty(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(fixture.Worker).Snapshot());
        Assert.Equal(succeeds, File.Exists(outputPath));
        if (succeeds)
            Assert.Equal(new[] { "1.5", "3.5" }, XDocument.Load(outputPath).Descendants().Where(e => e.Name.LocalName == "StartValue").Select(e => e.Value));
        else AssertRefused(result);
    }

    [Fact]
    public async Task V14_expanded_UDT_accepts_the_section_and_path_from_its_capabilities_row()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("14sp1");
        using var session = fixture.EnterSession();
        var filePath = Path.Combine(fixture.Root, "MCP_RC_DB3.xml");
        var exportFile = Path.Combine(fixture.Root, "patched.xml");
        // The six interface rows reported by rc7-verify-v14sp1, raw-V/031.
        File.WriteAllText(filePath, """
            <Document><Engineering version="V14 SP1"/><SW.Blocks.GlobalDB ID="0"><AttributeList><Name>MCP_RC_DB3</Name>
            <ProgrammingLanguage>DB</ProgrammingLanguage><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v2">
            <Section Name="Static"><Member Name="Count" Datatype="Int"><StartValue>4</StartValue></Member>
            <Member Name="Motor" Datatype="&quot;MCP_RC_UDT&quot;"/><Member Name="Grp" Datatype="Struct">
            <Member Name="Inner" Datatype="&quot;MCP_RC_UDT&quot;"><Sections><Section Name="None">
            <Member Name="Speed" Datatype="Int"><StartValue>8</StartValue></Member></Section></Sections></Member>
            <Member Name="Flag" Datatype="Bool"><StartValue>TRUE</StartValue></Member></Member>
            </Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>
            """);
        var original = File.ReadAllBytes(filePath);
        var inspection = (await fixture.Call("GetPlcBlockEditCapabilities", new { filePath })).StructuredContent!;
        var data = inspection["data"]!["evidence"]!["data"]!;
        var members = data["members"]!.AsArray();
        Assert.Equal(6, members.Count);
        var row = members.Single(m => (string?)m!["memberPath"] == "Grp/Inner/Speed")!;
        Assert.Equal(("None", "Int", "8"), ((string?)row["section"], (string?)row["datatype"], (string?)row["startValue"]));
        var changes = new[] { new { action = "setMemberStartValue", section = (string)row["section"]!,
            memberPath = (string)row["memberPath"]!, expectedValue = (string)row["startValue"]!, value = "13" } };
        foreach (var dryRun in new[] { true, false })
        {
            var result = (await fixture.Call("PatchPlcBlockDocument", new { filePath, exportFile, dryRun, changes,
                expectedDocumentHash = (string)data["documentFingerprint"]! })).StructuredContent!;
            Assert.True((bool?)result["ok"], result.ToJsonString());
            Assert.Equal(!dryRun, File.Exists(exportFile));
            Assert.Equal(original, File.ReadAllBytes(filePath));
        }
        Assert.Equal(new[] { "4", "13", "TRUE" }, XDocument.Load(exportFile).Descendants()
            .Where(e => e.Name.LocalName == "StartValue").Select(e => e.Value));
    }

    private static void AssertRefused(JsonNode result)
    {
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)result["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }

    [Fact]
    public void Offline_comparison_scrubs_the_worker_reason_before_storing_it()
    {
        const string tool = "ComparePlcBlockDocuments";
        var cause = new TiaMcp.FoundationHost.WorkerOperationException("Export failed.", -32603, "rejected-before-operation",
            new JsonObject { ["nativeMessage"] = @"Cannot export. password=fixture-secret. See C:\Private\Block.xml" }.ToJsonString());
        var evidence = new JsonObject();
        Host.OfflineToolExecution.AnalysisFailure(tool, evidence, cause);
        var reason = (string?)evidence["nativeMessage"];
        Assert.Contains("Cannot export", reason);
        Assert.DoesNotContain("Private", reason);
        Assert.DoesNotContain("fixture-secret", reason);
    }

    [Theory, InlineData(true), InlineData(false)]
    public void File_and_inconsistent_live_block_report_the_compile_precondition(bool fileOnLeft)
    {
        const string block = "MCP_RC_FC";
        const string software = "devices/MCP_RC_PLC/MCP_RC_PLC";
        const string reason = "Compile the inconsistent block before export. Inconsistent objects: MCP_RC_FC.";
        using var worker = new DirectoryImportRegressionTests.Worker((operation, args) => {
            Assert.Equal("plc-analysis.ExportBlockDocument", operation);
            Assert.Equal(block, (string?)args["blockPath"]);
            throw new TiaMcp.FoundationHost.WorkerOperationException(reason, -32603, "rejected-before-operation",
                new JsonObject { ["exceptionType"] = "AdapterPreconditionException", ["parameter"] = "blockPath",
                    ["isArgument"] = false, ["nativeMessage"] = reason }.ToJsonString());
        });
        var services = TiaMcp.FoundationHost.FoundationHostPorts.CreateServices(worker, "21", () => new JsonObject(), null);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", services, []);
        using var session = pipeline.EnterSession();
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, Document);
            var body = new Host.OfflineAnalysisTools().ComparePlcBlockDocumentsV4(fileOnLeft ? file : "", fileOnLeft ? "" : file,
                software, fileOnLeft ? "" : block, fileOnLeft ? block : "").StructuredContent!;
            Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]!["code"]);
            Assert.Equal(reason, (string?)body["error"]!["message"]);
            Assert.Equal(reason, (string?)body["error"]!["details"]!["nativeMessage"]);
        }
        finally { File.Delete(file); }
    }

    [Theory, MemberData(nameof(VerifiedImportOutcomeTests.Releases), MemberType = typeof(VerifiedImportOutcomeTests))]
    public async Task Comparison_keeps_each_expanded_instance_under_its_own_member(string release)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        string leftFilePath = Path.Combine(fixture.Root, "before.xml"), rightFilePath = Path.Combine(fixture.Root, "after.xml");
        string source = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "tests/KeptCode/TiaMcp.KeptCode.Tests/Fixtures/VerifiedImport/17-T2b.xml");
        var xml = XDocument.Load(source);
        // Recorded candidate A/B. Reconstruct the two nested UDT expansions observed in V17 rows 69/70.
        foreach (var instance in xml.Descendants().Where(e => e.Name.LocalName == "Member").ToArray())
        {
            XNamespace ns = instance.Name.Namespace;
            instance.Add(new XElement(ns + "Sections", new XElement(ns + "Section", new XAttribute("Name", "None"),
                new XElement(ns + "Member", new XAttribute("Name", "In"), new XAttribute("Datatype", "\"UDTI\""),
                    new XElement(ns + "Sections", new XElement(ns + "Section", new XAttribute("Name", "None"),
                        new XElement(ns + "Member", new XAttribute("Name", "Lim"), new XAttribute("Datatype", "Int"))))))));
        }
        xml.Save(leftFilePath);
        xml.Save(rightFilePath);
        async Task<JsonNode> Compare() => (await fixture.Call("ComparePlcBlockDocuments", new { leftFilePath, rightFilePath })).StructuredContent!;
        var same = await Compare();
        Assert.True((bool?)same["ok"], same.ToJsonString());
        Assert.True((bool?)same["data"]!["identicalAfterNormalization"]);
        var limit = xml.Descendants().Last(e => (string?)e.Attribute("Name") == "Lim");
        limit.SetAttributeValue("Datatype", "DInt");
        xml.Save(rightFilePath);
        var changed = await Compare();
        var member = Assert.Single(changed["data"]!["structural"]!["interface"]!["typeChanged"]!.AsArray());
        Assert.Equal("Static", (string?)member!["section"]);
        Assert.Equal("B.In.Lim", (string?)member["path"]);
        limit.AddAfterSelf(new XElement(limit));
        xml.Save(rightFilePath);
        var ambiguous = await Compare();
        AssertRefused(ambiguous);
        Assert.Contains("Static/B.In.Lim", (string?)ambiguous["error"]!["message"]);
        Assert.Equal("/rightFilePath", (string?)ambiguous["error"]!["details"]!["parameter"]);
    }

    [Theory]
    [InlineData("GetPlcBlockEditCapabilities")]
    [InlineData("ComparePlcBlockDocuments")]
    [InlineData("BuildPlcSymbolManifestFromPath")]
    [InlineData("BuildPlcGlobalDb")]
    [InlineData("BuildAndImportPlcArtifact")]
    [InlineData("BuildDeviceAmlDocument")]
    public async Task Invalid_offline_document_request_is_a_definite_refusal(string tool)
    {
        using var worker = new HmiTagPathContractTests.PreviewWorker();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1", worker: worker);
        using var session = fixture.EnterSession();
        string missing = Path.Combine(fixture.Root, "absent.xml");
        object args = tool switch {
            "GetPlcBlockEditCapabilities" => new { filePath = missing },
            "ComparePlcBlockDocuments" => new { leftFilePath = missing, rightFilePath = missing },
            "BuildPlcSymbolManifestFromPath" => new { inputRoot = fixture.Root, files = new[] { "absent.xml" },
                expectedOrigin = "caller-owned-immutable-export-snapshot" },
            "BuildAndImportPlcArtifact" => new { softwarePath = "", kind = "udt", dryRun = true, spec = new { name = "Drive",
                members = new[] { new { name = "Speed", datatype = "Real" }, new { name = "Speed", datatype = "Real" } } } },
            "BuildDeviceAmlDocument" => new { exportFile = Path.Combine(fixture.Root, "wrong.xml"), spec = new { projectName = "Fixture",
                devices = new[] { new { name = "PLC", typeIdentifier = "System:Device.S71500", deviceItems = Array.Empty<object>() } } } },
            _ => new { globalDb = new { dbName = "Fixture", dbNumber = 1, staticMembers = Array.Empty<object>() } }
        };
        var result = (await fixture.Call(tool, args)).StructuredContent!;
        AssertRefused(result);
        if (tool == "GetPlcBlockEditCapabilities") Assert.Equal("/filePath", (string?)result["error"]!["details"]!["parameter"]);
        if (tool == "BuildDeviceAmlDocument") AssertUnnamedArgument(result);
        Assert.Empty(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(worker).Snapshot());
        Assert.Empty(worker.Requests);
    }

    private static void AssertUnnamedArgument(JsonNode body)
    {
        var details = body["error"]!["details"]!.AsObject();
        Assert.Equal("", (string?)details["parameter"]);
        Assert.Null(details["expected"]);
        Assert.Empty(details["allowedValues"]!.AsArray());
    }

    [Fact]
    public async Task Missing_comparison_source_preserves_the_unnamed_argument()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        var body = (await fixture.Call("ComparePlcBlockDocuments", new { })).StructuredContent!;
        AssertRefused(body);
        AssertUnnamedArgument(body);
    }

    [Fact]
    public async Task Missing_patch_output_preserves_the_unnamed_argument()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        var filePath = Path.Combine(fixture.Root, "source.xml");
        File.WriteAllText(filePath, Document);
        var change = new { action = "setMemberStartValue", section = "None", memberPath = "Motor/Speed", expectedValue = "1.5", value = "2.5" };
        var body = (await fixture.Call("PatchPlcBlockDocument", new { filePath, changes = new[] { change }, dryRun = false,
            expectedDocumentHash = (string)Editing.Inspect(Document)["documentFingerprint"]! })).StructuredContent!;
        AssertRefused(body);
        AssertUnnamedArgument(body);
    }

    [Fact]
    public async Task Existing_aml_output_preserves_the_hardware_precondition()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        string exportFile = Path.Combine(fixture.Root, "occupied.aml");
        File.WriteAllText(exportFile, "original");
        var body = (await fixture.Call("BuildDeviceAmlDocument", new { exportFile, spec = new { projectName = "Fixture",
            devices = new[] { new { name = "PLC", typeIdentifier = "System:Device.S71500", deviceItems = Array.Empty<object>() } } } })).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]!["code"]);
        Assert.Equal("hardware-admission", (string?)body["error"]!["details"]!["condition"]);
        Assert.Null(body["error"]!["details"]!["parameter"]);
        Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
        Assert.False((bool?)body["data"]!["evidence"]!["mayHaveWrittenFiles"]);
        Assert.Equal("original", File.ReadAllText(exportFile));
    }

    [Fact]
    public void Failure_after_file_creation_identifies_the_unconfirmed_output()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        var output = new FileInfo(Path.Combine(fixture.Root, "patched.xml"));
        const string tool = "PatchPlcBlockDocument";
        var reply = Host.OfflineToolExecution.RunOfflineAnalysisTool(tool, meta => {
            meta["mayHaveWrittenFiles"] = false;
            Host.OfflineToolExecution.WriteNewDocument(output, Document, System.Text.Encoding.UTF8, meta);
            throw new IOException("Forced post-write failure.");
        });
        var body = Host.PlcToolContract.Map(tool, reply, true, false).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal(output.FullName, (string?)body["data"]?["evidence"]?["cleanupRequired"]?[0]?["target"]);
        Assert.True(File.Exists(output.FullName));
    }

    [Fact]
    public async Task Declared_integer_bounds_are_explained_on_another_tool()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        var body = (await fixture.Call("GetToolUsage", new { toolName = "ListPlcBlocks", limit = 201 })).StructuredContent!;
        AssertRefused(body);
        Assert.Equal("/limit", (string?)body["error"]!["details"]!["parameter"]);
        Assert.Contains("1..200", (string)body["error"]!["message"]!);
    }

    [Fact]
    public async Task Block_page_limit_refusal_gives_the_range()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        var body = (await fixture.Call("ListPlcBlocks", new { softwarePath = "PLC", limit = 1000 })).StructuredContent!;
        AssertRefused(body);
        Assert.Equal("/limit", (string?)body["error"]!["details"]!["parameter"]);
        Assert.Contains("1..100", (string)body["error"]!["message"]!);
        Assert.Equal("limit must be 1..100. (Parameter 'limit')", (string?)body["error"]!["message"]);
    }

    [Theory]
    [InlineData("GeneratePlcDocumentation")]
    [InlineData("RenderPlcBlockDocument")]
    [InlineData("RenderPlcVisualDiff")]
    public void Report_verification_failure_identifies_the_created_file(string tool)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        string input = Path.Combine(fixture.Root, "Fixture.xml");
        string output = Path.Combine(fixture.Root, tool == "RenderPlcVisualDiff" ? "report.html" : "report.md");
        const string aliases = """[{"source":["Sensor"],"destination":["DB","Reading"],"title":"Reading"}]""";
        File.WriteAllText(input, TiaMcpServer.ModelContextProtocol.PlcAliasAlarmBuilder.Build("FC_Map", 100, aliases));
        string? verifiedPath = null;
        JsonObject FailVerification(FileInfo file)
        {
            verifiedPath = file.FullName;
            throw new IOException("The written report could not be verified.");
        }
        var docs = new Host.PlcDocumentationTools { VerifyOutput = FailVerification };
        var result = tool switch {
            "GeneratePlcDocumentation" => docs.GeneratePlcDocumentationV4(fixture.Root, output),
            "RenderPlcBlockDocument" => docs.RenderPlcBlockDocumentV4(input, outputPath: output),
            _ => new Host.EcosystemTools { VerifyOutput = FailVerification }.RenderPlcVisualDiffV4(input, input, output)
        };
        var body = result.StructuredContent!;
        Assert.Equal(output, verifiedPath);
        Assert.True(File.Exists(output), body.ToJsonString());
        Assert.NotEmpty(File.ReadAllText(output));
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal(output, (string?)body["data"]?["cleanupRequired"]?[0]?["target"]);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("io")]
    [InlineData("unknown-kind")]
    [InlineData("before-dispatch")]
    public void Build_candidate_classification_failure_names_the_file_and_cleanup(string failure)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        int imports = 0;
        var port = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, _) => {
            imports++; throw new InvalidOperationException("Unexpected import.");
        }, () => failure == "before-dispatch" ? throw new IOException("Session unavailable before dispatch.") : true, () => "C:/fixture.ap15_1");
        string? candidate = null;
        var tool = new Host.PlcBuildTools(port) {
            ClassifyDocument = (string file, out string kind, out string name) => {
                candidate = file;
                kind = name = "";
                return failure switch {
                    "xml" => throw new System.Xml.XmlException("Malformed candidate."),
                    "io" => throw new IOException("Candidate read failed."),
                    "unknown-kind" => "unknown",
                    _ => Host.PlcProgramImport.ClassifyPlcXml(file, out kind, out name)
                };
            }
        };
        var spec = TiaMcp.Logic.V4.Construction.PlcArtifactSpec.Deserialize("udt", """{"name":"Drive","members":[{"name":"Speed","datatype":"Real"}]}""");
        var body = tool.PlcBuildAndImportV4("PLC", "udt", spec, dryRun: false).StructuredContent!;
        Assert.Equal(0, imports);
        Assert.True(File.Exists(candidate), body.ToJsonString());
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Equal(candidate, (string?)body["data"]?["cleanupRequired"]?[0]?["target"]);
    }

    [Fact]
    public void Scaffold_builder_receives_an_exception_when_the_candidate_cannot_be_verified()
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        var port = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, _) =>
            throw new InvalidOperationException("Unexpected import."), () => true, () => "C:/fixture.ap15_1");
        string? candidate = null;
        var tool = new Host.PlcBuildTools(port) {
            ClassifyDocument = (string file, out string kind, out string name) => {
                candidate = file;
                throw new IOException("Candidate cannot be verified.");
            }
        };
        const string spec = """{"name":"Drive","members":[{"name":"Speed","datatype":"Real"}]}""";
        var error = Assert.ThrowsAny<IOException>(() => tool.PlcBuildAndImport("PLC", "udt", spec, dryRun: true));
        Assert.True(File.Exists(candidate));
        Assert.Contains(candidate!, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_apply_tracks_only_a_dispatched_unconfirmed_import(bool valid)
    {
        int calls = 0;
        using var worker = new DirectoryImportRegressionTests.Worker((_, input) => {
            Assert.Equal("importType", (string?)input["request"]?["Action"]);
            Assert.True(File.Exists((string?)input["request"]?["FilePath"]));
            calls++;
            throw new IOException("Import reply lost.");
        });
        const string tool = "BuildAndImportPlcArtifact";
        var raw = TiaMcp.FoundationHost.PortedToolContract.Create(worker, "15.1").Single(t => t.ProtocolTool.Name == tool);
        var entry = new TiaMcp.FoundationHost.FoundationV4Tool(raw, "15.1", () => new TiaOpenness.Shared.ApprovalSettings(false, 5));
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1", worker: worker, replacementTool: entry);
        using var session = fixture.EnterSession();
        var speed = new { name = "Speed", datatype = "Real" };
        var body = (await fixture.Call("BuildAndImportPlcArtifact", new { softwarePath = "PLC", kind = "udt", dryRun = false,
            spec = new { name = "Drive", members = valid ? new[] { speed } : new[] { speed, speed } } })).StructuredContent!;
        var unsettled = toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(worker).Snapshot();
        Assert.True(calls == (valid ? 1 : 0), body.ToJsonString());
        if (valid)
        {
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Single(unsettled);
        }
        else { AssertRefused(body); Assert.Empty(unsettled); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Malformed_comparison_input_names_its_side(bool malformedLeft)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        string left = Path.Combine(fixture.Root, "left.xml"), right = Path.Combine(fixture.Root, "right.xml");
        File.WriteAllText(left, malformedLeft ? "<Document>" : Document);
        File.WriteAllText(right, malformedLeft ? Document : "<Document>");
        var body = (await fixture.Call("ComparePlcBlockDocuments", new { leftFilePath = left, rightFilePath = right })).StructuredContent!;
        AssertRefused(body);
        Assert.Equal(malformedLeft ? "/leftFilePath" : "/rightFilePath", (string?)body["error"]?["details"]?["parameter"]);
        Assert.Empty(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(fixture.Worker).Snapshot());
    }

    [Theory]
    [InlineData("GetPlcBlockEditCapabilities")]
    [InlineData("RenderPlcBlockDocument")]
    public async Task Unreadable_document_names_filePath(string tool)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        string filePath = Path.Combine(fixture.Root, "locked.xml");
        File.WriteAllText(filePath, Document);
        using var held = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var body = (await fixture.Call(tool, new { filePath })).StructuredContent!;
        if (tool == "RenderPlcBlockDocument")
        {
            // The registered renderer probes file sharing at admission, before its document reader runs.
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal("filePath", (string?)body["error"]?["details"]?["parameter"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            body = new Host.PlcDocumentationTools().RenderPlcBlockDocumentV4(filePath).StructuredContent!;
        }
        AssertRefused(body);
        Assert.Equal("/filePath", (string?)body["error"]?["details"]?["parameter"]);
    }

    [Theory]
    [InlineData("PatchPlcBlockDocument")]
    [InlineData("BuildDeviceAmlDocument")]
    public void Real_document_writer_retains_the_output_and_sanitizes_its_verification_error(string name)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        bool patch = name == "PatchPlcBlockDocument";
        var file = Path.Combine(fixture.Root, patch ? "edited.xml" : "hardware.aml");
        string? verified = null;
        JsonObject Fail(FileInfo output)
        {
            verified = output.FullName;
            throw new IOException("Verification interrupted, token=secret-fixture-value\n   at PrivateVerificationFrame()");
        }
        JsonNode body;
        if (patch)
        {
            string input = Path.Combine(fixture.Root, "source.xml");
            File.WriteAllText(input, Document);
            var edit = new TiaMcp.Logic.V4.Domain.MemberStartValueEdit(System.Text.Json.JsonSerializer.SerializeToElement(new {
                action = "setMemberStartValue", section = "None", memberPath = "Motor/Speed", expectedValue = "1.5", value = "3.5" }));
            var tool = new Host.PlcOfflineTools { VerifyOutput = Fail };
            body = tool.PatchPlcBlockDocumentV4(input, new[] { edit }, (string)Editing.Inspect(Document)["documentFingerprint"]!,
                file, dryRun: false).StructuredContent!;
        }
        else
        {
            var service = new toolpipeline::TiaMcpServer.Siemens.Services.HardwareAmlService((_, _) =>
                throw new InvalidOperationException("Unexpected native call."), () => false, () => "");
            var tool = new Host.HardwareAmlTools(service) { VerifyOutput = Fail };
            var device = new TiaMcp.Logic.V4.Hmi.AmlDeviceSpec("PLC", "System:Device.S71500", Array.Empty<TiaMcp.Logic.V4.Hmi.DeviceItemSpec>());
            body = tool.BuildDeviceAmlDocumentV4(new TiaMcp.Logic.V4.Hmi.DeviceAmlSpec("Fixture", new[] { device }), file).StructuredContent!;
        }
        Assert.Equal(file, verified);
        var document = XDocument.Load(file);
        if (patch) Assert.Contains(document.Descendants(), e => e.Name.LocalName == "StartValue" && e.Value == "3.5");
        else Assert.Equal("CAEXFile", document.Root!.Name.LocalName);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        var evidence = body["data"]!["evidence"]!;
        Assert.Equal(file, (string?)evidence["filePath"]);
        Assert.Equal(file, (string?)evidence["cleanupRequired"]?[0]?["target"]);
        Assert.Contains("Inspect", (string)evidence["cleanupRequired"]![0]!["action"]!);
        Assert.Contains("Verification interrupted", (string?)evidence["nativeMessage"]);
        Assert.DoesNotContain("secret-fixture-value", body.ToJsonString());
        Assert.DoesNotContain("PrivateVerificationFrame", body.ToJsonString());
    }

    [Theory]
    [InlineData("cancel", "CANCELLED")]
    [InlineData("precondition", "PRECONDITION_FAILED")]
    [InlineData("argument", "INVALID_ARGUMENT")]
    public void Build_refused_after_candidate_creation_keeps_definite_outcome(string failure, string expectedCode)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("15.1");
        using var session = fixture.EnterSession();
        int imports = 0;
        string? candidate = null;
        var port = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, _) => {
            imports++; throw new InvalidOperationException("Unexpected import.");
        }, () => true, () => throw (failure switch {
            "cancel" => (Exception)new OperationCanceledException(),
            "precondition" => TiaMcp.Adapters.Contracts.AdapterPreconditionException.Failed("Project changed.", "project-changed", "softwarePath"),
            _ => new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Invalid project.", "softwarePath")
        }));
        var tool = new Host.PlcBuildTools(port) {
            ClassifyDocument = (string file, out string kind, out string name) => {
                candidate = file;
                return Host.PlcProgramImport.ClassifyPlcXml(file, out kind, out name);
            }
        };
        var spec = TiaMcp.Logic.V4.Construction.PlcArtifactSpec.Deserialize("udt", """{"name":"Drive","members":[{"name":"Speed","datatype":"Real"}]}""");
        var result = tool.PlcBuildAndImportV4("PLC", "udt", spec, dryRun: false);
        var body = Host.McpServer.ObserveSessionOutcome("BuildAndImportPlcArtifact", result, true, """{"dryRun":false}""").StructuredContent!;
        Assert.Equal(0, imports);
        Assert.True(File.Exists(candidate));
        Assert.Equal(expectedCode, (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(candidate, (string?)body["data"]?["filePath"]);
        Assert.Equal(candidate, (string?)body["data"]?["cleanupRequired"]?[0]?["target"]);
        if (failure == "precondition") Assert.Equal("project-changed", (string?)body["error"]?["details"]?["condition"]);
        Assert.False(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.HasUnknownOutcome(body));
        Assert.Empty(toolpipeline::TiaMcp.FoundationHost.UnsettledCalls.For(fixture.Worker).Snapshot());
    }

    [Fact]
    public void Library_bound_document_lists_its_members_for_reading_and_refuses_patches()
    {
        string xml = Document.Replace("<Name>Fixture", "<LibraryTypeGuid>type</LibraryTypeGuid><Name>Fixture")
            .Replace("Name=\"Motor\"", "Name=\"Motor\" ReadOnly=\"true\"")
            .Replace("<StartValue>1.5", "<Comment><MultiLanguageText Lang=\"en-US\">Motor reading</MultiLanguageText></Comment><StartValue>1.5");
        var info = Editing.Inspect(xml);
        Assert.True((bool?)info["libraryBound"]);
        var members = info["members"]!.AsArray();
        Assert.Equal(new[] { "Motor", "Motor/Speed", "Grp", "Grp/Inner", "Grp/Inner/Speed" }, members.Select(m => (string?)m!["memberPath"]));
        Assert.Equal("1.5", (string?)members[1]!["startValue"]);
        Assert.Equal("Motor reading", (string?)members[1]!["comments"]?["en-US"]);
        Assert.Empty(info["supportedPatchActions"]!.AsArray());
        const string change = """[{"action":"setMemberStartValue","section":"None","memberPath":"Motor/Speed","expectedValue":"1.5","value":"2.5"}]""";
        var refusal = Assert.Throws<InvalidOperationException>(() => Editing.Patch(xml, change, (string)info["documentFingerprint"]!));
        Assert.Contains("Library-connected", refusal.Message);
    }

    [Theory]
    [InlineData("integer", 1, null, 0, "at least 1")]
    [InlineData("integer", null, 10, 11, "at most 10")]
    [InlineData("integer", 1, 10, 0, "1..10")]
    [InlineData("integer", 1, 10, 11, "1..10")]
    [InlineData("number", 1, null, 0.5, "at least 1")]
    [InlineData("number", null, 10, 10.5, "at most 10")]
    [InlineData("number", 1, 10, 0.5, "1..10")]
    [InlineData("number", 1, 10, 10.5, "1..10")]
    public void Numeric_range_failures_keep_the_declared_type(string type, int? minimum, int? maximum, double value, string range)
    {
        var definition = new JsonObject { ["type"] = type };
        if (minimum.HasValue) definition["minimum"] = minimum.Value;
        if (maximum.HasValue) definition["maximum"] = maximum.Value;
        var schema = new TiaMcp.Logic.V4.Inputs.InputSchema(System.Text.Json.JsonSerializer.SerializeToElement(definition));
        var error = schema.Validate(System.Text.Json.JsonSerializer.SerializeToElement(value), "limit")!;
        var details = Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(error.Details);
        Assert.Equal(type, details.Expected);
        Assert.Equal("/limit", details.Parameter);
        Assert.Empty(details.AllowedValues);
        Assert.Contains("/limit must be " + range, error.Message);
    }

    [Theory]
    [InlineData("string", "Length", "\"\"", "\"ab\"")]
    [InlineData("array", "Items", "[]", "[1,2]")]
    [InlineData("object", "Properties", "{}", "{\"a\":1,\"b\":2}")]
    public void Size_bounds_preserve_their_distinct_error_details(string type, string keyword, string below, string above)
    {
        var definition = new JsonObject { ["type"] = type, ["min" + keyword] = 1, ["max" + keyword] = 1 };
        var schema = new TiaMcp.Logic.V4.Inputs.InputSchema(System.Text.Json.JsonSerializer.SerializeToElement(definition));
        using var small = System.Text.Json.JsonDocument.Parse(below);
        using var large = System.Text.Json.JsonDocument.Parse(above);
        var minimum = Assert.IsType<TiaMcp.Logic.V4.InvalidArgumentDetails>(schema.Validate(small.RootElement, "field")!.Details);
        Assert.Equal("/field", minimum.Parameter);
        Assert.Equal(type, minimum.Expected);
        Assert.Empty(minimum.AllowedValues);
        var maximum = Assert.IsType<TiaMcp.Logic.V4.LimitExceededDetails>(schema.Validate(large.RootElement, "field")!.Details);
        Assert.Equal("field", maximum.Parameter);
        Assert.Equal(1, maximum.Limit);
        Assert.Equal(2, maximum.Actual);
    }

    [Fact]
    public void Each_invalid_range_names_its_own_field()
    {
        var schema = TiaMcp.Logic.V4.Inputs.InputSchema.Object(new Dictionary<string, TiaMcp.Logic.V4.Inputs.InputSchema> {
            ["limit"] = TiaMcp.Logic.V4.Inputs.InputSchema.Integer(1, 100),
            ["offset"] = TiaMcp.Logic.V4.Inputs.InputSchema.Integer(0, 200)
        });
        var input = System.Text.Json.JsonSerializer.SerializeToElement(new { limit = 101, offset = -1 });
        var message = schema.Validate(input, "arguments")!.Message;
        Assert.Matches(@"/limit[^./]*1\.\.100", message);
        Assert.Matches(@"/offset[^./]*0\.\.200", message);
    }

    [Fact]
    public void Conflicting_full_and_section_relative_member_paths_are_ambiguous()
    {
        const string xml = """
            <Document><SW.Blocks.GlobalDB><AttributeList><Name>Fixture</Name><Interface><Sections><Section Name="Static">
            <Member Name="Motor" Datatype="&quot;Drive&quot;"><Sections><Section Name="None">
            <Member Name="Speed" Datatype="Real"><StartValue>1</StartValue></Member>
            <Member Name="Motor" Datatype="Struct"><Member Name="Speed" Datatype="Real"><StartValue>1</StartValue></Member></Member>
            </Section></Sections></Member></Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>
            """;
        var info = Editing.Inspect(xml);
        const string change = """[{"action":"setMemberStartValue","section":"None","memberPath":"Motor/Speed","expectedValue":"1","value":"2"}]""";
        var refusal = Assert.Throws<ArgumentException>(() => Editing.Patch(xml, change, (string)info["documentFingerprint"]!));
        Assert.Contains("ambiguous", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(info["members"]!.AsArray(), row => (string?)row!["memberPath"] == "Motor/Speed");
    }

    [Fact]
    public void Capabilities_distinguish_editable_start_values_from_comment_only_members()
    {
        string xml = Document.Replace("Name=\"Motor\"", "Name=\"Motor\" Informative=\"true\"")
            .Replace("Name=\"Grp\" Datatype=\"Struct\">", "Name=\"Grp\" Datatype=\"Struct\"><StartValue>complex</StartValue>")
            .Replace("<Member Name=\"Inner\"", "<Member Name=\"NoStart\" Datatype=\"Int\"/><Member Name=\"Inner\"");
        var info = Editing.Inspect(xml);
        var members = info["members"]!.AsArray().ToDictionary(row => (string)row!["memberPath"]!, row => row!);
        Assert.DoesNotContain("Motor", members.Keys);
        Assert.DoesNotContain("Motor/Speed", members.Keys);
        Assert.Null(members["Grp"]["startValue"]);
        Assert.Null(members["Grp/NoStart"]["startValue"]);
        Assert.Equal("2.5", (string?)members["Grp/Inner/Speed"]["startValue"]);
        Assert.Contains("non-null startValue", (string)info["scope"]!);
        var changes = new[] { "Grp", "Grp/NoStart" }.Select(path => new { action = "setMemberComment", section = "Static",
            memberPath = path, culture = "en-US", expectedValue = "", value = "Editable comment" });
        string edited = Editing.Patch(xml, System.Text.Json.JsonSerializer.Serialize(changes), (string)info["documentFingerprint"]!);
        Assert.Equal(2, XDocument.Parse(edited).Descendants().Count(e => e.Name.LocalName == "MultiLanguageText" && e.Value == "Editable comment"));
    }
}
