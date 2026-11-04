using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{

    internal static int StaticCheck(string command, Options options)
    {
        if (options.Has("SelfTest"))
        {
            // The test project references this running tool. Rebuilding its Windows
            // apphost would try to replace the executable currently holding the gate.
            var result = SuiteHang.RunProcess(Dotnet, AddOfflineNuGetConfig(Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", .. options.Has("NoBuild") ? new[] { "--no-build", "--no-restore" } : [], "-p:UseAppHost=false", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false", "--filter", "Category=StaticChecks"]), Root);
            Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError);
            return result.ExitCode;
        }
        var root = Path.GetFullPath(options.Get("Root", Root));
        return command switch
        {
            "check-analyzer-warnings" => AnalyzerWarnings.Run(root, options),
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
