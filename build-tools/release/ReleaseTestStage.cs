using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

// Selection is diagnostic only; it never changes the publication inventory.
internal sealed class ReleaseTestSelection(Options options, IEnumerable<string> available)
{
    private readonly HashSet<string>? names = Parse(options, available);
    internal bool Includes(string name) => names is null || names.Contains(name);

    private static HashSet<string>? Parse(Options options, IEnumerable<string> available)
    {
        if (!options.Has("Only")) return null;
        if (!options.Has("TestStageOnly")) throw new ReleaseException("-Only requires -TestStageOnly", 64);
        var requested = options.Get("Only")!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Except(available, StringComparer.Ordinal).ToArray();
        if (requested.Count == 0 || unknown.Length != 0)
            throw new ReleaseException("Unknown or empty test-stage job selection: " + string.Join(", ", unknown) + ". Available: " + string.Join(", ", available.Order()), 64);
        return requested;
    }

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
        IReadOnlyList<(string Name, Func<PipelineResult> Run)>? exclusiveJobs = null, ReleaseTestSelection? selection = null,
        IReadOnlyList<(string Name, Func<PipelineResult> Run)>? postSuiteJobs = null,
        Func<Dictionary<string, CommandResult>>? foundationRun = null)
    {
        var results = new ConcurrentDictionary<string, CommandResult>(StringComparer.Ordinal);
        var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var buildFailures = new Dictionary<string, CommandResult>(StringComparer.Ordinal);
        var buildGate = compilationGate ?? new object();
        var prepared = new Dictionary<string, (IDictionary<string, string?> Environment, string? Output)>(StringComparer.Ordinal);
        var groups = Groups(catalog).Select(group => group.Where(name => selection?.Includes(name) != false && (foundationRun is null || !ReleaseFoundationStage.Names.Contains(name))).ToArray()).Where(group => group.Length != 0).ToArray();
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
                if (CheckAllProfiles.StudioSuites.Contains(name))
                {
                    env["TIA_MCP_TEST_PUBLIC_API_ROOT"] = api;
                    env["TIA_MCP_RELEASE_TEMP_ROOT"] = env["TEMP"];
                    env["TIA_MCP_CHECK_NO_BUILD"] = "1";
                    env["PYTHONIOENCODING"] = "utf-8";
                    env["TIA_MCP_ANALYZER_REPORT_DIRECTORY"] = null;
                    env["TIA_MCP_BUNDLE_ROOT"] = null;
                    var config = Path.Combine(env["TIA_MCP_DATA_DIRECTORY"]!, "config");
                    Directory.CreateDirectory(config);
                    File.WriteAllText(Path.Combine(config, "approval.settings"), "enabled=true\ntimeoutSeconds=120\n");
                }
                var identity = Identity(name);
                if (builtVariants.TryGetValue(identity, out var sharedOutput))
                {
                    if (sharedOutput != null) env["TIA_MCP_TEST_REGISTERED_OUTPUT"] = RegisteredOutput(root, suite);
                    prepared[name] = (env, sharedOutput);
                    if (buildFailures.TryGetValue(names.First(other => Identity(other) == identity), out var failed)) buildFailures[name] = failed;
                    File.WriteAllText(Path.Combine(directory, "build.log"), "Uses the already compiled project/property variant.\n");
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
        var parallelJobs = ordinary.Select(group => (group[0], (Func<PipelineResult>)(() => RunGroup(group))))
            .Concat((additionalJobs ?? []).Where(job => selection?.Includes(job.Name) != false)
                .Select(job => (job.Name, (Func<PipelineResult>)(() => Record(job.Name, job.Run))))).ToArray();
        RunParallel(parallelJobs, Path.Combine(output, "pipeline"));
        var exclusive = (exclusiveJobs ?? []).ToList();
        if (foundationRun != null && ReleaseFoundationStage.Names.Any(name => catalog.ContainsKey(name) && selection?.Includes(name) != false))
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
        foreach (var job in exclusive.Where(job => job.Name == "foundation-api" && foundationRun != null || selection?.Includes(job.Name) != false))
            Record(job.Name, job.Run);
        foreach (var name in new[] { "updater", "studio-gui" }.Where(name => catalog.ContainsKey(name) && selection?.Includes(name) != false))
            RunGroup([name]);
        var post = (postSuiteJobs ?? []).Where(job => selection?.Includes(job.Name) != false)
            .Select(job => (job.Name, (Func<PipelineResult>)(() => Record(job.Name, job.Run)))).ToArray();
        RunParallel(post, Path.Combine(output, "post-suites"));
        if (!failures.IsEmpty) throw new ReleaseException("Release test stage failed: " + string.Join(", ", failures.Keys.Order(StringComparer.Ordinal)) +
            "\n" + string.Join("\n", failures.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ": " + pair.Value)));
        return results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}

