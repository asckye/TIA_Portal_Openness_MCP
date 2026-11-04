using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ReleasePrerequisiteTests
{
    [Theory]
    [Trait("Category", "Prerequisites")]
    [InlineData("EcosystemPython")]
    [InlineData("CompanionPython")]
    public void MissingCompanionReportsTheExactPathAndRepairBeforeBuild(string option)
    {
        var path = Path.Combine(Path.GetTempPath(), "missing companion " + Guid.NewGuid().ToString("N"), "python.exe");
        var options = Options.Parse(["-" + option, path], new HashSet<string>());
        var error = Assert.Throws<ReleaseException>(() => ReleaseCommands.ReleaseCompanionPython(options));
        Assert.Contains(path, error.Message);
        Assert.Contains("tia install-plc-tools", error.Message);
        Assert.Contains("-EcosystemPython / -CompanionPython", error.Message);
        Assert.Contains("no build started", error.Message);
    }

    [Theory]
    [Trait("Category", "Prerequisites")]
    [InlineData("EcosystemPython")]
    [InlineData("CompanionPython")]
    public void BothCompanionOptionsResolveTheSameExistingFile(string option)
    {
        var path = typeof(ReleasePrerequisiteTests).Assembly.Location;
        Assert.Equal(Path.GetFullPath(path), ReleaseCommands.ReleaseCompanionPython(Options.Parse(["-" + option, path], new HashSet<string>())));
    }

    [Theory]
    [Trait("Category", "Prerequisites")]
    [InlineData(0)]
    [InlineData(7)]
    public void NativeCommandRunnerRetainsExitCodeAndBothStreams(int exitCode)
    {
        var result = OperatingSystem.IsWindows()
            ? ProcessRunner.Run("cmd.exe", ["/d", "/c", $"echo harmless warning 1>&2 & exit /b {exitCode}"], Path.GetTempPath())
            : ProcessRunner.Run("/bin/sh", ["-c", $"printf harmless-warning >&2; exit {exitCode}"], Path.GetTempPath());
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains("harmless", result.StandardError);
    }
}
