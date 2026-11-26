using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static partial class MergeCheck
{
    private static readonly object TransportFallbackGate = new();
    internal const string ConfigurationProject = "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj";
    internal const string DiagnosticsProject = "tests/FoundationHost/TiaMcp.Engine.Diagnostics.Tests/TiaMcp.Engine.Diagnostics.Tests.csproj";
    internal sealed record Evaluation(string[] Inputs, string[] Projects);
    internal sealed record Job(string Name, string Executable, string[] Arguments, bool Serial = false, bool PostSuite = false, bool Isolated = false);
    internal sealed class Plan
    {
        internal bool DocsOnly { get; set; }
        internal bool Wide { get; set; }
        internal Dictionary<string, HashSet<string>> Suites { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, HashSet<string>> Builds { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Derived { get; } = new(StringComparer.Ordinal);
        internal List<Job> Checks { get; } = [];
        internal bool Table => Builds.ContainsKey("eight-release-table");
    }

    internal const string SourceHostProject = "src/FoundationHost/TiaMcp.FoundationHost.csproj";
    internal const string TransportFixtureProject = "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/TiaMcp.WorkerChannel.TransportFixture.csproj";
    internal const string ApiCompileProject = "tests/FoundationHost/TiaMcp.Engine.ApiCompileChecks/TiaMcp.Engine.ApiCompileChecks.csproj";
    internal const string AdapterDiagnosticsProject = "tests/Adapters/TiaMcp.Adapters.Diagnostics.Tests/TiaMcp.Adapters.Diagnostics.Tests.csproj";
    internal const string CampaignProgram = "scripts/checks/Test-CampaignInputs.cs";

    internal sealed record BuildProject(string Path, string? Properties = null);

    internal static XDocument Traversal(IEnumerable<string> projects) => Traversal(projects.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new BuildProject(path)));

    internal static XDocument Traversal(IEnumerable<BuildProject> projects) => new(new XElement("Project",
        new XElement("ItemGroup", projects.Distinct().Select(project => new XElement("MergeProject", new XAttribute("Include", project.Path),
            project.Properties == null ? null : new XElement("AdditionalProperties", project.Properties)))),
        new[] { "Restore", "Build" }.Select(target => new XElement("Target", new XAttribute("Name", target),
            new XElement("MSBuild", new XAttribute("Projects", "@(MergeProject)"), new XAttribute("Targets", target),
                new XAttribute("BuildInParallel", target == "Build" ? "true" : "false"), new XAttribute("StopOnFirstFailure", "false"))))));

    internal static XDocument TableRestore(IEnumerable<PrepareBuild.Project> projects)
    {
        var document = PrepareBuild.BuildTableDocument(projects);
        document.Descendants("Target").Single().SetAttributeValue("Name", "Restore");
        var task = document.Descendants("MSBuild").Single();
        task.SetAttributeValue("Targets", "Restore");
        task.SetAttributeValue("BuildInParallel", "false");
        task.SetAttributeValue("StopOnFirstFailure", "false");
        task.SetAttributeValue("Properties", "Configuration=Release");
        return document;
    }

    internal static BuildProject VariantProject(string root, JsonObject suite, string output)
    {
        var properties = DotnetSuites.BuildArguments(suite["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>()))
            .Select(arg => arg[3..]);
        var intermediate = "obj/Release/net10.0/v" + suite["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>())
            .Single(arg => arg.StartsWith("-p:EngineRelease=", StringComparison.Ordinal))[17..] + "/";
        return new(Path.Combine(root, suite["project"]!.GetValue<string>()),
            string.Join(';', properties.Append("OutputPath=" + output + Path.DirectorySeparatorChar).Append("IntermediateOutputPath=" + intermediate)));
    }

    internal static string TimingHistory(string root, Func<string[], CommandResult> git)
    {
        var common = git(["rev-parse", "--path-format=absolute", "--git-common-dir"]);
        ProcessRunner.RequireSuccess(common, "Resolve timing history checkout");
        var directory = Path.GetFullPath(Path.Combine(root, common.StandardOutput.Trim()));
        return Path.Combine(Directory.GetParent(directory)!.FullName, "bin-build/check-timings");
    }

    internal sealed record Timing(string Name, double Seconds, CommandResult Result, double Start = 0, double End = 0);

    internal sealed record Prerequisites(string Api, string Python, string? NuGet);

    internal static string ResolvePublicApi(string root, string? api, Func<string[], CommandResult> git)
    {
        if (api == null)
        {
            api = Path.Combine(root, "sdk");
            if (!Directory.Exists(api))
            {
                var common = git(["rev-parse", "--path-format=absolute", "--git-common-dir"]);
                if (common.ExitCode != 0 || string.IsNullOrWhiteSpace(common.StandardOutput))
                    throw new ReleaseException("PublicAPI SDK is missing; pass -PublicApiRoot <SDK-root>. " + common.StandardError);
                api = Path.Combine(Directory.GetParent(Path.GetFullPath(Path.Combine(root, common.StandardOutput.Trim())))!.FullName, "sdk");
            }
        }
        return Path.GetFullPath(Path.Combine(root, api));
    }

    internal static Prerequisites RequirePrerequisites(string root, Options options, Func<string[], CommandResult> git,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var api = options.Get("PublicApiRoot") ?? environment("TIA_MCP_TEST_PUBLIC_API_ROOT");
        api = ResolvePublicApi(root, api, git);
        try { Prepare.RequirePublicApi(root, api); }
        catch (ReleaseException ex) { throw new ReleaseException("PublicAPI SDK is missing or incomplete; pass -PublicApiRoot <SDK-root>. " + ex.Message); }
        var python = ReleaseCommands.DecoderPythonOptions(options, environment).Get("Python")!;
        if (!Path.IsPathFullyQualified(python) || !File.Exists(python))
            throw new ReleaseException("Decoder Python is missing; pass -Python <absolute-python.exe> or set TIA_MCP_DECODER_PYTHON");
        var nuget = options.Get("NuGetConfig") ?? environment("TIA_MCP_OFFLINE_NUGET_CONFIG") ?? environment("RestoreConfigFile");
        if (nuget != null)
        {
            nuget = Path.GetFullPath(Path.Combine(root, nuget));
            if (!File.Exists(nuget)) throw new ReleaseException("NuGet config is missing: " + nuget);
            if (ReleaseValidation.OfflineNuGetError(nuget) is { } error) throw new ReleaseException(error);
        }
        return new(api, python, nuget);
    }

    internal static int Run(string root, Options options, SuiteProcess? process = null, Func<string[], CommandResult>? git = null,
        Func<Options, string, int>? prepareDecoder = null)
    {
        int parallelism = FastSuites.Positive(options, "MaxParallelism", Math.Max(1, Environment.ProcessorCount - 4));
        return GateWatchdog.Run("merge-check", GateWatchdog.TimeoutMinutes(options, parallelism, 90),
            () => RunChecks(root, options, process, git, prepareDecoder, parallelism));
    }

    private static int RunChecks(string root, Options options, SuiteProcess? process, Func<string[], CommandResult>? git,
        Func<Options, string, int>? prepareDecoder, int parallelism)
    {
        var total = Stopwatch.StartNew();
        var dotnet = ReleaseCommands.Dotnet;
        process ??= DotnetSuites.RunProcess;
        git ??= args => process("git", args, root, null);
        var started = options.Has("All") && !options.Has("DryRun") ? MergeCheckRuns.CleanRevision(git) : null;
        var changes = ChangeSet(options.Get("Base", "master"), git);
        Console.WriteLine($"Selecting from {changes.Length} changed paths.");
        bool docsOnly = !options.Has("All") && changes.All(Documentation);
        var prerequisites = docsOnly ? new Prerequisites("", "", null) : RequirePrerequisites(root, options, git);
        var (api, python, nuget) = prerequisites;
        if (!docsOnly) options = Options.Parse([.. options.Arguments(), "-Python=" + python], new HashSet<string>());
        var output = Path.Combine(root, "bin-build/merge-check");
        var transportFallback = docsOnly ? null : TransportFallbackPath(root, git);
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root);
        DerivedSuiteGates.IncludeApprovedSubsets(catalog);
        var context = new PrepareContext(root, "", api, nuget, python, python, output, DateOnly.FromDateTime(DateTime.Today), parallelism);
        var table = docsOnly ? [] : PrepareBuild.VersionProjects(context).Select(row => row with { Path = Normalize(Path.GetRelativePath(root, row.Path)) }).ToArray();
        var closures = SelectionClosures(changes, options.Has("All"), catalog, table, api,
            (project, properties) => Evaluate(root, project, properties, process, dotnet));
        var textReferences = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var testDirectories = catalog.Select(pair => Normalize(Path.GetDirectoryName(pair.Value!["project"]!.GetValue<string>())!)).Distinct().ToArray();
        bool Mentions(string name, string directory)
        {
            if (!textReferences.TryGetValue(name, out var paths))
            {
                var result = git(["grep", "-l", "-z", "-F", "-e", name, "--", .. testDirectories, "tests/Shared"]);
                if (result.ExitCode is not (0 or 1)) ProcessRunner.RequireSuccess(result, "Read text references");
                textReferences[name] = paths = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
            }
            return paths.Any(path => Under(path, directory) || Under(path, "tests/Shared"));
        }
        var plan = Select(changes, options.Has("All"), catalog, closures, Mentions,
            docsOnly ? [] : FileBasedPrograms.Discover(root), dotnet, python, api, output, parallelism, transportFallback);
        if (!plan.DocsOnly) CheckLoosening.AddJobs(plan, options.Get("Base", "master"), dotnet, output);
        plan.Checks.Add(new("wrap-long-lines", dotnet,
            [typeof(Program).Assembly.Location, "wrap-long-lines", "-Check", "-Base", options.Get("Base", "master")]));
        Console.Write(Describe(plan, table));
        if (File.Exists(transportFallback) && plan.Checks.Any(job => job.Name.StartsWith("foundation-transport", StringComparison.Ordinal)))
            Console.WriteLine("Transport release concurrency is capped at 4 by " + transportFallback
                + ". After maintainer review, delete this marker to restore the cap of 8.");
        if (options.Has("DryRun")) return 0;
        PrepareBuild.RequireOwnedTree(root, output);
        if (Directory.Exists(output)) Directory.Delete(output, true);
        Directory.CreateDirectory(Path.Combine(output, "analyzers"));
        var timings = new ConcurrentBag<Timing>();
        var selectionEnd = total.Elapsed.TotalSeconds;
        timings.Add(new("selection", selectionEnd, new(0, "", ""), 0, selectionEnd));
        CommandResult Timed(string name, Func<CommandResult> run)
        {
            using var job = GateWatchdog.BeginJob(name);
            var start = total.Elapsed.TotalSeconds;
            var watch = Stopwatch.StartNew();
            CommandResult result;
            try { result = run(); } catch (Exception ex) { result = new(1, "", ex.ToString()); }
            timings.Add(new(name, watch.Elapsed.TotalSeconds, result, start, total.Elapsed.TotalSeconds));
            File.WriteAllText(Path.Combine(output, name.Replace('/', '_').Replace(':', '_') + ".log"), result.StandardOutput + result.StandardError
                + $"\nExit code: {result.ExitCode}; elapsed: {watch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} s\n");
            return result;
        }
        var environment = new Dictionary<string, string?> { ["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = Path.Combine(output, "analyzers"),
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1", ["RestoreConfigFile"] = nuget };
        ReleaseCommands.ConfigureDecoderTestEnvironment(environment, root, options);
        void ConfigureTests(IDictionary<string, string?> env)
        {
            ReleaseCommands.ConfigureDecoderTestEnvironment(env, root, options);
            env["TIA_MCP_TEST_TREND_DIR"] = Path.Combine(output, "trend");
            // Synthetic projects created by checks must not become build-gate inputs.
            env["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = null;
        }
        string[] properties = ["-p:Configuration=Release", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:TiaPublicApiRoot=" + api,
            .. nuget == null ? Array.Empty<string>() : ["-p:RestoreConfigFile=" + nuget]];
        CommandResult Exec(string name, string exe, IEnumerable<string> args, IDictionary<string, string?>? env = null) =>
            Timed(name, () => process(exe, args, root, env ?? environment));
        var suiteOutput = Path.Combine(output, "suites");
        var variants = plan.Builds.Keys.Where(key => key.StartsWith("variant:", StringComparison.Ordinal))
            .Select(key => key[8..]).Where(name => name.StartsWith("plc-exchange-worker-", StringComparison.Ordinal))
            .ToDictionary(name => name, name => Path.Combine(suiteOutput, "compiled", name), StringComparer.Ordinal);
        if (!plan.DocsOnly)
        {
            var projects = plan.Builds.Keys.Where(key => key.EndsWith(".csproj", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (plan.Table) projects.UnionWith(PrepareBuild.SharedProjects);
            if (projects.Count != 0 || variants.Count != 0)
            {
                var traversal = Path.Combine(output, "build.proj");
                Traversal(projects.Select(project => new BuildProject(Path.Combine(root, project)))
                    .Concat(variants.Select(pair => VariantProject(root, catalog[pair.Key]!.AsObject(), pair.Value)))).Save(traversal);
                Exec("restore", dotnet, ["msbuild", traversal, "-t:Restore", "-m:1", "-nodeReuse:false", .. properties]);
                Exec("build", dotnet, ["msbuild", traversal, "-t:Build", "-m:" + parallelism, "-nodeReuse:false", .. properties]);
            }
            if (plan.Table)
            {
                var restore = Path.Combine(output, "table-restore.proj");
                TableRestore(table.Select(row => row with { Path = Path.Combine(root, row.Path) })).Save(restore);
                Exec("restore-table", dotnet, ["msbuild", restore, "-t:Restore", "-m:1", "-nodeReuse:false", .. properties]);
                foreach (var kind in new[] { "Adapter", "Worker", "Studio" })
                {
                    var document = PrepareBuild.BuildTableDocument(table.Where(row => row.Kind == kind).Select(row => row with { Path = Path.Combine(root, row.Path) }));
                    document.Descendants("MSBuild").Single().SetAttributeValue("Targets", "Build");
                    document.Descendants("MSBuild").Single().SetAttributeValue("StopOnFirstFailure", "false");
                    var path = Path.Combine(output, "table-" + kind + ".proj"); document.Save(path);
                    Exec("build-" + kind, dotnet, ["msbuild", path, "-t:Build", "-m:" + parallelism, "-nodeReuse:false", .. properties]);
                }
            }
            if (plan.Builds.ContainsKey("plc-fixtures"))
                foreach (var unit in PlcWorkerFixtures.Builds(["20", "21"]))
                {
                    string[] fixtureProperties = [.. unit.Properties(Path.Combine(output, "plc-fixtures")),
                        "-p:TiaReleaseKey=" + unit.Release,
                        "-p:SiemensEngineeringDirectory=" + Path.Combine(api, ReleaseCommands.PlcWorkerApi(unit.Release).Directory)];
                    Exec("fixture-restore-" + unit.Release, dotnet, ["restore", "src/PlcWorker/TiaMcp.PlcWorker.csproj", "-m:1", "-nodeReuse:false", .. properties, .. fixtureProperties]);
                    Exec("fixture-build-" + unit.Release, dotnet, ["build", "src/PlcWorker/TiaMcp.PlcWorker.csproj", "--no-restore",
                        "-p:BuildProjectReferences=false", "-m:1", "-nodeReuse:false", .. properties, .. fixtureProperties]);
                }
            if (plan.Builds.ContainsKey("campaign-file-program"))
                Timed("plc-file-programs", () =>
                {
                    ReleaseCommands.PreparePlcReleaseFilePrograms(root, dotnet, nuget, Path.Combine(output, "file-app-builds"),
                        Path.Combine(output, "file-apps"), process);
                    return new(0, "Campaign validator built.", "");
                });
            var files = plan.Builds.Keys.Where(key => key.StartsWith("file:", StringComparison.Ordinal)).Select(key => key[5..]);
            var groups = FileBasedPrograms.BuildGroups(root, files, plan.Wide);
            if (groups.Length != 0) ParallelPipeline.Run(groups.Select((group, index) => ("file-group-" + index, (Func<PipelineResult>)(() =>
            {
                var rows = group.Select(file => Exec("file-" + file.Replace('/', '_'), dotnet, ["build", Path.Combine(root, file), "-c", "Release", "-p:TiaMcpCollectFileBasedPrograms=true",
                    .. plan.Wide ? new[] { "-p:BuildProjectReferences=false" } : [], .. properties])).ToArray();
                return new("file-group-" + index, rows.Any(row => row.ExitCode != 0) ? 1 : 0, "", "");
            }))).ToArray(), Path.Combine(output, "file-builds"), parallelism, continueOnFailure: true);
            Timed("decoder-python", () => new((prepareDecoder ?? ReleaseCommands.PrepareDecoderPython)(Options.Parse(["-Python=" + python, "-Reuse=true"], new HashSet<string>()), root), "", ""));
        }
        CommandResult Check(Job job) => Timed(job.Name, () =>
        {
            var checkEnvironment = CheckEnvironment(job, output, api, environment);
            ConfigureTests(checkEnvironment);
            var executable = Path.GetDirectoryName(job.Executable) is { Length: > 0 }
                ? Path.GetFullPath(job.Executable, root) : job.Executable;
            var arguments = job.Arguments;
            if (job.Name == "check-analyzer-warnings")
            {
                var paths = AnalyzerWarnings.CompiledChanges(root, Path.Combine(output, "analyzers"), changes);
                arguments = [.. arguments, .. AnalyzerWarnings.CheckPathArguments(output, paths)];
            }
            var result = process(executable, arguments, root, checkEnvironment);
            return CheckResult(job.Name, result, output, transportFallback);
        });
        var preSuite = UpdaterCompanionChecks(plan).Select(job => (job.Name, (Func<PipelineResult>)(() =>
        {
            var result = Check(job); return new(job.Name, result.ExitCode, result.StandardOutput, result.StandardError);
        }))).ToList();
        if (plan.Suites.ContainsKey("updater")) preSuite.Add(("updater", () =>
        {
            var result = Timed("updater-stage", () =>
            {
                ReleaseSuiteStage.Run(root, new JsonObject { ["updater"] = catalog["updater"]!.DeepClone() }, suiteOutput, api,
                    dotnet, nuget, parallelism, process, ConfigureTests,
                    prebuilt: plan.Builds.Keys.Where(key => key.EndsWith(".csproj", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase));
                return new(0, "", "");
            });
            return new("updater", result.ExitCode, result.StandardOutput, result.StandardError);
        }));
        if (preSuite.Count != 0) ParallelPipeline.Run(preSuite, Path.Combine(output, "pre-suites"), parallelism, continueOnFailure: true);
        foreach (var job in plan.Checks.Where(job => job.Serial && job.Name == "native-diagnostics")) Check(job);
        var additional = plan.Checks.Where(job => !job.Serial && !job.PostSuite).Select(job => (job.Name, (Func<PipelineResult>)(() =>
        {
            var result = Check(job); return new(job.Name, result.ExitCode, result.StandardOutput, result.StandardError);
        }))).ToList();
        var suites = new JsonObject();
        foreach (var name in plan.Suites.Keys.Where(name => !plan.Derived.ContainsKey(name) && name is not ("foundation-api" or "updater" or "studio-gui"))) suites[name] = catalog[name]!.DeepClone();
        var priority = new List<(string Name, Func<PipelineResult> Run)>();
        if (plan.Suites.ContainsKey("foundation-api")) priority.Add(("foundation-api", () =>
        {
            var result = Timed("foundation-api", () =>
            {
                var rows = ReleaseFoundationStage.Run(root, catalog, suiteOutput, api, dotnet, nuget, parallelism,
                    ConfigureTests,
                    (opts, registrations, run) => PlainShards.Run(root, Options.Parse([.. opts.Arguments(), "-NoBuild=true",
                        "-TimingHistory=" + TimingHistory(root, git)], new HashSet<string>()), registrations,
                        (exe, args, cwd, env) => Timed("foundation-shard-" + env!["TIA_MCP_TEST_SHARD_INDEX"], () => run(exe, args, cwd, env))),
                    process: process, prebuilt: true);
                return new(rows.Values.Any(row => row.ExitCode != 0) ? 1 : 0, string.Join('\n', rows.Values.Select(row => row.StandardOutput)),
                    string.Join('\n', rows.Where(pair => pair.Value.ExitCode != 0).Select(pair => pair.Key + ": " + pair.Value.StandardError)));
            });
            return new("foundation-api", result.ExitCode, result.StandardOutput, result.StandardError);
        }));
        int variantBuild = 0;
        SuiteProcess SuiteRun = (exe, arguments, cwd, env) =>
        {
            var args = arguments.ToArray();
            string name = args[0] == "test"
                ? "suite-" + args.Single(arg => arg.StartsWith("trx;LogFileName=", StringComparison.Ordinal))[16..^4]
                : "variant-build-" + Interlocked.Increment(ref variantBuild) + "-" + Path.GetFileNameWithoutExtension(args[1]);
            env!["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = args[0] == "build" ? Path.Combine(output, "analyzers") : null;
            return Timed(name, () => process(exe, args, cwd, env));
        };
        var postSuite = new List<(string Name, Func<PipelineResult> Run)>();
        if (plan.Suites.ContainsKey("studio-gui")) postSuite.Add(("studio-gui", () =>
        {
            var result = Timed("studio-gui", () =>
            {
                var options = Options.Parse(["-Suite=studio-gui", "-Shards=4", "-MaxParallelism=" + Math.Min(4, parallelism).ToString(CultureInfo.InvariantCulture),
                    "-NoBuild", "-Dotnet=" + dotnet, "-ResultsDirectory=" + Path.Combine(suiteOutput, "studio-gui"),
                    "-TimingHistory=" + TimingHistory(root, git)], Program.BooleanOptions);
                return PlainShards.Run(root, options, catalog, (exe, args, cwd, env) =>
                {
                    ConfigureTests(env!);
                    env!["RestoreConfigFile"] = nuget;
                    env["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nuget;
                    env["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                    env["MSBUILDDISABLENODEREUSE"] = "1";
                    env["TIA_MCP_PRODUCTION_RELEASE"] = null;
                    env["TIA_MCP_BUILD_CACHE_DISABLED"] = null;
                    ReleaseSuiteStage.ConfigureStudio(env!, api);
                    return Timed("studio-gui-shard-" + env!["TIA_MCP_TEST_SHARD_INDEX"], () => process(exe, args, cwd, env));
                });
            });
            return new("studio-gui", result.ExitCode, result.StandardOutput, result.StandardError);
        }));
        postSuite.AddRange(plan.Checks.Where(job => job.PostSuite).Select(job => (job.Name, (Func<PipelineResult>)(() =>
        {
            var result = Check(job); return new(job.Name, result.ExitCode, result.StandardOutput, result.StandardError);
        }))));
        Timed("suite-stage", () =>
        {
            ReleaseSuiteStage.Run(root, suites, suiteOutput, api, dotnet, nuget, parallelism, SuiteRun,
                ConfigureTests, additional,
                postSuiteJobs: postSuite,
                prebuilt: plan.Builds.Keys.Where(key => key.EndsWith(".csproj", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                priorityJobs: priority, prebuiltVariants: variants);
            return new(0, "", "");
        });
        foreach (var (name, source) in plan.Derived.Where(pair => pair.Value != "foundation-api"))
            Timed("derived-" + name, () => DerivedSuiteGates.Evaluate(name, catalog[name]!.AsObject(), Path.Combine(suiteOutput, source, source + ".trx"), Path.Combine(suiteOutput, name), source));
        File.WriteAllText(Path.Combine(output, "timeline.tsv"), Timeline(timings));
        Timed("test-trends", () => MergeTrends(root, output, git, timings));
        Console.Write(Summary(timings, total.Elapsed.TotalSeconds));
        MergeCheckRuns.Append(root, options, plan, timings, total.Elapsed.TotalSeconds, output, git, started);
        return timings.Any(row => row.Result.ExitCode != 0) ? 1 : 0;
    }

    internal static CommandResult CheckResult(string name, CommandResult result, string output, string? transportFallback = null)
    {
        if (name.StartsWith("foundation-transport", StringComparison.Ordinal)
            && (result.StandardOutput + result.StandardError).Contains("WinError 10055", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = transportFallback ?? throw new ReleaseException("Missing shared transport fallback path.");
            lock (TransportFallbackGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
                File.WriteAllText(fallback, "WinError 10055: transport release parallelism is limited to 4 until maintainer review.\n");
            }
            return result with { ExitCode = 1, StandardError = result.StandardError
                + "\nWinError 10055: transport check failed. Subsequent merge checks use at most 4 releases per transport process. Fallback: " + fallback };
        }
        if (result.ExitCode != 0) return result;
        if (name is "adapter-inputs" or "worker-isolation")
        {
            var file = name == "adapter-inputs" ? "input-results.json" : "worker-isolation-results.json";
            PrepareChecks.RequireEvidence(Path.Combine(output, name, file));
        }
        if (name.StartsWith("native-safety-", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(result.StandardOutput, @"COMPLETE: (\d+) native harness safety checks passed; live TIA tests NOT RUN"))
            return result with { ExitCode = 1, StandardError = "Native safety check count missing." };
        if (name is "approval-v20-host" or "approval-v21-host")
            ReleaseCommands.ValidateApproval(Path.Combine(output, name, "result.json"));
        if (name == "configuration-page")
        {
            var match = System.Text.RegularExpressions.Regex.Match(result.StandardOutput, @"(?m)^Passed: (\d+)\s*$");
            if (!match.Success || int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) < 157)
                return result with { ExitCode = 1, StandardError = "Configuration page passed count below 157 or missing." };
        }
        if (name == "native-diagnostics")
        {
            var path = Path.Combine(output, "native-diagnostics/result.json");
            var report = JsonNode.Parse(File.ReadAllText(path))!;
            if (((int?)report["behaviorChecks"] ?? 0) < 36 || ((int?)report["rejectionChecks"] ?? 0) < 5 || (bool?)report["nativeTiaExecuted"] != false)
                return result with { ExitCode = 1, StandardError = "Native diagnostics gate failed." };
        }
        return result;
    }
}
