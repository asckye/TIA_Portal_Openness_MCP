using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Environment;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class WorkbenchEnvironmentChecksTests
{
    [Theory]
    [InlineData("D:(A;;GX;;;WD)", true)]
    [InlineData("D:(A;;GA;;;WD)", true)]
    [InlineData("D:(A;;GR;;;WD)", false)]
    [InlineData("D:(A;;GX;;;BA)", false)]
    [InlineData("D:(D;;GX;;;WD)(A;;GX;;;WD)", false)]
    [InlineData("D:(D;;GA;;;WD)(A;;GX;;;WD)", false)]
    [InlineData("D:", false)]
    public void URL_reservation_requires_current_principal_execute_permission_and_honours_denial(string sddl, bool allowed)
        => Assert.Equal(allowed, WindowsEnvironmentSources.UrlAclAllows(sddl, sid => sid.Value == "S-1-1-0"));

    public static IEnumerable<object[]> Checks => WorkbenchEnvironmentChecks.Ids.Where(id => id != "confirmation").Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(Checks))]
    public void Every_check_passes_with_complete_facts(string id)
    {
        var fixture = new ProbeFixture();
        Assert.Equal(EnvironmentFindingStatus.Pass, fixture.Check(id).Status);
    }

    [Theory]
    [MemberData(nameof(Checks))]
    public void Every_check_fails_with_missing_or_conflicting_facts(string id)
    {
        var fixture = new ProbeFixture();
        if (id == "installations") fixture.Report.Installations.Clear();
        else if (id is "membership" or "framework" or "openness-readiness") fixture.Report.Checks.Single(c => c.Id == (id == "membership" ? "TIA-GROUP" : id == "framework" ? "ENV-NETFX" : "TIA-INSTALL")).Status = CheckStatus.Fail;
        else if (id == "runtime" || id.StartsWith("engine-")) fixture.Sources.FileExists = _ => false;
        else if (id == "data") fixture.Sources.Writable = _ => false;
        else if (id == "port") fixture.Sources.PortOwners = _ => ["127.0.0.1:8765 PID=12 expected-host"];
        else if (id == "url") fixture.Sources.UrlReserved = _ => false;
        else if (id == "firewall") fixture.Sources.FirewallAllowed = _ => false;
        Assert.Equal(EnvironmentFindingStatus.Fail, fixture.Check(id).Status);
    }

    [Theory]
    [MemberData(nameof(Checks))]
    public void Every_check_preserves_unknown_when_inspection_is_denied(string id)
    {
        var fixture = new ProbeFixture();
        if (id is "installations" or "membership" or "openness-readiness" or "framework") fixture.Sources.Doctor = () => throw new UnauthorizedAccessException("denied");
        else if (id == "runtime" || id.StartsWith("engine-")) fixture.Sources.FileExists = _ => throw new UnauthorizedAccessException("denied");
        else if (id == "data") fixture.Sources.Writable = _ => throw new UnauthorizedAccessException("denied");
        else if (id == "port") fixture.Sources.PortOwners = _ => throw new UnauthorizedAccessException("denied");
        else if (id == "url") fixture.Sources.UrlReserved = _ => null;
        else if (id == "firewall") fixture.Sources.FirewallAllowed = _ => null;
        Assert.Equal(EnvironmentFindingStatus.Unknown, fixture.Check(id).Status);
    }

    [Fact]
    public void Listed_membership_needs_a_new_logon_until_the_token_contains_the_group()
    {
        var fixture = new ProbeFixture();
        fixture.Report.Checks.Single(c => c.Id == "TIA-GROUP").Status = CheckStatus.Fail;
        fixture.Sources.ListedGroupMember = () => true;
        Assert.Equal("NewLogon", fixture.Check("membership").Result);
        fixture.Report.Checks.Single(c => c.Id == "TIA-GROUP").Status = CheckStatus.Pass;
        fixture.Sources.ListedGroupMember = () => throw new InvalidOperationException("must not query member list with a valid token");
        Assert.Equal(EnvironmentFindingStatus.Pass, fixture.Check("membership").Status);
    }

    [Fact]
    public void Guidance_does_not_claim_to_have_attached_and_read_only_fallback_names_actual_locations()
    {
        var fixture = new ProbeFixture();
        fixture.Sources.Doctor = () => throw new InvalidOperationException("no TIA");
        Assert.Equal(EnvironmentFindingStatus.Guidance, fixture.Check("confirmation").Status);
        fixture.Context.UserFallback = true;
        var data = fixture.Check("data");
        Assert.Equal("Fallback", data.Result); Assert.Contains(fixture.Context.DataRoot, data.Evidence);
        Assert.Contains(fixture.Context.ConfigDirectory, data.Evidence); Assert.Contains(fixture.Context.LogsDirectory, data.Evidence);
    }

    [Fact]
    public void Engine_can_start_for_diagnostics_when_TIA_readiness_fails()
    {
        var fixture = new ProbeFixture();
        fixture.Report.Checks.Single(c => c.Id == "TIA-INSTALL").Status = CheckStatus.Fail;
        fixture.Report.Checks.Single(c => c.Id == "TIA-INSTALL").Detail = "no installation found";
        fixture.Report.Checks.Single(c => c.Id == "TIA-INSTALL").Remedy = "Install TIA Portal with Openness.";
        var finding = fixture.Check("openness-readiness");
        Assert.Equal(EnvironmentFindingStatus.Fail, finding.Status);
        Assert.Equal("NotReady", finding.Result);
        Assert.Contains("MCP engine can start", finding.Evidence);
        Assert.Contains("Install TIA Portal with Openness", finding.Evidence);
    }

    [Theory]
    [InlineData("hostfxr.dll")]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("Microsoft.AspNetCore.dll")]
    [InlineData("PresentationFramework.dll")]
    [InlineData("dotnet.exe")]
    public void The_bundled_runtime_requires_each_runtime_component(string missing)
    {
        var fixture = new ProbeFixture();
        fixture.Sources.FileExists = path => Path.GetFileName(path) != missing;
        Assert.Equal(EnvironmentFindingStatus.Fail, fixture.Check("runtime").Status);
    }

    [Fact]
    public void Wrong_runtime_major_missing_root_and_missing_endpoint_do_not_pass()
    {
        var fixture = new ProbeFixture();
        fixture.Sources.Directories = directory => [Path.Combine(directory, "9.0.1")];
        Assert.Equal(EnvironmentFindingStatus.Fail, fixture.Check("runtime").Status);
        fixture.Context.BundleRoot = null;
        Assert.Equal(EnvironmentFindingStatus.Unknown, fixture.Check("engine-21").Status);
        fixture.Context.HttpPrefix = null;
        Assert.Equal("NoEndpoint", fixture.Check("url").Result);
    }

    [Fact]
    public void Port_conflicts_include_pid_and_process_and_foundation_requires_exact_release_worker()
    {
        var fixture = new ProbeFixture();
        fixture.Sources.PortOwners = port => ["PID=42 host port=" + port];
        Assert.Contains("PID=42 host port=8765", fixture.Check("port").Evidence);
        foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
        {
            var required = EnvironmentBundleFiles.Required(fixture.Context.BundleRoot, key).ToArray();
            Assert.Contains(required, p => p.EndsWith(Path.Combine("worker", "TiaMcp.PlcWorker." + key + ".exe")));
            Assert.Contains(required, p => p.EndsWith("TiaMcp.FoundationHost.exe"));
            Assert.All(required, path => Assert.Contains("v" + key, path));
        }
    }

    [Theory]
    [InlineData("TiaMcp.Logic.dll", "engine-21")]
    [InlineData("TiaMcp.Runtime.dll", "engine-20")]
    [InlineData("TiaMcp.WorkerChannel.dll", "engine-14sp1")]
    [InlineData("TiaMcp.Adapters.Contracts.dll", "engine-15.1")]
    public void An_executable_alone_does_not_pass_when_its_bootstrap_dependencies_are_missing(string missing, string id)
    {
        var fixture = new ProbeFixture(); fixture.Sources.FileExists = path => Path.GetFileName(path) != missing;
        Assert.Equal(EnvironmentFindingStatus.Fail, fixture.Check(id).Status);
        Assert.Contains(missing, fixture.Check(id).Evidence);
    }

    private sealed class ProbeFixture
    {
        public DoctorReport Report { get; } = new()
        {
            Installations = [new() { Version = "21.0.0.0", EngineeringDllPath = "fake/PublicAPI/net48/Siemens.Engineering.Base.dll" }],
            Checks = [new() { Id = "ENV-NETFX", Status = CheckStatus.Pass, Detail = "Release 528040" }, new() { Id = "TIA-GROUP", Status = CheckStatus.Pass, Detail = "token" },
                new() { Id = "TIA-INSTALL", Status = CheckStatus.Pass, Detail = "V21 installed" }],
        };
        public EnvironmentCheckContext Context { get; } = new()
        {
            BundleRoot = Path.GetFullPath("fake-bundle"), DataRoot = Path.GetFullPath("user-data"),
            ConfigDirectory = Path.GetFullPath("actual-config"), LogsDirectory = Path.GetFullPath("actual-logs"), HttpPrefix = "http://127.0.0.1:8765/",
        };
        public EnvironmentProbeSources Sources { get; }
        public ProbeFixture() => Sources = new()
        {
            Doctor = () => Report, ListedGroupMember = () => false, FileExists = _ => true,
            Directories = directory => [Path.Combine(directory, "10.0.12")], Writable = _ => true,
            PortOwners = _ => [], UrlReserved = _ => true, FirewallAllowed = _ => true,
        };
        public EnvironmentFinding Check(string id) => new WorkbenchEnvironmentChecks(Context, Sources).Run().Single(f => f.Id == id);
    }
}
