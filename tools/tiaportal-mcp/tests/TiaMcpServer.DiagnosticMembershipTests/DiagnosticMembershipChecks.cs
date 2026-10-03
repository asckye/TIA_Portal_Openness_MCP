using System.Collections.Generic;
using TiaMcpServer.Tests.Shared;
using Xunit;
using Xunit.Sdk;

[XunitTestCaseDiscoverer("TiaMcpServer.Tests.Shared.CheckTheoryDiscoverer", "TiaMcpServer.DiagnosticMembershipTests")]
public sealed class CheckTheoryAttribute : TheoryAttribute { }

public sealed class DiagnosticMembershipChecks
{
    [CheckTheory]
    [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
    public void Check(CheckRow row) => row.Verify();

    public static IEnumerable<object[]> Rows() => CheckSuite.Run(nameof(DiagnosticMembershipTests), DiagnosticMembershipTests.Run);
}
