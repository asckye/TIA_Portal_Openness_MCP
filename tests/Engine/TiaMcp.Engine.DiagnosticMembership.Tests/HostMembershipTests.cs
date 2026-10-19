extern alias foundationhost;
using System;
using Xunit;
using Diagnostics = foundationhost::TiaMcp.FoundationHost.FoundationPassiveDiagnostics;

public sealed class HostMembershipTests
{
    [Theory, InlineData("20", false), InlineData("20", true), InlineData("21", false), InlineData("21", true)]
    public void Host_readiness_preserves_membership_and_never_repairs_it(string release, bool member)
    {
        int reads = 0;
        var readiness = Diagnostics.Readiness(release, _ => "C:/fixture/API", () => { reads++; return member; });
        Assert.Equal(member, (bool?)readiness["environment"]?["opennessGroupOk"]);
        Assert.Equal(member, (bool?)readiness["ready"]);
        Assert.Equal(member ? "ConnectPortal" : "EnsureOpennessUserGroup", (string?)readiness["recommendedNextTool"]);
        Assert.Equal(1, reads);
    }
    [Theory, InlineData("20"), InlineData("21")]
    public void Host_does_not_turn_unknown_membership_into_success(string release)
    {
        var unknown = new InvalidOperationException("Membership unavailable");
        var actual = Assert.Throws<InvalidOperationException>(() => Diagnostics.Readiness(release, _ => "C:/fixture/API", () => throw unknown));
        Assert.Same(unknown, actual);
    }
}
