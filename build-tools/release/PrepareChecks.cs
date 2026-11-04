using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.BuildCommon;

namespace TiaMcp.ReleaseTool;

internal sealed record PrepareCheckResults(JsonObject Suites, string[] Checks);
internal sealed record PrepareCheckJob(string Name, Func<PipelineResult> Run);
internal sealed record PrepareCheckPlan(PrepareCheckJob[] Producers, PrepareCheckJob[] Jobs,
    PrepareCheckJob[] Exclusive, PrepareCheckJob[] PostSuites);
internal delegate CommandResult PrepareProcess(string executable, IEnumerable<string> arguments, string directory,
    IDictionary<string, string?> environment, string? input);

internal sealed class PrepareTimings : IDisposable
{
    private static readonly AsyncLocal<PrepareTimings?> Current = new();
    private readonly ConcurrentQueue<(string Name, double Seconds)> rows = new();
    private readonly ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);
    private readonly PrepareTimings? previous = Current.Value;
    internal PrepareTimings() => Current.Value = this;
    internal static void Record(string name, double seconds) => Current.Value?.rows.Enqueue((name, seconds));
    internal static void RecordFailure(string name, string reason)
    {
        if (Current.Value is { } current) current.failures[name] = reason;
    }
    internal bool HasFailures => !failures.IsEmpty;
    public void Dispose()
    {
        Current.Value = previous;
        foreach (var (name, seconds) in rows.OrderByDescending(row => row.Seconds))
            Console.WriteLine($"PREPARE timing {name}: {seconds:F1} s");
        if (!failures.IsEmpty)
        {
            Console.WriteLine("PREPARE failed jobs:");
            foreach (var (name, reason) in failures.OrderBy(row => row.Key, StringComparer.Ordinal))
                Console.WriteLine($"FAIL {name}: {reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "No diagnostic output."}");
        }
    }
}

// Owns only this run's checks and local candidate. The existing suite stages retain
// their compilation/GUI/foundation ordering and registered TRX gates.
internal static class PrepareChecks
{
    internal static PrepareCheckResults Run(PrepareContext context, PreparedPackage package,
        IDictionary<string, string?> environment)
    {
        var extract = Path.Combine(environment["TEMP"]!, "extracted");
        Prepare.Timed("extract", () => ZipFile.ExtractToDirectory(package.ZipPath, extract));
        var bundle = Path.Combine(extract, context.PackageName);
        if (!Directory.Exists(bundle)) throw new ReleaseException("Extracted package root missing: " + bundle);
        var catalog = DotnetSuites.LoadCatalog(Path.Combine(context.Root, "tests/test-suites.json"), context.Root);
        var plan = ReleaseCommands.PrepareCheckJobs(context, bundle, environment);
        return Execute(context, package, environment, plan, catalog);
    }

