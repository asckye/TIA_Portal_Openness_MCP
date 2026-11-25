extern alias plcpathfixture;
extern alias toolpipeline;
using PathEngine = plcpathfixture::TiaMcp.Adapters.PlcFoundationEngine;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcpServer.ModelContextProtocol;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

public sealed class PlcBlockImportAdmissionTests
{
    [Fact]
    public void Delete_verifies_the_original_parent_even_when_another_table_has_the_same_name()
    {
        var engine = new PathEngine("16");
        var root = engine.Plc.TagTableGroup;
        var parent = new plcpathfixture::Siemens.Engineering.SW.Tags.PlcTagTableGroup { Name = "Sub" };
        root.Groups.Add(parent);
        var other = new plcpathfixture::Siemens.Engineering.SW.Tags.PlcTagTable { Name = "Tags" };
        var selected = new plcpathfixture::Siemens.Engineering.SW.Tags.PlcTagTable { Name = "Tags" };
        root.TagTables.Add(other);
        parent.TagTables.Add(selected);
        selected.OnDelete = () => parent.TagTables.Remove(selected);
        var result = engine.Artifacts.DeletePlcTagTable("PLC", "Sub/Tags", false);
        Assert.Equal(true, result["verifiedAbsent"]);
        Assert.Same(other, Assert.Single(root.TagTables));
    }

    [Fact]
    public void Restoring_unit_properties_cannot_settle_a_written_comment()
    {
        var engine = new PathEngine("16");
        var unit = new plcpathfixture::Siemens.Engineering.SW.Units.PlcUnit();
        var comment = new plcpathfixture::Siemens.Engineering.MultilingualTextItem();
        unit.Comment.Items.Add(comment);
        var result = engine.UpdateUnitComment(unit);
        Assert.Equal("original", unit.Author);
        Assert.Equal("after", comment.Retained);
        Assert.Equal(false, result["rollbackCompleted"]);
        Assert.Equal("unknown", result["rollbackOutcome"]);
    }

    [Fact]
    public void Refused_unit_comment_after_verified_scalar_rollback_is_definite()
    {
        var engine = new PathEngine("16");
        var unit = new plcpathfixture::Siemens.Engineering.SW.Units.PlcUnit();
        var comment = new plcpathfixture::Siemens.Engineering.MultilingualTextItem { Refuse = true };
        unit.Comment.Items.Add(comment);
        var result = engine.UpdateUnitComment(unit);
        Assert.Equal("original", unit.Author);
        Assert.Equal("before", comment.Retained);
        Assert.True((bool?)result["rollbackCompleted"]);
        Assert.False(engine.UnitMutationStarted);
    }

    private sealed class UnreadableTagGroup
    {
        public object TagTables => throw new IOException("Collection unavailable.");
        public object[] Groups => [];
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Tag_table_delete_requires_a_readable_fresh_group(bool disappeared)
    {
        var engine = new PathEngine("20");
        var group = engine.Plc.TagTableGroup;
        var table = new plcpathfixture::Siemens.Engineering.SW.Tags.PlcTagTable { Name = "Tags" };
        group.TagTables.Add(table);
        int deletes = 0;
        table.OnDelete = () => { group.TagTables.Remove(table); deletes++; };
        engine.TagGroupReader = () => deletes == 0 ? group : disappeared ? null : new UnreadableTagGroup();
        Assert.ThrowsAny<Exception>(() => engine.Artifacts.DeletePlcTagTable("PLC", "Tags", false));
        Assert.Equal(1, deletes);
    }

