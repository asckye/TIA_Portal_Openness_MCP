using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

// Record each job failure while allowing the remaining stage jobs to report.
internal static class ReleaseTestSelection
{
    internal static PipelineResult RunJob(string name, Func<PipelineResult> run)
    {
        try
        {
            var result = run();
            Console.WriteLine((result.ExitCode == 0 ? "PASS " : "FAIL ") + name);
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + name);
            return new(name, 1, "", ex.ToString());
        }
    }
}

// Compile before starting any reader. Distinct property variants retain separate
// outputs; registrations sharing an assembly still own one serial test lane.
internal static class ReleaseSuiteStage
{
    internal static readonly string[] StudioSuites = ["studio-core", "studio-gui"];

    internal static void ConfigureStudio(IDictionary<string, string?> environment, string api)
    {
        environment["TIA_MCP_TEST_PUBLIC_API_ROOT"] = api;
        environment["TIA_MCP_RELEASE_TEMP_ROOT"] = environment["TEMP"];
        environment["TIA_MCP_CHECK_NO_BUILD"] = "1";
        environment["PYTHONIOENCODING"] = "utf-8";
        environment["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = null;
        environment["TIA_MCP_BUNDLE_ROOT"] = null;
        var config = Path.Combine(environment["TIA_MCP_DATA_DIRECTORY"]!, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "approval.settings"), "enabled=true\ntimeoutSeconds=120\n");
    }

