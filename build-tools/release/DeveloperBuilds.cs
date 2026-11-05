using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

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

    internal static string[][] BuildGroups(string root, IEnumerable<string> programs, bool prebuiltReferences = false)
    {
        var ordered = programs.Order(StringComparer.Ordinal).ToArray();
        if (prebuiltReferences) return ordered.Select(program => new[] { program }).ToArray();
        // File apps have independent SDK runfile directories. Their project
        // references still write ordinary obj/bin paths, so keep every such
        // writer on one serial lane (including transitive/conditional references).
        var referenced = ordered.Where(program => References(root, program)).ToArray();
        return [.. referenced.Length == 0 ? Array.Empty<string[]>() : new[] { referenced },
            .. ordered.Except(referenced, StringComparer.Ordinal).Select(program => new[] { program })];
    }

    private static bool References(string root, string program) => File.ReadLines(Path.Combine(root, program)).Any(line =>
        System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*#:project(?:\s|$)"));

    internal static CommandResult Build(string root, string directory, string dotnet, string? nuget, int parallelism = 1, SuiteProcess? process = null)
    {
        Directory.CreateDirectory(directory);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, CommandResult>(StringComparer.Ordinal);
        var programs = Discover(root);
        Parallel.ForEach(BuildGroups(root, programs), new ParallelOptions { MaxDegreeOfParallelism = parallelism }, group =>
        {
            foreach (var program in group)
            {
                var reports = Path.Combine(directory, "file-programs", program.Replace('/', '_'));
                Directory.CreateDirectory(reports);
                var arguments = new List<string> { "build", Path.Combine(root, program), "-c", "Release", "--nologo",
                    "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:TiaMcpCollectFileBasedPrograms=true" };
                if (nuget != null) arguments.Add("-p:RestoreConfigFile=" + nuget);
                var environment = new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = reports, ["MSBUILDDISABLENODEREUSE"] = "1" };
                var log = Path.Combine(directory, program.Replace('/', '_') + ".log");
                var result = process == null ? ProcessRunner.Run(dotnet, arguments, root, environment, logPath: log) : process(dotnet, arguments, root, environment);
                if (process != null) File.WriteAllText(log, result.StandardOutput + result.StandardError);
                results[program] = result;
                foreach (var report in Directory.GetFiles(reports)) File.Copy(report, Path.Combine(directory, Path.GetFileName(report)), true);
            }
        });
        var ordered = programs.Select(program => results[program]).ToArray();
        return new(ordered.FirstOrDefault(result => result.ExitCode != 0)?.ExitCode ?? 0,
            string.Concat(ordered.Select(result => result.StandardOutput)), string.Concat(ordered.Select(result => result.StandardError)));
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
        var result = FileBasedPrograms.Build(Root, directory, Dotnet, nuget == null ? null : Path.GetFullPath(nuget),
            FastSuites.Positive(options, "BuildParallelism", GetMaxParallelism(options)));
        Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError);
        if (result.ExitCode == 0) AnalyzerWarnings.RememberDirectory(Root, directory);
        return result.ExitCode;
    }

    private static int BuildSolutions(Options options)
    {
        EnsureWindows("build-solutions");
        int parallelism = FastSuites.Positive(options, "BuildParallelism", GetMaxParallelism(options));
        // Ordinary solution variants can share obj/bin through reference edges.
        // The isolated check-all graph owns node parallelism; these roots retain
        // their serial writes, while independent file apps use the build budget.
        var properties = new List<string> { "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false" };
        if (options.Get("PublicApiRoot") is { } api) properties.Add("-p:TiaPublicApiRoot=" + Path.GetFullPath(api));
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (nuget is not null) properties.Add("-p:RestoreConfigFile=" + Path.GetFullPath(nuget));
        var directory = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(Root, "bin-build/analyzer-warnings/solutions-" + Guid.NewGuid().ToString("N"))));
        AnalyzerWarnings.LocalDirectory(directory);
        if (Directory.Exists(directory)) throw new ReleaseException("Analyzer report directory must be fresh: " + directory, 64);
        Directory.CreateDirectory(directory);
        var environment = new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = directory };
        foreach (var solution in new[] { "build-tools/TiaPortalOpenness.Offline.slnf", "TiaPortalOpenness.slnx" })
        {
            var result = ProcessRunner.Run(Dotnet, ["build", Path.Combine(Root, solution), "-c", "Release", "--nologo", .. properties], Root, environment,
                logPath: Path.Combine(directory, Path.GetFileNameWithoutExtension(solution) + ".log"));
            Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError); ProcessRunner.RequireSuccess(result, solution);
        }
        var scripts = FileBasedPrograms.Build(Root, directory, Dotnet, nuget == null ? null : Path.GetFullPath(nuget), parallelism);
        Console.Write(scripts.StandardOutput); Console.Error.Write(scripts.StandardError); ProcessRunner.RequireSuccess(scripts, "File-based programs");
        AnalyzerWarnings.RememberDirectory(Root, directory);
        var analyzerResult = AnalyzerWarnings.Run(Root, Options.Parse(["-Check", "-DiagnosticsDirectory", directory], new HashSet<string> { "Check" }));
        return analyzerResult;
    }
}