internal static class ReleaseFoundationStage
{
    internal static readonly string[] Names = ["foundation-api", .. CheckAllProfiles.Derived.Where(pair => pair.Value == "foundation-api").Select(pair => pair.Key)];

    internal static Dictionary<string, CommandResult> Run(string root, JsonObject catalog, string output, string api, string dotnet,
        string? nuget, int parallelism, Action<IDictionary<string, string?>> configure,
        Func<Options, JsonObject, SuiteProcess, CommandResult>? run = null)
    {
        var plan = CheckAllProfiles.Plan(catalog, api, nuget ?? "", Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON"), dotnet);
        foreach (var name in Names.Where(name => name != "foundation-api"))
            if (plan.Mappings.GetValueOrDefault(name) != "foundation-api")
                throw new ReleaseException("Foundation derivation is not approved for " + name + ": " + plan.Fallbacks.GetValueOrDefault(name));
        var directory = Path.Combine(output, "foundation-api");
        var options = Options.Parse([.. ReleaseCommands.FoundationApiSuiteArguments(dotnet, api, output), "-ResultsDirectory", directory,
            "-MaxParallelism", parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. nuget is null ? Array.Empty<string>() : ["-NuGetConfig", nuget]], new HashSet<string>());
        CommandResult Process(string executable, IEnumerable<string> arguments, string cwd, IDictionary<string, string?>? environment)
        {
            CheckAllProfiles.ApplyEnvironment(plan.Executions["foundation-api"], environment!, false);
            configure(environment!);
            environment!["RestoreConfigFile"] = nuget;
            environment["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nuget;
            environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            environment["MSBUILDDISABLENODEREUSE"] = "1";
            environment!["TIA_MCP_PRODUCTION_RELEASE"] = null;
            environment["TIA_MCP_BUILD_CACHE_DISABLED"] = null;
            return DotnetSuites.RunProcess(executable, arguments, cwd, environment);
        }
        var reported = ReleaseTestSelection.RunJob("foundation-api", () =>
        {
            var result = run is null ? FastSuites.Run(root, options, catalog, Process) : run(options, catalog, Process);
            return new("foundation-api", result.ExitCode, result.StandardOutput, result.StandardError);
        });
        var results = new Dictionary<string, CommandResult>(StringComparer.Ordinal);
        results["foundation-api"] = Stamp("foundation-api", catalog["foundation-api"]!.AsObject(), directory,
            new(reported.ExitCode, reported.StandardOutput, reported.StandardError));
        var trx = Path.Combine(directory, "foundation-api.trx");
        foreach (var name in Names.Where(name => name != "foundation-api"))
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

internal sealed class ReleaseTestResults
{
    internal Dictionary<string, CommandResult> Suites { get; set; } = new(StringComparer.Ordinal);
    internal Dictionary<string, CommandResult> Checks { get; } = new(StringComparer.Ordinal);

    internal CommandResult? Preflight(string name) => name switch
    {
        "release tool test suite" or "parallel pipeline behavior" or "repository check self-tests" => Suites["release-tool"],
        "suite runner self-tests" => Checks["dotnetsuites-self-test"],
        "release approval gate self-tests" => Checks["approval-self-test"],
        "relocation checker self-tests" => Checks["relocation-self-test"],
        "V4 contract snapshots" => Checks["contract-verify"],
        "V4 response snapshots" => Checks["response-verify"],
        _ => null
    };
}

internal static partial class ReleaseCommands
{
    private static ReleaseTestResults RunReleaseTestStage(Options options, string api, string logs, string temp, string python, string? nuget)
    {
        var results = new ReleaseTestResults();
        var output = Path.Combine(logs, "tests");
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
        var multiLogs = Path.Combine(Root, "bin-build/multi-version");
        var cli = ReleaseCliHome(temp);
        var policy = ReleaseCheckPolicy.Load(Root);
        var plan = ReleasePlan(options);
        var plcJobs = PlcReleaseJobs(Root, api, multiLogs, temp, Dotnet, python)
            .Where(job => PlcReleaseSelected(plan, job) && job.Check != "plc-approval").ToArray();
        var selection = new ReleaseTestSelection(options, catalog.Select(pair => pair.Key)
            .Concat(policy.SelfTests.Where(test => test.Command is null).Select(test => test.Check))
            .Concat(ReleasePythonTests.Select(test => test.Name))
            .Concat(plcJobs.Where(ReleasePlcExclusive).Select(job => job.Name))
            .Concat(["configuration-page", "plc-v20", "plc-v21", "host-behavior-parity", "foundation-transport", "foundation-transport-plc"]));
        var fileAppTemp = Path.Combine(temp, "file-apps");
        if (selection.Includes("plc-v20") || selection.Includes("plc-v21") || plcJobs.Where(ReleasePlcExclusive).Any(job => selection.Includes(job.Name)))
            fileAppTemp = PreparePlcReleaseFilePrograms(Root, Dotnet, nuget, Path.Combine(output, "file-app-builds"), fileAppTemp);
        var stageEnvironment = new Dictionary<string, string?> { [PlcReleaseSystemTempVariable] = fileAppTemp };
        CommandResult Run(string name, string executable, IEnumerable<string> args, bool approval = false) =>
            RunBuildIsolated(executable, args, Path.Combine(output, name + ".log"), temp, cli, api, nuget, approval, name,
                additionalEnvironment: stageEnvironment);
        const string configurationProject = "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj";
        if (selection.Includes("configuration-page")) ProcessRunner.RequireSuccess(Run("configuration-build", Dotnet, ["build", configurationProject, "-c", "Release",
            "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false"]), "Configuration fixture build");
        var parityManifest = selection.Includes("host-behavior-parity") ? HostParity.Prepare(Root, Path.Combine(output, "parity-build"), Dotnet, api, nuget) : "";
        var jobs = new List<(string Name, Func<PipelineResult> Run)>();
        var postSuiteJobs = new List<(string Name, Func<PipelineResult> Run)>();
        var sharedTests = new List<string>();
        var configurationPassed = 0;
        var gate = new object();
        foreach (var test in policy.SelfTests)
        {
            if (test.Command is not null)
            {
                // The full release-tool registration contains StaticChecks,
                // Generators and DotnetSuitesTests' original gate self-tests.
                sharedTests.Add(test.Check);
                continue;
            }
            jobs.Add((test.Check, () =>
            {
                var args = (test.Arguments ?? []).Select(arg => arg == "{output}" ? Path.Combine(output, test.Check) : arg).ToArray();
                var result = Run(test.Check, python, [Path.Combine(Root, test.Script), .. args]);
                lock (gate) results.Checks[test.Check] = result;
                return new(test.Check, result.ExitCode, result.StandardOutput, result.StandardError);
            }));
        }
        // The Python self-tests from the full check-all inventory, including the
        // source-only assertions and the console configuration fixture.
        foreach (var (name, script, args) in ReleasePythonTests)
            jobs.Add((name, () =>
            {
                var result = Run(name, python, [Path.Combine(Root, script), .. args]);
                lock (gate) results.Checks[name] = result;
                return new(name, result.ExitCode, result.StandardOutput, result.StandardError);
            }));
        jobs.Add(("configuration-page", () =>
        {
            var result = Run("configuration-page", Dotnet, ["run", "--project", configurationProject, "-c", "Release", "--no-build", "--no-restore", "--", Path.Combine(output, "configuration")]);
            var count = RequireCount("configuration", result.StandardOutput, "(?m)^Passed: (\\d+)\\s*$");
            if (count < 157) throw new ReleaseException("Configurator tests did not report at least 157 checks");
            configurationPassed = count;
            return new("configuration-page", result.ExitCode, result.StandardOutput, result.StandardError);
        }));
        postSuiteJobs.Add(("host-behavior-parity", () =>
        {
            var rows = new JsonObject();
            foreach (var key in new[] { "20", "21" })
            {
                var directory = Path.Combine(output, "host-behavior-parity-v" + key);
                var args = Options.Parse(["-Dotnet", Dotnet, "-EngineMajor", key, "-ResultsDirectory", directory, "-NoBuild", "-RuntimeManifest", parityManifest], new HashSet<string> { "NoBuild" });
                var env = FastSuites.EnvironmentFor(directory, api); env["RestoreConfigFile"] = nuget;
                env[PlcReleaseSystemTempVariable] = fileAppTemp;
                var result = HostParity.Run(Root, args, env); ProcessRunner.RequireSuccess(result, "Host behavior parity V" + key);
                rows[key] = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "host-behavior-parity.json")));
            }
            WriteJson(Path.Combine(output, "host-behavior-parity.json"), rows);
            return new("host-behavior-parity", 0, "V20/V21 host behavior parity passed.\n", "");
        }));
        // Approval already ran inside preparation. Every other functional PLC job
        // now runs once; registered suite aliases use the exact completed TRX gate.
        void RunPlc(IEnumerable<PlcReleaseJob> selected) => RunPlcReleaseJobs(selected, Root, multiLogs, job =>
        {
            ResetPlcReleaseJobOutputs(job, multiLogs);
            CommandResult result;
            if (job.Executable == "test-suites")
                result = results.Suites[job.Arguments[Array.IndexOf(job.Arguments, "-Suite") + 1]];
            else result = RunBuildIsolated(job.Check == "plc-ecosystem" ? ReleaseCompanionPython(options) : job.Executable,
                job.Arguments, Path.Combine(multiLogs, job.Name + ".log"), temp, cli, api, nuget, false, job.Name,
                additionalEnvironment: stageEnvironment);
            WriteLog(Path.Combine(multiLogs, job.Name + ".log"), result);
            return result;
        }, (script, args) => ProcessRunner.RequireSuccess(Run("plc-compare-" + Guid.NewGuid().ToString("N"), python, [script, .. args]), "PLC byte comparison"),
            parallelism: 1);
        // One lane per release owns its inventory and captures. These two lanes,
        // the independent Python checks and suite groups share one CPU budget.
        foreach (var key in new[] { "20", "21" }) jobs.Add(("plc-v" + key, () =>
        {
            RunPlc(plcJobs.Where(job => job.Release == key && job.Executable != "test-suites" && !ReleasePlcExclusive(job)));
            return new("plc-v" + key, 0, "PLC functional checks and exact captures passed.\n", "");
        }));
        var exclusiveJobs = plcJobs.Where(ReleasePlcExclusive).Select(job => (job.Name, (Func<PipelineResult>)(() =>
        {
            RunPlc([job]);
            return new(job.Name, 0, "Exclusive PLC harness passed.\n", "");
        }))).ToArray();
        postSuiteJobs.Add(("foundation-transport", () =>
        {
            var transport = Run("foundation-transport", python, [Path.Combine(Root, "scripts/checks/Test-FoundationTransport.py"),
                "--fixture", Path.Combine(Root, "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/bin/Release/net10.0/TiaMcp.WorkerChannel.TransportFixture.exe"),
                "--releases", "14sp1", "15.1", "16", "17", "18", "19", "--output", Path.Combine(multiLogs, "transport")]);
            ProcessRunner.RequireSuccess(transport, "Foundation transport");
            return new("foundation-transport", 0, transport.StandardOutput, transport.StandardError);
        }));
        postSuiteJobs.Add(("foundation-transport-plc", () =>
        {
            var plcTransport = Run("foundation-transport-plc", python, [Path.Combine(Root, "scripts/checks/Test-FoundationTransport.py"), "--plc-worker",
                "--fixture", Path.Combine(Root, "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/bin/Release/net10.0/TiaMcp.WorkerChannel.TransportFixture.exe"),
                "--host-exe", Path.Combine(Root, "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe"), "--releases", .. PlcWorkerFixtures.Keys,
                "--plc-sdk-worker", "20=" + ReleasePlcFixture(multiLogs, "20"), "--plc-sdk-worker", "21=" + ReleasePlcFixture(multiLogs, "21"),
                "--public-api-root", api, "--temp-root", Path.Combine(temp, "transport-plc"), "--output", Path.Combine(multiLogs, "transport-plc")]);
            ProcessRunner.RequireSuccess(plcTransport, "Eight-release PLC transport");
            return new("foundation-transport-plc", 0, plcTransport.StandardOutput, plcTransport.StandardError);
        }));
        results.Suites = ReleaseSuiteStage.Run(Root, catalog, output, api, Dotnet, nuget, GetMaxParallelism(options),
            configure: environment =>
            {
                ConfigureDecoderTestEnvironment(environment, Root, options);
                environment[PlcReleaseSystemTempVariable] = fileAppTemp;
            }, additionalJobs: jobs, exclusiveJobs: exclusiveJobs, selection: selection, postSuiteJobs: postSuiteJobs,
            foundationRun: () => ReleaseFoundationStage.Run(Root, catalog, output, api, Dotnet, nuget, GetMaxParallelism(options), environment =>
            {
                ConfigureDecoderTestEnvironment(environment, Root, options);
                environment[PlcReleaseSystemTempVariable] = fileAppTemp;
            }));
        if (options.Has("TestStageOnly")) return results;
        foreach (var name in sharedTests) results.Checks[name] = results.Suites["release-tool"];
        RunPlc(plcJobs.Where(job => job.Executable == "test-suites"));
        foreach (var group in plcJobs.Select(job => job.Check).Distinct(StringComparer.Ordinal))
            results.Checks[group] = new(0, "Passed in the current release test stage: " + group + "\n", "");
        var configurationPath = Path.Combine(Root, "manifest/configurator-build.json");
        var configuration = JsonNode.Parse(File.ReadAllText(configurationPath))!.AsObject();
        configuration["testsPassed"] = configurationPassed; WriteJson(configurationPath, configuration);
        BindReleaseTestResults(results, catalog, output, multiLogs, plan);
        return results;
    }

    internal static bool ReleasePlcExclusive(PlcReleaseJob job) =>
        job.Mode is "native-diagnostics-only" or "test-ecosystem-assembly";

    private static readonly (string Name, string Script, string[] Args)[] ReleasePythonTests =
    [
        ("campaign-runner-self-tests", "scripts/checks/Test-CampaignInputs.py", ["--self-test"]),
        ("check-all-speedup-self-tests", "scripts/checks/Test-CheckAllSpeedups.py", []),
        ("plc-script-self-tests", "scripts/checks/Test-PlcScriptSuccessors.py", []),
        ("plc-harness-self-tests", "scripts/checks/Test-PlcHarnessSuccessors.py", []),
        ("campaign-validator-self-tests", "scripts/checks/Test-CampaignInputs.py", ["--validator-self-test"]),
        ("domain-source", "scripts/checks/Test-DomainTools.py", ["--source-only"]),
        ("contract-verify", "scripts/checks/Snapshot-ToolContracts.py", ["verify"]),
        ("response-verify", "scripts/checks/Snapshot-ToolResponses.py", ["verify"])
    ];

    private static void BindReleaseTestResults(ReleaseTestResults results, JsonObject catalog, string output, string logs, ReleaseCheckPlan plan)
    {
        var recordPath = Path.Combine(Root, "manifest/multi-version-build.json");
        var record = JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();
        var suites = new JsonObject();
        foreach (var (name, _) in catalog)
        {
            ProcessRunner.RequireSuccess(results.Suites[name], name);
            var directory = Path.Combine(output, name);
            var row = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, name + ".json")))!.AsObject();
            row["trx"] = Path.GetRelativePath(Root, Path.Combine(directory, name + ".trx")).Replace('\\', '/');
            row["sha256"] = ReleaseRecords.HashFile(Path.Combine(directory, name + ".trx"));
            suites[name] = row;
        }
        record["test"] = true;
        record["adoptedValidation"] = new JsonObject { ["source"] = "release-run", ["suites"] = suites.DeepClone(),
            ["checks"] = ReleaseEvidence.Strings(results.Checks.Keys.Order(StringComparer.Ordinal)) };
        var validation = record["validation"]!.AsObject(); validation["dotnetSuites"] = suites;
        validation["hostBehaviorParity"] = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "host-behavior-parity.json")));
        foreach (var flag in new[] { "configurationFunctionalTestsExecuted", "studioFunctionalTestsExecuted", "foundationTransportExecuted", "plcFoundationTransportExecuted", "toolUsageCoverageExecuted", "plcToolUsageCoverageExecuted" }) validation[flag] = true;
        var receipts = new JsonArray();
        var oldReceipts = CatalogAudits.OneReceiptPerRelease(JsonNode.Parse(File.ReadAllText(Path.Combine(logs, "transport/tool-usage.json")))!.AsArray());
        foreach (var key in PlcWorkerFixtures.Keys)
            receipts.Add((key is "20" or "21" ? JsonNode.Parse(File.ReadAllText(Path.Combine(logs, "usage-v" + key + "-plc.json")))!.AsArray().Single()!
                : oldReceipts.Single(row => row!["releaseKey"]!.GetValue<string>() == key)!).DeepClone());
        var receiptPath = Path.Combine(logs, "usage-receipts-plc.json"); WriteJson(receiptPath, receipts);
        if (RunSelfCommand(["audit-tool-usage"], "All-release usage coverage") != 0)
            throw new ReleaseException("All-release usage coverage failed");
        WriteJson(Path.Combine(logs, "tool-usage-audit-plc.json"), CatalogAudits.Usage(Root, recordsPath: receiptPath));
        record["plcRuntimes"] = JsonSerializer.SerializeToNode(ReleaseRecords.PlcProof(Root, logs, plan));
        var artifacts = record["validationArtifacts"]!.AsArray();
        var paths = artifacts.Select(row => row!["path"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Concat(ReleaseRecords.PlcProofLogs(Root, logs, plan)).Concat(Directory.EnumerateFiles(logs, "*.json", SearchOption.AllDirectories)))
        {
            var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
            if (paths.Add(relative)) artifacts.Add(new JsonObject { ["path"] = relative, ["sha256"] = ReleaseRecords.HashFile(path) });
        }
        WriteJson(recordPath, record);
        using (var written = JsonDocument.Parse(File.ReadAllText(recordPath))) ReleaseRecords.AssertPlcProof(Root, written.RootElement, true);
        var commonPath = Path.Combine(Root, "manifest/release-build.json");
        var common = JsonNode.Parse(File.ReadAllText(commonPath))!.AsObject();
        var commonValidation = common["validation"]!.AsObject();
        foreach (var (suite, field) in new[] { ("offline", "offlinePassed"), ("kept-code", "keptCodePassed"), ("write-guard", "writeGuardPassed"),
            ("crash-evidence", "crashEvidencePassed"), ("version-policy", "versionPolicySdkPassed"), ("updater", "updaterPassed") })
            commonValidation[field] = suites[suite]!["passed"]!.DeepClone();
        commonValidation["nativeMcpSafetyPassed"] = RequireCount("nativeMcpSafety", results.Checks["native-mcp-session-self-test"].StandardOutput,
            "COMPLETE: (\\d+) native MCP safety checks passed", "nativeMcpSafety");
        commonValidation["nativeSupervisorPassed"] = RequireCount("native supervisor", results.Checks["nativelifecycle-self-test"].StandardOutput,
            "COMPLETE: (\\d+) native supervisor checks passed; live TIA tests NOT RUN", "nativeSupervisor");
        common["adoptedValidation"] = record["adoptedValidation"]!.DeepClone(); WriteJson(commonPath, common);
        using var commonDocument = JsonDocument.Parse(File.ReadAllText(commonPath));
        var commonReason = ReleaseRecords.EngineReuseReason(Root, record["release"]!.GetValue<string>(), commonDocument.RootElement,
            ReleaseRecords.GetSources(Root, "engine"), plan.Tier);
        if (commonReason.Length != 0) throw new ReleaseException("Completed release tests do not validate the producer: " + commonReason);
        // The producer built the configurator and this stage ran its console
        // assertions. Preparation validates those exact bytes without a second run.
        if (RunSelfCommand(["prepare-delivery", "-NoBuild", "-Release", record["release"]!.GetValue<string>(), "-ReleaseDate", GetJsonString(commonDocument.RootElement, "releaseDate"), "-Tier", plan.Tier], "Tested delivery preparation") != 0)
            throw new ReleaseException("Tested delivery preparation failed");
        var deliveryPath = Path.Combine(Root, "manifest/delivery.json");
        var delivery = JsonNode.Parse(File.ReadAllText(deliveryPath))!.AsObject();
        delivery.Remove("engineBuildSha256");
        delivery["multiVersionBuildSha256"] = ReleaseRecords.HashFile(recordPath);
        delivery["configuratorBuildSha256"] = ReleaseRecords.HashFile(Path.Combine(Root, "manifest/configurator-build.json"));
        delivery["releaseKeys"] = JsonSerializer.SerializeToNode(PlcWorkerFixtures.Keys);
        WriteJson(deliveryPath, delivery);
    }
}
