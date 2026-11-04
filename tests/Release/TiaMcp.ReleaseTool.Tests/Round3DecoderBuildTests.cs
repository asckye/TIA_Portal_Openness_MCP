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
    public void Decoder_python_resolves_the_environment_without_an_explicit_option()
    {
        var previous = Environment.GetEnvironmentVariable(ReleaseCommands.DecoderPythonVariable);
        var python = Path.Combine(Path.GetTempPath(), "pinned-python", "python.exe");
        try
        {
            Environment.SetEnvironmentVariable(ReleaseCommands.DecoderPythonVariable, python);
            Assert.Equal(python, ReleaseCommands.DecoderPythonOptions(Args()).Get("Python"));
            Assert.Equal("explicit-python.exe", ReleaseCommands.DecoderPythonOptions(Args("-Python", "explicit-python.exe")).Get("Python"));
        }
        finally { Environment.SetEnvironmentVariable(ReleaseCommands.DecoderPythonVariable, previous); }
    }

    [Fact]
    public void Check_jobs_receive_the_absolute_prepared_runtime_and_selected_pin()
    {
        string root = Repository.FindRoot();
        var environment = new Dictionary<string, string?> { ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = "suite-sdk" };
        ReleaseCommands.ConfigureDecoderTestEnvironment(environment, root, Args("-Python", "selected-python.exe"));
        Assert.Equal(DecoderRuntimeFixture.RuntimeVariable, ReleaseCommands.DecoderRuntimeVariable);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, DecoderRuntime.RelativeDirectory)), environment[DecoderRuntimeFixture.RuntimeVariable]);
        Assert.Equal("selected-python.exe", environment[ReleaseCommands.DecoderPythonVariable]);
        Assert.Equal(typeof(ReleaseCommands).Assembly.Location, environment["TIA_MCP_TEST_DECODER_PREPARE_TOOL"]);
        Assert.Equal("suite-sdk", environment["TIA_MCP_TEST_PUBLIC_API_ROOT"]);
    }

    [Fact]
    public void Prepared_fixture_verifies_the_receipt_without_starting_a_release_tool()
    {
        using var tree = new StaticTree();
        string directory = Path.GetFullPath(Path.Combine(tree.Root, DecoderRuntime.RelativeDirectory));
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(DecoderRuntimeFixture.Ensure())) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        string missingTool = Path.Combine(tree.Root, "not-built.dll");
        Assert.False(File.Exists(missingTool));
        Assert.Equal(directory, DecoderRuntimeFixture.Prepare(tree.Root, directory, missingTool));
        File.WriteAllText(Path.Combine(directory, "runtime.json"), "{}");
        var error = Assert.Throws<InvalidOperationException>(() => DecoderRuntimeFixture.Prepare(tree.Root, directory, missingTool));
        Assert.Contains(DecoderRuntimeFixture.RuntimeVariable, error.Message);
        Assert.IsType<DecoderRuntimeException>(error.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("runtime/decoder-python")]
    public void Configured_fixture_runtime_must_be_absolute_and_never_falls_back(string directory)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DecoderRuntimeFixture.Prepare(Repository.FindRoot(), directory, typeof(ReleaseCommands).Assembly.Location));
        Assert.Contains(DecoderRuntimeFixture.RuntimeVariable, error.Message);
        Assert.Contains("absolute", error.Message);
    }

    [Fact]
    public void Standalone_fixture_names_the_preparation_command_when_the_tool_is_not_built()
    {
        using var tree = new StaticTree();
        var error = Assert.Throws<InvalidOperationException>(() => DecoderRuntimeFixture.Prepare(tree.Root, null, null));
        Assert.Contains("dotnet run --project build-tools/release -- prepare-decoder-python -Python", error.Message);
        Assert.Contains(DecoderRuntimeFixture.RuntimeVariable, error.Message);
    }

    [Fact]
    public void Standalone_fixture_uses_an_existing_assembly_without_rebuilding_it()
    {
        string tool = typeof(ReleaseCommands).Assembly.Location;
        string hash = FileHash.Sha256(tool);
        var written = File.GetLastWriteTimeUtc(tool);
        Assert.Equal(Path.GetFullPath(Path.Combine(Repository.FindRoot(), DecoderRuntime.RelativeDirectory)), DecoderRuntimeFixture.Prepare(Repository.FindRoot(), null, tool));
        Assert.Equal(hash, FileHash.Sha256(tool));
        Assert.Equal(written, File.GetLastWriteTimeUtc(tool));
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
