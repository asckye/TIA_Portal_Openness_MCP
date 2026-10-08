using System.Collections.Generic;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Xunit.Sdk;

[XunitTestCaseDiscoverer("TiaMcp.Engine.Tests.Shared.CheckTheoryDiscoverer", "TiaMcp.FoundationHost.DeviceAdd.Tests")]
public sealed class CheckTheoryAttribute : TheoryAttribute { }

public sealed class DeviceAddChecks
{
    [CheckTheory]
    [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
    public void Check(CheckRow row) => row.Verify();

    public static IEnumerable<object[]> Rows() => CheckSuite.Run(nameof(DeviceAddTests), DeviceAddTests.Run);
}
