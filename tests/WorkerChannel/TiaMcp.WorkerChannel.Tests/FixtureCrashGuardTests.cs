using System.Reflection;
using TiaMcp.Engine.Tests.Shared;
using Xunit;

namespace TiaMcp.WorkerChannel.Tests;

public sealed class FixtureCrashGuardTests
{
    [Theory]
    [InlineData("first\nsecond", "first")]
    [InlineData("first\rsecond", "first")]
    [InlineData("first\r\nsecond", "first")]
    [InlineData("", "")]
    public void FormatReportsTypeAndFirstMessageLine(string message, string expected) =>
        Assert.Equal("FIXTURE_UNHANDLED_EXCEPTION System.InvalidOperationException: " + expected,
            FixtureCrashGuard.Format(new InvalidOperationException(message)));

    [Theory]
    [InlineData(499)] [InlineData(500)] [InlineData(501)]
    public void FormatCapsTheMessageAtFiveHundredCharacters(int length) =>
        Assert.Equal("FIXTURE_UNHANDLED_EXCEPTION System.Exception: " + new string('x', Math.Min(length, 500)),
            FixtureCrashGuard.Format(new Exception(new string('x', length) + "\nlater")));

    [Fact]
    public void FormatUnwrapsTheFirstInnerExceptionRecursively()
    {
        var error = new AggregateException(new TargetInvocationException(new InvalidOperationException("first\nsecret")), new Exception("second"));
        Assert.Equal("FIXTURE_UNHANDLED_EXCEPTION System.InvalidOperationException: first", FixtureCrashGuard.Format(error));
        Assert.Equal("FIXTURE_UNHANDLED_EXCEPTION System.Reflection.TargetInvocationException: outer",
            FixtureCrashGuard.Format(new TargetInvocationException("outer", null)));
    }

    [Fact]
    public async Task GuardPreservesExplicitResults()
    {
        Assert.Equal(7, FixtureCrashGuard.Run(() => 7));
        Assert.Equal(23, await FixtureCrashGuard.RunAsync(() => Task.FromResult(23)));
        Assert.Equal(86, FixtureCrashGuard.UnhandledExceptionExitCode);
    }

    [Fact]
    public void ExitDescriptionIncludesTheCrashLineOrOrdinaryStderr()
    {
        const string line = "FIXTURE_UNHANDLED_EXCEPTION System.Exception: fault";
        Assert.Equal("fixture crashed with an unhandled exception (exit 86): " + line, FixtureExit.Describe(86, "before\n" + line + "\nafter"));
        Assert.Equal("fixture crashed with an unhandled exception (exit 86): ", FixtureExit.Describe(86, ""));
        Assert.Equal("exit 7: stderr", FixtureExit.Describe(7, "stderr"));
        Assert.Equal("exit 0", FixtureExit.Describe(0, ""));
    }
}
