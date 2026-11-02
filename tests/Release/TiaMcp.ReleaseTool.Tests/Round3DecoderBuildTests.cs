using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;
using TiaMcp.Engine.Tests.Shared;
using TiaMcp.Logic.ModelContextProtocol;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class Round3DecoderBuildTests
{
    private static Options Args(params string[] args) => Options.Parse(args, new HashSet<string>());

    [Fact]
    public void Release_and_check_builds_share_explicit_configuration_without_PATH_fallback()
    {
        var fromEnvironment = ReleaseCommands.DecoderPythonOptions(Args(), name => name == ReleaseCommands.DecoderPythonVariable ? "configured-python.exe" : null);
        Assert.Equal("configured-python.exe", fromEnvironment.Get("Python"));
        var explicitChoice = ReleaseCommands.DecoderPythonOptions(Args("-Python", "explicit-python.exe"), _ => "other-python.exe");
        Assert.Equal("explicit-python.exe", explicitChoice.Get("Python"));
        var error = Assert.Throws<ReleaseException>(() => ReleaseCommands.DecoderPythonOptions(Args(), _ => null));
        Assert.Contains(ReleaseCommands.DecoderPythonVariable, error.Message);
        Assert.Contains(DecoderRuntime.Version, error.Message);
        Assert.Contains("No PATH fallback", error.Message);
    }

    [Fact]
    public void Runtime_preparation_is_an_always_run_check_build_job()
    {
        Assert.Single(CheckAllJobInputs.ToolJobs, name => name == "prepare-decoder-python");
        Assert.True(CheckAllJobInputs.AlwaysRun("prepare-decoder-python"));
        Assert.NotNull(CheckAllJobInputs.Find(new("prepare-decoder-python", () => new(0, "", ""), []), new JsonObject()));
        foreach (string file in DecoderRuntime.RequiredFiles.Append("runtime.json"))
            Assert.Contains(DecoderRuntime.RelativeDirectory + "/" + file, ReleaseCommands.TestPackageProducts());
    }

    [Fact]
    public void Fixture_reuse_validates_the_pin_and_receipt_and_repairs_incomplete_outputs()
    {
        string prepared = DecoderRuntimeFixture.Ensure();
        string root = Path.Combine(Repository.FindRoot(), "bin-build", "decoder-build-" + Guid.NewGuid().ToString("N"));
        string runtime = Path.Combine(root, DecoderRuntime.RelativeDirectory);
        Directory.CreateDirectory(runtime);
        try
        {
            foreach (string file in Directory.EnumerateFiles(prepared)) File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)));
            string interpreter = Path.Combine(runtime, "python.exe");
            var timestamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(interpreter, timestamp);
            var options = ReleaseCommands.DecoderPythonOptions(Args("-Reuse=true"));
            Assert.Equal(0, ReleaseCommands.PrepareDecoderPython(options, root));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(interpreter));
            File.WriteAllText(Path.Combine(runtime, "runtime.json"), "{}");
            File.WriteAllText(Path.Combine(runtime, "stale.pyd"), "stale");
            Assert.Equal(0, ReleaseCommands.PrepareDecoderPython(options, root));
            Assert.Equal(interpreter, DecoderRuntime.Require(root));
            Assert.False(File.Exists(Path.Combine(runtime, "stale.pyd")));
            var absent = Args("-Python", Path.Combine(root, "absent-python.exe"), "-Reuse=true");
            Assert.Throws<ReleaseException>(() => ReleaseCommands.PrepareDecoderPython(absent, root));
            Assert.Equal(interpreter, DecoderRuntime.Require(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
