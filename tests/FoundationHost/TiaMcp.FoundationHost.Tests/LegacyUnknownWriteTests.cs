extern alias enginehost;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;

public sealed class LegacyUnknownWriteTests
{
    public static IEnumerable<object[]> Mappers => new[] { "drive", "engineering", "library", "optional", "hmi-exchange", "hmi-inspection",
        "unified", "plc", "motion", "hardware", "offline", "session" }
        .SelectMany(mapper => new[] { new object[] { mapper, false }, new object[] { mapper, true } });

    [Theory, MemberData(nameof(Mappers))]
    public void Legacy_flag_is_unknown_evidence_for_writes_only(string mapper, bool writes)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var response = new Host.ResponseMessage { Meta = new JsonObject { ["operationSuccess"] = true, ["apiCallSuccess"] = true,
            ["mayHaveChanged"] = true, ["requiresSessionReset"] = true } };
        string tool = writes ? "CreatePlcTag" : "GetProjectInfo";
        CallToolResult result = mapper switch {
            "drive" => Host.DriveToolContract.Map(tool, response, writes, true),
            "engineering" => Host.EngineeringToolContract.Map(tool, response, writes),
            "library" => Host.LibraryToolContract.Map(tool, response, writes),
            "optional" => Host.OptionalPackageContract.Map(tool, response, writes),
            "hmi-exchange" => Host.HmiExchangeContract.Map(tool, response, writes, true),
            "hmi-inspection" => Host.HmiInspectionContract.Map(tool, response, writes, true),
            "unified" => Host.UnifiedHmiContract.Map(tool, response, writes, true),
            "plc" => Host.PlcToolContract.Map(tool, response, writes, true),
            "motion" => Host.ClassicMotionToolContract.Map(tool, response, !writes, true),
            "hardware" => Host.HardwareToolContract.MapResult(tool, response, !writes, true),
            "offline" => Host.OfflineContracts.Map(tool, response, writes, true),
            "session" => Host.SessionToolContract.Map(tool, response, writes, true),
            _ => throw new ArgumentOutOfRangeException(nameof(mapper)) };
        Assert.Equal(writes ? "unknown" : "succeeded", (string?)result.StructuredContent?["meta"]?["outcome"]);
        UnsettledOutcomeAssertions.Result(tool, result, writes, writes, (ok, message) => Assert.True(ok, message), mapper);
    }

    [Fact]
    public void Hmi_connection_unavailable_keeps_a_write_unknown()
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var result = Host.HmiExchangeContract.Map("CreateHmiTag", new Host.ResponseMessage { Meta = new JsonObject {
            ["operationSuccess"] = true, ["connectionUnavailable"] = true } }, true, true);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        UnsettledOutcomeAssertions.Result("CreateHmiTag", result, true, true, (ok, message) => Assert.True(ok, message), "HMI connection evidence");
    }

    [Theory, InlineData(false), InlineData(true)]
    public void Plc_B5_legacy_flag_never_downgrades_an_unknown_write(bool writes)
    {
        using var release = Host.HardwareContract.UseRelease("21");
        string file = Path.Combine(Path.GetTempPath(), "b5-legacy-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, "<AlarmClasses />");
        try
        {
            var service = new enginehost::TiaMcpServer.Siemens.Services.PlcB5PortService((_, _) =>
                System.Text.Json.JsonSerializer.SerializeToNode(new TiaMcp.Adapters.Contracts.PlcB5Reply {
                    Failure = new TiaMcp.Adapters.Contracts.PlcSoftwareFailure { Code = "OpennessError", Message = "Synthetic native result unavailable." },
                    MayHaveChanged = false, RequiresSessionReset = true }), () => "C:/fixture.ap21");
            string tool = writes ? "ImportAlarmClasses" : "GetPlcOpcUaConfiguration";
            var result = Host.EngineeringToolContract.Run(tool, writes,
                () => writes ? service.ImportAlarmClasses("PLC", file) : service.GetOpcUaConfig("PLC"));
            Assert.Equal(writes ? "unknown" : "read-failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
            UnsettledOutcomeAssertions.Result(tool, result, writes, writes, (ok, message) => Assert.True(ok, message), "PLC B5 legacy evidence");
        }
        finally { File.Delete(file); }
    }
}
