extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Runtime;
using TiaOpenness.Shared;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;

public sealed class RuntimeOnlineTargetAdmissionTests
{
    private static readonly string[] Tools = {
        "GetPlcLiveValuesS7", "SamplePlcLiveValuesS7", "MonitorPlcWatchTableS7", "TraceTagCauseLive", "ProbeS7CpuIdentity",
        "GetPlcRunStateS7", "GetPlcWebVars", "GetPlcWebDiagnostics", "WritePlcWebVars", "SetPlcWebOperatingMode", "GetPlcLiveValuesOpcUa" };

    public static IEnumerable<object[]> InvalidArguments =>
        from tool in Tools
        from missing in new[] { false, true }
        select new object[] { tool, missing };

    [Theory, MemberData(nameof(InvalidArguments))]
    public async Task Address_schema_errors_still_precede_list_admission(string tool, bool missing)
    {
        using var settings = new OnlineTargetSettingsScope("target=192.168.1.10");
        using var channels = new ForbiddenChannels();
        using var approval = new ApprovalCounter();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        using var session = fixture.EnterSession();
        var input = Arguments(tool, "192.168.1.10", true);
        string parameter = OnlineTargetPolicy.AddressParameter(tool)!;
        if (missing) input.Remove(parameter); else input[parameter] = 42;
        var body = (await fixture.Call(tool, input)).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]?["code"]);
        Assert.Equal(0, fixture.Worker.Calls);
        Assert.Equal(0, approval.Calls);
        Assert.Equal(0, channels.Calls);
    }

    public static IEnumerable<object[]> Refusals =>
        from release in ReleaseRange.All
        from tool in Tools
        from scenario in new[] { "unlisted", "hostname", "empty-list", "invalid", "unreadable" }
        from nested in new[] { false, true }
        select new object[] { release, tool, scenario, nested };

    [Theory, MemberData(nameof(Refusals))]
    public async Task Every_runtime_target_refuses_before_approval_worker_and_channel_creation(
        string release, string tool, string scenario, bool nested)
    {
        using var settings = new OnlineTargetSettingsScope(scenario switch {
            "empty-list" => "", "invalid" => "target=invalid", _ => "target=192.168.1.10" });
        using var unreadable = scenario == "unreadable"
            ? new FileStream(OnlineTargetSettings.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var channels = new ForbiddenChannels();
        using var approval = new ApprovalCounter();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        var arguments = Arguments(tool, scenario == "hostname" ? "plc.example" : "10.1.2.3", false);
        if (!PortedFamilies.Available(release, tool))
        {
            var error = Host.McpServer.ToolInvoker.Bind(tool, new ToolArguments(JsonSerializer.SerializeToElement(arguments)), out var unavailable);
            Assert.IsType<UnsupportedCapabilityDetails>(error!.Details);
            Assert.Null(unavailable);
            Assert.Equal(0, fixture.Worker.Calls);
            Assert.Equal(0, approval.Calls);
            Assert.Equal(0, channels.Calls);
            return;
        }
        var body = (await fixture.Call(nested ? "CallTool" : tool, nested ? new { name = tool, arguments } : (object)arguments)).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal(scenario is "invalid" or "unreadable" ? "online-target-list-invalid" : "online-target-not-allowed",
            (string?)body["error"]?["details"]?["condition"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
        Assert.Equal(0, fixture.Worker.Calls);
        Assert.Equal(0, approval.Calls);
        Assert.Equal(0, channels.Calls);
        Assert.DoesNotContain("192.168.1.10", body.ToJsonString());
    }

    public static IEnumerable<object[]> Admitted =>
        from release in ReleaseRange.All
        from tool in Tools
        from scenario in new[] { "listed", "cidr", "missing-list", "hostname-without-list" }
        select new object[] { release, tool, scenario };

    [Theory, MemberData(nameof(Admitted))]
    public void Listed_targets_and_unconfigured_lists_bind_without_creating_channels(string release, string tool, string scenario)
    {
        using var settings = new OnlineTargetSettingsScope(scenario switch {
            "listed" => "target=192.168.1.10", "cidr" => "target=192.168.1.0/24", _ => null });
        using var channels = new ForbiddenChannels();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release);
        using var session = fixture.EnterSession();
        var input = new ToolArguments(JsonSerializer.SerializeToElement(Arguments(tool,
            scenario == "hostname-without-list" ? "plc.example" : "192.168.1.10", true)));
        var error = Host.McpServer.ToolInvoker.Bind(tool, input, out var call);
        if (PortedFamilies.Available(release, tool))
        { Assert.Null(error); Assert.NotNull(call); }
        else
        { Assert.IsType<UnsupportedCapabilityDetails>(error!.Details); Assert.Null(call); }
        Assert.Equal(0, channels.Calls);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory]
    [InlineData("ProbeS7CpuIdentity")]
    [InlineData("GetPlcLiveValuesS7")]
    [InlineData("SamplePlcLiveValuesS7")]
    [InlineData("GetPlcRunStateS7")]
    [InlineData("GetPlcWebVars")]
    [InlineData("GetPlcWebDiagnostics")]
    [InlineData("WritePlcWebVars")]
    [InlineData("SetPlcWebOperatingMode")]
    [InlineData("GetPlcLiveValuesOpcUa")]
    public async Task Admitted_host_tools_reach_the_injected_channel_factory_without_opening_sockets(string tool)
    {
        using var settings = new OnlineTargetSettingsScope("target=192.168.1.10");
        using var channels = new ForbiddenChannels();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        using var session = fixture.EnterSession();
        var body = (await fixture.Call(tool, Arguments(tool, "192.168.1.10", true))).StructuredContent!;
        Assert.True(channels.Calls > 0, body.ToJsonString());
        Assert.DoesNotContain("online-target-", body.ToJsonString());
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private static JsonObject Arguments(string tool, string host, bool preview)
    {
        string parameter = OnlineTargetPolicy.AddressParameter(tool)!;
        var input = new JsonObject { [parameter] = parameter == "endpointUrl" ? "opc.tcp://" + host + ":4840/server"
            : parameter == "host" ? host + ":8443" : host };
        if (tool is "GetPlcLiveValuesS7" or "SamplePlcLiveValuesS7") input["items"] = new JsonArray("MW0");
        if (tool == "GetPlcLiveValuesOpcUa") input["nodeIds"] = new JsonArray("i=2258");
        if (parameter == "host") { input["username"] = "fixture"; input["password"] = ""; }
        if (tool == "GetPlcWebVars") input["vars"] = new JsonArray("Tag_1");
        if (tool == "WritePlcWebVars")
        { input["writes"] = new JsonArray(new JsonObject { ["name"] = "Tag_1", ["value"] = true }); input["confirmWrite"] = true; }
        if (tool == "SetPlcWebOperatingMode") { input["mode"] = "stop"; input["confirmModeChange"] = true; }
        if (tool is "WritePlcWebVars" or "SetPlcWebOperatingMode") input["dryRun"] = preview;
        if (tool is "MonitorPlcWatchTableS7" or "TraceTagCauseLive") input["softwarePath"] = "PLC_1";
        if (tool == "MonitorPlcWatchTableS7") input["watchTableName"] = "Watch_1";
        if (tool == "TraceTagCauseLive") input["tag"] = "Tag_1";
        return input;
    }

    private sealed class ForbiddenChannels : IDisposable
    {
        private readonly Func<Sharp7.S7Client>? s7 = S7LiveReader.ClientFactory.Value;
        private readonly Func<System.Net.Http.HttpClientHandler>? web = S7WebApiChannel.HandlerFactory.Value;
        private readonly Func<string, Workstation.ServiceModel.Ua.Channels.ClientSessionChannel>? ua = OpcUaLiveReader.ChannelFactory.Value;
        internal int Calls;
        internal ForbiddenChannels()
        {
            S7LiveReader.ClientFactory.Value = () => throw Created();
            S7WebApiChannel.HandlerFactory.Value = () => throw Created();
            OpcUaLiveReader.ChannelFactory.Value = _ => throw Created();
        }
        private Exception Created() { Calls++; return new InvalidOperationException("Injected channel factory: no sockets opened."); }
        public void Dispose()
        {
            S7LiveReader.ClientFactory.Value = s7;
            S7WebApiChannel.HandlerFactory.Value = web;
            OpcUaLiveReader.ChannelFactory.Value = ua;
        }
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