    [Fact]
    public void Seed_cannot_confirm_an_import_from_its_file_name()
    {
        var root = Directory.CreateTempSubdirectory("seed-blocks-");
        try
        {
            var engine = new PathEngine("15.1");
            engine.Plc.BlockGroup.EmptyImportResult = true;
            File.WriteAllText(Path.Combine(root.FullName, "unrelated.xml"), Before);
            var error = Assert.Throws<PlcSoftwareException>(() => engine.Artifacts.ImportBlocksFromDirectory("PLC", "", root.FullName));
            Assert.Equal("OpennessError", error.Code);
            var evidence = Assert.IsType<Dictionary<string, object?>>(error.Data["nativeResultEvidence"]);
            Assert.Empty(Assert.IsAssignableFrom<IEnumerable<string>>(evidence["imported"]));
            Assert.Contains("unrelated.xml", System.Text.Json.JsonSerializer.Serialize(evidence["failed"]));
            Assert.Equal("FC_A", Assert.Single(engine.Plc.BlockGroup.Blocks).Name);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Seed_preserves_the_names_confirmed_before_an_unconfirmed_import()
    {
        var root = Directory.CreateTempSubdirectory("seed-partial-");
        try
        {
            var engine = new PathEngine("15.1");
            var group = engine.Plc.BlockGroup;
            group.AfterImport = block => group.EmptyImportResult = block.Name == "FC_B";
            File.WriteAllText(Path.Combine(root.FullName, "a.xml"), Before);
            File.WriteAllText(Path.Combine(root.FullName, "b.xml"), Before.Replace("<Name>FC_A</Name>", "<Name>FC_B</Name>"));
            var error = Assert.Throws<PlcSoftwareException>(() => engine.Artifacts.ImportBlocksFromDirectory("PLC", "", root.FullName));
            var evidence = Assert.IsType<Dictionary<string, object?>>(error.Data["nativeResultEvidence"]);
            Assert.Equal("FC_A", Assert.Single(Assert.IsAssignableFrom<IEnumerable<string>>(evidence["imported"])));
            Assert.Contains("b.xml", System.Text.Json.JsonSerializer.Serialize(evidence["failed"]));
            Assert.Equal(new[] { "FC_A", "FC_B" }, group.Blocks.Select(block => block.Name));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Seed_response_keeps_confirmed_blocks_and_the_file_that_lost_its_reply()
    {
        using var scope = Host.HardwareContract.UseRelease("20");
        var reference = Directory.CreateTempSubdirectory("seed-reference-");
        try
        {
            var blocks = Directory.CreateDirectory(Path.Combine(reference.FullName, "plc", "blocks"));
            File.WriteAllText(Path.Combine(blocks.FullName, "a.xml"), Before.Replace("V15.1", "V20"));
            var service = new toolpipeline::TiaMcpServer.Siemens.Services.PlcOrganisationPortService((_, args) => {
                Assert.Equal("importBlockDirectory", (string?)args["request"]?["Action"]);
                return new JsonObject { ["MayHaveChanged"] = true, ["OutcomeUnknown"] = true, ["Failure"] = new JsonObject {
                    ["Code"] = "OpennessError", ["Message"] = "No confirmed object names", ["Evidence"] = new JsonObject {
                        ["imported"] = new JsonArray("FC_A"),
                        ["failed"] = new JsonArray(new JsonObject { ["Path"] = "b.xml", ["Error"] = "No confirmed object names" })
                    } } };
            }, () => true, () => "Fixture.ap20");
            var result = service.SeedProjectFromReference("PLC", "HMI", reference.FullName);
            Assert.Equal("plc:block:FC_A", Assert.Single(result.Imported!));
            Assert.Equal("b.xml", Assert.Single(result.Failed!).Path);
            Assert.True((bool?)result.Meta?["writeOutcomeUnknown"]);
        }
        finally { reference.Delete(true); }
    }

    private const string FcSpec = """
        {"blockName":"FC_Ready","blockNumber":10,"inputs":[],"outputs":[{"name":"Ready","datatype":"Bool"}],
         "structuredText":{"operations":[{"op":"assign","target":"#Ready","literalValue":"TRUE"}]}}
        """;

    private static string GeneratedScl() => (string)PlcBuilderToolJson.ComposeFcBlock(FcSpec)["xml"]!;

    internal const string Scl15 = """
        <Document><Engineering version="V15.1"/><SW.Blocks.FC ID="0"><AttributeList>
        <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v3">
        <Section Name="Output"><Member Name="Ready" Datatype="Bool"/></Section></Sections></Interface>
        <Name>FC_Ready</Name><Number>10</Number><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
        <ObjectList><SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits"><AttributeList><NetworkSource>
        <StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v2">
        <Access Scope="LocalVariable" UId="21"><Symbol><Component Name="Ready"/></Symbol></Access><Token Text=":="/>
        <Access Scope="LiteralConstant" UId="22"><Constant><ConstantType>Bool</ConstantType><ConstantValue>TRUE</ConstantValue></Constant></Access>
        <Token Text=";"/></StructuredText></NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage>
        </AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>
        """;

    [Theory, InlineData(false), InlineData(true)]
    public void Generated_SCL_imports_the_exact_reviewed_bytes(bool bom)
    {
        foreach (string release in new[] { "21", "15.1" })
        {
            var xml = release == "15.1" ? Scl15 : GeneratedScl();
            var bytes = Encoding.UTF8.GetBytes(xml);
            SimaticMlImportWindow.Require(release, bytes, "importPath");
            var document = SimaticMlImportWindow.Load(bytes);
            Assert.Contains(document.Descendants(), e => e.Name.LocalName == "StructuredText" && e.HasElements);
            SimaticMlImportWindow.RequireBlockProgram(release, document, "importPath");
            string file = Path.Combine(AppContext.BaseDirectory, "FC-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, xml, new UTF8Encoding(bom));
                var engine = new PathEngine(release);
                engine.Plc.BlockGroup.OnCall = _ => File.WriteAllText(file, Before);
                Assert.True(engine.Artifacts.ImportBlock("PLC", "", file, true));
                Assert.Equal("FC_Ready", Assert.Single(engine.Plc.BlockGroup.Blocks).Name);
                Assert.Equal(Before, File.ReadAllText(file));
            }
            finally { File.Delete(file); }
        }
    }

    [Theory, InlineData("udt"), InlineData("tags")]
    public void V14_declarations_pass_the_same_import_guard_without_a_programming_language(string kind)
    {
        var artifact = kind == "udt" ? PlcBuilderToolJson.BuildUdt("""{"name":"Motor","members":[{"name":"Ready","datatype":"Bool"}]}""", "14sp1")
            : PlcBuilderToolJson.BuildTagTableForRelease("""{"name":"Tags","tags":[{"name":"Ready","datatype":"Bool","address":"%M0.0"}]}""", "14sp1");
        var bytes = Encoding.UTF8.GetBytes((string)artifact["xml"]!);
        SimaticMlImportWindow.Require("14sp1", bytes, "importPath");
        var document = SimaticMlImportWindow.Load(bytes);
        Assert.DoesNotContain(document.Descendants(), e => e.Name.LocalName == "ProgrammingLanguage");
        SimaticMlImportWindow.RequireBlockProgram("14sp1", document, "importPath");
        string file = Path.Combine(AppContext.BaseDirectory, "declaration-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllBytes(file, bytes);
            var engine = new PathEngine("14sp1");
            if (kind == "udt")
            {
                Assert.True(engine.Artifacts.ImportType("PLC", "", file, true));
                Assert.Equal("Motor", Assert.Single(engine.Plc.TypeGroup.Types).Name);
            }
            else
            {
                engine.Artifacts.ImportPlcTagTable("PLC", "", file, true);
                Assert.Equal("Tags", Assert.Single(engine.Plc.TagTableGroup.TagTables).Name);
            }
        }
        finally { File.Delete(file); }
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Incomplete_SCL_refuses_single_Overwrite_and_directory_imports_before_the_first_native_write(bool directory)
    {
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "blocks-" + Guid.NewGuid().ToString("N")));
        try
        {
            var engine = new PathEngine("14sp1");
            int writes = 0;
            engine.Plc.BlockGroup.OnCall = _ => writes++;
            string oldRelease = Before.Replace("V15.1", "V14 SP1");
            File.WriteAllText(Path.Combine(root.FullName, "A.xml"), oldRelease);
            string file = Path.Combine(root.FullName, "Z.xml");
            File.WriteAllText(file, oldRelease.Replace("FC_A", "FC_Z").Replace("LAD", "SCL"));
            var refusal = Assert.Throws<AdapterPreconditionException>(() => {
                if (directory) engine.Artifacts.ImportBlocksFromDirectory("PLC", "", root.FullName);
                else engine.Artifacts.ImportBlock("PLC", "", file, true);
            });
            Assert.Equal("incomplete-block-xml", refusal.Condition);
            Assert.Equal(0, writes);
            Assert.Empty(engine.Plc.BlockGroup.Blocks);
        }
        finally { root.Delete(true); }
    }

    [Theory, InlineData("14sp1", "V14 SP1"), InlineData("15.1", "V14 SP1"), InlineData("15.1", ""), InlineData("21", "V21.unknown")]
    public void Override_refuses_incomplete_SCL_even_when_the_candidate_inherits_the_live_language(string release, string producer)
    {
        var document = XDocument.Parse(GeneratedScl());
        document.Descendants().Where(e => e.Name.LocalName == "ProgrammingLanguage").Remove();
        document.Root!.Element("Engineering")!.Remove();
        if (producer.Length != 0) document.Root.AddFirst(new XElement("Engineering", new XAttribute("version", producer)));
        var error = Assert.Throws<AdapterPreconditionException>(() => SimaticMlImportWindow.RequireBlockProgram(release, document, "importPath", "SCL"));
        Assert.Equal("incomplete-block-xml", error.Condition);
        Assert.Equal("importPath", error.ParamName);
    }

    private const string Before = """
        <Document><Engineering version="V15.1"/><SW.Blocks.FC ID="0"><AttributeList>
        <HeaderAuthor>Author</HeaderAuthor><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v3">
        <Section Name="Input"><Member Name="Run" Datatype="Bool"><AttributeList>
        <BooleanAttribute Name="ExternalAccessible">true</BooleanAttribute><BooleanAttribute Name="ExternalVisible">true</BooleanAttribute>
        </AttributeList></Member><Member Name="Stop" Datatype="Bool"/></Section></Sections></Interface>
        <Name>FC_A</Name><Number>7</Number><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
        <ObjectList><SW.Blocks.CompileUnit ID="1"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit>
        <SW.Blocks.CompileUnit ID="2"><AttributeList><ProgrammingLanguage>FBD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit></ObjectList>
        </SW.Blocks.FC></Document>
        """;

    [Theory, InlineData("reordered", true), InlineData("value", false), InlineData("members", false), InlineData("networks", false)]
    public void Readback_only_ignores_AttributeList_order(string difference, bool equal)
    {
        var candidate = XDocument.Parse(Before);
        candidate.Descendants("HeaderAuthor").Remove();
        candidate.Descendants().First(e => (string?)e.Attribute("Name") == "ExternalAccessible").Remove();
        var preserved = new JsonArray();
        var plan = XDocument.Parse(PlcDocumentEditing.PrepareImport(Before, candidate.ToString(), preserved));
        Assert.Contains(preserved, value => value?.ToString() == "HeaderAuthor");
        var readback = XDocument.Parse(Before);
        if (difference == "value") readback.Descendants().First(e => (string?)e.Attribute("Name") == "ExternalVisible").Value = "false";
        if (difference is "members" or "networks")
        {
            var first = readback.Descendants().First(e => e.Name.LocalName == (difference == "members" ? "Member" : "SW.Blocks.CompileUnit"));
            var parent = first.Parent!;
            first.Remove();
            parent.Add(first);
        }
        Assert.Equal(equal, PlcDocumentEditing.Canonical(plan) == PlcDocumentEditing.Canonical(readback));
    }

    [Fact]
    public void Seed_reads_the_substituted_block_before_staging_files()
    {
        string root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "seed-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var blocks = Directory.CreateDirectory(Path.Combine(root, "plc", "blocks"));
            File.WriteAllText(Path.Combine(blocks.FullName, "FC.xml"), Before.Replace("V15.1", "V14 SP1").Replace("LAD", "{{language}}"));
            var placeholders = new JsonObject { ["language"] = "SCL" };
            var refusal = Assert.Throws<AdapterPreconditionException>(() => SeedReferenceLayout.Read(root, "14sp1", placeholders));
            Assert.Equal("incomplete-block-xml", refusal.Condition);
            Assert.Equal("referenceDir", refusal.ParamName);
            placeholders["language"] = "LAD";
            Assert.Single(SeedReferenceLayout.Read(root, "14sp1", placeholders).PlannedFiles);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory, InlineData(true), InlineData(false)]
    public void Patch_warns_and_still_edits_an_interface_only_SCL_file(bool preview)
    {
        using var scope = Host.HardwareContract.UseRelease("14sp1");
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "patch-" + Guid.NewGuid().ToString("N")));
        try
        {
            string source = Path.Combine(root.FullName, "source.xml"), output = Path.Combine(root.FullName, "patched.xml");
            string xml = Before.Replace("V15.1", "V14 SP1").Replace("LAD", "SCL");
            File.WriteAllText(source, xml);
            string hash = PlcDocumentEditing.HashText(PlcDocumentEditing.Canonical(XDocument.Parse(xml)));
            var response = new Host.PlcOfflineTools().PatchPlcBlockDocument(source,
                """[{"action":"setMemberComment","section":"Input","memberPath":"Run","culture":"en-US","expectedValue":"","value":"Run motor"}]""",
                hash, output, preview);
            Assert.Equal(true, (bool?)response.Meta["operationSuccess"]);
            Assert.Contains("SCL", Assert.Single(response.Meta["warnings"]!.AsArray())!.ToString());
            var body = Host.PlcToolContract.Map("PatchPlcBlockDocument", response, !preview, false).StructuredContent!;
            Assert.True((bool?)body["ok"]);
            Assert.Contains(body["meta"]!["warnings"]!.AsArray(), warning => (string?)warning!["code"] == "NATIVE_CAPABILITY_LIMIT");
            Assert.Equal(!preview, File.Exists(output));
            if (!preview) Assert.Contains("Run motor", File.ReadAllText(output));
        }
        finally { root.Delete(true); }
    }
}
