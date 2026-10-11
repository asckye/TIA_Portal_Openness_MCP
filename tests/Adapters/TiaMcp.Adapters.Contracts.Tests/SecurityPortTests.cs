using System;
using System.Collections.Generic;
using System.Text.Json;
using TiaMcp.Adapters.Contracts;
using Xunit;

public sealed class SecurityPortTests
{
    public static IEnumerable<object[]> Availability()
    {
        var minimum = new Dictionary<string, int> {
            ["GetPlcPutGetAccess"] = 15, ["SetPlcPutGetAccess"] = 15,
            ["GetProjectProtection"] = 15, ["ManagePlcProtection"] = 15,
            ["ManageDeviceUsers"] = 16, ["ManagePlcCertificate"] = 16, ["ManageWatchForceTableWebAccess"] = 16,
            ["GetProjectUserManagement"] = 17, ["ManageProjectUserManagement"] = 17, ["ManageUmcUsers"] = 17,
            ["ManagePasswordPolicy"] = 18, ["ManageSyslogServers"] = 19
        };
        foreach (var release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (var tool in minimum)
            yield return new object[] { release, tool.Key, (release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release)) >= tool.Value };
    }

    [Theory, MemberData(nameof(Availability))]
    public void ExactReleaseRosterRetainsOnlySupportedTools(string release, string tool, bool expected)
        => Assert.Equal(expected, PortedFamilies.Available(release, tool));

    [Theory]
    [InlineData("16", "ManageDeviceUsers", "read", "simpleWebserver", true)]
    [InlineData("17", "ManageDeviceUsers", "read", "simpleWebserver", true)]
    [InlineData("18", "ManageDeviceUsers", "read", "simpleWebserver", false)]
    [InlineData("16", "ManagePlcProtection", "protectMasterSecret", "", true)]
    [InlineData("17", "ManagePlcProtection", "protectMasterSecret", "", false)]
    [InlineData("20", "ManagePlcProtection", "protectAllConfiguration", "", true)]
    [InlineData("21", "ManagePlcProtection", "protectAllConfiguration", "", false)]
    [InlineData("19", "ManageSyslogServers", "read", "project", true)]
    [InlineData("19", "ManageSyslogServers", "read", "device", false)]
    [InlineData("20", "ManageSyslogServers", "read", "project", false)]
    [InlineData("19", "ManageUmcUsers", "createOffline", "user", true)]
    [InlineData("20", "ManageUmcUsers", "createOffline", "user", false)]
    [InlineData("17", "ManageUmcUsers", "synchronize", "server", true)]
    [InlineData("18", "ManageUmcUsers", "synchronize", "server", false)]
    [InlineData("19", "ManageProjectUserManagement", "deleteDeviceRight", "", true)]
    [InlineData("20", "ManageProjectUserManagement", "deleteDeviceRight", "", false)]
    public void PartialApisRefuseOnlyTheirUnavailableActions(string release, string tool, string action, string scope, bool refused)
        => Assert.Equal(refused, SecurityCapabilities.Unsupported(release, tool, action, scope) != null);

    [Fact]
    public void UncertainPutGetWriteRetainsEvidenceAndLocksTheWorker()
    {
        var result = new SecurityDataResult(new Dictionary<string, object?> {
            ["ok"] = false, ["mayHaveChanged"] = true, ["writeOutcomeKnown"] = false, ["message"] = "SetAttribute failed"
        });
        Assert.True(result.RequiresSessionReset);
        Assert.True(result.BlockReadsAfterUncertain);
        var wire = JsonSerializer.SerializeToElement(result);
        Assert.Equal("SetAttribute failed", wire.GetProperty("message").GetString());
        Assert.False(wire.TryGetProperty(nameof(SecurityDataResult.RequiresSessionReset), out _));
        Assert.True(wire.GetProperty("mayHaveChanged").GetBoolean());
    }
}
