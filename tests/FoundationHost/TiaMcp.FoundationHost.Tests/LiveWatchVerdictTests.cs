extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;

public sealed class LiveWatchVerdictTests
{
    public static IEnumerable<object[]> Releases => new[] { "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Fact]
    public void Force_table_preview_counts_planned_rows()
    {
        using var release = Host.HardwareContract.UseRelease("21");
        var service = new Services.PlcOrganisationPortService((_, _) => new JsonObject { ["Batch"] = new JsonObject {
            ["Imported"] = new JsonArray(), ["Failed"] = new JsonArray(), ["Meta"] = new JsonObject {
                ["items"] = new JsonArray(new JsonObject { ["objectPath"] = "Force table", ["status"] = "planned" }) }
        } }, () => true, () => "C:/Fixture.ap21");
        var preview = new Host.PlcWatchTools(service).ExportPlcWatchTablesToDirectory("PLC", "C:/Exports", tableKind: "force");
        Assert.Contains("Previewed 1 PLC force table to", preview.Message);
        Assert.Empty(preview.Imported!);
    }

    private static JsonObject Monitor(S7ReadResult read)
    {
        var service = new Services.PlcOrganisationPortService((_, _) => new JsonObject {
            ["Report"] = new JsonObject {
                ["Ok"] = true, ["Data"] = new JsonObject(),
                ["Rows"] = new JsonArray(new JsonObject { ["Name"] = "Start", ["Address"] = "%DB1.DBX0.0", ["Format"] = "Bool" })
            }
        }, () => true, () => "fixture");
        int calls = 0;
        service.LiveRead = (ip, rack, slot, specs, module) => {
            calls++;
            Assert.Equal("192.0.2.10", ip);
            Assert.Equal(0, rack);
            Assert.Equal(1, slot);
            Assert.Single(specs);
            Assert.Null(module);
            return read;
        };
        var body = new Host.PlcWatchTools(service).MonitorWatchTableLiveS7V4("PLC_A", "Watch", "192.0.2.10").StructuredContent!.AsObject();
        Assert.Equal(1, calls);
        return body;
    }

    [Theory, MemberData(nameof(Releases))]
    public void Missing_table_preserves_the_native_target_before_live_read(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var catalog = new toolpipeline::TiaMcp.FoundationHost.FoundationCatalog(release,
            TiaMcp.FoundationHost.LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false));
        using var session = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(
            new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog });
        var service = new Services.PlcOrganisationPortService((_, _) => new JsonObject { ["Report"] = new JsonObject {
            ["Ok"] = false, ["Message"] = "Watch table not found.", ["Data"] = new JsonObject(), ["Meta"] = new JsonObject {
                ["status"] = "NotFound", ["mayHaveChanged"] = false, ["parameter"] = "watchTableName", ["nativeMessage"] = "Watch table not found." } } },
            () => true, () => "fixture");
        service.LiveRead = (_, _, _, _, _) => throw new InvalidOperationException("Unexpected live read.");
        var tool = new Host.PlcWatchTools(service);
        foreach (var result in new[] { tool.MonitorWatchTableLiveS7V4("PLC_1", "Missing", "192.0.2.10"),
            tool.ReadPlcWatchTableCurrentValuesReadOnlyV4("PLC_1", "Missing") })
        {
            Assert.Equal("NOT_FOUND", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.Equal("/watchTableName", (string?)result.StructuredContent?["error"]?["details"]?["target"]);
        }
    }

    [Theory]
    [InlineData("DB1.DBX0.0:BOOL", true)]
    [InlineData("DB3.DBX2.7:bool", true)]
    [InlineData("DB1.DBW0:BOOL", false)]
    public void Explicit_boolean_requires_a_bit_address(string address, bool valid)
    {
        var item = S7LiveReader.ParseSpec(address);
        Assert.Equal(valid, item.Error == null);
        if (valid) Assert.Equal("BOOL", item.Type);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Failed_connection_is_a_read_failure(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        const string reason = "Connect to 192.0.2.10 (rack 0, slot 1) failed: TCP: Error receiving Data";
        var body = Monitor(new S7ReadResult { Error = reason });
        Assert.False((bool)body["ok"]!);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("read-failed", (string?)body["meta"]?["outcome"]);
        Assert.NotEqual("complete", (string?)body["meta"]?["completeness"]);
        Assert.Contains("Connect to 192.0.2.10", (string?)body["error"]?["details"]?["nativeMessage"], StringComparison.Ordinal);
        Assert.Equal(reason, (string?)body["error"]?["message"]);
        Assert.Equal(reason, (string?)body["data"]?["readError"]);
        Assert.False((bool)body["data"]!["evidence"]!["success"]!);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Read_values_stay_a_success(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Monitor(new S7ReadResult { Ok = true, Items = new List<S7ReadItem> {
            new() { Spec = "DB1.DBX0.0", Type = "BOOL", Value = true } } });
        Assert.True((bool)body["ok"]!);
        Assert.Equal("succeeded", (string?)body["meta"]?["outcome"]);
        Assert.Equal("complete", (string?)body["meta"]?["completeness"]);
        Assert.True((bool)body["data"]!["values"]![0]!["value"]!);
    }

    [Theory, MemberData(nameof(Releases))]
    public void One_failed_item_is_not_a_success(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Monitor(new S7ReadResult { Items = new List<S7ReadItem> {
            new() { Spec = "DB1.DBX0.0", Type = "BOOL", Error = "Address out of range" } } });
        Assert.False((bool)body["ok"]!);
        Assert.Equal("read-failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("Address out of range", (string?)body["data"]?["values"]?[0]?["error"]);
    }
}