    internal static PrepareCheckResults Execute(PrepareContext context, PreparedPackage package,
        IDictionary<string, string?> environment, PrepareCheckPlan plan, JsonObject catalog,
        SuiteProcess? process = null, Func<Dictionary<string, CommandResult>>? foundation = null, Action? runtimeCheck = null)
    {
        var output = Path.Combine(context.Logs, "tests");
        var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var passed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var times = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
        PipelineResult RunJob(PrepareCheckJob job)
        {
            var clock = Stopwatch.StartNew();
            var result = ReleaseTestSelection.RunJob(job.Name, job.Run);
            times[job.Name] = clock.Elapsed.TotalSeconds;
            PrepareTimings.Record("job " + job.Name, clock.Elapsed.TotalSeconds);
            if (result.ExitCode != 0) failures[job.Name] = result.StandardOutput + result.StandardError;
            else passed[job.Name] = 0;
            return result;
        }
        (string, Func<PipelineResult>) Wrap(PrepareCheckJob job) => (job.Name, () => RunJob(job));
        CommandResult SuiteProcess(string exe, IEnumerable<string> arguments, string cwd, IDictionary<string, string?>? env)
        {
            var args = arguments.ToArray();
            var resultsIndex = Array.IndexOf(args, "--results-directory");
            var label = resultsIndex >= 0 ? Path.GetFileName(args[resultsIndex + 1]) : Path.GetFileNameWithoutExtension(args[1]);
            var name = "suite-" + label + "-" + args[0];
            var result = Prepare.Timed(name, () => (process ?? DotnetSuites.RunProcess)(exe, args, cwd, env));
            if (result.ExitCode != 0) PrepareTimings.RecordFailure(name, result.StandardOutput + result.StandardError);
            return result;
        }
        // Inventory writers and file-app builds finish before any consumer starts.
        foreach (var job in plan.Producers) RunJob(job);
        var fileApps = Path.Combine(environment["TEMP"]!, "file-apps");
        void Configure(IDictionary<string, string?> env)
        {
            // FastSuites owns each suite's TEMP; only propagate functional inputs.
            env["TIA_MCP_PLC_TOOLS_PYTHON"] = context.CompanionPython;
            env["TIA_MCP_DECODER_PYTHON"] = context.Python;
            env[ReleaseCommands.PlcReleaseSystemTempVariable] = fileApps;
        }
        try
        {
            Prepare.Timed("suites-and-checks", () => ReleaseSuiteStage.Run(context.Root, catalog, output,
                context.PublicApiRoot, ReleaseCommands.Dotnet, context.NuGetConfig, context.TestParallelism > 0 ? context.TestParallelism : context.MaxParallelism,
                process: SuiteProcess, configure: Configure, additionalJobs: plan.Jobs.Select(Wrap).ToArray(),
                exclusiveJobs: plan.Exclusive.Select(Wrap).ToArray(), postSuiteJobs: plan.PostSuites.Select(Wrap).ToArray(),
                foundationRun: foundation ?? (() => ReleaseFoundationStage.Run(context.Root, catalog, output, context.PublicApiRoot,
                    ReleaseCommands.Dotnet, context.NuGetConfig, context.TestParallelism > 0 ? context.TestParallelism : context.MaxParallelism, Configure,
                    (options, selected, run) => PlainShards.Run(context.Root, options, selected,
                        (exe, args, cwd, env) => Prepare.Timed("foundation-" + args.First(), () => run(exe, args, cwd, env))), process: SuiteProcess))));
        }
        catch (Exception error) { failures["suites-and-checks"] = error.Message; }
        // Always check the actual shipped payload, including the installed updater.
        RunJob(new("runtime-bytes", () =>
        {
            if (runtimeCheck is not null) runtimeCheck();
            else ReleaseCommands.RequireRuntimeFiles(context.Root, ReleaseCommands.PackageInventory(package.ZipPath));
            return new("runtime-bytes", 0, "Tested runtime equals packaged runtime.\n", "");
        }));
        foreach (var (name, seconds) in times.OrderByDescending(row => row.Value))
            Console.WriteLine($"PREPARE job {name}: {seconds:F1} s");
        return RequireResults(catalog, output, passed.Keys, failures);
    }

    internal static PrepareCheckResults RequireResults(JsonObject catalog, string output, IEnumerable<string> checks,
        IReadOnlyDictionary<string, string> failures)
    {
        var errors = failures.Select(row => row.Key + ": " + row.Value).ToList();
        foreach (var (name, reason) in failures) PrepareTimings.RecordFailure(name, reason);
        var suites = new JsonObject();
        foreach (var (name, registration) in catalog)
        {
            try
            {
                var path = Path.Combine(output, name, name + ".trx");
                var gate = DotnetSuites.EvaluateTrx(path, (int)registration!["minimumPassed"]!, (int)registration["maximumSkipped"]!);
                errors.AddRange(gate.Errors.Select(error => name + ": " + error));
                if (gate.Errors.Count != 0) PrepareTimings.RecordFailure(name, string.Join("; ", gate.Errors));
                suites[name] = new JsonObject { ["passed"] = gate.Counts["passed"]!.DeepClone(), ["skipped"] = gate.Counts["skipped"]!.DeepClone() };
            }
            catch (Exception error)
            {
                errors.Add(name + ": " + error.Message);
                PrepareTimings.RecordFailure(name, error.Message);
            }
        }
        if (errors.Count != 0) throw new ReleaseException("Prepare checks failed:\n" + string.Join("\n", errors));
        return new(suites, checks.Order(StringComparer.Ordinal).ToArray());
    }

