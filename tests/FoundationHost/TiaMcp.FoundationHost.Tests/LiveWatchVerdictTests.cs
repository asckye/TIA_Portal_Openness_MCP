extern alias enginehost;
using System.Text.Json.Nodes;
using TiaMcpServer.Runtime;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;

public sealed class LiveWatchVerdictTests
{
    public static IEnumerable<object[]> Releases => new[] { "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

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
