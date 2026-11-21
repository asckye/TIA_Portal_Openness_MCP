extern alias toolpipeline;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;

public sealed class OnlineCompareRefusalTests
{
    public static IEnumerable<object[]> Releases => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory]
    [InlineData("GetPlcLiveValuesS7", "ip", "Connect to 192.0.2.10 (rack 0, slot 1) failed: TCP: Error receiving Data")]
    [InlineData("SamplePlcLiveValuesS7", "ip", "Connect to 192.0.2.10 (rack 0, slot 1) failed: TCP: Connection timed out")]
    [InlineData("GetPlcRunStateS7", "ip", "Connect to 192.0.2.10 (rack 0, slot 1) failed: TCP: Connection refused")]
    [InlineData("ProbeS7CpuIdentity", "ip", "Connect failed: TCP: Error receiving Data")]
    [InlineData("GetPlcLiveValuesOpcUa", "endpoint",
                "OPC UA read timed out after 5000 ms (server 'opc.tcp://192.0.2.10:4840' unreachable or no UA server enabled).")]
    [InlineData("GetPlcWebDiagnostics", "host", "S7 Web API error: Timeout: no answer from the CPU web server within the request timeout (CPU unreachable).")]
    [InlineData("GetPlcRunStateS7", "ip", "No online connection.")]
    [InlineData("ProbeS7CpuIdentity", "ip", "Target not reachable")]
    [InlineData("GetPlcLiveValuesOpcUa", "endpoint", "OPC UA connection unavailable: Connection refused")]
    [InlineData("GetPlcWebVars", "host", "S7 Web API error: Connection unavailable: No route to host")]
    public void Unreachable_runtime_reads_identify_the_target(string tool, string field, string reason)
    {
        string target = field == "endpoint" ? "opc.tcp://192.0.2.10:4840" : "192.0.2.10";
        var data = new System.Text.Json.Nodes.JsonObject { [field] = target, ["error"] = reason };
        if (tool == "ProbeS7CpuIdentity") data["connected"] = false;
        if (field == "host") data["safety"] = new System.Text.Json.Nodes.JsonObject { ["connected"] = false };
        var body = Host.RuntimeToolContract.Map(tool, new Host.ResponseJsonReport { Ok = false, Data = data }, true, false).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("plc-online", (string?)body["error"]?["details"]?["condition"]);
        Assert.Equal(target, (string?)body["error"]?["details"]?["target"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    [Theory]
    [InlineData("GetPlcLiveValuesS7", "Connect to 192.0.2.10 (rack 0, slot 1) failed: ISO: Invalid PDU")]
    [InlineData("GetPlcLiveValuesS7", "Read failed: TCP: Error receiving Data")]
    [InlineData("GetPlcLiveValuesOpcUa", "OPC UA error: BadUserAccessDenied")]
    [InlineData("GetPlcWebVars", "S7 Web API error: HttpRequestException: The SSL connection could not be established")]
    [InlineData("GetPlcLiveValuesOpcUa", "OPC UA read timed out after 5000 ms waiting for a read at 'opc.tcp://192.0.2.10:4840'.")]
    public void Runtime_protocol_and_item_failures_keep_native_diagnostics(string tool, string reason)
    {
        var data = new System.Text.Json.Nodes.JsonObject { ["ip"] = "192.0.2.10", ["host"] = "192.0.2.10",
            ["endpoint"] = "opc.tcp://192.0.2.10:4840", ["error"] = reason };
        var body = Host.RuntimeToolContract.Map(tool, new Host.ResponseJsonReport { Ok = false, Data = data }, true, false).StructuredContent!;
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
    }

    [Theory]
    [InlineData(false, "PRECONDITION_FAILED")]
    [InlineData(true, "OUTCOME_UNKNOWN")]
    public void Unreachable_web_write_preserves_dispatch_evidence(bool attempted, string expected)
    {
        var data = new System.Text.Json.Nodes.JsonObject { ["host"] = "192.0.2.10", ["writeAttempted"] = attempted,
            ["error"] = "Target not reachable", ["safety"] = new System.Text.Json.Nodes.JsonObject { ["connected"] = false } };
        var result = Host.RuntimeToolContract.Map("WritePlcWebVars", new Host.ResponseJsonReport { Ok = false, Data = data }, false, true);
        var error = result.StructuredContent!["error"];
        Assert.Equal(expected, (string?)error?["code"]);
        if (!attempted) Assert.Equal("192.0.2.10", (string?)error?["details"]?["target"]);
    }

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
