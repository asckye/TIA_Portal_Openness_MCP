using System.Text.Json.Nodes;
using System.Net.NetworkInformation;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

public sealed class DownloadSafetyPolicyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("PLCSIM", "physical")]
    [InlineData("Siemens PLCSIM Virtual Ethernet Adapter", "physical")]
    [InlineData("plcsim", "physical")]
    [InlineData("Intel(R) Ethernet", "physical")]
    [InlineData("", "physical")]
    [InlineData(null, "physical")]
    public void Target_kind_is_conservative(string? adapter, string expected)
    {
        using var evidence = TiaMcpServer.Siemens.DownloadTargetPolicy.UseSources(() => null, _ => new JsonObject());
        var arguments = new JsonObject { ["pgPcInterface"] = adapter, ["targetIpAddress"] = "192.0.2.10", ["dryRun"] = false };
        Assert.Equal(expected, HostBehavior.DownloadTargetKind((string?)arguments["targetIpAddress"], adapter));
        Assert.True(HostBehavior.ApprovalRequired(false, "DownloadPlc", arguments));
    }

    [Fact]
    public void Approval_matrix_preserves_previews_and_simulator_exceptions()
    {
        foreach (bool collision in new[] { false, true })
        {
            using var evidence = TiaMcpServer.Siemens.DownloadTargetPolicy.UseSources(() => RunningInstance("192.0.2.10"),
                _ => DownloadTargetCases.Network(collision ? OperationalStatus.Up : null));
            foreach (bool enabled in new[] { false, true })
            foreach (bool preview in new[] { false, true })
            foreach (string tool in new[] { "DownloadPlc", "WritePlcSimAdvancedTags", "ManagePlcSimAdvancedInstance",
                "RunPlcSimAdvancedTestScenario", "WritePlcWebVars", "SetPlcWebOperatingMode", "SetPlcBlockAttribute" })
            foreach (string adapter in new[] { "PLCSIM", "Intel(R) Ethernet", "" })
            foreach (string target in new[] { "192.0.2.10", "192.0.2.11" })
            {
                var arguments = new JsonObject { ["dryRun"] = preview, ["pgPcInterface"] = adapter, ["targetIpAddress"] = target };
                bool physicalWrite = tool is "WritePlcWebVars" or "SetPlcWebOperatingMode" || tool == "DownloadPlc"
                    && (target != "192.0.2.10" || adapter != "PLCSIM" || collision);
                Assert.Equal(enabled || !preview && physicalWrite, HostBehavior.ApprovalRequired(enabled, tool, arguments));
            }
        }
        Assert.False(HostBehavior.ApprovalRequired(false, "DownloadPlc", new JsonObject()));
        Assert.True(HostBehavior.ApprovalRequired(false, "WritePlcWebVars", new JsonObject { ["dryRun"] = "true" }));
    }

    internal static JsonObject RunningInstance(string ip) => DownloadTargetCases.Listing(ip);

    [Theory, MemberData(nameof(DownloadTargetCases.Cases), MemberType = typeof(DownloadTargetCases))]
    public void Target_evidence_drives_host_kind_and_approval(string name, string adapter, string target, string listingJson, string networkJson,
        string expected)
    {
        output.WriteLine(name);
        using var evidence = TiaMcpServer.Siemens.DownloadTargetPolicy.UseSources(
            () => JsonNode.Parse(listingJson)!.AsObject(), _ => JsonNode.Parse(networkJson)!.AsObject());
        Assert.Equal(expected, HostBehavior.DownloadTargetKind(target, adapter));
        Assert.Equal(expected == "physical", HostBehavior.ApprovalRequired(false, "DownloadPlc", new JsonObject {
            ["targetIpAddress"] = target, ["pgPcInterface"] = adapter, ["dryRun"] = false }));
    }

    [Fact]
    public void Real_subnet_and_ambiguous_subnets_are_physical_and_network_reports_hide_unrelated_interfaces()
    {
        const string target = "192.168.1.10";
        var sim = new TiaMcpServer.Siemens.LocalNetworkPolicy.Address { Name = "Sim", Description = "Siemens PLCSIM Virtual Ethernet Adapter",
            Type = NetworkInterfaceType.Ethernet, Status = OperationalStatus.Up,
            Ip = "192.168.1.2", Mask = "255.255.255.0" };
        var real = new TiaMcpServer.Siemens.LocalNetworkPolicy.Address { Name = "Real", Description = "Intel Ethernet",
            Type = NetworkInterfaceType.Ethernet, Status = OperationalStatus.Up,
            Ip = "192.168.1.3", Mask = "255.255.255.0" };
        var simOnly = TiaMcpServer.Siemens.LocalNetworkPolicy.Report(target, new[] { sim });
        Assert.Equal("plcsim", TiaMcpServer.Siemens.DownloadTargetPolicy.Classify(target, "PLCSIM", null, simOnly));
        var actual = TiaMcpServer.Siemens.LocalNetworkPolicy.Report(target, new[] { real });
        using var evidence = TiaMcpServer.Siemens.DownloadTargetPolicy.UseSources(() => null, _ => actual);
        Assert.Equal("physical", HostBehavior.DownloadTargetKind(target));
        Assert.True(HostBehavior.ApprovalRequired(false, "DownloadPlc", new JsonObject {
            ["targetIpAddress"] = target, ["pgPcInterface"] = "PLCSIM", ["dryRun"] = false }));
        Assert.Equal("physical", TiaMcpServer.Siemens.DownloadTargetPolicy.Classify(target, "PLCSIM", null,
            TiaMcpServer.Siemens.LocalNetworkPolicy.Report(target, new[] { sim, real })));
        var hidden = new[] {
            new TiaMcpServer.Siemens.LocalNetworkPolicy.Address { Name = "Down", Description = "Down", Status = OperationalStatus.Down,
                Ip = "192.168.1.4", Mask = "255.255.255.0" },
            new TiaMcpServer.Siemens.LocalNetworkPolicy.Address { Name = "Other", Description = "Other", Status = OperationalStatus.Up,
                Ip = "10.0.0.1", Mask = "255.255.255.0" },
            new TiaMcpServer.Siemens.LocalNetworkPolicy.Address { Name = "VPN", Description = "VPN", Status = OperationalStatus.Up,
                Type = NetworkInterfaceType.Tunnel, Ip = "192.168.1.5", Mask = "255.255.255.0" },
            new TiaMcpServer.Siemens.LocalNetworkPolicy.Address { Name = "Loopback", Description = "Loopback", Status = OperationalStatus.Up,
                Type = NetworkInterfaceType.Loopback, Ip = "192.168.1.6", Mask = "255.255.255.0" }, sim };
        hidden = hidden.Concat(new[] { "TAP-Windows Adapter V9", "WireGuard", "Wintun" }.Select(description =>
            new TiaMcpServer.Siemens.LocalNetworkPolicy.Address { Name = "Ethernet 2", Description = description, Status = OperationalStatus.Up,
                Type = NetworkInterfaceType.Ethernet, Ip = "192.168.1.7", Mask = "255.255.255.0" })).ToArray();
        var report = TiaMcpServer.Siemens.LocalNetworkPolicy.Report(target, hidden);
        Assert.Equal("Sim", (string?)Assert.Single(report["adapters"]!.AsArray())!["name"]);
        Assert.False((bool?)report["simulationOnlySubnet"]);
        Assert.False((bool?)TiaMcpServer.Siemens.LocalNetworkPolicy.Report(target, new[] { sim }, false)["simulationOnlySubnet"]);
    }

    [Fact]
    public void Repeated_preview_past_half_life_has_a_new_hash_and_deadline_without_extending_the_old_plan()
    {
        var plans = new OnlinePlanLifetime();
        var now = DateTimeOffset.Parse("2026-11-17T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var first = plans.Preview("fingerprint", now, 900);
        Assert.Equal(first.Hash, plans.Preview("fingerprint", now.AddSeconds(450), 900).Hash);
        var fresh = plans.Preview("fingerprint", now.AddSeconds(451), 900);
        Assert.NotEqual(first.Hash, fresh.Hash);
        Assert.Equal(now.AddSeconds(1351), fresh.Expires);
        Assert.Null(plans.Refusal(first.Hash, now.AddSeconds(899)));
        Assert.NotNull(plans.Refusal(first.Hash, now.AddSeconds(900)));
        Assert.Null(plans.Refusal(fresh.Hash, now.AddSeconds(900)));
    }

    [Fact]
    public void Online_plan_expires_at_the_deadline_and_a_fresh_preview_renews_it()
    {
        var plans = new OnlinePlanLifetime();
        string unknown = new string('b', 64);
        var now = DateTimeOffset.Parse("2026-11-17T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(plans.Refusal(unknown, now));
        var first = plans.Preview("fingerprint", now, 900);
        string plan = first.Hash;
        Assert.Equal(now.AddMinutes(15), first.Expires);
        Assert.Null(plans.Refusal(plan, now.AddSeconds(899)));
        var expired = plans.Refusal(plan, now.AddSeconds(900))!;
        Assert.IsType<PlanStaleDetails>(expired.Details);
        Assert.Equal("expired", ((PlanStaleDetails)expired.Details).Reason);
        Assert.NotNull(new OnlinePlanLifetime().Refusal(plan, now));
        plan = plans.Preview("fingerprint", now.AddMinutes(16), 60).Hash;
        Assert.Null(plans.Refusal(plan, now.AddMinutes(16)));
        Assert.NotNull(plans.Refusal(plan, now.AddMinutes(17)));
    }

    [Fact]
    public void Identical_previews_keep_the_first_deadline_and_expired_hashes_are_not_revived()
    {
        var plans = new OnlinePlanLifetime();
        var now = DateTimeOffset.Parse("2026-11-17T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var first = plans.Preview("fingerprint", now, 900);
        var repeated = plans.Preview("fingerprint", now.AddMinutes(5), 900);
        Assert.Equal(first.Hash, repeated.Hash);
        Assert.Equal(first.Expires, repeated.Expires);
        Assert.NotNull(plans.Refusal(first.Hash, now.AddMinutes(15)));
        var fresh = plans.Preview("fingerprint", now.AddMinutes(15), 900);
        Assert.NotEqual(first.Hash, fresh.Hash);
        Assert.Equal(now.AddMinutes(30), fresh.Expires);
        Assert.NotNull(plans.Refusal(first.Hash, now.AddMinutes(15)));
        Assert.Null(plans.Refusal(fresh.Hash, now.AddMinutes(15)));
    }

    [Fact]
    public void Online_plan_lifetime_is_a_local_setting_and_effective_approval_preserves_it()
    {
        string root = Path.GetFullPath(Path.Combine("bin-build", "refactor", "settings-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "approval.settings");
        try
        {
            File.WriteAllText(path, "enabled=false\ntimeoutSeconds=10\n");
            Assert.Equal(900, ApprovalSettings.Load(path).OnlinePlanLifetimeSeconds);
            new ApprovalSettings(false, 10, 60).Save(path);
            var settings = ApprovalSettings.Load(path);
            Assert.False(settings.Enabled);
            Assert.Equal(60, settings.OnlinePlanLifetimeSeconds);
            Assert.True(settings.WithRequiredApproval(true).Enabled);
            Assert.Equal(60, settings.WithRequiredApproval(true).OnlinePlanLifetimeSeconds);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ApprovalSettings(false, 10, 0));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("192.168.1.2", "255.255.255.0", "192.168.1.250", true)]
    [InlineData("192.168.1.2", "255.255.255.0", "192.168.2.2", false)]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.254", true)]
    [InlineData("10.1.2.3", "255.255.255.255", "10.1.2.4", false)]
    [InlineData("::1", "255.255.255.0", "192.168.1.2", false)]
    [InlineData("192.168.1.2", "255.255.255.0", "host.invalid", false)]
    public void Local_subnet_check_does_not_resolve_or_contact_targets(string local, string mask, string target, bool expected)
        => Assert.Equal(expected, TiaMcpServer.Siemens.LocalNetworkPolicy.SharesSubnet(local, mask, target));
}
