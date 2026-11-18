using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class LadTextReadbackRegressionTests
{
    [Fact]
    public void ExistingRendererChecksKeepEveryAssertion()
        => LadTextRendererTests.Run((passed, message) => Assert.True(passed, message));
}
