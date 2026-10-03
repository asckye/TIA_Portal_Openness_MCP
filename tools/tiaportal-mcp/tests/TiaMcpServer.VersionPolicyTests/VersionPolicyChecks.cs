using System.Collections.Generic;
using TiaMcpServer.Tests.Shared;
using Xunit;

public sealed class VersionPolicyChecks
{
    [Theory]
    [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
    public void Check(CheckRow row) => row.Verify();

    public static IEnumerable<object[]> Rows() => CheckSuite.Run(nameof(VersionPolicyTests), VersionPolicyTests.Run);
}
