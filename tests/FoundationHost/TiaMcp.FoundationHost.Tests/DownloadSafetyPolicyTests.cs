using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;

public sealed class DownloadSafetyPolicyTests
{
    [Theory]
    [InlineData("PLCSIM", "plcsim")]
    [InlineData("Siemens PLCSIM Virtual Ethernet Adapter", "plcsim")]
    [InlineData("plcsim", "plcsim")]
    [InlineData("Intel(R) Ethernet", "physical")]
    [InlineData("", "physical")]
    [InlineData(null, "physical")]
    public void Target_kind_is_conservative(string? adapter, string expected)
        => Assert.Equal(expected, HostBehavior.DownloadTargetKind(adapter));

    [Fact]
    public void Approval_matrix_preserves_previews_and_simulator_exceptions()
    {
        foreach (bool enabled in new[] { false, true })
        foreach (bool preview in new[] { false, true })
        foreach (string tool in new[] { "DownloadPlc", "WritePlcSimAdvancedTags", "ManagePlcSimAdvancedInstance",
            "RunPlcSimAdvancedTestScenario", "WritePlcWebVars", "SetPlcWebOperatingMode", "SetPlcBlockAttribute" })
        foreach (string adapter in new[] { "PLCSIM", "Intel(R) Ethernet", "" })
        {
            var arguments = new JsonObject { ["dryRun"] = preview, ["pgPcInterface"] = adapter };
            bool physicalWrite = tool is "WritePlcWebVars" or "SetPlcWebOperatingMode" || tool == "DownloadPlc" && adapter != "PLCSIM";
            Assert.Equal(enabled || !preview && physicalWrite, HostBehavior.ApprovalRequired(enabled, tool, arguments));
        }
        Assert.False(HostBehavior.ApprovalRequired(false, "DownloadPlc", new JsonObject()));
        Assert.True(HostBehavior.ApprovalRequired(false, "WritePlcWebVars", new JsonObject { ["dryRun"] = "true" }));
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