    internal static string WriteCandidate(PrepareContext context, PreparedPackage package, PrepareCheckResults results,
        string? commit = null)
    {
        // A check must not be able to replace the archive after extraction.
        var digest = FileHash.Sha256(package.ZipPath);
        if (digest != package.Sha256 || new FileInfo(package.ZipPath).Length != package.Size)
            throw new ReleaseException("Prepared ZIP changed during checks");
        if (commit is null)
        {
            var git = ProcessRunner.Run("git", ["rev-parse", "HEAD"], context.Root);
            ProcessRunner.RequireSuccess(git, "Read prepared commit");
            commit = git.StandardOutput.Trim();
        }
        var candidate = new JsonObject
        {
            ["version"] = context.Version, ["commit"] = commit, ["zip"] = Path.GetFileName(package.ZipPath),
            ["zipSha256"] = digest, ["zipSize"] = package.Size, ["fileCount"] = package.FileCount,
            ["suites"] = results.Suites.DeepClone(), ["checks"] = JsonSerializer.SerializeToNode(results.Checks),
            ["finishedUtc"] = DateTimeOffset.UtcNow.ToString("o")
        };
        var path = Path.Combine(context.OutputDirectory, "candidate.json");
        File.WriteAllText(path, candidate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        return path;
    }

    internal static void RequireWriteGuard(CommandResult denied, CommandResult read)
    {
        ProcessRunner.RequireSuccess(denied, "Write guard unknown tool");
        ProcessRunner.RequireSuccess(read, "Write guard read tool");
        var result = JsonNode.Parse(denied.StandardOutput);
        if ((string?)result?["hookSpecificOutput"]?["permissionDecision"] != "deny" || read.StandardOutput.Length != 0)
            throw new ReleaseException("Write guard must deny unknown tools and allow read tools without output");
    }
}

internal static partial class ReleaseCommands
{
    internal static PrepareCheckPlan PrepareCheckJobs(PrepareContext context, string bundle,
        IDictionary<string, string?> environment, PrepareProcess? process = null, Func<string>? prepareParity = null)
    {
        var root = context.Root;
        var temp = environment["TEMP"]!;
        var output = Path.Combine(context.Logs, "tests");
        var fileApps = Path.Combine(temp, "file-apps");
        var parityManifest = "";
        var producers = new List<PrepareCheckJob>();
        var jobs = new List<PrepareCheckJob>();
        var exclusive = new List<PrepareCheckJob>();
        var post = new List<PrepareCheckJob>();
        CommandResult Run(string name, string executable, IEnumerable<string> arguments, string? input = null, bool bundled = false,
            IDictionary<string, string?>? overrides = null)
        {
            var directory = Path.Combine(temp, "checks", name);
            foreach (var child in new[] { "config", "temp", "local-app-data", "app-data" }) Directory.CreateDirectory(Path.Combine(directory, child));
            File.WriteAllText(Path.Combine(directory, "config/approval.settings"), "enabled=true\ntimeoutSeconds=120\n");
            var env = new Dictionary<string, string?>(environment)
            {
                ["TIA_MCP_DATA_DIRECTORY"] = directory, ["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(directory, "diagnostics"),
                ["TEMP"] = Path.Combine(directory, "temp"), ["TMP"] = Path.Combine(directory, "temp"), ["TMPDIR"] = Path.Combine(directory, "temp"),
                ["LOCALAPPDATA"] = Path.Combine(directory, "local-app-data"), ["APPDATA"] = Path.Combine(directory, "app-data"),
                ["TIA_MCP_RELEASE_TEMP_ROOT"] = temp, ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = context.PublicApiRoot,
                ["TIA_MCP_PLC_TOOLS_PYTHON"] = context.CompanionPython, [PlcReleaseSystemTempVariable] = fileApps,
                ["TIA_MCP_BUNDLE_ROOT"] = bundled ? bundle : null
            };
            if (bundled) env["CLAUDE_PLUGIN_ROOT"] = bundle;
            env["TIA_MCP_WRITE_GUARD"] = "1";
            if (overrides is not null) foreach (var (key, value) in overrides) env[key] = value;
            var result = Prepare.Timed(name, () => process is null ? ProcessRunner.Run(executable, arguments, bundled ? bundle : root, env, standardInput: input)
                : process(executable, arguments, bundled ? bundle : root, env, input));
            WriteLog(Path.Combine(output, name + ".log"), result);
            return result;
        }
        PrepareCheckJob ActionJob(string name, Action action) => new(name, () => { action(); return new(name, 0, "Passed.\n", ""); });
        PrepareCheckJob Command(string name, string executable, IEnumerable<string> args, bool bundled = false, Action<CommandResult>? validate = null) =>
            ActionJob(name, () => { var result = Run(name, executable, args, bundled: bundled); ProcessRunner.RequireSuccess(result, name); validate?.Invoke(result); });
        PrepareCheckJob Python(string name, string script, string[] args, bool bundled = false, Action<CommandResult>? validate = null) =>
            Command(name, context.Python, [Path.Combine(root, "scripts/checks", script), .. args], bundled, validate);
        const string configurationProject = "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj";
        producers.Add(ActionJob("plc-file-programs", () => PreparePlcReleaseFilePrograms(root, Dotnet, context.NuGetConfig,
            Path.Combine(output, "file-app-builds"), fileApps, (exe, args, cwd, env) => Run("file-" + Path.GetFileNameWithoutExtension(args.Skip(1).First()), exe, args, overrides: env))));
        producers.Add(Command("configuration-build", Dotnet, ["build", configurationProject, "-c", "Release", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false"]));
        producers.Add(ActionJob("host-parity-build", () => parityManifest = prepareParity?.Invoke()
            ?? HostParity.Prepare(root, Path.Combine(output, "parity-build"), Dotnet, context.PublicApiRoot, context.NuGetConfig)));

        producers.Add(Command("adapter-inputs", Dotnet, ["run", "src/Adapters/build/Test-AdapterInputs.cs", "--", "--source-root", "src", "--public-api-root", context.PublicApiRoot, "--evidence-directory", Path.Combine(output, "adapter-inputs")]));
        producers.Add(Command("worker-isolation", Dotnet, ["run", "src/Adapters/build/Test-WorkerIsolation.cs", "--", "--evidence-directory", Path.Combine(output, "worker-isolation"), "--native-call-weaver", "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll"]));
        producers.Add(Python("native-diagnostics", "Test-NativeDiagnostics.py", ["--fixture", "tests/Engine/TiaMcp.Engine.Diagnostics.Tests/bin/Release/net48/TiaMcp.Engine.Diagnostics.Tests.exe",
            "--weaver", "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll", "--output", Path.Combine(output, "native-diagnostics")],
            validate: result =>
            {
                var report = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "native-diagnostics/result.json")))!;
                CheckReleaseCount("diagnosticBehavior", (int?)report["behaviorChecks"] ?? 0, "Diagnostic behavior checks incomplete");
                CheckReleaseCount("diagnosticRejection", (int?)report["rejectionChecks"] ?? 0, "Diagnostic rejection checks incomplete");
                if ((bool?)report["nativeTiaExecuted"] != false) throw new ReleaseException("Native diagnostics executed TIA");
            }));
        jobs.Add(Python("product-smoke", "Test-ReleaseSmoke.py", ["--runtime-root", Path.Combine(bundle, "runtime"), "--public-api-root", context.PublicApiRoot,
            "--temp-root", temp, "--output", Path.Combine(context.Logs, "smoke.json")], bundled: true));
        jobs.Add(Python("relocated-bundle", "Test-RelocatedBundle.py", ["--bundle-root", bundle, "--public-api-root", context.PublicApiRoot], bundled: true));
        foreach (var (name, exe, args) in new[]
        {
            ("foundation-host", Path.Combine(bundle, "runtime/v14sp1/TiaMcp.FoundationHost.exe"), new[] { "--catalog" }),
            ("studio", Path.Combine(bundle, "runtime/studio/TiaOpenness.exe"), new[] { "--network", "127.0.0.1", "not-a-port", "S-1-5-18" })
        }) jobs.Add(ActionJob("bundled-runtime-" + name, () => AssertBundledRuntime(exe, args, name, output, bundle,
            new Dictionary<string, string?>(environment) { ["TIA_MCP_BUNDLE_ROOT"] = bundle },
            (file, arguments, cwd, env) => Run("bundled-runtime-" + name, file, arguments, bundled: true,
                overrides: env!.Where(pair => pair.Key.StartsWith("COREHOST_", StringComparison.Ordinal) || pair.Key == "TIA_OPENNESS_NETWORK_NO_DIALOG")
                    .ToDictionary(pair => pair.Key, pair => pair.Value)))));
        jobs.Add(ActionJob("bundle-write-guard", () =>
        {
            var exe = Path.Combine(bundle, "runtime/tools/TiaMcp.WriteGuard.exe");
            CommandResult Hook(string name) => Run("write-guard-" + name, exe, [],
                JsonSerializer.Serialize(new { tool_name = "mcp__tia-portal__" + name, tool_input = new { } }), bundled: true);
            PrepareChecks.RequireWriteGuard(Hook("UnknownV4Tool"), Hook("GetToolUsage"));
        }));
        var foundationApproval = Path.Combine(context.Logs, "foundation-approval");
        jobs.Add(Python("foundation-approval", "Test-ReleaseApprovalGate.py", ["--product", "foundation", "--runtime-root", Path.Combine(bundle, "runtime"),
            "--public-api", context.PublicApiRoot, "--temp-root", temp, "--output", foundationApproval], true,
            _ => RequirePreparedFoundationApproval(Path.Combine(foundationApproval, "result.json"))));
        foreach (var key in new[] { "20", "21" })
        {
            var api = Path.Combine(context.PublicApiRoot, PlcWorkerApi(key).Directory);
            var approval = Path.Combine(context.Logs, "approval-v" + key + "-bundle");
            jobs.Add(Python("approval-v" + key + "-bundle", "Test-ReleaseApprovalGate.py", ["--product", "engine", "--major", key,
                "--exe", Path.Combine(bundle, $"runtime/v{key}/TiaMcp.FoundationHost.exe"), "--portal-root", api, "--public-api", api,
                "--host-harness", Path.Combine(root, "tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe"),
                "--plc-worker", ReleasePlcFixture(context.Logs, key),
                "--readiness-worker", Path.Combine(bundle, $"runtime/v{key}/worker/TiaMcp.PlcWorker.{key}.exe"),
                "--temp-root", temp, "--output", approval], true,
                _ => ValidateApproval(Path.Combine(approval, "result.json"))));
        }
        foreach (var key in PlcWorkerFixtures.Keys)
        {
            jobs.Add(Command("weave-v" + key + "-bundle", Dotnet, [Path.Combine(root, "runtime/verification/NativeCallWeaver.dll"),
                "verify", Path.Combine(bundle, $"runtime/v{key}/worker/TiaMcp.Adapter.{key}.dll")], true));
            var captured = Path.Combine(context.Logs, "contracts-v" + key);
            jobs.Add(ActionJob("contracts-v" + key, () =>
            {
                var capture = Python("contracts-capture-v" + key, "Snapshot-ToolContracts.py", ["capture", "--repo-root", bundle, "--public-api-root", context.PublicApiRoot,
                    "--releases", key, "--exe", key + "=" + Path.Combine(bundle, $"runtime/v{key}/TiaMcp.FoundationHost.exe"),
                    .. key is "20" or "21" ? new[] { "--plc-worker", key + "=" + ReleasePlcFixture(context.Logs, key) } : [], "--output", captured], true);
                ProcessRunner.RequireSuccess(ToCommand(capture.Run()), capture.Name);
                var compare = Python("contracts-compare-v" + key, "Snapshot-ToolContracts.py", ["compare", "--baseline", Path.Combine(root, "manifest/contracts/v4/baseline"),
                    "--current", captured, "--releases", key]);
                ProcessRunner.RequireSuccess(ToCommand(compare.Run()), compare.Name);
                if (!File.ReadAllBytes(Path.Combine(root, "manifest/contracts/v4/baseline", key + ".json"))
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(captured, key + ".json"))))
                    throw new ReleaseException("Bundle contract bytes differ: V" + key);
            }));
        }
        foreach (var test in new[] { (Check: "native-mcp-session-self-test", Script: "Test-NativeMcpSession.py"),
            (Check: "nativelifecycle-self-test", Script: "Test-NativeLifecycle.py") })
            jobs.Add(Python(test.Check, test.Script, ["--self-test"], validate: result =>
                RequireCount(test.Check, result.StandardOutput, test.Check == "native-mcp-session-self-test"
                    ? "COMPLETE: (\\d+) native MCP safety checks passed" : "COMPLETE: (\\d+) native supervisor checks passed; live TIA tests NOT RUN",
                    test.Check == "native-mcp-session-self-test" ? "nativeMcpSafety" : "nativeSupervisor")));
        jobs.Add(Command("configuration-page", Dotnet, ["run", "--project", configurationProject, "-c", "Release", "--no-build", "--no-restore", "--", Path.Combine(output, "configuration")],
            validate: result => { if (RequireCount("configuration", result.StandardOutput, "(?m)^Passed: (\\d+)\\s*$") < 157) throw new ReleaseException("Configurator tests did not report at least 157 checks"); }));
        var plcJobs = PreparedPlcJobs(root, context.PublicApiRoot, context.Logs, temp, Dotnet, context.Python);
        foreach (var job in plcJobs)
        {
            var check = ActionJob(job.Name, () => RunPlcReleaseJobs([job], root, context.Logs, selected =>
            {
                if (selected.Executable == "test-suites")
                {
                    var name = selected.Arguments[Array.IndexOf(selected.Arguments, "-Suite") + 1];
                    var catalog = DotnetSuites.LoadCatalog(Path.Combine(root, "tests/test-suites.json"), root);
                    var gate = DotnetSuites.EvaluateTrx(Path.Combine(output, name, name + ".trx"), (int)catalog[name]!["minimumPassed"]!, (int)catalog[name]!["maximumSkipped"]!);
                    return new(gate.Errors.Count == 0 ? 0 : 1, "Registered suite: " + name, string.Join("\n", gate.Errors));
                }
                ResetPlcReleaseJobOutputs(selected, context.Logs);
                return Run(selected.Name, selected.Check == "plc-ecosystem" ? context.CompanionPython : selected.Executable, selected.Arguments);
            }, (script, args) => ProcessRunner.RequireSuccess(Run(job.Name + "-compare", context.Python, [script, .. args]), job.Name)));
            if (IsPlcInventoryProducer(job)) producers.Add(check);
            else if (job.Executable == "test-suites") post.Add(check);
            else if (ReleasePlcExclusive(job)) exclusive.Add(check);
            else jobs.Add(check);
        }
        post.Add(ActionJob("host-behavior-parity", () =>
        {
            var failures = new List<string>();
            foreach (var key in new[] { "20", "21" })
            {
                var directory = Path.Combine(output, "host-behavior-parity-v" + key);
                var options = Options.Parse(["-Dotnet", Dotnet, "-EngineMajor", key, "-ResultsDirectory", directory, "-NoBuild", "-BuildDirectory", parityManifest], new HashSet<string> { "NoBuild" });
                var env = FastSuites.EnvironmentFor(directory, context.PublicApiRoot);
                env["RestoreConfigFile"] = context.NuGetConfig;
                env[PlcReleaseSystemTempVariable] = fileApps;
                env["TIA_MCP_PLC_TOOLS_PYTHON"] = context.CompanionPython;
                var name = "host-behavior-parity-v" + key;
                var result = ReleaseTestSelection.RunJob(name, () =>
                {
                    var tested = Prepare.Timed(name, () => HostParity.Run(root, options, env,
                        process is null ? null : (exe, args, cwd, values) => process(exe, args, cwd, values!, null)));
                    return new(name, tested.ExitCode, tested.StandardOutput, tested.StandardError);
                });
                if (result.ExitCode != 0) failures.Add(name + ": " + result.StandardOutput + result.StandardError);
            }
            if (failures.Count != 0) throw new ReleaseException(string.Join("\n", failures));
        }));
        var fixture = Path.Combine(root, "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/bin/Release/net10.0/TiaMcp.WorkerChannel.TransportFixture.exe");
        post.Add(Python("foundation-transport", "Test-FoundationTransport.py", ["--fixture", fixture, "--releases", "14sp1", "15.1", "16", "17", "18", "19", "--output", Path.Combine(context.Logs, "transport")]));
        post.Add(Python("foundation-transport-plc", "Test-FoundationTransport.py", ["--plc-worker", "--fixture", fixture,
            "--host-exe", Path.Combine(root, "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe"), "--releases", .. PlcWorkerFixtures.Keys,
            "--plc-sdk-worker", "20=" + ReleasePlcFixture(context.Logs, "20"), "--plc-sdk-worker", "21=" + ReleasePlcFixture(context.Logs, "21"),
            "--public-api-root", context.PublicApiRoot, "--temp-root", Path.Combine(temp, "transport-plc"), "--output", Path.Combine(context.Logs, "transport-plc")]));
        return new(producers.ToArray(), jobs.ToArray(), exclusive.ToArray(), post.ToArray());
    }

