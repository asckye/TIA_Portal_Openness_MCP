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
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseRefusesMissingCompanionBeforeVersionChangesBuildOrAdoption(bool adopt)
    {
        var root = TiaMcp.BuildCommon.Repository.FindRoot();
        var versionBytes = File.ReadAllBytes(Path.Combine(root, "Version.props"));
        var previousCompanion = Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        var missing = Path.Combine(root, "bin-build", "missing-" + Guid.NewGuid().ToString("N"), "python.exe");
        var arguments = new List<string> { "-Version", "4.0.0-beta.3", "-DryRun", "-EcosystemPython", missing };
        if (adopt) arguments.AddRange(["-AdoptEvidence", Path.Combine(root, "bin-build", "missing-evidence.json")]);
        var error = Assert.Throws<ReleaseException>(() => ReleaseCommands.Run("release", Options.Parse(arguments.ToArray(), new HashSet<string> { "DryRun" })));
        Assert.Contains(missing, error.Message);
        Assert.Contains("no build started", error.Message);
        Assert.Equal(versionBytes, File.ReadAllBytes(Path.Combine(root, "Version.props")));
        Assert.Equal(previousCompanion, Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON"));
    }

    [Theory]
    [Trait("Category", "Prerequisites")]
    [InlineData("10.0.100 [sdk]", true)]
    [InlineData("9.0.100 [sdk]", false)]
    public void SdkProbeAcceptsOnlyDotnetTen(string output, bool expected)
    {
        if (expected) Assert.StartsWith("10.", ReleasePrerequisites.RequireSdk(output, "10", ".NET 10 SDK"));
        else Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequireSdk(output, "10", ".NET 10 SDK"));
    }

    [Theory]
    [Trait("Category", "Prerequisites")]
    [InlineData("3.12.0", true)]
    [InlineData("3.10.0", false)]
    public void PythonProbeEnforcesTheCompanionMinimum(string output, bool expected)
    {
        if (expected) Assert.Equal(output, ReleasePrerequisites.RequirePython(output, 3, 12, "Python"));
        else Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequirePython(output, 3, 12, "Python"));
    }

    [Fact]
    [Trait("Category", "Prerequisites")]
    public void MissingTokenIsAProbeFailureWithoutPrintingASecret() =>
        Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequireToken(""));

    [Fact]
    [Trait("Category", "Prerequisites")]
    public void DirtyNonManagedTreeIsRejected() =>
        Assert.False(ReleaseValidation.IsReleaseManagedChange(" M tracked-script.txt"));

    [Fact]
    [Trait("Category", "Prerequisites")]
    public void InsufficientDiskSpaceIsRejected() =>
        Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequireFreeSpace(1L << 30, 10));

    [Fact]
    [Trait("Category", "Prerequisites")]
    public void CachedArchiveHashMismatchIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "release-archive-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            File.WriteAllText(path, "not the pinned archive");
            var archive = new JsonElementArchive(Path.GetFileName(path), "https://builds.dotnet.microsoft.com/dotnet/test.zip", new string('0', 128));
            Assert.Throws<ReleaseException>(() => ReleasePrerequisites.RequirePinnedArchive(archive, path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Category", "Prerequisites")]
    public void ProbeRunnerEvaluatesEveryProbeAfterFailure()
    {
        var finalRan = false;
        var results = ReleasePrerequisites.Evaluate([
            new("first", () => throw new ReleaseException("synthetic")),
            new("last", () => { finalRan = true; return "ok"; })
        ]);
        Assert.True(finalRan);
        Assert.Equal(2, results.Count);
        Assert.False(results[0].Passed);
        Assert.True(results[1].Passed);
    }

    [Theory]
    [Trait("Category", "Prerequisites")]
    [InlineData("A")]
    [InlineData("B")]
    public void ProbeValuesRemainBoundToTheirOwnProbe(string value)
    {
        var results = ReleasePrerequisites.Evaluate([new("bound", () => value)]);
        Assert.Equal(value, results.Single().Detail);
    }

    [Fact]
    [Trait("Category", "Prerequisites")]
    public void TwoProbeValuesStayBoundDuringTheSameEvaluation()
    {
        var probes = new[] { "A", "B" }
            .Select(value => new PrerequisiteProbe(value, () => value))
            .ToArray();
        var results = ReleasePrerequisites.Evaluate(probes);
        Assert.Equal(["A", "B"], results.Select(result => result.Detail));
    }

    [Theory]
    [Trait("Category", "Prerequisites")]
    [InlineData(" M manifest/release-build.json", true)]
    [InlineData(" M Version.props", true)]
    [InlineData(" M build-tools/release/ReleaseCommands.cs", false)]
    [InlineData("?? manifest/new.json", false)]
    [InlineData(" M manifest/history/contracts-v3/baseline/21.json", false)]
    public void ReleaseManagedFileFilterIsNarrow(string status, bool expected) =>
        Assert.Equal(expected, ReleaseValidation.IsReleaseManagedChange(status));

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
