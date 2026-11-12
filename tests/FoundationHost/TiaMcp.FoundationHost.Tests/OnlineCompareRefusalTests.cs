extern alias enginehost;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;

public sealed class OnlineCompareRefusalTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public void Offline_compare_is_a_precondition(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Host.RuntimeToolContract.Map("CompareSoftwareToOnline", new Host.ResponseCompare {
            IsOnline = false, Message = "'PLC_A' is offline. Call ConnectOnlinePlc first."
        }, true, true).StructuredContent!;
        Assert.False((bool)body["ok"]!);
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("plc-online", (string?)body["error"]?["details"]?["condition"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Contains("ConnectOnlinePlc", (string?)body["error"]?["message"], StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Online_compare_stays_a_success(string release)
    {
        using var scope = Host.HardwareContract.UseRelease(release);
        var body = Host.RuntimeToolContract.Map("CompareSoftwareToOnline", new Host.ResponseCompare {
            IsOnline = true, Entries = Array.Empty<Host.CompareEntry>(), Summary = new Dictionary<string, int>(StringComparer.Ordinal)
        }, true, true).StructuredContent!;
        Assert.True((bool)body["ok"]!);
        Assert.Null(body["error"]);
        Assert.Equal("succeeded", (string?)body["meta"]?["outcome"]);
    }
}
