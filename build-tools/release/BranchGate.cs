using System.Diagnostics;
using System.Text.Json;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static int BuildTool(Options options)
    {
        RunDotnetLogged(options, Path.Combine(Root, "bin-build/release-tool"),
            ["build", "build-tools/release/TiaMcp.ReleaseTool.csproj", "-c", "Release", "--nologo"], "build.log");
        return 0;
    }

    private static int BranchGate(Options options)
    {
        EnsureWindows("branch-gate");
        var timer = Stopwatch.StartNew();
        var apiRoot = RequiredDirectory(options.Get("PublicApiRoot"), "-PublicApiRoot");
        var dotnet = options.Get("Dotnet", Dotnet);
        var python = options.Get("Python", Py);
        var logs = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(Root, "bin-build/branch-gate", Guid.NewGuid().ToString("N"))));
        if (!logs.StartsWith(Path.Combine(Root, "bin-build") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(logs))
            throw new ReleaseException("Branch gate OutputDirectory must be new and under this worktree's bin-build.");
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (string.IsNullOrWhiteSpace(nuget)) throw new ReleaseException("Branch gate requires -NuGetConfig (offline).");
        if (ReleaseValidation.OfflineNuGetError(nuget) is { } error) throw new ReleaseException(error);
        var rounds = int.Parse(options.Get("LocalStabilityRounds", "10"), System.Globalization.CultureInfo.InvariantCulture);
        if (rounds < 10) throw new ReleaseException("Branch gate requires at least 10 stability rounds.");
        Directory.CreateDirectory(logs);
        var temp = Path.Combine(Path.GetTempPath(), "tmr-branch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var cli = Path.Combine(logs, "dotnet-home");
        Directory.CreateDirectory(cli);
        var cacheLimit = CacheLimit(options);
        var names = new[] { "TIA_MCP_BUILD_CACHE_DIRECTORY", "TIA_MCP_BUILD_CACHE_DISABLED", "TIA_MCP_BUILD_CACHE_RECORDS", "TIA_MCP_OFFLINE_NUGET_CONFIG", "RestoreConfigFile", "TIA_MCP_BUILD_CACHE_MAX_BYTES" };
        var original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable(names[0], Path.GetFullPath(options.Get("BuildCacheDirectory", DefaultBuildCache())));
        Environment.SetEnvironmentVariable(names[1], options.Has("NoBuildCache") ? "1" : "0");
        Environment.SetEnvironmentVariable(names[2], Path.Combine(logs, "cache-records"));
        Environment.SetEnvironmentVariable(names[3], Path.GetFullPath(nuget));
        Environment.SetEnvironmentVariable(names[4], Path.GetFullPath(nuget));
        Environment.SetEnvironmentVariable(names[5], cacheLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            void Build(string project, string[]? properties = null)
            {
                RestoreBuildProject(dotnet, Path.Combine(Root, project), nuget, logs, temp, cli, apiRoot, properties);
                RunBuildRaw(dotnet, ["build", project, "-c", "Release", "--no-restore", .. properties ?? []],
                    Path.Combine(logs, Path.GetFileName(project) + ".log"), temp, cli, apiRoot, nuget, "Branch gate build");
            }
            Build("build-tools/native-call-weaver/NativeCallWeaver.csproj");
            var weaverOutput = Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0");
            CopyDirectoryContents(weaverOutput, Path.Combine(Root, "runtime/verification"));
            Build("src/FoundationHost/TiaMcp.FoundationHost.csproj");
            foreach (var key in new[] { "20", "21" })
            {
                CleanLegacyEngineLayout(key);
                var runtime = Path.Combine(Root, "runtime/v" + key);
                Directory.CreateDirectory(runtime);
                foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src/FoundationHost/bin/Release/net10.0"))
                    .Where(file => Path.GetExtension(file) is ".exe" or ".dll" or ".json" or ".config"))
                    File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)), true);
                File.WriteAllText(Path.Combine(runtime, "release-key.txt"), key, new System.Text.UTF8Encoding(false));
            }
            Build("tests/Engine/TiaMcp.Engine.Harness/TiaMcp.Engine.Harness.csproj");
            Build("tests/Engine/TiaMcp.Engine.Diagnostics.Tests/TiaMcp.Engine.Diagnostics.Tests.csproj");
            var fixture = Path.Combine(Root, "tests/Engine/TiaMcp.Engine.Diagnostics.Tests/bin/Release/net48/TiaMcp.Engine.Diagnostics.Tests.exe");
            VerifyDiagnosticFixture(python, fixture, Path.Combine(weaverOutput, "NativeCallWeaver.dll"), logs, temp, cli, apiRoot);
            var harness = Path.Combine(Root, "tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe");
            foreach (var major in new[] { 20, 21 })
            {
                var api = ResolveReleaseApi(null, apiRoot, major);
                Build($"src/Engine/TiaMcp.Engine.V{major}.csproj", [$"-p:SiemensEngineeringDirectory={api}"]);
                var built = Path.Combine(Root, major == 20 ? "src/Engine/bin-v20/Release/net48" : "src/Engine/bin/Release/net48");
                var runtime = Path.Combine(Root, $"runtime/v{major}/worker");
                Directory.CreateDirectory(runtime);
                foreach (var file in Directory.EnumerateFiles(built).Where(file => Path.GetExtension(file) is ".exe" or ".dll" or ".config" && !Path.GetFileName(file).StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase)))
                    File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)), true);
                RunBuildRaw(Path.Combine(runtime, $"TiaMcp.Engine.V{major}.exe"), ["--write-tool-catalog", Path.Combine(runtime, "tool-catalog.json")],
                    Path.Combine(logs, $"tool-catalog-v{major}.log"), temp, cli, apiRoot, nuget, "Engine worker catalog");
            }
            var jobs = new[] { 20, 21 }.Select(major => ("branch-v" + major, (Func<PipelineResult>)(() =>
            {
                var majorLogs = Path.Combine(logs, "v" + major);
                Directory.CreateDirectory(majorLogs);
                var api = ResolveReleaseApi(null, apiRoot, major);
                var exe = Path.Combine(Root, $"runtime/v{major}/worker/TiaMcp.Engine.V{major}.exe");
                VerifyNativeJit(dotnet, harness, exe, api, majorLogs, temp, cli, apiRoot, major);
                var worker = RunBuildSpec("worker-supervisor", harness, [exe, "worker-supervisor-only"], majorLogs, temp, cli, apiRoot, major);
                RequireCount("worker supervisor", worker, "COMPLETE: (\\d+) worker supervisor checks passed; no TIA connection attempted", "workerFaults");
                VerifyHttpConcurrency(harness, exe, api, majorLogs, temp, cli, apiRoot, major);
                var protocolWorker = PrepareReleaseSdkWorker(dotnet, api, major, majorLogs, temp, cli, apiRoot, nuget);
                VerifyEngineApproval(python, exe, harness, api, majorLogs, temp, cli, apiRoot, major, protocolWorker);
                foreach (var isolated in new[] { false, true }) VerifyStability(python, exe, harness, api, majorLogs, temp, cli, apiRoot, major, rounds.ToString(), isolated, protocolWorker);
                RunBuildSpec("worker-protocol", python, [Path.Combine(Root, "scripts/checks/Test-FoundationTransport.py"),
                    "--releases", major.ToString(), "--output", Path.Combine(majorLogs, "foundation-transport")], majorLogs, temp, cli, apiRoot, major);
                return new PipelineResult("branch-v" + major, 0, "Branch engine checks passed.\n", "");
            }))).ToArray();
            ParallelPipeline.Run(jobs, Path.Combine(logs, "pipeline"), GetMaxParallelism(options));
            RunBuildSpec("approval-gate-self-test", python, ["scripts/checks/Test-ReleaseApprovalGate.py", "--self-test"], logs, temp, cli, apiRoot);
            foreach (var script in new[] { "Snapshot-ToolContracts.py", "Snapshot-ToolResponses.py" })
                RunBuildRaw(python, [Path.Combine(Root, "scripts/checks", script), "verify"], Path.Combine(logs, script + ".log"), temp, cli, apiRoot, null, script);
            WriteJson(Path.Combine(logs, "result.json"), new { status = "passed", elapsedSeconds = timer.Elapsed.TotalSeconds, rounds, nativeTiaExecuted = false, buildCache = CacheEvents() });
            Console.WriteLine($"BRANCH GATE PASSED; elapsed={timer.Elapsed.TotalSeconds:F3}s; logs={logs}");
            return 0;
        }
        catch (Exception ex)
        {
            WriteJson(Path.Combine(logs, "result.json"), new { status = "failed", elapsedSeconds = timer.Elapsed.TotalSeconds, error = ex.Message, buildCache = CacheEvents() });
            throw;
        }
        finally { foreach (var name in names) Environment.SetEnvironmentVariable(name, original[name]); }
    }
}
