using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TiaMcp.ReleaseTool;

internal static class FileBasedPrograms
{
    internal static string[] Discover(string root)
    {
        // Ignore generated outputs and third-party sources. A directive/shebang
        // identifies a file app even below a project that explicitly excludes it.
        var files = new[] { "scripts", "src", "tests", "build-tools" }.SelectMany(directory => SourceCheck.Files(Path.Combine(root, directory)))
            .Concat(Directory.EnumerateFiles(root, "*.cs"));
        return files.Where(path => path.EndsWith(".cs", StringComparison.Ordinal)).Where(path =>
        {
            var source = File.ReadAllText(path);
            if (source.StartsWith("#!/usr/bin/env dotnet", StringComparison.Ordinal) || source.Split('\n').Any(line => line.StartsWith("#:", StringComparison.Ordinal))) return true;
            if (!CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().Members.OfType<GlobalStatementSyntax>().Any()) return false;
            for (var parent = new FileInfo(path).Directory; parent != null && parent.FullName.Length >= root.Length; parent = parent.Parent)
                if (parent.EnumerateFiles("*.csproj").Any()) return false;
            return true;
        }).Select(path => AnalyzerWarnings.RepositoryPath(root, path)).Order(StringComparer.Ordinal).ToArray();
    }

    internal static CommandResult Build(string root, string directory, string dotnet, string? nuget)
    {
        Directory.CreateDirectory(directory);
        var results = new List<CommandResult>();
        foreach (var program in Discover(root))
        {
            var arguments = new List<string> { "build", Path.Combine(root, program), "-c", "Release", "--nologo",
                "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:TiaMcpCollectFileBasedPrograms=true" };
            if (nuget != null) arguments.Add("-p:RestoreConfigFile=" + nuget);
            var result = ProcessRunner.Run(dotnet, arguments, root,
                new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = directory },
                logPath: Path.Combine(directory, program.Replace('/', '_') + ".log"));
            results.Add(result);
            // Keep compiling the remaining programs so failures cannot conceal coverage.
        }
        return new(results.FirstOrDefault(result => result.ExitCode != 0)?.ExitCode ?? 0,
            string.Concat(results.Select(result => result.StandardOutput)), string.Concat(results.Select(result => result.StandardError)));
    }
}

internal static partial class ReleaseCommands
{
    private static int BuildFilePrograms(Options options)
    {
        var directory = Path.GetFullPath(options.Get("OutputDirectory", Environment.GetEnvironmentVariable("TIA_MCP_ANALYZER_REPORT_DIRECTORY") ??
            AnalyzerWarnings.DefaultDirectory(Root)));
        AnalyzerWarnings.LocalDirectory(directory);
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        var result = FileBasedPrograms.Build(Root, directory, Dotnet, nuget == null ? null : Path.GetFullPath(nuget));
        Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError);
        if (result.ExitCode == 0) AnalyzerWarnings.RememberDirectory(Root, directory);
        return result.ExitCode;
    }

    private static int BuildSolutions(Options options)
    {
        EnsureWindows("build-solutions");
        var properties = new List<string> { "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false" };
        if (options.Get("PublicApiRoot") is { } api) properties.Add("-p:TiaPublicApiRoot=" + Path.GetFullPath(api));
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (nuget is not null) properties.Add("-p:RestoreConfigFile=" + Path.GetFullPath(nuget));
        var directory = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(Root, "bin-build/analyzer-warnings/solutions-" + Guid.NewGuid().ToString("N"))));
        AnalyzerWarnings.LocalDirectory(directory);
        if (Directory.Exists(directory)) throw new ReleaseException("Analyzer report directory must be fresh: " + directory, 64);
        Directory.CreateDirectory(directory);
        var environment = new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = directory };
        foreach (var solution in new[] { "TiaPortalOpenness.Offline.slnx", "TiaPortalOpenness.slnx" })
        {
            var result = ProcessRunner.Run(Dotnet, ["build", Path.Combine(Root, solution), "-c", "Release", "--nologo", .. properties], Root, environment,
                logPath: Path.Combine(directory, Path.GetFileNameWithoutExtension(solution) + ".log"));
            Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError); ProcessRunner.RequireSuccess(result, solution);
        }
        var scripts = FileBasedPrograms.Build(Root, directory, Dotnet, nuget == null ? null : Path.GetFullPath(nuget));
        Console.Write(scripts.StandardOutput); Console.Error.Write(scripts.StandardError); ProcessRunner.RequireSuccess(scripts, "File-based programs");
        AnalyzerWarnings.RememberDirectory(Root, directory);
        var analyzerResult = AnalyzerWarnings.Run(Root, Options.Parse(["-Check", "-DiagnosticsDirectory", directory], new HashSet<string> { "Check" }));
        if (options.Has("ResultCacheInventory"))
        {
            // Read-only policy/planning evidence after the final compilation. No
            // suite delegate, assertion, capture or comparison is executed.
            var catalog = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
            var jobs = new List<CheckJob>();
            foreach (var (name, node) in catalog)
            {
                var suite = node!.AsObject();
                FastSuites.AddJobs(jobs, Root, name, suite, Path.Combine(directory, "result-cache-inventory"), Dotnet,
                    suite["minimumPassed"]!.GetValue<int>() >= 100 ? FastSuites.Positive(options, "Shards", 8) : 1, options.Get("PublicApiRoot"), []);
            }
            CheckAllInputClosure.WriteInventory(Root, jobs, directory, Dotnet);
        }
        return analyzerResult;
    }
}
