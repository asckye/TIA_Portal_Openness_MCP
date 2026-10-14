using TiaMcp.BuildCommon;
using TiaMcp.ReleaseTool;
using Xunit;

namespace TiaMcp.ReleaseTool.Tests;

public sealed class ChildToolInvocationTests
{
    [Theory]
    [InlineData("preflight")]
    [InlineData("build-tool")]
    [InlineData("validate-bundle")]
    public void ChildStepsInvokeTheExecutingAssemblyWithoutBuilding(string command)
    {
        var arguments = ReleaseValidation.ChildToolArguments(command, "-Strict");
        Assert.Equal(typeof(ReleaseCommands).Assembly.Location, arguments[0]);
        Assert.Equal(new[] { command, "-Strict" }, arguments.Skip(1));
        Assert.DoesNotContain("run", arguments);
        Assert.DoesNotContain("--project", arguments);
        Assert.Equal(new[] { "--release-tool", arguments[0] }, ReleaseValidation.PackageToolArguments());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PythonPackagerUsesProvidedAssemblyOrBuildsOnePrivateRunner(bool standalone)
    {
        using var fixture = new OfflineFixtures();
        var root = Repository.FindRoot();
        var script = """
            import importlib.util, pathlib, sys
            spec = importlib.util.spec_from_file_location('packager', pathlib.Path(sys.argv[1]) / 'build-tools/Package-Release.py')
            p = importlib.util.module_from_spec(spec); spec.loader.exec_module(p)
            root = pathlib.Path(sys.argv[2]); assembly = root / 'snapshot-runner/TiaMcp.ReleaseTool.dll'
            assembly.parent.mkdir(); assembly.touch()
            calls = []
            def build(args, **kwargs):
                calls.append(args)
                output = pathlib.Path(args[args.index('-o') + 1]); output.mkdir(parents=True)
                (output / 'TiaMcp.ReleaseTool.dll').touch()
            p.subprocess.run = build
            command = p.validator_command(root, None if sys.argv[3] == 'True' else assembly)
            assert command[2:] == ['validate-bundle'], command
            assert pathlib.Path(command[1]).is_file()
            assert 'run' not in command and '--project' not in command
            if sys.argv[3] == 'True':
                assert len(calls) == 1 and calls[0][1] == 'build', calls
                assert pathlib.Path(command[1]).is_relative_to(root / 'bin-build/package-runners')
            else:
                assert command[1] == str(assembly) and calls == [], (command, calls)
            """;
        var result = ProcessRunner.Run(Environment.GetEnvironmentVariable("PYTHON_EXE") ?? "python",
            ["-c", script, root, fixture.DirectoryPath, standalone.ToString()], root);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
    }
}
