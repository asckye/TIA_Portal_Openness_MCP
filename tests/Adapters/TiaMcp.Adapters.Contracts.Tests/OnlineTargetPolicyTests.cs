using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class OnlineTargetPolicyTests
{
    [Theory]
    [InlineData("target=192.168.1.10")]
    [InlineData("target=192.168.1.0/24")]
    [InlineData("target=0.0.0.0/0")]
    [InlineData("target=255.255.255.255/32")]
    [InlineData(" \t# comment\r\n\r\n target = 192.168.1.10 \t# inline\r\n")]
    public void Exact_CIDR_and_comments_parse(string text)
    {
        var list = OnlineTargetPolicy.Parse(text);
        Assert.True(list.IsValid);
        Assert.Single(list.Entries);
    }

    [Theory]
    [InlineData("address=192.168.1.10")]
    [InlineData("target=192.168.1")]
    [InlineData("target=192.168.1.256")]
    [InlineData("target=+192.168.1.10")]
    [InlineData("target=192.168.1.-1")]
    [InlineData("target=localhost")]
    [InlineData("target=::1")]
    [InlineData("target=192.168.1.10/33")]
    [InlineData("target=192.168.1.10/-1")]
    [InlineData("target=192.168.1.10/+24")]
    [InlineData("target=192.168.1.10/")]
    [InlineData("target=192.168.1.10/24/32")]
    [InlineData("target=192.168. 1.10")]
    [InlineData("target=192.168.1.10 /24")]
    [InlineData("target=192.168.1.10\nbad")]
    [InlineData("target=192.168.1.10=extra")]
    [InlineData("target=192.168.001.010")]
    [InlineData("target=010.001.001.001/24")]
    public void Any_invalid_line_invalidates_the_whole_list(string text)
        => Assert.False(OnlineTargetPolicy.Parse(text).IsValid);

    public static IEnumerable<object[]> InvalidAddresses =>
        from operation in new[] { "ConnectOnlinePlc", "DownloadPlc", "UploadDeviceParameters", "GetPlcBlockFingerprints", "UploadStationFromPlc" }
        from address in new[] { "192.168.001.010", "010.001.001.001", "00.1.1.1", "0.0.0.00", " 192.168.1.10", "192.168.1.10 ",
            "192.168.1.\t10", "::1", "plc.example", "256.1.1.1", "192.168.1.999" }
        select new object[] { operation, address };

    [Theory, MemberData(nameof(InvalidAddresses))]
    public void Ambiguous_or_nonliteral_addresses_are_invalid_even_without_a_list(string operation, string address)
    {
        var refusal = OnlineTargetPolicy.Refusal(new OnlineRequest { Operation = operation, IpAddress = address, TargetIpAddress = address }, null);
        Assert.NotNull(refusal);
        Assert.Null(refusal.Value.Condition);
        Assert.Equal(operation == "ConnectOnlinePlc" ? "ipAddress" : "targetIpAddress", refusal.Value.Parameter);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.0.10")]
    public void A_single_zero_octet_is_unambiguous(string address)
        => Assert.Null(OnlineTargetPolicy.Refusal(new OnlineRequest { Operation = "ConnectOnlinePlc", IpAddress = address }, null));

    public static IEnumerable<object[]> Refusals =>
        from operation in new[] { "ConnectOnlinePlc", "DownloadPlc", "UploadDeviceParameters", "GetPlcBlockFingerprints", "UploadStationFromPlc" }
        from scenario in new[] { "empty", "malformed", "missing-list", "listed", "unlisted", "cidr", "invalid-list", "empty-list" }
        select new object[] { operation, scenario };

    [Theory, MemberData(nameof(Refusals))]
    public void Address_and_list_refusal_matrix(string operation, string scenario)
    {
        string address = scenario switch { "empty" => "", "malformed" => "192.168.1", _ => "192.168.1.10" };
        var request = new OnlineRequest { Operation = operation, IpAddress = address, TargetIpAddress = address };
        var list = scenario switch {
            "missing-list" => null,
            "unlisted" => OnlineTargetPolicy.Parse("target=10.1.2.3"),
            "cidr" => OnlineTargetPolicy.Parse("target=192.168.1.0/24"),
            "invalid-list" => OnlineTargetList.Invalid,
            "empty-list" => OnlineTargetPolicy.Parse("# no entries\n"),
            _ => OnlineTargetPolicy.Parse("target=192.168.1.10") };
        var refusal = OnlineTargetPolicy.Refusal(request, list);
        if (scenario is "missing-list" or "listed" or "cidr") { Assert.Null(refusal); return; }
        Assert.NotNull(refusal);
        Assert.Equal(operation == "ConnectOnlinePlc" ? "ipAddress" : "targetIpAddress", refusal.Value.Parameter);
        Assert.Equal(scenario switch {
            "invalid-list" => "online-target-list-invalid",
            "unlisted" or "empty-list" => "online-target-not-allowed",
            _ => null }, refusal.Value.Condition);
        Assert.DoesNotContain("10.1.2.3", refusal.Value.Message);
    }

    [Theory]
    [InlineData("0.0.0.0/0", "255.255.255.255", true)]
    [InlineData("192.168.1.99/24", "192.168.1.0", true)]
    [InlineData("192.168.1.99/24", "192.168.1.255", true)]
    [InlineData("192.168.1.99/24", "192.168.2.0", false)]
    [InlineData("192.168.1.10/32", "192.168.1.10", true)]
    [InlineData("192.168.1.10/32", "192.168.1.11", false)]
    public void CIDR_matches_network_bits(string entry, string address, bool allowed)
        => Assert.Equal(allowed, OnlineTargetPolicy.Refusal(new OnlineRequest { Operation = "ConnectOnlinePlc", IpAddress = address },
            OnlineTargetPolicy.Parse("target=" + entry)) == null);

    [Theory]
    [InlineData("GetOnlineState")]
    [InlineData("DisconnectOnlinePlc")]
    [InlineData("ListTransferRoutes")]
    public void Other_operations_do_not_need_an_address(string operation)
        => Assert.Null(OnlineTargetPolicy.Refusal(new OnlineRequest { Operation = operation }, OnlineTargetList.Invalid));
}
