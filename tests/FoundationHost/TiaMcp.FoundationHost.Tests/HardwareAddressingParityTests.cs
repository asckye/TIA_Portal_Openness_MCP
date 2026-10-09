extern alias enginehost;
extern alias enginefixture;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Xunit;
using HostContract = enginehost::TiaMcpServer.ModelContextProtocol.HardwareContract;
using HostResponse = enginehost::TiaMcpServer.ModelContextProtocol.ResponseMessage;

public sealed class HardwareAddressingParityTests
{
    [Theory]
    [InlineData("GetDeviceAddressing", "{\"success\":true,\"dataComplete\":false,\"records\":[],\"expectedCount\":0,\"offset\":0,\"limit\":100}", false, "succeeded")]
    [InlineData("GetDeviceAddressing", "{\"success\":false,\"status\":\"ReadOrWriteFailed\",\"error\":\"private stack\"}", false, "read-failed")]
    [InlineData("GetDeviceAddressing", "{\"success\":false,\"status\":\"NotFound\"}", false, "rejected-before-operation")]
    [InlineData("SetDeviceAddress", "{\"success\":true,\"mayHaveChanged\":true,\"before\":{\"startAddress\":1},\"after\":{\"startAddress\":2}}", true, "succeeded")]
    [InlineData("SetDeviceAddress", "{\"success\":false,\"mayHaveChanged\":true,\"postStateKnown\":true}", true, "failed")]
    [InlineData("SetDeviceAddress", "{\"success\":false,\"mayHaveChanged\":true,\"writeOutcomeUnknown\":true}", true, "unknown")]
    [InlineData("SetDeviceAddress", "{\"success\":false,\"applied\":[\"A\"],\"rejected\":[\"B\"],\"postStateKnown\":true}", true, "partial")]
    [InlineData("SetDeviceItemIoAddress", "{\"success\":true,\"verified\":true,\"postStateKnown\":true,\"beforeStartAddress\":1,\"afterStartAddress\":2}", true, "succeeded")]
    [InlineData("SetDeviceItemIoAddress", "{\"success\":false,\"mayHaveChanged\":true,\"writeOutcomeUnknown\":true,\"beforeStartAddress\":1,\"afterStartAddress\":null}", true, "unknown")]
    [InlineData("SetDeviceItemIoAddress", "{\"feasible\":true,\"dryRun\":true,\"currentStartAddress\":1}", false, "succeeded")]
    public void Released_hardware_mapping_matches_the_host(string tool, string evidence, bool write, string outcome)
    {
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
        using var release = HostContract.UseRelease("21");
        var expected = Normalize(enginefixture::TiaMcp.Engine.Tests.HardwareAddressingParityEngine.Map(tool, evidence, write));
        var actual = Normalize(HostContract.Map(tool, new HostResponse { Message = "fixture", Meta = JsonNode.Parse(evidence)!.AsObject() }, write, false));
        Assert.Equal(outcome, (string?)actual["meta"]?["outcome"]);
        Assert.True(JsonNode.DeepEquals(expected, actual), actual.ToJsonString());
        Assert.DoesNotContain("private stack", actual.ToJsonString());
    }

    private static JsonObject Normalize(CallToolResult result)
    {
        var body = result.StructuredContent!.DeepClone().AsObject();
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        body["meta"]!.AsObject().Remove("timestamp"); body["meta"]!.AsObject().Remove("requestId");
        return body;
    }

    [Fact]
    public void Blocked_read_mapping_preserves_the_released_reset_invariant()
    {
        const string evidence = "{\"success\":false,\"status\":\"HmiReadSessionBlocked\"}";
        using var release = HostContract.UseRelease("21");
        var expected = Assert.Throws<ArgumentException>(() => enginefixture::TiaMcp.Engine.Tests.HardwareAddressingParityEngine.Map("GetDeviceAddressing", evidence, false));
        var actual = Assert.Throws<ArgumentException>(() => HostContract.Map("GetDeviceAddressing",
            new HostResponse { Message = "fixture", Meta = JsonNode.Parse(evidence)!.AsObject() }, false, false));
        Assert.Equal(expected.Message, actual.Message);
        Assert.Contains("SESSION_RESET_REQUIRED", actual.Message);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("21")]
    public void Unsupported_action_uses_the_selected_release(string selected)
    {
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
        using var release = HostContract.UseRelease(selected);
        var result = Normalize(HostContract.Run("GetDeviceAddressing",
            () => throw new NotSupportedException("action is unavailable"), false, true));
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result["error"]?["code"]);
        Assert.Equal(selected, (string?)result["error"]?["details"]?["releaseKey"]);
        Assert.Equal(selected, (string?)result["meta"]?["releaseKey"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }
}
