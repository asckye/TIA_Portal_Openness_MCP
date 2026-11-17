using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class RuntimeOnlineTargetPolicyTests
{
    [Theory]
    [InlineData("GetPlcLiveValuesS7", "ip")]
    [InlineData("SamplePlcLiveValuesS7", "ip")]
    [InlineData("MonitorPlcWatchTableS7", "ip")]
    [InlineData("TraceTagCauseLive", "ip")]
    [InlineData("ProbeS7CpuIdentity", "ip")]
    [InlineData("GetPlcRunStateS7", "ip")]
    [InlineData("GetPlcWebVars", "host")]
    [InlineData("GetPlcWebDiagnostics", "host")]
    [InlineData("WritePlcWebVars", "host")]
    [InlineData("SetPlcWebOperatingMode", "host")]
    [InlineData("GetPlcLiveValuesOpcUa", "endpointUrl")]
    public void Runtime_tools_share_the_list_and_preserve_unconfigured_behavior(string tool, string parameter)
    {
        Assert.Equal(parameter, OnlineTargetPolicy.AddressParameter(tool));
        string Target(string host) => parameter == "endpointUrl" ? "opc.tcp://" + host + ":4840/server" : host;
        Assert.Null(OnlineTargetPolicy.RuntimeRefusal(tool, Target("plc.example"), null));
        Assert.Null(OnlineTargetPolicy.RuntimeRefusal(tool, Target("192.168.1.10"), OnlineTargetPolicy.Parse("target=192.168.1.0/24")));
        Assert.Equal("online-target-not-allowed", OnlineTargetPolicy.RuntimeRefusal(tool, Target("10.1.2.3"),
            OnlineTargetPolicy.Parse("target=192.168.1.10"))!.Value.Condition);
        Assert.Equal("online-target-not-allowed", OnlineTargetPolicy.RuntimeRefusal(tool, Target("192.168.1.10"),
            OnlineTargetPolicy.Parse(""))!.Value.Condition);
        Assert.Equal("online-target-list-invalid", OnlineTargetPolicy.RuntimeRefusal(tool, Target("plc.example"),
            OnlineTargetList.Invalid)!.Value.Condition);
    }

    [Theory]
    [InlineData("GetPlcWebVars", "192.168.1.10:8443", true)]
    [InlineData("GetPlcWebVars", " 192.168.1.10:443 ", true)]
    [InlineData("GetPlcWebVars", "192.168.1.10:443@plc.example", false)]
    [InlineData("GetPlcWebVars", "plc.example:443", false)]
    [InlineData("GetPlcLiveValuesOpcUa", "opc.tcp://192.168.1.10:4840/path?x=y", true)]
    [InlineData("GetPlcLiveValuesOpcUa", "opc.tcp://192.168.1.10@plc.example:4840", false)]
    [InlineData("GetPlcLiveValuesOpcUa", "opc.tcp://plc.example:4840/192.168.1.10", false)]
    [InlineData("GetPlcLiveValuesOpcUa", "192.168.1.10", false)]
    public void Host_extraction_checks_the_actual_authority(string tool, string target, bool allowed)
        => Assert.Equal(allowed, OnlineTargetPolicy.RuntimeRefusal(tool, target, OnlineTargetPolicy.Parse("target=192.168.1.10")) == null);

    public static IEnumerable<object[]> NonLiterals =>
        from tool in new[] { "ProbeS7CpuIdentity", "GetPlcWebVars", "GetPlcLiveValuesOpcUa" }
        from host in new[] { "plc.example", "localhost", "127.1", "2130706433", "0x7f000001", "192.168.001.010", "[::1]", "", "+192.168.1.10" }
        select new object[] { tool, tool == "GetPlcLiveValuesOpcUa" ? "opc.tcp://" + host + ":4840" : host };

    [Theory, MemberData(nameof(NonLiterals))]
    public void Configured_lists_refuse_DNS_IPv6_and_ambiguous_IPv4_even_with_an_all_addresses_CIDR(string tool, string target)
    {
        Assert.Null(OnlineTargetPolicy.RuntimeRefusal(tool, target, null));
        var refusal = OnlineTargetPolicy.RuntimeRefusal(tool, target, OnlineTargetPolicy.Parse("target=0.0.0.0/0"));
        Assert.NotNull(refusal);
        Assert.Equal("online-target-not-allowed", refusal.Value.Condition);
    }

    [Theory]
    [InlineData("ManagePlcSimAdvancedInstance")]
    [InlineData("WritePlcSimAdvancedTags")]
    [InlineData("RunPlcSimAdvancedTestScenario")]
    [InlineData("GetUnifiedRuntimeTags")]
    [InlineData("UnifiedOpenPipeRequest")]
    public void Non_address_channels_are_excluded(string tool)
        => Assert.Null(OnlineTargetPolicy.RuntimeRefusal(tool, "", OnlineTargetList.Invalid));
}
