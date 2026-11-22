extern alias toolpipeline;
using System.Text.Json.Nodes;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Service = toolpipeline::TiaMcpServer.Siemens.Services.HmiDescribeService;
using Xunit;

public sealed class HmiDescribeToolsTests
{
    [Fact]
    public void Plc_software_cannot_produce_a_successful_HMI_program_description()
    {
        var service = new Service((_, args) => {
            Assert.Equal("MCP_RC_PLC", (string?)args["request"]?["SoftwarePath"]);
            return new JsonObject { ["MayHaveChanged"] = false, ["OutcomeUnknown"] = false,
                ["Failure"] = new JsonObject { ["Code"] = "NotFound", ["Message"] = "No HMI software at 'MCP_RC_PLC'." } };
        }, () => "fixture");
        var result = new Host.HmiDescribeTools(service).GetHmiProgramInfoV4("MCP_RC_PLC").StructuredContent!;
        Assert.False((bool?)result["ok"]);
        Assert.Equal("NOT_FOUND", (string?)result["error"]?["code"]);
    }

    [Fact]
    public void Worker_truncation_reaches_completeness()
    {
        var service = new Service((_, _) => new JsonObject { ["Value"] = JsonNode.Parse(
            "{\"Message\":\"OK\",\"ObjectKind\":\"Software\",\"ObjectPath\":\"HMI_RT_1\","
            + "\"TypeName\":\"Siemens.Engineering.HmiUnified.HmiSoftware\","
            + "\"Members\":[{\"Kind\":\"Property\",\"Name\":\"Screens\",\"Type\":\"X\"}],"
            + "\"Meta\":{\"truncated\":true,\"totalMembers\":54}}") }, () => "fixture");
        var result = new Host.HmiDescribeTools(service).DescribeHmiSoftwareV4("HMI_RT_1", 5).StructuredContent!;
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal("partial", (string?)result["meta"]?["completeness"]);
        Assert.Equal(54, (int?)result["data"]?["totalMembers"]);
        Assert.Equal(1, (int?)result["data"]?["memberCount"]);
    }
}
