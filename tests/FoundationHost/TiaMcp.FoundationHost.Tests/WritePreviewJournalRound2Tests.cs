extern alias adaptertests;
extern alias enginehost;
using System.Text.Json.Nodes;
using Xunit;
using Native = adaptertests::TiaMcp.Adapters.Contracts.Tests.WriteRollbackProductionTests;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;

[Collection(BundleSelectionCollection.Name)]
public sealed class WritePreviewJournalRound2Tests
{
    [Theory]
    [InlineData("EnsureSubnet", "write")]
    [InlineData("AttachDeviceNodeToSubnet", "undo")]
    [InlineData("SetPlcCpuSettings", "unknown")]
    public void Legacy_hardware_loss_remains_unknown_with_session_reset(string tool, string failure)
    {
        using var scope = EngineHostParity.EnterScope();
        var native = JsonNode.Parse(Native.LegacyFailureJson(tool, failure))!;
        var result = Host.HardwareContract.Run(tool, () => new Host.ResponseMessage {
            Message = (string?)native["Message"], Meta = native["Meta"]!.AsObject() }, true, false).StructuredContent!;
        Assert.Equal("unknown", (string?)result["meta"]!["outcome"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)result["error"]!["code"]);
        Assert.True((bool?)result["meta"]!["requiresSessionReset"]);
    }

    [Fact]
    public void Real_group_creation_restore_maps_to_failed_without_session_reset()
    {
        using var scope = EngineHostParity.EnterScope();
        var native = JsonNode.Parse(Native.GroupFailureJson())!;
        var result = Host.PlcToolContract.Run("ManagePlcUserGroup", true, true, () => new Host.ResponseMessage {
            Message = (string?)native["Message"], Meta = native["Meta"]!.AsObject() }).StructuredContent!;
        Assert.Equal("failed", (string?)result["meta"]!["outcome"]);
        Assert.False((bool?)result["meta"]!["requiresSessionReset"]);
        Assert.False((bool?)result["data"]!["evidence"]!["mayHaveChanged"]);
    }

    [Theory]
    [InlineData(true, true, "partial")]
    [InlineData(true, false, "unknown")]
    [InlineData(false, true, "unknown")]
    public void Cleanup_requires_both_typed_refusal_and_reachability(bool refused, bool reachable, string expected)
    {
        using var scope = EngineHostParity.EnterScope();
        var evidence = new JsonObject { ["success"] = false, ["operationSuccess"] = false, ["mayHaveChanged"] = true,
            ["rollbackCompleted"] = false, ["rollbackRefused"] = refused, ["rollbackReachable"] = reachable,
            ["cleanupRequired"] = new JsonArray(new JsonObject { ["target"] = "PLC/Node" }) };
        var result = Host.HardwareContract.Run("EnsureSubnet", () => new Host.ResponseMessage { Meta = evidence }, true, false).StructuredContent!;
        Assert.Equal(expected, (string?)result["meta"]!["outcome"]);
        Assert.Equal(expected == "unknown", (bool?)result["meta"]!["requiresSessionReset"]);
    }
}
