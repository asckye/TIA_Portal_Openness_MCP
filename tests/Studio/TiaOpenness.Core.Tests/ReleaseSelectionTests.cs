using System;
using System.Reflection;
using TiaOpenness.Client;
using Xunit;

namespace TiaOpenness.Core.Tests;

public class ReleaseSelectionTests
{
    [Theory]
    [InlineData("14.0.1.0", "21", "14sp1")]
    [InlineData("15.1.0.0,19.0.0.0,16.0.0.0", "21", "19")]
    [InlineData("20.0.0.0,21.0.0.0", "19", "21")]
    [InlineData("", "16,19,17", "19")]
    [InlineData("unsupported", "14sp1,15.1", "15.1")]
    public void Default_uses_newest_installed_identity_then_newest_bundled_release(string installed, string bundled, string expected)
        => Assert.Equal(expected, TiaOpenness.Core.Environment.WorkbenchReleaseSelection.Select(installed.Split(','), bundled.Split(',')));

    [Theory]
    [InlineData("14sp1", "14.0.1.0")]
    [InlineData("15.1", "15.1.0.0")]
    [InlineData("16", "16.0.0.0")]
    [InlineData("17", "17.0.0.0")]
    [InlineData("18", "18.0.0.0")]
    [InlineData("19", "19.0.0.0")]
    [InlineData("20", "20.0.0.0")]
    [InlineData("21", "21.0.0.0")]
    public void Selection_reaches_the_bridge_as_an_exact_API_identity(string key, string identity)
    {
        using var client = new BridgeClient(new[] { "--openness-version", "21" });
        var arguments = typeof(BridgeClient).GetMethod("NativeArguments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal("--openness-version " + identity, arguments.Invoke(client, new object[] { key }));
        Assert.Equal("--openness-version 21.0.0.0", arguments.Invoke(client, new object[] { null! }));
    }
}
