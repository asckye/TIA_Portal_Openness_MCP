using System.Runtime.CompilerServices;
using TiaMcpServer.Tests.Shared;
using Xunit;

public sealed class PublicApiTheoryAttribute : TheoryAttribute
{
    public PublicApiTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT")))
            Skip = "Set TIA_MCP_TEST_PUBLIC_API_ROOT to run the official PublicAPI metadata checks.";
    }
}

public sealed class ApiMetadataChecks
{
    [PublicApiTheory]
    [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
    public void Check(CheckRow row) => row.Verify();

    public static IEnumerable<object[]> Rows() => CheckSuite.Run(nameof(ApiMetadataTests), check =>
        ApiMetadataTests.Run(Environment.GetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT")!, AdapterRoot(), check));

    private static string AdapterRoot([CallerFilePath] string currentFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(currentFile)!, "../../src/TiaMcp.Adapters"));
}
