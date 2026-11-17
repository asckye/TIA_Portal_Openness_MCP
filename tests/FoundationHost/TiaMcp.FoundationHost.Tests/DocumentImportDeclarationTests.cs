using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;

public sealed class DocumentImportDeclarationTests
{
    private const string BlockName = "示例数据块_1";
    private const string ExportedStartValue = """
        {
            S7_Optimized := "TRUE";
            S7_StandardRetain := "FALSE";
            S7_Version := "0.1"
        }
        DATA_BLOCK 示例数据块_1
            VAR
                "Value" : Int;
            END_VAR

            "Value" := 20;

        END_DATA_BLOCK
        """;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Tia_export_with_postamble_passes_admission(bool unicodeName, bool bom)
    {
        string name = unicodeName ? BlockName : "ExportedStartValues";
        byte[] bytes = Encoding.UTF8.GetBytes(ExportedStartValue.Replace(BlockName, name));
        if (bom) bytes = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
        Assert.Equal(name, PlcDocumentDeclaration.Parse(bytes));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Assignments_match_quoted_or_plain_declared_members(bool quotedDeclaration, bool quotedAssignment)
    {
        string Declaration(string name) => quotedDeclaration ? "\"" + name + "\"" : name;
        string Assignment(string name) => quotedAssignment ? "\"" + name + "\"" : name;
        string code = "DATA_BLOCK Demo\nVAR\n" + Declaration("Flag") + " : Bool;\n" + Declaration("Count")
            + " : DInt;\nEND_VAR\n" + Assignment("fLaG") + " := true;\n" + Assignment("Count") + " := -12;\nEND_DATA_BLOCK";
        Assert.Equal("Demo", PlcDocumentDeclaration.Parse(Encoding.UTF8.GetBytes(code)));
    }

    [Theory]
    [InlineData("Other := 1;")]
    [InlineData("\"Other\" := 1;")]
    [InlineData("Count 20;")]
    [InlineData("\"Count\" 20;")]
    [InlineData("Flag := 1;")]
    [InlineData("Count := true;")]
    [InlineData("Count := 1 + 2;")]
    [InlineData("Count := \"20\";")]
    [InlineData("Count := ;")]
    [InlineData("Count := 20")]
    [InlineData("BEGIN")]
    [InlineData("BEGIN Count := 20;")]
    public void Invalid_postamble_stays_refused(string postamble)
    {
        string code = "DATA_BLOCK Demo VAR Flag : Bool; Count : Int; END_VAR\n" + postamble + "\nEND_DATA_BLOCK";
        Assert.Throws<AdapterPreconditionException>(() => PlcDocumentDeclaration.Parse(Encoding.UTF8.GetBytes(code)));
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public void Unicode_export_preview_binds_exact_identity_without_native_calls(string release)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "document-preview");
        var request = new PlcDocumentImportRequest {
            Release = release, Project = Path.Combine(AppContext.BaseDirectory, "Example.ap" + release), Software = "PLC",
            Directory = directory, Name = BlockName, ProcessId = 123, TargetIdentity = "synthetic/project/plc/group"
        };
        string codePath = Path.Combine(directory, BlockName + ".s7dcl");
        string[] paths = release == "20" ? new[] { codePath, Path.ChangeExtension(codePath, ".s7res") } : new[] { codePath };
        int calls = 0;
        var result = PlcDocumentImportPolicy.Run(request, () => paths,
            path => new MemoryStream(Encoding.UTF8.GetBytes(path == codePath ? ExportedStartValue
                : "MultiLingualTexts:\n  - id: MLC_Example\n    en-US: Example\n"), false),
            () => Array.Empty<string>(), _ => false, () => { }, () => { calls++; return new PlcDocumentImportNative(); }, _ => false);
        Assert.Equal(BlockName, result.DeclaredName);
        Assert.Equal("planned", result.Status);
        Assert.False(result.Attempted);
        Assert.Equal(0, calls);
        var arguments = new JsonObject {
            ["softwarePath"] = request.Software, ["groupPath"] = request.Group, ["importPath"] = directory,
            ["fileNameWithoutExtension"] = BlockName, ["dryRun"] = true
        };
        DocumentImportContract.Validate(JsonSerializer.SerializeToNode(result), arguments);
    }

    [Fact]
    public void Inline_initializers_and_empty_postamble_remain_admitted()
    {
        const string code = "DATA_BLOCK Demo VAR Flag : Bool := true; Count : DInt := -12; END_VAR END_DATA_BLOCK";
        Assert.Equal("Demo", PlcDocumentDeclaration.Parse(Encoding.ASCII.GetBytes(code)));
    }

    [Fact]
    public void Invalid_utf8_and_control_characters_stay_refused()
    {
        Assert.Throws<AdapterPreconditionException>(() => PlcDocumentDeclaration.Parse(new byte[] { 0xc3, 0x28 }));
        foreach (char control in new[] { '\0', '\u001f', '\u007f' })
            Assert.Throws<AdapterPreconditionException>(() => PlcDocumentDeclaration.Parse(
                Encoding.UTF8.GetBytes("DATA_BLOCK Demo VAR Flag : Bool; END_VAR END_DATA_BLOCK" + control)));
    }
}
