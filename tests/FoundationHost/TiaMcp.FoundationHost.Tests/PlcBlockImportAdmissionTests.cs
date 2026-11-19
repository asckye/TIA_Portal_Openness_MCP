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
    private const string FcSpec = """
        {"blockName":"FC_Ready","blockNumber":10,"inputs":[],"outputs":[{"name":"Ready","datatype":"Bool"}],
         "structuredText":{"operations":[{"op":"assign","target":"#Ready","literalValue":"TRUE"}]}}
        """;

    private static string GeneratedScl() => (string)PlcBuilderToolJson.ComposeFcBlock(FcSpec)["xml"]!;

    [Theory, InlineData(false), InlineData(true)]
    public void Generated_SCL_imports_the_exact_reviewed_bytes(bool bom)
    {
        var bytes = Encoding.UTF8.GetBytes(GeneratedScl());
        SimaticMlImportWindow.Require("21", bytes, "importPath");
        var document = SimaticMlImportWindow.Load(bytes);
        Assert.Contains(document.Descendants(), e => e.Name.LocalName == "StructuredText" && e.HasElements);
        SimaticMlImportWindow.RequireBlockProgram("21", document, "importPath");
        string file = Path.Combine(AppContext.BaseDirectory, "FC-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(file, GeneratedScl(), new UTF8Encoding(bom));
            var engine = new PathEngine("21");
            engine.Plc.BlockGroup.OnCall = _ => File.WriteAllText(file, Before);
            Assert.True(engine.Artifacts.ImportBlock("PLC", "", file, true));
            Assert.Equal("FC_Ready", Assert.Single(engine.Plc.BlockGroup.Blocks).Name);
            Assert.Equal(Before, File.ReadAllText(file));
        }
        finally { File.Delete(file); }
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
