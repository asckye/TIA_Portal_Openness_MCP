using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static readonly object StaticOutputLock = new();
    internal static CommandResult RunStaticCaptured(string command, string[] arguments)
    {
        lock (StaticOutputLock)
        {
            var stdout = Console.Out; var stderr = Console.Error;
            using var output = new StringWriter(); using var error = new StringWriter();
            try
            {
                Console.SetOut(output); Console.SetError(error);
                var code = StaticCheck(command, Options.Parse(arguments, new HashSet<string>(["SelfTest", "NoBinaries", "PackageMode", "UpdateBaseline", "AllowGrowth", "Fix"], StringComparer.OrdinalIgnoreCase)));
                return new CommandResult(code, output.ToString(), error.ToString());
            }
            catch (ReleaseException ex) { return new CommandResult(ex.ExitCode, output.ToString(), error + ex.Message); }
            finally { Console.SetOut(stdout); Console.SetError(stderr); }
        }
    }
    internal static int StaticCheck(string command, Options options)
    {
        if (options.Has("SelfTest"))
        {
            // The test project references this running tool. Rebuilding its Windows
            // apphost would try to replace the executable currently holding the gate.
            var result = ProcessRunner.Run(Dotnet, AddOfflineNuGetConfig(Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", "-p:UseAppHost=false", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "--filter", "Category=StaticChecks"]), Root);
            Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError);
            return result.ExitCode;
        }
        var root = Path.GetFullPath(options.Get("Root", Root));
        return command switch
        {
            "check-ratchet" => RatchetChecks.Run(root, options),
            "check-envelope-rewrite" => EnvelopeRewrite.Run(root, options),
            "check-adapter-boundary" => SourceCheck.Report("Adapter boundary", AdapterBoundary.Check(root)),
            "check-bundle-layout" => BundleLayoutChecks.Run(root),
            "check-repository" => RepositoryChecks.Run(root, options),
            "check-dead-tool-references" => DeadToolReferences.Run(root, options.Has("Fix")),
            "check-tia-features" => TiaFeatures.Run(root, options),
            "check-script-tool-calls" => ScriptToolCalls.Run(root),
            _ => throw new ReleaseException("Unknown static check", 64)
        };
    }
}
