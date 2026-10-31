using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal static class FileBasedPrograms
{
    internal sealed record Receipt(string Key, string Target, Dictionary<string, string> Files, Dictionary<string, string> Reports, string[]? OutputRoots = null);

    internal static bool Reuse(string receiptPath, string key, string directory)
    {
        if (!File.Exists(receiptPath)) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath));
            if (receipt == null || receipt.Key != key || !File.Exists(receipt.Target) || receipt.Files is not { Count: > 0 } || receipt.Reports is not { Count: > 0 } ||
                receipt.Files.Concat(receipt.Reports).Any(row => !File.Exists(row.Key) || ReleaseRecords.HashFile(row.Key) != row.Value)) return false;
            if (receipt.OutputRoots != null && !receipt.OutputRoots.SelectMany(path => Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).SequenceEqual(receipt.Files.Keys.Order(StringComparer.Ordinal))) return false;
            foreach (var report in receipt.Reports.Keys) File.Copy(report, Path.Combine(directory, Path.GetFileName(report)), true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

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

    internal static string[][] BuildGroups(string root, IEnumerable<string> programs)
    {
        var ordered = programs.Order(StringComparer.Ordinal).ToArray();
        // File apps have independent SDK runfile directories. Their project
        // references still write ordinary obj/bin paths, so keep every such
        // writer on one serial lane (including transitive/conditional references).
        var referenced = ordered.Where(program => References(root, program)).ToArray();
        return [.. referenced.Length == 0 ? Array.Empty<string[]>() : new[] { referenced },
            .. ordered.Except(referenced, StringComparer.Ordinal).Select(program => new[] { program })];
    }

    private static bool References(string root, string program) => File.ReadLines(Path.Combine(root, program)).Any(line =>
        System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*#:project(?:\s|$)"));

    internal static CommandResult Build(string root, string directory, string dotnet, string? nuget, int parallelism = 1, bool noBuildCache = false, SuiteProcess? process = null)
    {
        Directory.CreateDirectory(directory);
        var programs = Discover(root);
        bool disabled = noBuildCache || Environment.GetEnvironmentVariable("TIA_MCP_BUILD_CACHE_DISABLED") == "1";
        var sdk = (process ?? DotnetSuites.RunProcess)(dotnet, ["--version"], root, null);
        ProcessRunner.RequireSuccess(sdk, "File program SDK version");
        // Include referenced/linked sources and imported build configuration as
        // well as the program. Conservative invalidation avoids stale SDK apps.
        var inputs = new[] { "scripts", "src", "tests", "build-tools", "third_party", "reference", "schemas", "templates" }.SelectMany(path => SourceCheck.Files(Path.Combine(root, path)))
            .Concat(Directory.GetFiles(root)).Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("bin-build"))
            .Order(StringComparer.Ordinal).Select(path => new ReleaseArtifact(path, ReleaseRecords.HashFile(path))).ToList();
        if (nuget != null) inputs.Add(new(nuget, ReleaseRecords.HashFile(nuget)));
        var properties = new[] { sdk.StandardOutput.Trim(), Path.GetFullPath(root), dotnet,
            Path.GetTempPath(), Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "file-apps-v2;Release;NuGetAudit=false;UseSharedCompilation=false;Collect=true" };
        var configuration = inputs.Where(row => Path.GetExtension(row.Path) is ".props" or ".targets" or ".config" ||
            Path.GetFileName(row.Path) is ".editorconfig" or "global.json").ToArray();
        var referencedKey = BuildOutputCache.Key(inputs, properties);
        var configurationKey = BuildOutputCache.Key(configuration, properties);
        var sourceHashes = programs.ToDictionary(program => program, program => ReleaseRecords.HashFile(Path.Combine(root, program)), StringComparer.Ordinal);
        var keys = programs.ToDictionary(program => program, program => BuildOutputCache.Key(
            [new(program, sourceHashes[program])], [References(root, program) ? referencedKey : configurationKey]), StringComparer.Ordinal);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, CommandResult>(StringComparer.Ordinal);
        Parallel.ForEach(BuildGroups(root, programs), new ParallelOptions { MaxDegreeOfParallelism = parallelism }, group =>
        {
            foreach (var program in group)
            {
                var key = keys[program];
                var cache = Path.Combine(root, "bin-build/file-program-cache", key);
                var receipt = Path.Combine(cache, "success.json");
                var log = Path.Combine(directory, program.Replace('/', '_') + ".log");
                var reports = Path.Combine(directory, "file-programs", key);
                Directory.CreateDirectory(reports);
                if (!disabled && Reuse(receipt, key, reports))
                {
                    var message = "Verified file-program cache hit: " + program + "\n";
                    File.WriteAllText(log, message); results[program] = new(0, message, ""); continue;
                }
                // Reports are private even when two compilations include the
                // same leaf project; publication copies them in stable order.
                var arguments = new List<string> { "build", Path.Combine(root, program), "-c", "Release", "--nologo",
                    "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:TiaMcpCollectFileBasedPrograms=true",
                    "-t:Build", "-getProperty:TargetPath,IntermediateOutputPath" };
                if (nuget != null) arguments.Add("-p:RestoreConfigFile=" + nuget);
                var environment = new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = reports, ["MSBUILDDISABLENODEREUSE"] = "1" };
                var result = process == null ? ProcessRunner.Run(dotnet, arguments, root, environment, logPath: log) : process(dotnet, arguments, root, environment);
                if (process != null) File.WriteAllText(log, result.StandardOutput + result.StandardError);
                results[program] = result;
                if (result.ExitCode == 0 && !disabled)
                {
                    try
                    {
                        var selectedInputs = References(root, program) ? inputs : (IEnumerable<ReleaseArtifact>)configuration;
                        if (ReleaseRecords.HashFile(Path.Combine(root, program)) != sourceHashes[program] ||
                            selectedInputs.Any(row => !File.Exists(row.Path) || ReleaseRecords.HashFile(row.Path) != row.Sha256))
                        { File.Delete(receipt); continue; }
                        // MSBuild prints compiler warnings before its final
                        // property JSON. Keep them in the log and parse the tail.
                        var jsonStart = result.StandardOutput.LastIndexOf("\n{", StringComparison.Ordinal);
                        using var buildProperties = JsonDocument.Parse(jsonStart < 0 ? result.StandardOutput : result.StandardOutput[(jsonStart + 1)..]);
                        var target = buildProperties.RootElement.GetProperty("Properties").GetProperty("TargetPath").GetString()!;
                        var intermediate = buildProperties.RootElement.GetProperty("Properties").GetProperty("IntermediateOutputPath").GetString()!;
                        var outputRoots = new[] { Path.GetDirectoryName(target)!, Path.GetFullPath(intermediate, root) };
                        var outputFiles = outputRoots
                            .SelectMany(path => Directory.GetFiles(path, "*", SearchOption.AllDirectories)).Distinct(StringComparer.Ordinal)
                            .ToDictionary(path => path, ReleaseRecords.HashFile, StringComparer.Ordinal);
                        Directory.CreateDirectory(Path.Combine(cache, "reports"));
                        var reportFiles = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var file in Directory.GetFiles(reports))
                        {
                            var cached = Path.Combine(cache, "reports", Path.GetFileName(file)); File.Copy(file, cached, true);
                            reportFiles.Add(cached, ReleaseRecords.HashFile(cached));
                        }
                        File.WriteAllText(receipt, JsonSerializer.Serialize(new Receipt(key, target, outputFiles, reportFiles, outputRoots)) + "\n");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
                    { File.Delete(receipt); Console.Error.WriteLine("File program built without a cache receipt: " + program + ": " + ex.Message); }
                }
                // Keep compiling the remaining programs so failures cannot conceal coverage.
            }
        });
        foreach (var program in programs)
        {
            var key = keys[program];
            var reports = Path.Combine(directory, "file-programs", key);
            if (Directory.Exists(reports)) foreach (var file in Directory.GetFiles(reports)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        }
        var orderedResults = programs.Select(program => results[program]).ToArray();
        return new(orderedResults.FirstOrDefault(result => result.ExitCode != 0)?.ExitCode ?? 0,
            string.Concat(orderedResults.Select(result => result.StandardOutput)), string.Concat(orderedResults.Select(result => result.StandardError)));
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
            FastSuites.Positive(options, "BuildParallelism", GetMaxParallelism(options)), options.Has("NoBuildCache"));
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
        foreach (var solution in new[] { "TiaPortalOpenness.Offline.slnx", "TiaPortalOpenness.slnx" })
        {
            var result = ProcessRunner.Run(Dotnet, ["build", Path.Combine(Root, solution), "-c", "Release", "--nologo", .. properties], Root, environment,
                logPath: Path.Combine(directory, Path.GetFileNameWithoutExtension(solution) + ".log"));
            Console.Write(result.StandardOutput); Console.Error.Write(result.StandardError); ProcessRunner.RequireSuccess(result, solution);
        }
        var scripts = FileBasedPrograms.Build(Root, directory, Dotnet, nuget == null ? null : Path.GetFullPath(nuget), parallelism, options.Has("NoBuildCache"));
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
