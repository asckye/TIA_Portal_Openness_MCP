using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class ScaffoldNullSpecTests
{
    [Theory]
    [InlineData("null", true)]
    [InlineData("null", false)]
    [InlineData(" \n null \t", true)]
    [InlineData(" \n null \t", false)]
    public void JsonNullRetainsTheInvalidParamsErrorBeforeCallingTheService(string spec, bool dryRun)
    {
        var tools = new ProjectLifecycleProjectTools(null!);
        var error = Assert.Throws<McpException>(() => tools.ScaffoldProject(spec, dryRun));
        Assert.Equal("BuildProjectScaffold: invalid spec JSON: spec parsed to null", error.Message);
        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
        Assert.Null(error.InnerException);
    }
}
