extern alias toolpipeline;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Diagnostics;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;

public sealed class OnlineTargetAdmissionTests
{
    [Theory]
    [InlineData("15.1", "ConnectOnlinePlc")]
    [InlineData("20", "DownloadPlc")]
    [InlineData("21", "UploadDeviceParameters")]
    public void Required_address_schema_and_CallTool_admission_preserve_other_input_validation(string release, string operation)
    {
        using var settings = new OnlineTargetSettingsScope(null);
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tool = PortedToolContract.Create(worker, release).OfType<FoundationTool>()
            .Single(tool => tool.ProtocolTool.Name == operation);
        Assert.Contains(tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray(), item => item.GetString() == Parameter(operation));
        Func<JsonElement, bool> refuses = tool.RefusesByCapability;
        Assert.True(refuses(JsonSerializer.SerializeToElement(Arguments(operation, "missing", true))));
        Assert.True(refuses(JsonSerializer.SerializeToElement(Arguments(operation, "host.invalid", true))));
        var invalid = Arguments(operation, "missing", true);
        invalid[operation == "UploadDeviceParameters" ? "devicePath" : "softwarePath"] = 42;
        Assert.False(refuses(JsonSerializer.SerializeToElement(invalid)));
        Assert.Equal(0, worker.Calls);
    }

    public static IEnumerable<object[]> MissingAddresses =>
        from release in new[] { "15.1", "16", "17", "18", "19", "20", "21" }
        from operation in new[] { "ConnectOnlinePlc", "DownloadPlc", "UploadDeviceParameters" }
        where operation != "UploadDeviceParameters" || release == "21"
        from dryRun in operation == "DownloadPlc" ? new[] { true, false } : new[] { true }
        from address in new[] { "missing", "", "192.168.1", "host.invalid", "::1", "192.168.001.010", "010.001.001.001",
            " 192.168.1.10", "192.168.1.10 ", "256.1.1.1" }
        select new object[] { release, operation, dryRun, address };

    [Theory, MemberData(nameof(MissingAddresses))]
    public async Task Missing_or_malformed_addresses_refuse_before_approval_and_worker(
        string release, string operation, bool dryRun, string address)
    {
        using var settings = new OnlineTargetSettingsScope(null);
        using var approval = new ApprovalCounter();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        var arguments = Arguments(operation, address, dryRun);
        var body = (await fixture.Call(operation, arguments)).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal("/" + Parameter(operation), (string?)body["error"]?["details"]?["parameter"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(0, fixture.Worker.Calls);
        Assert.Equal(0, approval.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Non_address_tools_never_read_the_list_and_unexpected_load_failures_fail_closed(bool unexpectedFailure)
    {
        using var settings = new OnlineTargetSettingsScope("target=192.168.1.10\n");
        using var unreadable = new FileStream(OnlineTargetSettings.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var listener = new SettingsReadListener(unexpectedFailure);
        Trace.Listeners.Add(listener);
        try
        {
            using var approval = new ApprovalCounter();
            using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
            using var session = fixture.EnterSession();
            await fixture.Call("GetOnlineState", new { softwarePath = "PLC_1" });
            Assert.Equal(0, listener.Reads);
            Assert.Equal(1, fixture.Worker.Calls);
            var body = (await fixture.Call("ConnectOnlinePlc", Arguments("ConnectOnlinePlc", "192.168.1.10", true))).StructuredContent!;
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal("online-target-list-invalid", (string?)body["error"]?["details"]?["condition"]);
            Assert.Equal(1, listener.Reads);
            Assert.Equal(1, fixture.Worker.Calls);
            Assert.Equal(0, approval.Calls);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    private sealed class SettingsReadListener(bool throwOnRead) : TraceListener
    {
        internal int Reads;
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message?.StartsWith("Online-target", StringComparison.Ordinal) != true) return;
            if (message.StartsWith("Online-target settings unavailable:", StringComparison.Ordinal)) Reads++;
            if (throwOnRead) throw new InvalidOperationException("Unexpected loader/diagnostic failure fixture.");
        }
    }

    public static IEnumerable<object[]> Lists =>
        from release in new[] { "15.1", "16", "17", "18", "19", "20", "21" }
        from operation in new[] { "ConnectOnlinePlc", "DownloadPlc", "UploadStationFromPlc", "GetPlcBlockFingerprints", "UploadDeviceParameters" }
        where operation != "UploadDeviceParameters" || release == "21"
        where operation != "GetPlcBlockFingerprints" || release != "15.1"
        from scenario in new[] { "listed", "unlisted", "invalid" }
        select new object[] { release, operation, scenario, false };

    [Theory, MemberData(nameof(Lists))]
    [InlineData("21", "ConnectOnlinePlc", "listed", true)]
    [InlineData("21", "ConnectOnlinePlc", "unlisted", true)]
    [InlineData("21", "ConnectOnlinePlc", "invalid", true)]
    public async Task Online_target_list_admission_precedes_approval_and_worker(string release, string operation, string scenario, bool nested)
    {
        using var settings = new OnlineTargetSettingsScope(scenario switch {
            "listed" => "target=192.168.1.10\n",
            "unlisted" => "target=10.2.3.4\n",
            _ => "target=not-an-ip\n" });
        using var approval = new ApprovalCounter();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        var arguments = Arguments(operation, "192.168.1.10", true);
        var body = (await fixture.Call(nested ? "CallTool" : operation,
            nested ? new { name = operation, arguments } : (object)arguments)).StructuredContent!;
        if (scenario == "listed")
        {
            Assert.Equal(1, fixture.Worker.Calls);
            Assert.DoesNotContain("online-target-", body.ToJsonString());
        }
        else
        {
            Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
            Assert.Equal(scenario == "invalid" ? "online-target-list-invalid" : "online-target-not-allowed",
                (string?)body["error"]?["details"]?["condition"]);
            Assert.DoesNotContain("10.2.3.4", body.ToJsonString());
            Assert.Equal(0, fixture.Worker.Calls);
        }
        Assert.Equal(0, approval.Calls);
    }

    private static string Parameter(string operation) => operation == "ConnectOnlinePlc" ? "ipAddress" : "targetIpAddress";
    private static JsonObject Arguments(string operation, string address, bool dryRun)
    {
        var arguments = new JsonObject();
        if (operation is "ConnectOnlinePlc" or "DownloadPlc" or "GetPlcBlockFingerprints") arguments["softwarePath"] = "PLC_1";
        if (operation == "UploadDeviceParameters")
        { arguments["devicePath"] = new JsonArray("Drive_1"); arguments["itemPath"] = new JsonArray(); }
        if (operation != "ConnectOnlinePlc" && operation != "GetPlcBlockFingerprints") arguments["dryRun"] = dryRun;
        if (address != "missing") arguments[Parameter(operation)] = address;
        return arguments;
    }

    private sealed class ApprovalCounter : IDisposable
    {
        private readonly Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>? previous
            = Host.McpServer.ApprovalWaitOverrideForTests;
        internal int Calls;
        internal ApprovalCounter() => Host.McpServer.ApprovalWaitOverrideForTests = Wait;
        private Task<ApprovalOutcome> Wait(PendingApproval card, ApprovalSettings _, CancellationToken token)
        { Calls++; return Task.FromResult(new ApprovalOutcome(card, false, "denied")); }
        public void Dispose() => Host.McpServer.ApprovalWaitOverrideForTests = previous;
    }
}