    internal static string[][] Groups(JsonObject catalog) => catalog
        .GroupBy(pair => pair.Value!["project"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Select(pair => pair.Key).ToArray()).ToArray();

    private static string RegisteredOutput(string root, JsonObject suite)
    {
        var project = Path.GetFullPath(Path.Combine(root, suite["project"]!.GetValue<string>()));
        var framework = XDocument.Load(project).Descendants("TargetFramework").Single().Value;
        return Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", framework);
    }

    internal static Dictionary<string, CommandResult> Run(string root, JsonObject catalog, string output, string api,
        string dotnet, string? nuget, int parallelism, SuiteProcess? process = null, Action<IDictionary<string, string?>>? configure = null,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)>? additionalJobs = null, object? compilationGate = null,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)>? exclusiveJobs = null,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)>? postSuiteJobs = null,
        Func<Dictionary<string, CommandResult>>? foundationRun = null, IReadOnlySet<string>? prebuilt = null,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)>? priorityJobs = null,
        IReadOnlyDictionary<string, string>? prebuiltVariants = null)
    {
        var results = new ConcurrentDictionary<string, CommandResult>(StringComparer.Ordinal);
        var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var buildFailures = new Dictionary<string, CommandResult>(StringComparer.Ordinal);
        var buildGate = compilationGate ?? new object();
        var prepared = new Dictionary<string, (IDictionary<string, string?> Environment, string? Output)>(StringComparer.Ordinal);
        var groups = Groups(catalog).Select(group => group.Where(name => (foundationRun is null || !ReleaseFoundationStage.Names.Contains(name))).ToArray()).Where(group => group.Length != 0).ToArray();
        string? Sdk(string name, JsonObject suite) => name == "foundation-api" || suite["publicApiRoot"]?.GetValue<bool>() == true ? api : null;
        string Identity(string name) => string.Join('\n', DotnetSuites.BuildArguments(catalog[name]!["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>())))
            + "\n" + Sdk(name, catalog[name]!.AsObject());
        foreach (var names in groups)
        {
            var variants = names.Select(Identity).Distinct(StringComparer.Ordinal).Count() > 1;
            var builtVariants = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var suite = catalog[name]!.AsObject();
                var directory = Path.Combine(output, name);
                var sdk = Sdk(name, suite);
                var env = FastSuites.EnvironmentFor(directory, sdk);
                configure?.Invoke(env);
                env["RestoreConfigFile"] = nuget;
                env["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nuget;
                env["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                env["MSBUILDDISABLENODEREUSE"] = "1";
                env["TIA_MCP_PRODUCTION_RELEASE"] = null;
                env["TIA_MCP_BUILD_CACHE_DISABLED"] = null;
                if (StudioSuites.Contains(name))
                    ConfigureStudio(env, api);
                var identity = Identity(name);
                if (prebuiltVariants?.TryGetValue(name, out var variantOutput) == true)
                {
                    env["TIA_MCP_TEST_REGISTERED_OUTPUT"] = RegisteredOutput(root, suite);
                    builtVariants[identity] = variantOutput;
                    prepared[name] = (env, variantOutput);
                    File.WriteAllText(Path.Combine(directory, "build.log"), "Variant built by the merge traversal.\n");
                    continue;
                }
                if (builtVariants.TryGetValue(identity, out var sharedOutput))
                {
                    if (sharedOutput != null) env["TIA_MCP_TEST_REGISTERED_OUTPUT"] = RegisteredOutput(root, suite);
                    prepared[name] = (env, sharedOutput);
                    if (buildFailures.TryGetValue(names.First(other => Identity(other) == identity), out var failed)) buildFailures[name] = failed;
                    File.WriteAllText(Path.Combine(directory, "build.log"), "Uses the already compiled project/property variant.\n");
                    continue;
                }
                if (prebuilt?.Contains(suite["project"]!.GetValue<string>()) == true && !DotnetSuites.BuildArguments(suite["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>())).Any())
                {
                    builtVariants[identity] = null;
                    prepared[name] = (env, null);
                    File.WriteAllText(Path.Combine(directory, "build.log"), "Built by the merge traversal.\n");
                    continue;
                }
                var compiled = variants ? Path.Combine(output, "compiled", name) : null;
                if (compiled != null) env["TIA_MCP_TEST_REGISTERED_OUTPUT"] = RegisteredOutput(root, suite);
                List<string> build = ["build", Path.Combine(root, suite["project"]!.GetValue<string>()), "-c", "Release", "-v:q",
                    "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false",
                    .. DotnetSuites.BuildArguments(suite["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>()))];
                if (sdk != null) build.Add("-p:TiaPublicApiRoot=" + sdk);
                if (nuget != null) build.Add("-p:RestoreConfigFile=" + nuget);
                if (name == "release-tool") build.Add("-p:UseAppHost=false");
                if (compiled != null) build.AddRange(["--output", compiled]);
                CommandResult built;
                // Shared project references also write bin/obj. Finish every build
                // before a suite or a PLC lane can execute those binaries.
                var compilation = ReleaseTestSelection.RunJob("build:" + name, () =>
                {
                    lock (buildGate)
                    {
                        var result = (process ?? DotnetSuites.RunProcess)(dotnet, build, root, env);
                        return new(name, result.ExitCode, result.StandardOutput, result.StandardError);
                    }
                });
                built = new(compilation.ExitCode, compilation.StandardOutput, compilation.StandardError);
                File.WriteAllText(Path.Combine(directory, "build.log"), built.StandardOutput + built.StandardError);
                if (built.ExitCode != 0) buildFailures[name] = built;
                builtVariants[identity] = compiled;
                prepared[name] = (env, compiled);
            }
        }
        PipelineResult RunGroup(string[] names)
        {
            foreach (var name in names)
            {
                var suite = catalog[name]!.AsObject();
                var directory = Path.Combine(output, name);
                var (env, compiled) = prepared[name];
                string[] arguments = ["--no-build", "--no-restore", "-m:1", "-nodeReuse:false",
                    .. compiled != null ? new[] { "--output", compiled } : []];
                var reported = ReleaseTestSelection.RunJob(name, () =>
                {
                    var tested = buildFailures.TryGetValue(name, out var failed) ? failed
                        : DotnetSuites.RunSuite(root, name, suite, directory, dotnet, arguments, env, process);
                    return new(name, tested.ExitCode, tested.StandardOutput, tested.StandardError);
                });
                var result = new CommandResult(reported.ExitCode, reported.StandardOutput, reported.StandardError);
                File.WriteAllText(Path.Combine(directory, "test.log"), result.StandardOutput + result.StandardError);
                results[name] = result;
                if (result.ExitCode != 0) failures[name] = result.StandardOutput + result.StandardError;
            }
            return new(names[0], names.Any(failures.ContainsKey) ? 1 : 0, "Registered suite gates completed.\n", "");
        }
        PipelineResult Record(string name, Func<PipelineResult> run)
        {
            var result = ReleaseTestSelection.RunJob(name, run);
            if (result.ExitCode != 0) failures[name] = result.StandardOutput + result.StandardError;
            return result;
        }
        void RunParallel((string Name, Func<PipelineResult> Run)[] jobs, string directory)
        {
            if (jobs.Length == 0) return;
            foreach (var result in ParallelPipeline.Run(jobs, directory,
                Math.Max(1, Math.Min(parallelism, Environment.ProcessorCount)), continueOnFailure: true))
                if (result.ExitCode != 0) failures.TryAdd(result.Name, result.StandardOutput + result.StandardError);
        }
        var ordinary = groups.Select(group => group.Where(name => name is not ("studio-gui" or "updater")).ToArray()).Where(group => group.Length != 0);
        var parallelJobs = (priorityJobs ?? []).Select(job => (job.Name, (Func<PipelineResult>)(() => Record(job.Name, job.Run))))
            .Concat(ordinary.Select(group => (group[0], (Func<PipelineResult>)(() => RunGroup(group)))))
            .Concat((additionalJobs ?? [])
                .Select(job => (job.Name, (Func<PipelineResult>)(() => Record(job.Name, job.Run))))).ToArray();
        RunParallel(parallelJobs, Path.Combine(output, "pipeline"));
        var exclusive = (exclusiveJobs ?? []).ToList();
        if (foundationRun != null && ReleaseFoundationStage.Names.Any(name => catalog.ContainsKey(name)))
            exclusive.Add(("foundation-api", () =>
            {
                var rows = foundationRun();
                foreach (var (name, result) in rows)
                {
                    results[name] = result;
                    if (result.ExitCode != 0) failures[name] = result.StandardOutput + result.StandardError;
                }
                return new("foundation-api", rows.Values.Any(result => result.ExitCode != 0) ? 1 : 0, "Foundation suite gates completed.\n", "");
            }));
        foreach (var job in exclusive)
            Record(job.Name, job.Run);
        if (catalog.ContainsKey("updater")) RunGroup(["updater"]);
        var post = (catalog.ContainsKey("studio-gui")
                ? new[] { (Name: "studio-gui", Run: (Func<PipelineResult>)(() => RunGroup(["studio-gui"]))) } : [])
            .Concat((postSuiteJobs ?? []).Select(job => (job.Name, (Func<PipelineResult>)(() => Record(job.Name, job.Run))))).ToArray();
        RunParallel(post, Path.Combine(output, "post-suites"));
        if (!failures.IsEmpty) throw new ReleaseException("Release test stage failed: " + string.Join(", ", failures.Keys.Order(StringComparer.Ordinal)) +
            "\n" + string.Join("\n", failures.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ": " + pair.Value)));
        return results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}

internal static class ReleaseFoundationStage
{
    internal static readonly string[] Names = ["foundation-api", .. DerivedSuiteGates.Supersets.Where(pair => pair.Value == "foundation-api").Select(pair => pair.Key)];

    internal static Dictionary<string, CommandResult> Run(string root, JsonObject catalog, string output, string api, string dotnet,
        string? nuget, int parallelism, Action<IDictionary<string, string?>> configure,
        Func<Options, JsonObject, SuiteProcess, CommandResult>? run = null, SuiteProcess? process = null, bool prebuilt = false)
    {
        DerivedSuiteGates.IncludeApprovedSubsets(catalog);
        foreach (var name in Names.Where(name => name != "foundation-api" && catalog.ContainsKey(name)))
            if (!DerivedSuiteGates.FilterContains(catalog["foundation-api"]!.AsObject(), catalog[name]!.AsObject()))
                throw new ReleaseException("Foundation derivation is not approved for " + name);
        var directory = Path.Combine(output, "foundation-api");
        var options = Options.Parse([.. ReleaseCommands.FoundationApiSuiteArguments(dotnet, api, output), "-ResultsDirectory", directory,
            "-NoBuild", "-MaxParallelism", parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. nuget is null ? Array.Empty<string>() : ["-NuGetConfig", nuget]], new HashSet<string> { "NoBuild" });
        CommandResult Process(string executable, IEnumerable<string> arguments, string cwd, IDictionary<string, string?>? environment)
        {
            environment!["TIA_MCP_TEST_PUBLIC_API_ROOT"] = api;
            configure(environment!);
            environment!["RestoreConfigFile"] = nuget;
            environment["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nuget;
            environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            environment["MSBUILDDISABLENODEREUSE"] = "1";
            environment!["TIA_MCP_PRODUCTION_RELEASE"] = null;
            environment["TIA_MCP_BUILD_CACHE_DISABLED"] = null;
            return (process ?? DotnetSuites.RunProcess)(executable, arguments, cwd, environment);
        }
        var reported = ReleaseTestSelection.RunJob("foundation-api", () =>
        {
            if (!prebuilt)
            {
                var built = Process(dotnet, ["build", Path.Combine(root, catalog["foundation-api"]!["project"]!.GetValue<string>()), "-c", "Release", "-m:1", "-nodeReuse:false",
                    "-p:UseSharedCompilation=false", "-p:NuGetAudit=false", "-p:TiaPublicApiRoot=" + api], root, FastSuites.EnvironmentFor(directory, api));
                ProcessRunner.RequireSuccess(built, "Foundation build");
            }
            var result = run is null ? PlainShards.Run(root, options, catalog, Process) : run(options, catalog, Process);
            return new("foundation-api", result.ExitCode, result.StandardOutput, result.StandardError);
        });
        var results = new Dictionary<string, CommandResult>(StringComparer.Ordinal);
        results["foundation-api"] = Stamp("foundation-api", catalog["foundation-api"]!.AsObject(), directory,
            new(reported.ExitCode, reported.StandardOutput, reported.StandardError));
        var trx = Path.Combine(directory, "foundation-api.trx");
        foreach (var name in Names.Where(name => name != "foundation-api" && catalog.ContainsKey(name)))
        {
            var target = Path.Combine(output, name);
            var derived = ReleaseTestSelection.RunJob(name, () =>
            {
                var result = DerivedSuiteGates.Evaluate(name, catalog[name]!.AsObject(), trx, target, "foundation-api");
                return new(name, result.ExitCode, result.StandardOutput, result.StandardError);
            });
            results[name] = Stamp(name, catalog[name]!.AsObject(), target, new(derived.ExitCode, derived.StandardOutput, derived.StandardError));
        }
        return results;
    }

    private static CommandResult Stamp(string name, JsonObject suite, string directory, CommandResult result)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".json");
        var row = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject
        {
            ["suite"] = name, ["minimumPassed"] = suite["minimumPassed"]!.DeepClone(), ["maximumSkipped"] = suite["maximumSkipped"]!.DeepClone(),
            ["errors"] = new JsonArray(result.StandardError.Length == 0 ? "Missing suite result" : result.StandardError)
        };
        if (!File.Exists(path) && result.ExitCode == 0) result = result with { ExitCode = 1, StandardError = "Missing suite result: " + name };
        row["exitCode"] = result.ExitCode;
        File.WriteAllText(path, row.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.WriteAllText(Path.Combine(directory, "test.log"), result.StandardOutput + result.StandardError);
        return result;
    }
}

internal static partial class ReleaseCommands
{
    internal static string[] FoundationApiSuiteArguments(string dotnet, string apiRoot, string logs) =>
        ["-Suite", "foundation-api", "-Dotnet", dotnet, "-Shards", "8", "-PublicApiRoot", apiRoot, "-ResultsDirectory", Path.Combine(logs, "dotnet-suites", "foundation-api")];
}