    private static CommandResult ToCommand(PipelineResult result) => new(result.ExitCode, result.StandardOutput, result.StandardError);

    internal static PlcReleaseJob[] PreparedPlcJobs(string root, string api, string logs, string temp, string dotnet, string python) =>
        PlcReleaseJobs(root, api, logs, temp, dotnet, python, weaver: Path.Combine(root, "runtime/verification/NativeCallWeaver.dll"))
            .Where(job => job.Check != "plc-approval").ToArray();

    private static void RequirePreparedFoundationApproval(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var approval = document.RootElement;
        if (GetJsonString(approval, "status") != "passed" || GetJsonInt(approval, "checksPassed") != 30 || GetJsonInt(approval, "checksExpected") != 30 ||
            GetJsonBool(approval, "workbenchConnected") || GetJsonBool(approval, "tiaConnected") || GetJsonString(approval, "approvalSettings") != "explicit enabled=true; timeoutSeconds=120" ||
            !approval.GetProperty("releases").EnumerateObject().Select(row => row.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "14sp1", "15.1", "16", "17", "18", "19" }))
            throw new ReleaseException("Foundation approval count, state or roster is invalid");
        foreach (var release in approval.GetProperty("releases").EnumerateObject())
            if (GetJsonInt(release.Value, "checksPassed") != 5 || GetJsonString(release.Value, "directWrite") != "refused-before-dispatch" ||
                GetJsonString(release.Value, "stagingWrite") != "approval-refused-before-filesystem-write" || GetJsonString(release.Value, "read") != "succeeded" ||
                GetJsonString(release.Value, "CallTool") != "refused-before-dispatch")
                throw new ReleaseException("Foundation approval incomplete: " + release.Name);
    }

    internal static JsonObject PackageInventory(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        var rows = new JsonObject();
        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
        {
            var slash = entry.FullName.IndexOf('/');
            if (slash < 0) throw new ReleaseException("Candidate archive lacks its package root");
            var relative = entry.FullName[(slash + 1)..];
            if (!relative.StartsWith("runtime/", StringComparison.Ordinal) && Path.GetExtension(relative) is not (".exe" or ".dll")) continue;
            using var stream = entry.Open();
            if (rows.ContainsKey(relative)) throw new ReleaseException("Duplicate candidate binary: " + relative);
            rows[relative] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }
        if (rows.Count == 0) throw new ReleaseException("Candidate has no runtime files");
        return rows;
    }

    internal static void RequireRuntimeFiles(string root, JsonObject hashes)
    {
        foreach (var (relative, digest) in hashes)
        {
            var path = Resolve(root, relative);
            if (!File.Exists(path) || FileHash.Sha256(path) != (digest?.GetValue<string>() ?? ""))
                throw new ReleaseException("Candidate checks used changed package runtime bytes: " + relative);
        }
    }

    internal static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new ReleaseException("Unsafe candidate path: " + relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        PrepareBuild.RequireOwnedTree(root, path);
        return path;
    }
}
