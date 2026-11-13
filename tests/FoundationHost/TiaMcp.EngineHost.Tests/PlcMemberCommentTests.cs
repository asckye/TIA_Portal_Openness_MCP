using System.Text.Json.Nodes;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

public sealed class PlcMemberCommentTests
{
    [Fact]
    public void File_capabilities_and_patch_tools_accept_a_single_udt_export_without_native_calls()
    {
        const string xml = """
            <Document><Engineering version="V21"/><SW.Types.PlcStruct ID="0"><AttributeList><Name>Motor</Name>
            <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="None">
            <Member Name="Speed" Datatype="Int"><StartValue>1</StartValue></Member>
            </Section></Sections></Interface></AttributeList></SW.Types.PlcStruct></Document>
            """;
        string directory = Path.Combine(AppContext.BaseDirectory, "udt-comments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "Motor.xml"); File.WriteAllText(file, xml);
            var capabilities = new PlcReadModifyTools(null!).ReadPlcBlockEditCapabilitiesV4(filePath: file);
            Assert.False(capabilities.IsError, capabilities.StructuredContent?.ToJsonString());
            var inspection = capabilities.StructuredContent!["data"]!["evidence"]!["data"]!;
            Assert.Equal("SW.Types.PlcStruct", (string?)inspection["kind"]);
            Assert.Empty(inspection["networks"]!.AsArray());
            Assert.Empty(inspection["members"]![0]!["comments"]!.AsObject());
            var edits = DomainValidation.Read<BlockEdit[]>("""
                [{"action":"setMemberComment","section":"None","memberPath":"Speed","culture":"zh-CN","expectedValue":"","value":"转速"}]
                """);
            var patch = new PlcOfflineTools().PatchPlcBlockDocumentV4(file, edits, (string)inspection["documentFingerprint"]!);
            Assert.False(patch.IsError, patch.StructuredContent?.ToJsonString());
            Assert.Equal("转速", (string?)patch.StructuredContent!["data"]!["evidence"]!["result"]!["members"]![0]!["comments"]!["zh-CN"]);
            Assert.Equal(xml, File.ReadAllText(file));
            Assert.Single(Directory.GetFiles(directory));
            string output = Path.Combine(directory, "Motor.edited.xml");
            var written = new PlcOfflineTools().PatchPlcBlockDocumentV4(file, edits, (string)inspection["documentFingerprint"]!, output, dryRun: false);
            Assert.False(written.IsError, written.StructuredContent?.ToJsonString());
            Assert.Equal("转速", (string?)PlcDocumentEditing.Inspect(File.ReadAllText(output))["members"]![0]!["comments"]!["zh-CN"]);
            Assert.Equal(xml, File.ReadAllText(file));
        }
        finally { Directory.Delete(directory, true); }
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
    public void Every_release_serves_the_member_comment_example_and_both_import_workflows(string release)
    {
        var call = ToolUsageCatalog.ServedCall("PatchPlcBlockDocument", release)!;
        var edit = Assert.Single(call["arguments"]!["changes"]!.AsArray(), c => (string?)c!["action"] == "setMemberComment")!;
        Assert.Equal(("Static", "Motor/Speed", "zh-CN", ""),
            ((string?)edit["section"], (string?)edit["memberPath"], (string?)edit["culture"], (string?)edit["expectedValue"]));
        var reading = call["resultReading"]!.AsArray().Select(row => (string)row!).ToArray();
        Assert.Contains(reading, row => row.Contains("ImportPlcBlockVerified", StringComparison.Ordinal));
        Assert.Contains(reading, row => row.Contains("ExportPlcType", StringComparison.Ordinal)
            && row.Contains("ImportPlcType overwrite=true", StringComparison.Ordinal) && row.Contains("until compiled", StringComparison.Ordinal));
    }
}
