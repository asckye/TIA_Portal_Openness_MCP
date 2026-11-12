extern alias enginehost;
using System.Text.Json.Nodes;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Service = enginehost::TiaMcpServer.Siemens.Services.HmiDescribeService;
using Xunit;

public sealed class HmiDescribeToolsTests
{
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
