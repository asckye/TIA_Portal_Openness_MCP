using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Core.Tests;

public sealed class WindowsFirewallRuleTests
{
    [Theory]
    [InlineData(true, 1, 1, 6, "8080", "192.168.1.20", "8080", "192.168.1.20", true)]
    [InlineData(false, 1, 1, 6, "8080", "192.168.1.20", "8080", "192.168.1.20", false)]
    [InlineData(true, 2, 1, 6, "8080", "192.168.1.20", "8080", "192.168.1.20", false)]
    [InlineData(true, 1, 2, 6, "8080", "192.168.1.20", "8080", "192.168.1.20", false)]
    [InlineData(true, 1, 1, 17, "8080", "192.168.1.20", "8080", "192.168.1.20", false)]
    [InlineData(true, 1, 1, 6, "8080-8090", "192.168.1.20", "8080", "192.168.1.20", false)]
    [InlineData(true, 1, 1, 6, "8080", "192.168.1.21", "8080", "192.168.1.20", false)]
    public void Only_the_configured_enabled_inbound_tcp_rule_is_accepted(bool enabled, int direction, int action,
        int protocol, string localPorts, string localAddresses, string expectedPort, string expectedAddress, bool expected)
    {
        Assert.Equal(expected, WindowsFirewallRule.Matches(enabled, direction, action, protocol, localPorts, localAddresses,
            expectedPort, expectedAddress));
    }

    [Fact]
    public void Rules_created_by_earlier_releases_are_found_by_display_name_and_address()
    {
        const string name = "TIA-MCP-192.168.1.10-8765";
        string legacy = WindowsFirewallRule.LegacyDisplayName(8765);
        Assert.Equal("TIA MCP TCP 8765", legacy);
        Assert.True(WindowsFirewallRule.IsSameRule(name, "192.168.1.10", name, legacy, "192.168.1.10"));
        Assert.True(WindowsFirewallRule.IsSameRule("TIA MCP TCP 8765", "192.168.1.10", name, legacy, "192.168.1.10"));
        Assert.False(WindowsFirewallRule.IsSameRule("TIA MCP TCP 8765", "192.168.1.11", name, legacy, "192.168.1.10"));
        Assert.False(WindowsFirewallRule.IsSameRule("TIA MCP TCP 8766", "192.168.1.10", name, legacy, "192.168.1.10"));
    }
}
