using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static readonly string Root = FindRoot(Environment.CurrentDirectory);
    private static string Py => Environment.GetEnvironmentVariable("PYTHON") ?? "python";
    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet";

    internal static int Run(string command, Options options) => command switch
    {
        "preflight" => Preflight(options),
        "prerequisites" => Prerequisites(options),
        "release" => Release(options),
        "run-release-build" => RunReleaseBuild(options),
        "validate-bundle" => ValidateBundle(options),
        "get-bundled-dotnet" => GetBundledDotnet(options),
        "build-configurator" => BuildConfigurator(options),
        "build-studio" => BuildStudio(options),
        "build-plc-workers" => BuildWorkers(options),
        "prepare-delivery" => PrepareDelivery(options),
        "build-release" => BuildRelease(options),
        "build-multi-version" => BuildMultiVersion(options),
        "publish" => Publish(options),
        _ => throw new ReleaseException($"Unknown release command '{command}'.", 64)
    };

    private static int Preflight(Options options)
    {
        var checks = new List<(string Name, string Exe, string[] Args)>
        {
            ("repository, links, shipped-document links, CHANGELOG version", Py, ["scripts/checks/Check-Repository.py", "--no-binaries"]),
            ("repository check self-tests", Py, ["scripts/checks/Check-Repository.py", "--self-test"]),
            ("dead tool references (descriptions and current docs)", Py, ["scripts/checks/Check-DeadToolReferences.py"]),
            ("phase-6 tables generator", Py, ["-B", "scripts/generate/Generate-Phase6Plan.py", "--check"]),
            ("tool usage catalog generator", Py, ["scripts/generate/Generate-ToolUsage.py", "--check"]),
            ("V4 contract snapshots", Py, ["scripts/checks/Snapshot-ToolContracts.py", "verify"]),
            ("V4 response snapshots", Py, ["scripts/checks/Snapshot-ToolResponses.py", "verify"]),
            ("suite runner self-tests", Py, ["scripts/checks/Test-DotnetSuites.py", "--self-test"]),
            ("release approval gate self-tests", Py, ["scripts/checks/Test-ReleaseApprovalGate.py", "--self-test"]),
            ("relocation checker self-tests", Py, ["scripts/checks/Test-RelocatedBundle.py", "--self-test"]),
            ("strict bundle rules", Dotnet, ["run", "--project", "build-tools/release", "--", "validate-bundle", "-Strict", "-NoBinaries", "-SkipSourceHashes"]),
            ("release documentation rules", Dotnet, ["run", "--project", "build-tools/release", "--", "release", "-DocumentationOnly"]),
            ("release tool test suite", Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release"]),
            ("parallel pipeline behavior", Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", "--filter", "Category=Pipeline"])
        };
        if (File.Exists(Path.Combine(Root, "scripts/generate/Generate-ReleaseToolMigration.py")))
            checks.Insert(5, ("release migration tables generator", Py, ["scripts/generate/Generate-ReleaseToolMigration.py", "--check"]));

        var failures = new List<(string Name, CommandResult Result)>();
        foreach (var check in checks)
        {
            var arguments = AddOfflineNuGetConfig(check.Exe, check.Args);
            var result = ProcessRunner.Run(check.Exe, arguments, Root);
            if (result.ExitCode == 0)
            {
                Console.WriteLine($"ok   {check.Name}");
                continue;
            }
            failures.Add((check.Name, result));
            Console.WriteLine($"FAIL {check.Name} (exit {result.ExitCode})");
            foreach (var line in (result.StandardOutput + result.StandardError).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                         .Where(line => new[] { "FAIL", "error", "issue", "mismatch", "stale", "missing" }.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase)))
                         .Take(8)) Console.WriteLine("     " + line);
        }
        if (failures.Count != 0)
        {
            Console.WriteLine($"PREFLIGHT FAILED: {failures.Count} check(s): {string.Join("; ", failures.Select(f => f.Name))}");
            return 1;
        }
        Console.WriteLine("PREFLIGHT PASSED: every build-free release check passed");
        return 0;
    }

    private static int Prerequisites(Options options)
    {
        if (options.Has("SelfTest"))
        {
            var result = ProcessRunner.Run(Dotnet, AddOfflineNuGetConfig(Dotnet,
                ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", "--filter", "Category=Prerequisites"]), Root);
            Console.Write(result.StandardOutput);
            Console.Error.Write(result.StandardError);
            return result.ExitCode;
        }

        var repo = Path.GetFullPath(options.Get("RepoRoot", Root));
        var dotnet = options.Get("Dotnet", Dotnet);
        var python = options.Get("Python", Py);
        var git = options.Get("Git", "git");
        var token = options.Get("Token") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";
        var probes = new List<PrerequisiteProbe>
        {
            new(".NET 10 SDK", () =>
            {
                var executable = ResolveApplication(dotnet);
                var result = ProcessRunner.Run(executable, ["--list-sdks"], repo);
                ProcessRunner.RequireSuccess(result, "dotnet --list-sdks");
                return executable + " " + ReleasePrerequisites.RequireSdk(result.StandardOutput, "10", ".NET 10 SDK");
            }),
            new("Python >= 3.12", () =>
            {
                var executable = ResolveApplication(python);
                var result = ProcessRunner.Run(executable, ["-B", "-c", "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')"], repo);
                ProcessRunner.RequireSuccess(result, "Python version check");
                return executable + " " + ReleasePrerequisites.RequirePython(result.StandardOutput, 3, 12, "Python");
            }),
            new("Ecosystem Python", () =>
            {
                var executable = options.Get("EcosystemPython") ?? Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
                if (string.IsNullOrWhiteSpace(executable))
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrWhiteSpace(local)) throw new ReleaseException("IO_FAILED: LocalAppData is unavailable");
                    executable = Path.Combine(local, "TiaMcp/ecosystem-python/Scripts/python.exe");
                }
                executable = ResolveApplication(executable);
                return ReleasePrerequisites.RequireEcosystemPython(executable, Path.Combine(repo, "scripts/ecosystem/plc_tools_bridge.py"), python);
            }),
            new("Git / clean tree", () =>
            {
                var executable = ResolveApplication(git);
                var result = ProcessRunner.Run(executable, ["-C", repo, "status", "--porcelain", "--untracked-files=normal"], repo);
                ProcessRunner.RequireSuccess(result, "git status");
                var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var managed = lines.Where(ReleaseValidation.IsReleaseManagedChange).ToArray();
                var other = lines.Where(line => !ReleaseValidation.IsReleaseManagedChange(line)).ToArray();
                if (other.Length != 0) throw new ReleaseException("Working tree must be clean apart from release-managed files; commit or revert: " + string.Join(", ", other.Take(5)));
                return managed.Length == 0 ? "clean" : $"clean apart from {managed.Length} release-managed file(s) from an earlier run";
            }),
            new("GitHub token", () =>
            {
                var available = ReleasePrerequisites.GetToken(token, ResolveApplication(git), repo, options.Has("Offline"));
                return ReleasePrerequisites.RequireToken(available);
            }),
            new("Free disk space", () =>
            {
                var drive = new DriveInfo(Path.GetPathRoot(repo)!);
                var minimum = int.Parse(options.Get("MinimumFreeGB", "10"), System.Globalization.CultureInfo.InvariantCulture);
                return ReleasePrerequisites.RequireFreeSpace(drive.AvailableFreeSpace, minimum);
            })
        };

        var apiRoot = options.Get("PublicApiRoot");
        if (string.IsNullOrWhiteSpace(apiRoot))
        {
            var v20 = options.Get("V20ReferenceRoot");
            var v21 = options.Get("V21ReferenceRoot");
            apiRoot = v20 is not null ? Directory.GetParent(Directory.GetParent(Path.GetFullPath(v20))!.FullName)!.FullName
                : v21 is not null ? Directory.GetParent(Directory.GetParent(Directory.GetParent(Path.GetFullPath(v21))!.FullName)!.FullName)!.FullName
                : Directory.Exists(Path.Combine(repo, "sdk")) ? Path.Combine(repo, "sdk")
                : Directory.Exists(Path.Combine(repo, "TIA_V21_PublicAPI")) ? repo
                : Directory.GetParent(repo)!.FullName;
        }
        apiRoot = Path.GetFullPath(apiRoot);
        (string Key, string Relative, string[] Assemblies)[] apiReleases =
        [
            ("14sp1", "TIA_V14SP1_PublicAPI/V14 SP1", ["Siemens.Engineering.dll"]),
            ("15.1", "TIA_V15.1_PublicAPI/V15.1", ["Siemens.Engineering.dll"]),
            ("16", "TIA_V16_PublicAPI/V16", ["Siemens.Engineering.dll"]),
            ("17", "TIA_V17_PublicAPI/V17", ["Siemens.Engineering.dll"]),
            ("18", "TIA_V18_PublicAPI/V18", ["Siemens.Engineering.dll"]),
            ("19", "TIA_V19_PublicAPI/V19", ["Siemens.Engineering.dll"]),
            ("20", "TIA_V20_PublicAPI/V20", ["Siemens.Engineering.dll"]),
            ("21", "TIA_V21_PublicAPI/V21/net48", ["Siemens.Engineering.Base.dll", "Siemens.Engineering.Step7.dll"])
        ];
        foreach (var release in apiReleases)
        {
            var canonical = Path.GetFullPath(Path.Combine(apiRoot, release.Relative.Replace('/', Path.DirectorySeparatorChar)));
            var directory = release.Key switch
            {
                "20" => options.Get("V20ReferenceRoot") ?? canonical,
                "21" => options.Get("V21ReferenceRoot") ?? canonical,
                _ => canonical
            };
            directory = Path.GetFullPath(directory);
            probes.Add(new("PublicAPI " + release.Key, () => ReleasePrerequisites.RequirePublicApi(directory, canonical, release.Assemblies)));
        }

        var cache = Path.Combine(repo, "bin-build/cache/dotnet-10.0.12");
        foreach (var archive in ReleasePrerequisites.ReadPinnedArchives(Path.Combine(repo, "scripts/build/bundled-dotnet.json")))
        {
            var path = Path.Combine(cache, archive.Name);
            probes.Add(new(archive.Name, () => ReleasePrerequisites.RequirePinnedArchive(archive, path)));
        }
        var results = ReleasePrerequisites.Evaluate(probes);
        foreach (var result in results)
            Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")}  {result.Name}: {result.Detail}");
        var failed = results.Count(result => !result.Passed);
        Console.WriteLine($"Release prerequisites: {results.Count - failed} passed, {failed} failed");
        if (failed != 0) throw new ReleaseException("Release prerequisite check failed; no build started");
        return 0;
    }

    private static string ResolveApplication(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var path = Path.GetFullPath(executable);
            if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Windows app execution aliases are not supported");
            if (!File.Exists(path)) throw new ReleaseException("Required application was not found: " + executable);
            return path;
        }
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';')
            : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), executable + (Path.HasExtension(executable) ? "" : extension));
                if (!File.Exists(candidate)) continue;
                if (candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Windows app execution aliases are not supported");
                return Path.GetFullPath(candidate);
            }
        throw new ReleaseException("Required application was not found: " + executable);
    }

    private static int Release(Options options)
    {
        if (options.Has("SelfTest"))
            return RunChild(Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", "--filter", "Category=ReleaseParity"], Root, "Release reuse/archive tests");
        if (options.Has("DocumentationOnly"))
        {
            var error = ReleaseValidation.ReleaseDocumentationError(Root, null, false);
            if (error is not null) throw new ReleaseException(error);
            Console.WriteLine("Release documentation assertions passed.");
            return 0;
        }
        if (ReleaseCheckPolicy.Tier(options.Get("Tier")) != "full" || ReleasePlan(options).Tier != "full") throw new ReleaseException("Release requires -Tier full.");
        if (options.Get("PackagePath") is { } packagePath) ReleaseCheckPolicy.Load(Root).RequireFullPackage(Path.GetFullPath(packagePath));
        var version = options.Get("Version");
        if (version is null || !System.Text.RegularExpressions.Regex.IsMatch(version, "^\\d+\\.\\d+\\.\\d+$"))
            throw new ReleaseException("-Version must be X.Y.Z");
        if (options.Has("DryRun"))
        {
            Console.WriteLine($"DRY RUN Release {version}: {options.Get("Summary", "test")}");
            Console.WriteLine("No files, Git index, commits, tags, or remote services were changed.");
            return 0;
        }
        return RunReleaseProduction(options);
    }

    private static int RunReleaseBuild(Options options)
    {
        var wallTime = Stopwatch.StartNew();
        var steps = new[] { "00-preflight", "01-multi-version", "02-build-release", "03-package-local", "04-validate-bundle", "05-prompt-registration", "06-v4-contracts-capture", "07-v4-contracts-compare", "08-v4-responses-capture", "09-v4-responses-compare", "10-relocated-bundle" };
        var policy = ReleaseCheckPolicy.Load(Root);
        var tier = options.Has("SelfTest") ? "full" : ReleaseCheckPolicy.Tier(options.Get("Tier"), required: true);
        var baseline = Path.GetFullPath(options.Get("FullBaseline", Path.Combine(Root, "bin-build/release-review/last-full.json")));
        if (!baseline.StartsWith(Path.Combine(Root, "bin-build") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("FullBaseline must be under this worktree's bin-build directory.");
        var candidateSources = ReleaseCheckPolicy.SourceInventory(Root, options.Get("Git", "git"));
        var plan = policy.Select(tier, tier == "full" ? null : policy.ChangesSinceFull(baseline, candidateSources));
        var maxParallelism = GetMaxParallelism(options);
        var orderError = ReleaseValidation.AssertStepOrder(steps);
        if (orderError is not null) throw new ReleaseException(orderError);
        if (options.Has("SelfTest"))
            return RunChild(Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", "--filter", "Category=ReviewerChain"], Root, "Reviewer chain tests");
        if (options.Has("DryRun"))
        {
            foreach (var step in steps)
            {
                var check = step[..2] switch { "06" or "07" => "engine-contracts", "08" or "09" => "engine-responses", "10" => "relocation", _ => null };
                Console.WriteLine((check is null || plan.Includes(check) ? "DRY " : "SKIP ") + step);
            }
            Console.WriteLine($"Tier={plan.Tier}; checks selected: {string.Join(", ", plan.SelectedChecks)}; checks skipped: {string.Join(", ", plan.SkippedChecks)}");
            Console.WriteLine($"02: full engines -> multi-version completion; 04: ZIP parent + filename without extension; max parallelism={maxParallelism}.");
            Console.WriteLine("After step 04, selected checks run as bounded jobs; each snapshot comparison waits for its capture.");
            return 0;
        }

        var api = options.Get("PublicApiRoot") ?? throw new ReleaseException("-PublicApiRoot is required for a real reviewer chain");
        api = Path.GetFullPath(api);
        if (!Directory.Exists(api)) throw new ReleaseException("PublicApiRoot does not exist: " + api);
        var python = options.Get("Python", Py);
        var dotnet = options.Get("Dotnet", Dotnet);
        var companionPython = options.Get("CompanionPython") ?? Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON");
        if (string.IsNullOrWhiteSpace(companionPython)) throw new ReleaseException("Prepare a companion Python environment and pass -CompanionPython (or set TIA_MCP_PLC_TOOLS_PYTHON)");
        companionPython = ResolveApplication(companionPython);

        var output = Path.GetFullPath(options.Get("OutputDirectory", Path.Combine(Root, "bin-build/release-review-packages", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8])));
        if (Directory.Exists(output) || File.Exists(output)) throw new ReleaseException("Choose a new OutputDirectory; existing packages are never overwritten");
        var fullRoot = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !output.StartsWith(Path.Combine(fullRoot, "bin-build") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException("Repository output must be under bin-build");

        var logs = Path.Combine(Root, "bin-build/release-review", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(logs, "original-records");
        var state = Path.Combine(logs, "build-records");
        var trackedResult = ProcessRunner.Run(options.Get("Git", "git"), ["ls-files", "--", "manifest/*", "docs/reference/version-tool-catalog.md", "docs/reference/tool-matrix.md"], Root);
        ProcessRunner.RequireSuccess(trackedResult, "Could not enumerate release records");
        var backedUp = trackedResult.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (backedUp.Length == 0) throw new ReleaseException("Cannot enumerate tracked release records");
        var recordSnapshot = new ReleaseRecordSnapshot(Root, original, state, backedUp);

        var nugetConfig = options.Get("NuGetConfig");
        if (string.IsNullOrWhiteSpace(nugetConfig))
        {
            nugetConfig = Path.Combine(logs, "offline-nuget.config");
            File.WriteAllText(nugetConfig, "<configuration><packageSources><clear /></packageSources></configuration>", new System.Text.UTF8Encoding(false));
        }
        nugetConfig = Path.GetFullPath(nugetConfig);
        var offlineError = ReleaseValidation.OfflineNuGetError(nugetConfig);
        if (offlineError is not null) throw new ReleaseException(offlineError);
        var runtimePin = ReleasePrerequisites.ReadPinnedArchives(Path.Combine(Root, "scripts/build/bundled-dotnet.json"));
        var runtimeCache = Path.Combine(Root, "bin-build/cache/dotnet-10.0.12");
        foreach (var archive in runtimePin)
            _ = ReleasePrerequisites.RequirePinnedArchive(archive, Path.Combine(runtimeCache, archive.Name));
        var processEnv = new Dictionary<string, string?>
        {
            ["TIA_MCP_PLC_TOOLS_PYTHON"] = companionPython,
            ["RestoreConfigFile"] = nugetConfig,
            ["TIA_MCP_OFFLINE_NUGET_CONFIG"] = nugetConfig,
            ["NuGetAudit"] = "false",
            ["UseSharedCompilation"] = "false",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false"
        };
        var planPath = Path.Combine(logs, "check-plan.json");
        WriteJson(planPath, plan);
        processEnv["TIA_MCP_RELEASE_CHECK_PLAN"] = planPath;
        var releaseTemp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8]));
        if (releaseTemp.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Release temp root must be outside the repository");
        processEnv["TIA_MCP_RELEASE_TEMP_ROOT"] = releaseTemp;
        var harness = Path.Combine(Root, "tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe");
        var activeLog = new AsyncLocal<string?>();
        var failedHostDataRetained = false;
        void RecordProcessOutput(CommandResult result)
        {
            if (activeLog.Value is { } logPath) File.AppendAllText(logPath, result.StandardOutput + result.StandardError, new System.Text.UTF8Encoding(false));
        }

        void RestoreOriginal() => recordSnapshot.RestoreOriginal();
        CommandResult RunIsolated(string name, string executable, IReadOnlyList<string> args, bool productDefaults = false, string? workingDirectory = null)
        {
            var root = Path.Combine(releaseTemp, "rc", Guid.NewGuid().ToString("N")[..8]);
            foreach (var directory in new[] { "config", "temp", "local-app-data", "app-data" }) Directory.CreateDirectory(Path.Combine(root, directory));
            File.WriteAllText(Path.Combine(root, "check.txt"), executable + " " + string.Join(' ', args), new System.Text.UTF8Encoding(false));
            var approvalSettings = ReleaseValidation.HostApprovalSettings(productDefaults);
            if (approvalSettings is not null) File.WriteAllText(Path.Combine(root, "config/approval.settings"), approvalSettings, new System.Text.UTF8Encoding(false));
            var env = new Dictionary<string, string?>(processEnv, StringComparer.OrdinalIgnoreCase)
            {
                ["TIA_MCP_DATA_DIRECTORY"] = root,
                ["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(root, "diagnostics"),
                ["LOCALAPPDATA"] = Path.Combine(root, "local-app-data"),
                ["APPDATA"] = Path.Combine(root, "app-data"),
                ["TEMP"] = Path.Combine(root, "temp"),
                ["TMP"] = Path.Combine(root, "temp")
            };
            var result = ProcessRunner.Run(executable, args, workingDirectory ?? Root, env);
            RecordProcessOutput(result);
            if (result.ExitCode == 0)
            {
                try { Directory.Delete(root, true); }
                catch (Exception ex) { Console.Error.WriteLine($"warning: release host data remains at {root}: {ex.Message}"); }
            }
            else
            {
                Interlocked.Exchange(ref failedHostDataRetained, true);
                Console.WriteLine($"Retained failed release check data ({name}): {root}");
            }
            return result;
        }
        void RunStep(string name, Action action, bool tracksReleaseRecords = true)
        {
            var logPath = Path.Combine(logs, name + ".log");
            activeLog.Value = logPath;
            File.WriteAllText(logPath, "", new System.Text.UTF8Encoding(false));
            Console.WriteLine($"START {name}; {logPath}");
            var timer = Stopwatch.StartNew();
            try
            {
                if (tracksReleaseRecords) recordSnapshot.RestoreStepState();
                action();
                if (tracksReleaseRecords) recordSnapshot.CaptureStepState();
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, ex.Message + Environment.NewLine, new System.Text.UTF8Encoding(false));
                throw new ReleaseException($"{name} failed; see {logPath}; {ex.Message}");
            }
            finally
            {
                timer.Stop();
                File.AppendAllText(logPath, $"Elapsed: {timer.Elapsed.TotalSeconds:F3} seconds{Environment.NewLine}", new System.Text.UTF8Encoding(false));
                try { if (tracksReleaseRecords) RestoreOriginal(); }
                finally { activeLog.Value = null; }
            }
            Console.WriteLine($"PASS {name}; elapsed={timer.Elapsed.TotalSeconds:F3}s; {logPath}");
        }
        void RunTool(string command, params string[] args)
        {
            var arguments = new List<string> { "run", "--project", "build-tools/release", "--no-restore", "--", command };
            arguments.AddRange(args);
            var result = RunIsolated("review-" + command, dotnet, arguments);
            Console.Write(result.StandardOutput);
            Console.Error.Write(result.StandardError);
            if (result.ExitCode != 0) throw new ReleaseException($"{command} exited {result.ExitCode}: {Tail(result.StandardOutput + result.StandardError)}");
        }
        void RunPython(string script, params string[] args)
        {
            var all = new List<string> { script };
            all.AddRange(args);
            var result = RunIsolated("review-" + Path.GetFileNameWithoutExtension(script), python, all);
            Console.Write(result.StandardOutput);
            Console.Error.Write(result.StandardError);
            if (result.ExitCode != 0) throw new ReleaseException($"{script} exited {result.ExitCode}: {Tail(result.StandardOutput + result.StandardError)}");
        }
        void RunPythonProductDefaults(string script, params string[] args)
        {
            var all = new List<string> { script };
            all.AddRange(args);
            var result = RunIsolated("review-" + Path.GetFileNameWithoutExtension(script), python, all, productDefaults: true);
            Console.Write(result.StandardOutput);
            Console.Error.Write(result.StandardError);
            if (result.ExitCode != 0) throw new ReleaseException($"{script} exited {result.ExitCode}: {Tail(result.StandardOutput + result.StandardError)}");
        }
        void RequireFile(string path, string explanation)
        {
            if (!File.Exists(path)) throw new ReleaseException(explanation + ": " + path);
        }

        try
        {
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(logs);
            RunStep("00-preflight", () => RunTool("preflight"));
            RunStep("01-multi-version", () => RunTool("build-multi-version", "-PublicApiRoot", api, "-PrepareOnly", "-Offline", "-Test", "-Dotnet", dotnet, "-Python", python, "-NuGetConfig", nugetConfig, "-MaxParallelism", maxParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            RunStep("02-build-release", () =>
            {
                RunTool("build-release", "-V20ReferenceRoot", Path.Combine(api, "TIA_V20_PublicAPI/V20"), "-V21ReferenceRoot", Path.Combine(api, "TIA_V21_PublicAPI/V21/net48"), "-Dotnet", dotnet, "-Python", python, "-NuGetConfig", nugetConfig, "-MaxParallelism", maxParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture));
                RunTool("build-multi-version", "-PublicApiRoot", api, "-CompleteOnly", "-Offline", "-Test", "-Dotnet", dotnet, "-Python", python, "-NuGetConfig", nugetConfig, "-MaxParallelism", maxParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture));
            });
            RunStep("02-tier-record", () => WriteTierRecord(plan, passed: false));
            RunStep("03-package-local", () => RunPython("scripts/build/Package-Release.py", "--local", "--output-directory", output));
            RunStep("04-validate-bundle", () =>
            {
                var packagePath = Path.Combine(output, "package-result.json");
                RequireFile(packagePath, "Local packaging result missing");
                using var package = JsonDocument.Parse(File.ReadAllText(packagePath));
                var zip = package.RootElement.GetProperty("path").GetString() ?? throw new ReleaseException("Package result has no path");
                var bundle = ReleaseValidation.GetBundleDirectory(zip);
                if (bundle.EndsWith(".", StringComparison.Ordinal)) throw new ReleaseException("Bundle directory must not have a trailing dot");
                RunTool("validate-bundle", "-Strict", "-BundleRoot", bundle, "-PackageMode");
            });
            RunCandidateChecks(plan, api, logs, output, releaseTemp, dotnet, python, maxParallelism,
                (name, action) => RunStep(name, action, tracksReleaseRecords: false), RunPython, RunPythonProductDefaults, RunIsolated);
            RunStep("13-finalize-package", () =>
            {
                WriteTierRecord(plan, passed: true);
                var finalOutput = Path.Combine(output, "validated");
                RunPython("scripts/build/Package-Release.py", "--local", "--output-directory", finalOutput);
                File.Copy(Path.Combine(finalOutput, "package-result.json"), Path.Combine(output, "package-result.json"), true);
                using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "package-result.json")));
                RunTool("validate-bundle", "-Strict", "-BundleRoot", ReleaseValidation.GetBundleDirectory(package.RootElement.GetProperty("path").GetString()!), "-PackageMode");
            });
            var resultPath = Path.Combine(output, "package-result.json");
            using var finalPackage = JsonDocument.Parse(File.ReadAllText(resultPath));
            var finalZip = finalPackage.RootElement.GetProperty("path").GetString()!;
            if (plan.Tier == "full")
            {
                policy.RequireFullPackage(finalZip);
                var currentSources = ReleaseCheckPolicy.SourceInventory(Root, options.Get("Git", "git"));
                if (!candidateSources.SequenceEqual(currentSources)) throw new ReleaseException("Candidate inputs changed during validation; full baseline was not advanced.");
                WriteJson(baseline, new { tier = "full", package = finalZip, packageSha256 = ReleaseRecords.HashFile(finalZip), sourceFiles = candidateSources });
            }
            WriteJson(Path.Combine(logs, "run-result.json"), new { tier = plan.Tier, status = "passed", elapsedSeconds = wallTime.Elapsed.TotalSeconds,
                checksRan = plan.SelectedChecks, checksSkipped = plan.SkippedChecks, package = finalZip });
            Console.WriteLine($"COMPLETE: tier={plan.Tier}; bundle={ReleaseValidation.GetBundleDirectory(finalZip)}; logs={logs}; all release records restored");
            return 0;
        }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(logs, "failure.log"), ex.Message + Environment.NewLine, new System.Text.UTF8Encoding(false));
            WriteJson(Path.Combine(logs, "run-result.json"), new { tier = plan.Tier, status = "failed", elapsedSeconds = wallTime.Elapsed.TotalSeconds, error = ex.Message });
            throw;
        }
        finally
        {
            RestoreOriginal();
            if (!failedHostDataRetained)
            {
                try { if (Directory.Exists(releaseTemp)) Directory.Delete(releaseTemp, true); }
                catch (Exception ex) { Console.Error.WriteLine($"warning: release temp remains at {releaseTemp}: {ex.Message}"); }
            }
            else Console.Error.WriteLine($"failed host-check data retained under {releaseTemp}");
        }
    }

    private static string Tail(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(12));
    }

    private static int ValidateBundle(Options options)
    {
        if (options.Has("SelfTest"))
            return RunChild(Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", "--filter", "Category=BundleParity"], Root, "Bundle validation tests");
        var root = Path.GetFullPath(options.Get("BundleRoot", Root));
        if (!Directory.Exists(root)) throw new ReleaseException($"Bundle root does not exist: {root}");
        var package = options.Has("PackageMode") || !File.Exists(Path.Combine(root, "Version.props"));
        if (options.Get("PendingRelease") is not null && (!options.Has("NoBinaries") || !options.Has("SkipSourceHashes") || package))
            throw new ReleaseException("-PendingRelease is only for the repository pre-build gate with -NoBinaries -SkipSourceHashes");
        var errors = new List<string>();
        var noBinaries = options.Has("NoBinaries");
        var strict = options.Has("Strict");
        var deliveryRulesPath = Path.Combine(root, "scripts/operations/delivery-files.json");
        JsonDocument? rules = null;
        try
        {
            if (!File.Exists(deliveryRulesPath)) errors.Add("Missing delivery-files.json");
            else
            {
                rules = JsonDocument.Parse(File.ReadAllText(deliveryRulesPath));
                if (!rules.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1) errors.Add("Unsupported delivery-files schema");
                foreach (var group in new[] { "include", "exclude" })
                {
                    foreach (var kind in new[] { "files", "prefixes" })
                    {
                        var paths = JsonStringArray(rules.RootElement.GetProperty(group), kind);
                        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Count) errors.Add($"Invalid duplicate delivery rule: {group}/{kind}");
                    }
                }
                foreach (var path in JsonStringArray(rules.RootElement.GetProperty("include"), "files").Concat(JsonStringArray(rules.RootElement.GetProperty("include"), "prefixes")))
                {
                    if (noBinaries && (path == "TiaOpenness.exe" || path.StartsWith("runtime/", StringComparison.Ordinal) && path != "runtime/README.md")) continue;
                    var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(file) && !Directory.Exists(file) && !GeneratedDeliveryResourceAvailable(root, path, package))
                        errors.Add("Missing delivery resource: " + path);
                }
                if (package)
                    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                        if (!IsDeliveryFile(relative, rules.RootElement)) errors.Add("File outside delivery set: " + relative);
                    }
            }
        }
        catch (Exception ex) { errors.Add("Delivery rules could not be read: " + ex.Message); }

        foreach (var resource in BundleManifestRequirements.MissingBundleResources(root, package, noBinaries)) errors.Add("Missing bundle resource: " + resource);
        foreach (var path in BundleManifestRequirements.GuiRequiredPaths)
        {
            if (package && rules is not null && !IsDeliveryFile(path, rules.RootElement)) continue;
            if (noBinaries && path == "TiaOpenness.exe") continue;
            if (!File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))) errors.Add("Missing GUI entry: " + path);
        }
        if (!package)
            foreach (var name in new[] { "TiaMcp.Runtime.csproj", "S7LiveReader.cs", "OpcUaLiveReader.cs", "S7WebApiChannel.cs", "UnifiedOpenPipeChannel.cs" })
                if (!File.Exists(Path.Combine(root, "src/Runtime", name))) errors.Add("Missing runtime channel source: src/Runtime/" + name);
        if (strict)
        {
            var assemblies = Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
                .Concat(Directory.Exists(Path.Combine(root, "runtime")) ? Directory.EnumerateFiles(Path.Combine(root, "runtime"), "*.dll", SearchOption.AllDirectories) : []);
            foreach (var assembly in assemblies)
                if (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(assembly)).Contains("TiaMcpTestPolicy", StringComparison.Ordinal)) errors.Add("Test-only behavior policy assembly cannot enter a release bundle: " + assembly);
        }
        if (rules is not null)
        {
            var pyArgs = new List<string> { "scripts/checks/Check-Repository.py", "--root", root };
            if (noBinaries) pyArgs.Add("--no-binaries");
            if (package) pyArgs.Add("--package-mode");
            var pyCheck = ProcessRunner.Run(Py, pyArgs, Root);
            if (pyCheck.ExitCode != 0)
            {
                var details = (pyCheck.StandardOutput + pyCheck.StandardError)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase))
                    .Take(40)
                    .Select(line => "Repository check: " + line);
                errors.AddRange(details);
                if (!errors.Any(error => error.StartsWith("Repository check:", StringComparison.Ordinal)))
                    errors.Add("Repository check exited " + pyCheck.ExitCode);
            }
            else Console.Write(pyCheck.StandardOutput);
        }
        try { ValidateRequiredJson(root, package); }
        catch (Exception ex) { errors.Add(ex.Message); }
        if (strict && !options.Has("PendingRelease"))
        {
            try { ValidateBuildRecords(root, package, noBinaries, options.Has("SkipSourceHashes")); }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        rules?.Dispose();
        foreach (var error in errors) Console.WriteLine("[FAIL] " + error);
        if (errors.Count != 0)
        {
            Console.WriteLine($"Validation FAILED ({errors.Count} issue(s)).");
            return 1;
        }
        Console.WriteLine("[ OK ] bundle metadata, resources, and release records");
        Console.WriteLine("Validation PASSED.");
        return 0;
    }

    private static bool GeneratedDeliveryResourceAvailable(string root, string relative, bool package)
    {
        if (package) return false;
        var generated = relative switch
        {
            "runtime/tools/TiaMcp.Updater.exe" => "bin-build/updater/TiaMcp.Updater.exe",
            "runtime/tools/TiaMcp.Updater.exe.config" => "bin-build/updater/TiaMcp.Updater.exe.config",
            _ => ""
        };
        return generated.Length != 0 && File.Exists(Path.Combine(root, generated.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void ValidateRequiredJson(string root, bool package)
    {
        var names = new[] { "manifest/package-manifest.json", "manifest/delivery.json", "manifest/release-build.json", "manifest/configurator-build.json", "manifest/multi-version-build.json", "manifest/tools-list.json" };
        foreach (var name in names)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new ReleaseException($"Required build record missing: {name}");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ReleaseException($"Build record must contain a JSON object: {name}");
        }
        var rulesPath = Path.Combine(root, "scripts/operations/delivery-files.json");
        if (!File.Exists(rulesPath)) throw new ReleaseException("Required delivery-files.json missing");
        using var rules = JsonDocument.Parse(File.ReadAllText(rulesPath));
        if (!rules.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            throw new ReleaseException("Unsupported delivery-files schema");
        if (!package && !File.Exists(Path.Combine(root, "Version.props"))) throw new ReleaseException("Repository bundle is missing Version.props");
        var blueprintPath = Path.Combine(root, "templates/project-blueprints/full_plc_hmi_project.json");
        if (File.Exists(blueprintPath))
        {
            using var blueprint = JsonDocument.Parse(File.ReadAllText(blueprintPath));
            if (blueprint.RootElement.TryGetProperty("requiredBundleFiles", out var required))
                foreach (var relative in required.EnumerateArray().Select(value => value.GetString() ?? ""))
                    if (!File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))) && !Directory.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))
                        throw new ReleaseException("Blueprint requiredBundleFiles missing: " + relative);
        }
        var pluginPath = Path.Combine(root, ".claude-plugin/plugin.json");
        if (File.Exists(pluginPath))
        {
            using var plugin = JsonDocument.Parse(File.ReadAllText(pluginPath));
            using var delivery = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/delivery.json")));
            var release = JsonString(delivery.RootElement, "release");
            if (JsonString(plugin.RootElement, "version") != release) throw new ReleaseException("Plugin version differs from release version");
            if (!JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/package-manifest.json"))).RootElement.TryGetProperty("capabilities", out _))
                throw new ReleaseException("package-manifest.json lacks capabilities");
        }
    }

    private static void ValidateBuildRecords(string root, bool package, bool noBinaries, bool skipSourceHashes)
    {
        if (!package)
            foreach (var relative in new[] { "build-tools/release/ReleaseTiers.cs", "build-tools/release/ReleaseCandidateChecks.cs",
                "build-tools/release/release-checks.json", "scripts/checks/Test-ReleaseSmoke.py", "tests/Release/TiaMcp.ReleaseTool.Tests/ReleaseTierTests.cs" })
                if (!File.Exists(Path.Combine(root, relative))) throw new ReleaseException("Required release tier file missing: " + relative);
        using var deliveryDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/delivery.json")));
        using var buildDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/release-build.json")));
        using var configuratorDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/configurator-build.json")));
        var delivery = deliveryDoc.RootElement; var build = buildDoc.RootElement; var configurator = configuratorDoc.RootElement;
        var release = JsonString(delivery, "release");
        var engineRelease = JsonString(build, "release");
        var fileVersion = JsonString(build, "fileVersion");
        if (!Version.TryParse(release, out _) || !Version.TryParse(engineRelease, out _)) throw new ReleaseException("Release must be X.Y.Z");
        var changelog = Regex.Match(File.ReadAllText(Path.Combine(root, "CHANGELOG.md")), "(?m)^##\\s*\\[(?<v>\\d+\\.\\d+\\.\\d+)\\]");
        if (!changelog.Success) throw new ReleaseException("CHANGELOG.md has no release version entry");
        var newest = changelog.Groups["v"].Value;
        var note = File.Exists(Path.Combine(root, $"docs/releases/v{newest}.md"));
        if (!ReleaseValidation.ChangelogVersion(newest, release, noBinaries && !package, note)) throw new ReleaseException("CHANGELOG must match the release, or name a newer documented release in repository -NoBinaries mode");
        if (noBinaries && !package && Version.Parse(newest) > Version.Parse(release)) newest = release;
        if (JsonString(delivery, "release") != newest || JsonString(delivery, "engineRelease") != engineRelease || JsonString(delivery, "fileVersion") != fileVersion)
            throw new ReleaseException("Delivery version differs from CHANGELOG or engine build record");
        var sourceRelease = package ? release : (string?)XDocument.Load(Path.Combine(root, "Version.props")).Root?.Element("PropertyGroup")?.Element("TiaMcpRelease") ?? "";
        if (sourceRelease != engineRelease || sourceRelease + ".0" != fileVersion) throw new ReleaseException("Engine build version differs from Version.props");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest/package-manifest.json")));
        if (!noBinaries && manifest.RootElement.TryGetProperty("tier", out var tier))
        {
            var plan = JsonSerializer.Deserialize<ReleaseCheckPlan>(build.GetProperty("checkPlan").GetRawText(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new ReleaseException("Candidate check plan missing");
            ReleaseCheckPolicy.Load(Root).ValidatePlan(plan);
            if (tier.GetString() != plan.Tier || JsonString(build, "tier") != plan.Tier ||
                !JsonStringArray(manifest.RootElement, "checksSelected").SequenceEqual(plan.SelectedChecks) ||
                !JsonStringArray(manifest.RootElement, "checksSkipped").SequenceEqual(plan.SkippedChecks))
                throw new ReleaseException("Package tier/check selection differs from the validated engine build");
            var status = JsonString(manifest.RootElement, "checkStatus");
            if (status is not ("pending" or "passed") || !JsonStringArray(manifest.RootElement, "checksRan").SequenceEqual(status == "passed" ? plan.SelectedChecks : []))
                throw new ReleaseException("Package check completion record is invalid");
        }
        var bundleVersion = JsonString(manifest.RootElement, "bundleVersion");
        if (bundleVersion != newest) throw new ReleaseException($"Version mismatch: CHANGELOG says {newest}, manifest bundleVersion says {bundleVersion}");
        var packageName = JsonString(manifest.RootElement, "packageName");
        if (!Regex.IsMatch(packageName, "v" + Regex.Escape(newest) + "[_-]")) throw new ReleaseException($"Version mismatch: manifest packageName '{packageName}' does not carry v{newest}");
        if (strictRecordHash(root, delivery, "manifest/release-build.json", "engineBuildSha256") is { } engineHash) throw new ReleaseException(engineHash);
        if (strictRecordHash(root, delivery, "manifest/configurator-build.json", "configuratorBuildSha256") is { } configHash) throw new ReleaseException(configHash);
        if (IntValue(configurator, "testsPassed") <= 0) throw new ReleaseException("Configurator test result missing");
        var executable = configurator.GetProperty("executable");
        if (!noBinaries && ReleaseRecords.HashFile(Path.Combine(root, JsonString(executable, "path"))) != JsonString(executable, "sha256")) throw new ReleaseException("Configurator EXE changed after validation");
        if (!package && !skipSourceHashes)
        {
            foreach (var rows in new[] { TryArray(configurator, "sourceFiles"), TryArray(build, "sourceFiles"), TryArray(build, "validationInputs") })
                foreach (var row in rows)
                {
                    var relative = JsonString(row, "path");
                    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) throw new ReleaseException("Validated source missing: " + relative);
                    if (!string.Equals(ReleaseValidation.SourceHash(path), JsonString(row, "sha256"), StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Source changed after validation: " + relative);
                }
        }
        if (!noBinaries)
            foreach (var row in TryArray(build, "runtimeFiles"))
            {
                var relative = JsonString(row, "path");
                // The release-only IL verifier is recorded with the engine but never delivered (Validate-Bundle.ps1 skipped it in package mode).
                if (package && relative.StartsWith("runtime/verification/", StringComparison.Ordinal)) continue;
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) throw new ReleaseException("Runtime dependency missing: " + relative);
                if (!string.Equals(ReleaseRecords.HashFile(path), JsonString(row, "sha256"), StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Runtime hash differs: " + relative);
            }
    }

    private static string? strictRecordHash(string root, JsonElement record, string relative, string property)
    {
        if (!record.TryGetProperty(property, out var value) || !File.Exists(Path.Combine(root, relative)) || ReleaseRecords.HashFile(Path.Combine(root, relative)) != value.GetString()) return "Build record changed: " + relative;
        return null;
    }

    private static IReadOnlyList<string> JsonStringArray(JsonElement parent, string property) => parent.GetProperty(property).EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
    private static IReadOnlyList<JsonElement> TryArray(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static string JsonString(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int IntValue(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static bool IsDeliveryFile(string path, JsonElement rules)
    {
        bool matches(JsonElement rule) => JsonStringArray(rule, "files").Contains(path, StringComparer.Ordinal) || JsonStringArray(rule, "prefixes").Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
        return matches(rules.GetProperty("include")) && !matches(rules.GetProperty("exclude"));
    }

    private static int GetBundledDotnet(Options options)
    {
        var scriptDir = Path.Combine(Root, "scripts/build");
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(scriptDir, "bundled-dotnet.json")));
        var version = pin.RootElement.GetProperty("version").GetString()!;
        var targetRelative = pin.RootElement.GetProperty("target").GetString()!;
        var cache = Path.GetFullPath(options.Get("Cache", Path.Combine(Root, $"bin-build/cache/dotnet-{version}")));
        var target = Path.GetFullPath(Path.Combine(Root, targetRelative));
        Directory.CreateDirectory(cache);
        var archives = new List<string>();
        foreach (var archive in pin.RootElement.GetProperty("archives").EnumerateArray())
        {
            var name = archive.GetProperty("name").GetString()!;
            var url = archive.GetProperty("url").GetString()!;
            var hash = archive.GetProperty("sha512").GetString()!;
            if (!url.StartsWith("https://builds.dotnet.microsoft.com/dotnet/", StringComparison.Ordinal)) throw new ReleaseException($"Only the official Microsoft download location is allowed: {url}");
            var path = Path.Combine(cache, name);
            if (!File.Exists(path))
            {
                if (options.Has("Offline")) throw new ReleaseException($"Missing cached archive (download disabled): {path}");
                using var client = new HttpClient();
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                File.WriteAllBytes(path + ".partial", response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                File.Move(path + ".partial", path, true);
            }
            var actual = Convert.ToHexStringLower(SHA512.HashData(File.ReadAllBytes(path)));
            if (actual != hash.ToLowerInvariant()) { File.Delete(path); throw new ReleaseException($"SHA-512 mismatch for {name}; the cached copy was removed"); }
            archives.Add(path);
        }
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith("/", StringComparison.Ordinal) || relative.Split('/').Contains("..", StringComparer.Ordinal)) throw new ReleaseException($"Unsafe archive path: {relative}");
                if (!seen.Add(relative)) throw new ReleaseException($"Archives overlap at {relative}");
                var output = Path.GetFullPath(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!output.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException($"Unsafe archive path: {relative}");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                entry.ExtractToFile(output, false);
            }
        }
        foreach (var framework in pin.RootElement.GetProperty("frameworks").EnumerateArray())
            if (!Directory.Exists(Path.Combine(target, "shared", framework.GetString()!, version))) throw new ReleaseException($"Bundled runtime lacks {framework.GetString()} {version}");
        if (!File.Exists(Path.Combine(target, "host", "fxr", version, "hostfxr.dll"))) throw new ReleaseException("Bundled runtime lacks hostfxr");
        foreach (var name in new[] { "LICENSE.txt", "ThirdPartyNotices.txt" })
            if (!File.Exists(Path.Combine(target, name))) throw new ReleaseException($"Bundled runtime lacks {name}");
        Console.WriteLine($"Microsoft .NET {version} laid out in {target}: {seen.Count} files");
        return 0;
    }

    private static int BuildConfigurator(Options options)
    {
        EnsureWindows("build-configurator");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var compiler = Path.Combine(windows, "Microsoft.NET/Framework64/v4.0.30319/csc.exe");
        if (!File.Exists(compiler)) throw new ReleaseException(".NET Framework 4.8 developer tools are required (csc.exe missing)");
        var studio = Path.Combine(Root, "src/Studio");
        var resourceOutput = Path.Combine(Root, "bin-build/configurator-resources");
        Directory.CreateDirectory(resourceOutput);
        var versionDoc = XDocument.Load(Path.Combine(Root, "Version.props"));
        var release = (string?)versionDoc.Root?.Element("PropertyGroup")?.Element("TiaMcpRelease") ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(release, "^\\d+\\.\\d+\\.\\d+$")) throw new ReleaseException("Release version must be X.Y.Z");
        var metadata = Path.Combine(resourceOutput, "DesktopVersion.cs");
        File.WriteAllText(metadata, "using System.Reflection;\n" +
            "[assembly: AssemblyTitle(\"TIA Portal Workbench\")]\n" +
            "[assembly: AssemblyDescription(\"Unified TIA Portal engineering, MCP service and AI client desktop\")]\n" +
            $"[assembly: AssemblyVersion(\"{release}\")]\n[assembly: AssemblyFileVersion(\"{release}.0\")]\n[assembly: AssemblyInformationalVersion(\"{release}\")]\n[assembly: AssemblyMetadata(\"TiaMcpRelease\", \"{release}\")]\n", new System.Text.UTF8Encoding(false));
        var output = Path.Combine(resourceOutput, "TiaOpenness.exe");
        var wpf = Path.Combine(windows, "Microsoft.NET/Framework64/v4.0.30319/WPF");
        var references = new[] { "/r:System.Windows.Forms.dll", "/r:System.Web.Extensions.dll", "/r:System.Security.dll", "/r:System.Core.dll", "/r:System.Xaml.dll", $"/r:{Path.Combine(wpf, "WindowsBase.dll")}", $"/r:{Path.Combine(wpf, "PresentationFramework.dll")}", $"/r:{Path.Combine(wpf, "PresentationCore.dll")}" };
        var args = new List<string> { "/nologo", "/target:winexe", "/optimize+", "/utf8output", $"/out:{output}" };
        args.AddRange(references);
        args.AddRange([Path.Combine(studio, "Launcher", "Launcher.cs"), Path.Combine(Root, "src", "Shared", "ProcessArguments.cs"), metadata]);
        ProcessRunner.RequireSuccess(ProcessRunner.Run(compiler, args, Root), "Configurator build");
        var launcher = Path.Combine(Root, "TiaOpenness.exe");
        File.Copy(output, launcher, true);
        Console.WriteLine("Configurator launcher built: TiaOpenness.exe");
        if (options.Has("Test"))
        {
            var testRoot = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
            if (string.IsNullOrWhiteSpace(testRoot)) testRoot = Path.Combine(Path.GetTempPath(), "tmr-configurator-" + Guid.NewGuid().ToString("N")[..8]);
            var logRoot = Path.Combine(testRoot, "configurator-tests");
            Directory.CreateDirectory(logRoot);
            const string configurationProject = "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj";
            RunDotnetLogged(options, "bin-build", ["build", configurationProject, "-c", "Release", "--nologo"], "configurator-tests-build.log");
            var testResult = RunLogged(Dotnet, ["run", "--no-build", "--project", configurationProject, "-c", "Release", "--", logRoot], "bin-build/configurator-tests.log");
            ProcessRunner.RequireSuccess(testResult, "Configurator tests");
            var resultText = testResult.StandardOutput + testResult.StandardError;
            var match = System.Text.RegularExpressions.Regex.Match(resultText, "(?m)^Passed: (\\d+)\\s*$");
            if (!match.Success || int.Parse(match.Groups[1].Value) < 157) throw new ReleaseException("Configurator tests did not report at least 157 checks");
            var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in new[] { "Gui", "Client", "Core", "Contracts", "Bridge" })
            {
                var path = Path.Combine(Root, "src/Studio", folder);
                // Generated sources under bin/obj are build output, not inputs (as Build-Configurator.ps1 and Package-Release.py define them).
                if (Directory.Exists(path)) foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(file => IsBuildSource(file) && !IsBuildOutput(file))) inputs.Add(file);
            }
            var testsFolder = Path.Combine(Root, "tests/Studio/TiaOpenness.Configuration.Tests");
            foreach (var file in Directory.EnumerateFiles(testsFolder, "*", SearchOption.TopDirectoryOnly).Where(file => Path.GetExtension(file) is ".cs" or ".csproj")) inputs.Add(file);
            foreach (var relative in new[] { "src/Studio/Launcher/Launcher.cs", "Version.props", "src/Studio/Directory.Build.props", "tests/Studio/Directory.Build.props", "src/Shared/LocalProcess.cs", "src/Logic/Siemens/TiaVersionCatalog.cs", "src/Shared/ProcessArguments.cs", "src/Shared/OpennessEnvironment.cs", "src/Shared/BundleLayout.cs" })
                if (File.Exists(Path.Combine(Root, relative))) inputs.Add(Path.Combine(Root, relative));
            var fonts = Path.Combine(Root, "src/Studio/Gui/Fonts");
            if (Directory.Exists(fonts)) foreach (var file in Directory.EnumerateFiles(fonts, "*", SearchOption.AllDirectories)) inputs.Add(file);
            var releaseSources = Path.Combine(Root, "build-tools/release");
            foreach (var file in Directory.EnumerateFiles(releaseSources, "*.cs", SearchOption.AllDirectories).Where(path => !IsBuildOutput(path))) inputs.Add(file);
            var rows = inputs.Order(StringComparer.OrdinalIgnoreCase).Select(path => new { path = Path.GetRelativePath(Root, path).Replace('\\', '/'), sha256 = ReleaseValidation.SourceHash(path) }).ToArray();
            var record = new
            {
                generatedAt = DateTimeOffset.UtcNow.ToString("o"), testsPassed = int.Parse(match.Groups[1].Value),
                realTiaAcceptance = "NOT PERFORMED; isolated client configuration and WPF tests only",
                executable = new { path = "TiaOpenness.exe", sha256 = ReleaseRecords.HashFile(launcher) }, sourceFiles = rows
            };
            WriteJson(Path.Combine(Root, "manifest/configurator-build.json"), record);
        }
        return 0;
    }

    private static int BuildStudio(Options options)
    {
        EnsureWindows("build-studio");
        var sharedText = options.Get("TiaSharedAdapterPaths");
        if (sharedText is not null && sharedText is not ("true" or "false")) throw new ReleaseException("-TiaSharedAdapterPaths must be true or false");
        var shared = sharedText is null
            ? string.Equals((string?)XDocument.Load(Path.Combine(Root, "src/Shared/TiaSharedAdapterPaths.props")).Root?.Element("PropertyGroup")?.Element("TiaSharedAdapterPaths"), "true", StringComparison.OrdinalIgnoreCase)
            : sharedText == "true";
        var keys = options.All("ReleaseKeys").ToArray();
        if (keys.Length == 0) keys = options.Get("PublicApiRoot") is null ? ["20", "21"] : ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"];
        var logs = Path.Combine(Root, shared ? "bin-build/studio-shared-adapter" : "bin-build/studio-native");
        Directory.CreateDirectory(logs);
        Environment.SetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false");
        var configArgs = new List<string> { "build-configurator" };
        if (options.Has("Test") && !shared) configArgs.Add("-Test");
        if (options.Get("NuGetConfig") is { Length: > 0 } nugetConfig) configArgs.AddRange(["-NuGetConfig", nugetConfig]);
        var configCode = RunSelfCommand(configArgs.ToArray(), "Desktop launcher/configuration module build");
        if (configCode != 0) return configCode;
        var studio = Path.Combine(Root, "src/Studio");
        RunDotnetLogged(options, logs, ["build", Path.Combine(studio, "Gui/TiaOpenness.Gui.csproj"), "-c", "Release", "--nologo"], "gui.log", shared);
        var outputSubdir = shared ? "shared-adapter/" : "";
        var app = Path.Combine(studio, $"Gui/bin/Release/{outputSubdir}net10.0-windows");
        if (Directory.Exists(app) && Directory.EnumerateFiles(app, "Newtonsoft.Json.dll", SearchOption.AllDirectories).Any()) throw new ReleaseException("Studio payload must use only System.Text.Json; remove stale Newtonsoft output before packaging");
        RequireFile(app, "TiaMcp.WorkerChannel.dll", "Studio client worker channel is missing");
        foreach (var name in new[] { "TiaMcp.WorkerChannel.dll", "System.Text.Json.dll", "System.Text.Encodings.Web.dll", "System.IO.Pipelines.dll", "Microsoft.Bcl.AsyncInterfaces.dll", "System.Buffers.dll", "System.Memory.dll", "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll", "System.Threading.Tasks.Extensions.dll" })
            RequireFile(Path.Combine(app, "bridge"), name, "Studio bridge channel dependency is missing: " + name);
        var weaver = Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll");
        if (shared) RunDotnetLogged(options, logs, ["build", "build-tools/native-call-weaver/NativeCallWeaver.csproj", "-c", "Release", "--nologo"], "weaver.log", shared);
        var native = new List<object>();
        foreach (var key in keys)
        {
            var folder = key switch { "14sp1" => "V14Sp1", "15.1" => "V15_1", _ => "V" + key };
            var relative = key switch { "14sp1" => "TIA_V14SP1_PublicAPI/V14 SP1", "15.1" => "TIA_V15.1_PublicAPI/V15.1", "21" => "TIA_V21_PublicAPI/V21/net48", _ => $"TIA_V{key}_PublicAPI/V{key}" };
            var apiInput = key == "20" ? options.Get("V20ReferenceRoot") : key == "21" ? options.Get("V21ReferenceRoot") : null;
            apiInput ??= options.Get("PublicApiRoot") is { } apiRoot ? Path.Combine(apiRoot, relative) : null;
            if (apiInput is null || !Directory.Exists(apiInput)) throw new ReleaseException($"Missing PublicAPI root for {key}");
            var api = Path.GetFullPath(apiInput);
            var project = shared ? Path.Combine(Root, $"src/Adapters/{folder}/Adapter.{key}.csproj") : Path.Combine(studio, $"Openness/{folder}/StudioOpenness.{folder}.csproj");
            RunDotnetLogged(options, logs, ["build", project, "-c", "Release", "--nologo", $"-p:SiemensEngineeringDirectory={api}"], $"native-v{key}.log", shared);
            var built = shared ? Path.Combine(Path.GetDirectoryName(project)!, $"bin/{key}/Release/net48") : Path.Combine(Path.GetDirectoryName(project)!, "bin/Release/net48");
            if (Directory.Exists(built) && Directory.EnumerateFiles(built, "Siemens.*.dll").Any()) throw new ReleaseException("Siemens assemblies must not be copied into the Studio build");
            var destination = Path.Combine(app, $"bridge/adapters/v{key}");
            Directory.CreateDirectory(destination);
            string assembly;
            if (shared)
            {
                assembly = Path.Combine(built, $"TiaMcp.Adapter.{key}.dll");
                foreach (var dll in Directory.EnumerateFiles(built, "*.dll")) File.Copy(dll, Path.Combine(destination, Path.GetFileName(dll)), true);
                var packaged = Path.Combine(destination, Path.GetFileName(assembly));
                var result = ProcessRunner.Run(Dotnet, [weaver, "verify", packaged], Root);
                WriteLog(Path.Combine(logs, $"weave-v{key}.log"), result);
                ProcessRunner.RequireSuccess(result, "Packaged Studio adapter weave verification");
            }
            else
            {
                assembly = Path.Combine(built, "TiaOpenness.Openness.dll");
                File.Copy(assembly, Path.Combine(destination, Path.GetFileName(assembly)), true);
            }
            native.Add(new { releaseKey = key, assemblySha256 = ReleaseRecords.HashFile(assembly), nativeAcceptance = "NOT RUN" });
        }
        if (options.Has("Test"))
        {
            RunDotnetLogged(options, logs, ["build", "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj", "-c", "Release", "--nologo"], "configuration-build.log", shared);
            var configExe = Path.Combine(Root, $"tests/Studio/TiaOpenness.Configuration.Tests/bin/Release/{outputSubdir}net10.0-windows/TiaOpenness.Configuration.Tests.exe");
            var result = ProcessRunner.Run(configExe, [Path.Combine(logs, "configuration-tests")], Root);
            WriteLog(Path.Combine(logs, "configuration-tests.log"), result);
            ProcessRunner.RequireSuccess(result, "Embedded configuration tests");
            RunDotnetLogged(options, logs, ["test", "tests/Studio/TiaOpenness.Core.Tests/TiaOpenness.Core.Tests.csproj", "-c", "Release", "--nologo"], "client-tests.log", shared);
            if (ReleasePlan(options).Includes("gui-tests")) RunDotnetLogged(options, logs, ["test", "tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj", "-c", "Release", "--nologo"], "gui-tests.log", shared);
        }
        if (Directory.Exists(app) && Directory.EnumerateFiles(app, "Siemens.*.dll", SearchOption.AllDirectories).Any()) throw new ReleaseException("Studio output contains a Siemens assembly");
        if (Directory.Exists(app) && Directory.EnumerateFiles(app, "Newtonsoft.Json.dll", SearchOption.AllDirectories).Any()) throw new ReleaseException("Studio payload must use only System.Text.Json; remove stale Newtonsoft output before packaging");
        WriteJson(Path.Combine(logs, "result.json"), new { createdAt = DateTimeOffset.UtcNow.ToString("o"), app, nativeAdapters = native, functionalTestsExecuted = options.Has("Test"), nativeTiaExecuted = false });
        Console.WriteLine($"Studio and selected direct Openness adapters built: {app}");
        Console.WriteLine("Native TIA/project acceptance NOT RUN. Start TiaOpenness.exe --mock for the synthetic workflow.");
        return 0;
    }

    private static int BuildWorkers(Options options)
    {
        EnsureWindows("build-plc-workers");
        var apiRoot = options.Get("PublicApiRoot") ?? throw new ReleaseException("-PublicApiRoot is required");
        var source = Path.GetFullPath(options.Get("SourceRoot", Path.Combine(Root, "src")));
        var project = Path.Combine(Root, "src/Worker/TiaMcpServer.PlcWorker.csproj");
        var weaver = Path.GetFullPath(options.Get("NativeCallWeaverPath", Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll")));
        if (!File.Exists(weaver)) throw new ReleaseException($"Native call weaver missing: {weaver}");
        var output = Path.GetFullPath(options.Get("EvidenceDirectory", Path.Combine(Root, "bin-build/plc-adapter-workers")));
        Directory.CreateDirectory(output);
        var keys = options.All("ReleaseKeys").ToArray();
        if (keys.Length == 0) keys = ["14sp1", "15.1", "16", "17", "18", "19", "20", "21"];
        var results = new List<object>();
        foreach (var key in keys)
        {
            var rel = key switch { "14sp1" => ("TIA_V14SP1_PublicAPI/V14 SP1", "Siemens.Engineering"), "15.1" => ("TIA_V15.1_PublicAPI/V15.1", "Siemens.Engineering"), "16" => ("TIA_V16_PublicAPI/V16", "Siemens.Engineering"), "17" => ("TIA_V17_PublicAPI/V17", "Siemens.Engineering"), "18" => ("TIA_V18_PublicAPI/V18", "Siemens.Engineering"), "19" => ("TIA_V19_PublicAPI/V19", "Siemens.Engineering"), "20" => ("TIA_V20_PublicAPI/V20", "Siemens.Engineering"), "21" => ("TIA_V21_PublicAPI/V21/net48", "Siemens.Engineering.Base"), _ => throw new ReleaseException("Unsupported precise release key: " + key) };
            var api = Path.GetFullPath(Path.Combine(apiRoot, rel.Item1));
            var props = new List<string> { $"-p:TiaReleaseKey={key}", $"-p:SiemensEngineeringDirectory={api}", $"-p:AdapterSourceRoot={source}", $"-p:WorkerSourceRoot={Path.Combine(source, "Worker")}", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false" };
            if (options.Has("UseReferenceAssemblyPackage")) props.Add("-p:UseReferenceAssemblyPackage=true");
            props.Add($"-p:NativeCallWeaverPath={weaver}");
            if (!options.Has("NoRestore"))
            {
                var restore = new List<string> { "restore", project, "-v:minimal" };
                restore.AddRange(["-m:1", "-nodeReuse:false"]);
                restore.AddRange(props);
                AddNugetConfig(restore, options.Get("NuGetConfig"));
                var restoreResult = ProcessRunner.Run(options.Get("Dotnet", Dotnet), restore, Root);
                WriteLog(Path.Combine(output, $"restore-{key}.log"), restoreResult);
                ProcessRunner.RequireSuccess(restoreResult, $"Worker restore failed for {key}; see restore-{key}.log");
            }
            var build = new List<string> { "build", project, "-c", "Release", "--no-restore", "-v:minimal" };
            build.AddRange(["-m:1", "-nodeReuse:false"]);
            if (options.Has("Rebuild")) build.Add("--no-incremental");
            build.AddRange(props);
            var buildResult = ProcessRunner.Run(options.Get("Dotnet", Dotnet), build, Root);
            WriteLog(Path.Combine(output, $"build-{key}.log"), buildResult);
            ProcessRunner.RequireSuccess(buildResult, $"Worker compile failed for {key}; no native code was run");
            var buildOutput = Path.Combine(Root, $"src/Worker/bin/{key}/Release/net48");
            var worker = Path.Combine(buildOutput, $"TiaMcp.PlcWorker.{key}.exe");
            var adapter = Path.Combine(buildOutput, $"TiaMcp.Adapter.{key}.dll");
            var verify = ProcessRunner.Run(options.Get("Dotnet", Dotnet), [weaver, "verify", adapter, Path.Combine(output, $"coverage-{key}.json")], Root);
            WriteLog(Path.Combine(output, $"verify-{key}.log"), verify);
            ProcessRunner.RequireSuccess(verify, $"Copied Adapter DLL coverage verification failed for {key}");
            var adapters = Directory.EnumerateFiles(buildOutput, "TiaMcp.Adapter.*.dll").ToArray();
            if (adapters.Length != 1 || Path.GetFileName(adapters[0]) != $"TiaMcp.Adapter.{key}.dll") throw new ReleaseException($"Worker output must contain exactly its selected adapter: {buildOutput}");
            if (Directory.EnumerateFiles(buildOutput).Any(path => Path.GetFileName(path).StartsWith("Siemens", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).StartsWith("TiaMcp.PlcFoundation", StringComparison.OrdinalIgnoreCase))) throw new ReleaseException($"Unexpected Siemens/old Foundation dependency copied to {buildOutput}");
            var core = Path.Combine(api, rel.Item2 + ".dll");
            results.Add(new { releaseKey = key, targetFramework = "net48", referenceIdentity = System.Reflection.AssemblyName.GetAssemblyName(core).FullName, referenceSha256 = ReleaseRecords.HashFile(core), workerSha256 = ReleaseRecords.HashFile(worker), adapterSha256 = ReleaseRecords.HashFile(adapter), compile = "passed", nativeAcceptance = "NOT RUN", nativeCallInstrumentation = "Adapter DLL weave and verify required by build", deployment = "Use build-multi-version", scope = "PLC foundation worker; full engines remain separate" });
            Console.WriteLine($"{key} worker + single adapter compiled; native acceptance NOT RUN; ready for foundation bundle tests");
        }
        WriteJson(Path.Combine(output, "summary.json"), new { generatedAt = DateTimeOffset.UtcNow.ToString("o"), scope = "compile only; no worker/TIA launch, no change to full V20/V21 engines", results });
        return 0;
    }
    private static int PrepareDelivery(Options options)
    {
        EnsureWindows("prepare-delivery");
        var release = options.Get("Release") ?? throw new ReleaseException("-Release is required");
        if (!System.Text.RegularExpressions.Regex.IsMatch(release, "^\\d+\\.\\d+\\.\\d+$")) throw new ReleaseException("-Release must be X.Y.Z");
        var releaseDate = options.Get("ReleaseDate", DateTime.Now.ToString("yyyyMMdd"));
        if (!System.Text.RegularExpressions.Regex.IsMatch(releaseDate, "^\\d{8}$") || !DateTime.TryParseExact(releaseDate, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new ReleaseException("-ReleaseDate must be a valid yyyyMMdd date");
        ReleaseRecords.AssertRuntimePreparation(Root);
        var buildPath = Path.Combine(Root, "manifest/release-build.json");
        if (!File.Exists(buildPath)) throw new ReleaseException("Engine build record missing; run build-release after multi-version preparation");
        using var build = JsonDocument.Parse(File.ReadAllText(buildPath));
        var fileVersion = ReleaseRecordsProperty(build.RootElement, "fileVersion");
        if (ReleaseRecordsProperty(build.RootElement, "release") != release || fileVersion != release + ".0") throw new ReleaseException("Engine build record differs from the requested release; run build-release first");
        foreach (var major in new[] { 20, 21 })
            if (!File.Exists(Path.Combine(Root, $"runtime/v{major}/TiaMcp.Engine.V{major}.exe"))) throw new ReleaseException($"V{major} engine output missing; run build-release first");
        var code = RunSelfCommand(["build-configurator", "-Test"], "Configurator validation");
        if (code != 0) return code;
        var package = $"TIA_MCP_Delivery_v{release}_{releaseDate}";
        var timestamp = DateTimeOffset.UtcNow.ToString("o");
        var delivery = new JsonObject
        {
            ["release"] = release, ["releaseDate"] = releaseDate, ["package"] = package,
            ["engineRelease"] = release, ["fileVersion"] = fileVersion,
            ["engineBuildSha256"] = ReleaseRecords.HashFile(buildPath),
            ["configuratorBuildSha256"] = ReleaseRecords.HashFile(Path.Combine(Root, "manifest/configurator-build.json")),
            ["generatedAt"] = timestamp
        };
        WriteJson(Path.Combine(Root, "manifest/delivery.json"), delivery);
        var manifestPath = Path.Combine(Root, "manifest/package-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new ReleaseException("package-manifest.json is not an object");
        manifest["bundleVersion"] = release;
        manifest["packageName"] = package;
        manifest["fileVersion"] = fileVersion;
        manifest["refreshedAt"] = timestamp;
        var entrypoints = manifest["entrypoints"]?.AsObject() ?? throw new ReleaseException("package-manifest.json lacks entrypoints");
        entrypoints["configurator"] = "TiaOpenness.exe";
        entrypoints["mcpServerExe"] = "runtime/v21/TiaMcp.Engine.V21.exe";
        if (manifest["cli"] is JsonObject cli)
        {
            cli["exe"] = "runtime/v21/TiaMcp.Engine.V21.exe";
            cli["description"] = "Call runtime/v21/TiaMcp.Engine.V21.exe or runtime/v20/TiaMcp.Engine.V20.exe with a CLI verb. JSON/YAML generation requires no MCP client.";
        }
        manifest["validationStatus"] = $"Delivery {release}; engine {fileVersion} validation retained with exact source/runtime hashes; configurator tested separately; real TIA acceptance pending";
        WriteJson(manifestPath, manifest);
        if (build.RootElement.TryGetProperty("checkPlan", out _)) WriteTierRecord(ReleasePlan(options), passed: false);
        code = RunSelfCommand(["validate-bundle", "-Strict"], "Delivery validation");
        if (code != 0) return code;
        Console.WriteLine($"Prepared {package} using engine {fileVersion}. Review and commit before packaging.");
        return 0;
    }
    private static int BuildRelease(Options options)
    {
        return BuildReleasePipeline(options);
    }
    private static int BuildMultiVersion(Options options)
    {
        EnsureWindows("build-multi-version");
        var api = Path.GetFullPath(options.Get("PublicApiRoot") ?? throw new ReleaseException("-PublicApiRoot is required"));
        if (!Directory.Exists(api)) throw new ReleaseException("PublicApiRoot does not exist: " + api);
        var versionDoc = XDocument.Load(Path.Combine(Root, "Version.props"));
        var release = (string?)versionDoc.Root?.Element("PropertyGroup")?.Element("TiaMcpRelease") ?? "";
        if (!Regex.IsMatch(release, "^\\d+\\.\\d+\\.\\d+$")) throw new ReleaseException("Public release version must be X.Y.Z without fork or feature suffixes");
        if (options.Has("PrepareOnly") && options.Has("CompleteOnly")) throw new ReleaseException("-PrepareOnly and -CompleteOnly cannot be combined");

        var logs = Path.Combine(Root, "bin-build/multi-version");
        var releaseTempRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_MCP_RELEASE_TEMP_ROOT") ?? Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8]));
        var repoPrefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (releaseTempRoot.StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Release temp root must be outside the repository: " + releaseTempRoot);
        var runTemp = Path.Combine(releaseTempRoot, "mv");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(runTemp);
        Environment.SetEnvironmentVariable("TIA_MCP_RELEASE_TEMP_ROOT", runTemp);
        Environment.SetEnvironmentVariable("TIA_MCP_TEST_PUBLIC_API_ROOT", api);
        Environment.SetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false");
        Environment.SetEnvironmentVariable("DOTNET_CLI_USE_MSBUILD_SERVER", "0");
        Environment.SetEnvironmentVariable("UseSharedCompilation", "false");

        var dotnet = options.Get("Dotnet", Dotnet);
        var python = options.Get("Python", Py);
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG") ?? Environment.GetEnvironmentVariable("RestoreConfigFile");
        if (options.Has("Offline") && string.IsNullOrWhiteSpace(nuget)) nuget = Path.Combine(Root, "bin-build/P6-53/offline-nuget.config");
        if (!string.IsNullOrWhiteSpace(nuget))
        {
            nuget = Path.GetFullPath(nuget);
            Environment.SetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG", nuget);
            Environment.SetEnvironmentVariable("RestoreConfigFile", nuget);
        }
        Environment.SetEnvironmentVariable("TIA_MCP_RELEASE_TEMP_ROOT", releaseTempRoot);
        var test = options.Has("Test");
        var plan = ReleasePlan(options);
        var pendingPath = Path.Combine(logs, "prepared-build.json");
        var sources = ReleaseRecords.GetSources(Root, "multi");
        var validationInputs = ReleaseRecords.GetValidationInputs(Root, "multi");
        JsonDocument? pending = null;
        JsonElement validation;
        var releaseCheckEvidence = new List<string>();

        if (options.Has("CompleteOnly"))
        {
            if (!File.Exists(pendingPath)) throw new ReleaseException("Multi-version preparation missing; run build-multi-version -PrepareOnly -Test before build-release");
            pending = JsonDocument.Parse(File.ReadAllText(pendingPath));
            var record = pending.RootElement;
            var preparedTest = record.TryGetProperty("test", out var testProperty) && testProperty.ValueKind == JsonValueKind.True;
            ReleaseRecords.AssertMultiVersionPreparation(Root, record, release, sources, validationInputs,
                ReleaseRecords.GetRuntimeFiles(Root, prepared: true), api);
            if (preparedTest != test) throw new ReleaseException("Preparation/completion -Test must match; rebuild the preparation");
            validation = record.GetProperty("validation");
        }
        else
        {
            if (File.Exists(pendingPath)) File.Delete(pendingPath);
            var adapterInputEvidence = Path.Combine(logs, "adapter-inputs");
            var adapterInputArgs = new[]
            {
                "run", Path.Combine(Root, "src/Adapters/build/Test-AdapterInputs.cs"), "--",
                "--source-root", Path.Combine(Root, "src"), "--public-api-root", api,
                "--evidence-directory", adapterInputEvidence
            };
            ProcessRunner.RequireSuccess(RunLoggedProcess(dotnet, adapterInputArgs, logs, "adapter-inputs.log", nuget), "Adapter input contract checks failed");
            var adapterInputResults = Path.Combine(adapterInputEvidence, "input-results.json");
            if (!File.Exists(adapterInputResults)) throw new ReleaseException("Adapter input checks did not write their evidence record");
            releaseCheckEvidence.Add(adapterInputResults);

            var weaver = RunLoggedProcess(dotnet, ["build", "build-tools/native-call-weaver/NativeCallWeaver.csproj", "-c", "Release", "-v:q", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false"], logs, "weaver.log", nuget);
            ProcessRunner.RequireSuccess(weaver, "Native weaver build failed");

            var bundledArgs = new List<string> { "get-bundled-dotnet" };
            if (options.Has("Offline")) bundledArgs.Add("-Offline");
            var bundledCode = RunSelfCommand(bundledArgs.ToArray(), "Bundled .NET runtime layout");
            if (bundledCode != 0) throw new ReleaseException("Bundled .NET runtime layout failed");

            var workerArgs = new List<string> { "build-plc-workers", "-PublicApiRoot", api, "-Dotnet", dotnet,
                "-EvidenceDirectory", Path.Combine(logs, "adapters"), "-UseReferenceAssemblyPackage" };
            if (!string.IsNullOrWhiteSpace(nuget)) workerArgs.AddRange(["-NuGetConfig", nuget]);
            var workerCode = RunSelfCommand(workerArgs.ToArray(), "PLC adapter worker builds");
            if (workerCode != 0) throw new ReleaseException("PLC adapter worker builds failed");

            var workerIsolationEvidence = Path.Combine(logs, "worker-isolation");
            var workerIsolationArgs = new[]
            {
                "run", Path.Combine(Root, "src/Adapters/build/Test-WorkerIsolation.cs"), "--",
                "--evidence-directory", workerIsolationEvidence,
                "--native-call-weaver", Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll")
            };
            ProcessRunner.RequireSuccess(RunLoggedProcess(dotnet, workerIsolationArgs, logs, "worker-isolation.log", nuget), "Worker isolation checks failed");
            var workerIsolationResults = Path.Combine(workerIsolationEvidence, "worker-isolation-results.json");
            if (!File.Exists(workerIsolationResults)) throw new ReleaseException("Worker isolation checks did not write their evidence record");
            releaseCheckEvidence.Add(workerIsolationResults);
            releaseCheckEvidence.AddRange(Directory.EnumerateFiles(workerIsolationEvidence, "coverage-*.json"));

            var studioArgs = new List<string> { "build-studio", "-PublicApiRoot", api, "-Dotnet", dotnet,
                "-TiaSharedAdapterPaths", "false", "-ReleaseKeys", "14sp1", "-ReleaseKeys", "15.1", "-ReleaseKeys", "16", "-ReleaseKeys", "17", "-ReleaseKeys", "18", "-ReleaseKeys", "19", "-ReleaseKeys", "20", "-ReleaseKeys", "21" };
            if (test) studioArgs.Add("-Test");
            if (!string.IsNullOrWhiteSpace(nuget)) studioArgs.AddRange(["-NuGetConfig", nuget]);
            WithIsolatedHost(runTemp, "studio-build-and-tests", approvalEnabled: false, _ =>
            {
                var code = RunSelfCommand(studioArgs.ToArray(), "Studio build and tests");
                if (code != 0) throw new ReleaseException("Studio build and tests failed");
                return 0;
            });

            var hostProject = Path.Combine(Root, "src/FoundationHost/TiaMcpServer.LegacyHost.csproj");
            var publish = Path.Combine(logs, "foundation-host");
            var publishArgs = new List<string> { "publish", hostProject, "-c", "Release", "-o", publish, "-v:q", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false", $"-p:Version={release}", $"-p:FileVersion={release}.0" };
            AddNugetConfig(publishArgs, nuget);
            ProcessRunner.RequireSuccess(RunLoggedProcess(dotnet, publishArgs, logs, "host.log", nuget), "Foundation host publication failed");

            var releaseRows = new List<object>();
            foreach (var key in new[] { "14sp1", "15.1", "16", "17", "18", "19" })
            {
                var runtime = Path.Combine(Root, $"runtime/v{key}");
                var worker = Path.Combine(runtime, "worker");
                Directory.CreateDirectory(worker);
                foreach (var file in Directory.EnumerateFiles(publish).Where(file => new[] { ".exe", ".dll", ".config", ".json" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
                    File.Copy(file, Path.Combine(runtime, Path.GetFileName(file)), true);
                File.WriteAllText(Path.Combine(runtime, "release-key.txt"), key, new System.Text.UTF8Encoding(false));
                var workerBuild = Path.Combine(Root, $"src/Worker/bin/{key}/Release/net48");
                if (!Directory.Exists(workerBuild)) throw new ReleaseException($"Worker build output missing: {workerBuild}");
                foreach (var file in Directory.EnumerateFiles(workerBuild).Where(file => new[] { ".exe", ".dll", ".config" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
                    File.Copy(file, Path.Combine(worker, Path.GetFileName(file)), true);
                if (Directory.EnumerateFiles(worker, "TiaMcp.Adapter.*.dll").Count() != 1) throw new ReleaseException($"Exactly one matching adapter is required: {key}");

                var catalog = WithIsolatedHost(runTemp, $"foundation-catalog-{key}", approvalEnabled: false, _ =>
                {
                    var result = ProcessRunner.Run(Path.Combine(runtime, "TiaMcp.FoundationHost.exe"), ["--catalog"], Root);
                    WriteLog(Path.Combine(logs, $"tools-{key}.json"), new CommandResult(0, result.StandardOutput, ""));
                    ProcessRunner.RequireSuccess(result, $"Foundation catalog failed: {key}");
                    return JsonDocument.Parse(result.StandardOutput);
                });
                using (catalog)
                {
                    var toolCount = catalog.RootElement.GetProperty("tools").GetArrayLength();
                    releaseRows.Add(new { releaseKey = key, profile = "plc-foundation", toolCount, nativeAcceptance = "NOT RUN" });
                }
            }

            var studioBuild = Path.Combine(Root, "src/Studio/Gui/bin/Release/net10.0-windows");
            if (!Directory.Exists(studioBuild)) throw new ReleaseException("Studio GUI build output missing: " + studioBuild);
            var studioOutput = Path.Combine(Root, "runtime/studio");
            CopyDirectoryContents(studioBuild, studioOutput);
            var studioApphost = Path.Combine(logs, "studio-apphost");
            var apphostArgs = new List<string> { "publish", "src/Studio/Gui/TiaOpenness.Gui.csproj", "-c", "Release", "--no-build", "-o", studioApphost, "-v:q", "-p:TiaSharedAdapterPaths=false" };
            AddNugetConfig(apphostArgs, nuget);
            ProcessRunner.RequireSuccess(RunLoggedProcess(dotnet, apphostArgs, logs, "studio-apphost.log", nuget), "Studio apphost publication failed");
            File.Copy(Path.Combine(studioApphost, "TiaOpenness.exe"), Path.Combine(studioOutput, "TiaOpenness.exe"), true);

            WithIsolatedHost(runTemp, "foundation-bundled-runtime", approvalEnabled: false, _ =>
            {
                AssertBundledRuntime(Path.Combine(Root, "runtime/v14sp1/TiaMcp.FoundationHost.exe"), ["--catalog"], "foundation-host", logs);
                return 0;
            });
            WithIsolatedHost(runTemp, "studio-bundled-runtime", approvalEnabled: false, _ =>
            {
                AssertBundledRuntime(Path.Combine(studioOutput, "TiaOpenness.exe"), ["--network", "127.0.0.1", "not-a-port", "S-1-5-18"], "studio", logs);
                return 0;
            });
            if (Directory.EnumerateFiles(Path.Combine(Root, "runtime"), "Siemens.Engineering*.dll", SearchOption.AllDirectories).Any())
                throw new ReleaseException("Siemens PublicAPI redistribution is forbidden");

            var validationNode = new JsonObject
            {
                ["nativeTiaExecuted"] = false, ["studioFunctionalTestsExecuted"] = test && plan.Includes("gui-tests"),
                ["configurationFunctionalTestsExecuted"] = test, ["foundationTransportExecuted"] = false,
                ["toolUsageCoverageExecuted"] = false, ["approvalSafetyExecuted"] = false
            };
            if (test)
            {
                var suiteResults = Path.Combine(logs, "dotnet-suites");
                Directory.CreateDirectory(suiteResults);
                var suites = new[] { "foundation-api", "prompt-registration", "software-read", "special-export-shape", "device-add", "hardware-catalog", "diagnostic-membership" };
                var catalogPath = Path.Combine(Root, "tests/test-suites.json");
                using var suiteCatalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
                foreach (var suite in suites)
                {
                    if (!suiteCatalog.RootElement.TryGetProperty(suite, out var spec)) throw new ReleaseException("Suite is missing from tests/test-suites.json: " + suite);
                    var project = spec.GetProperty("project").GetString() ?? throw new ReleaseException("Suite project missing: " + suite);
                    var buildArgs = new List<string> { "build", project, "-c", "Release", "-v:q" };
                    foreach (var argument in spec.GetProperty("arguments").EnumerateArray()) buildArgs.Add(argument.GetString()!);
                    if (spec.TryGetProperty("publicApiRoot", out var usesApi) && usesApi.ValueKind == JsonValueKind.True)
                        buildArgs.Add("-p:TiaPublicApiRoot=" + api);
                    ProcessRunner.RequireSuccess(RunLoggedProcess(dotnet, buildArgs, logs, suite + "-build.log", nuget), "Suite build failed: " + suite);
                }
                var suiteJobs = suites.Select(suite => (suite, (Func<PipelineResult>)(() =>
                {
                    var host = CreateIsolatedHost(runTemp, "dotnet-suite-" + suite, approvalEnabled: false, apiRoot: api);
                    try
                    {
                        var suiteArgs = new List<string> { "scripts/checks/Test-DotnetSuites.py", "--suite", suite, "--dotnet", dotnet,
                            "--no-restore", "--dotnet-arg=--no-build", "--results-directory", Path.Combine(suiteResults, suite) };
                        if (!string.IsNullOrWhiteSpace(nuget)) suiteArgs.Add("--dotnet-arg=-p:RestoreConfigFile=" + Path.GetFullPath(nuget));
                        var result = RunLoggedProcess(python, suiteArgs, logs, suite + "-tests.log", null, host.Environment);
                        ProcessRunner.RequireSuccess(result, "TRX suite gate failed: " + suite);
                        try { Directory.Delete(host.Root, true); }
                        catch (Exception ex) { Console.Error.WriteLine("WARNING release check data still in use (kept): " + host.Root + " (" + ex.Message + ")"); }
                        return new PipelineResult(suite, 0, result.StandardOutput, result.StandardError);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Retained failed release check data ({suite}): {host.Root}");
                        return new PipelineResult(suite, 1, "", ex.ToString());
                    }
                }))).ToArray();
                ParallelPipeline.Run(suiteJobs, Path.Combine(logs, "dotnet-suite-pipeline"), GetMaxParallelism(options));
                foreach (var suite in suites)
                {
                    using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(suiteResults, suite, suite + ".json")));
                    var row = summary.RootElement;
                    validationNode["dotnetSuites"] ??= new JsonObject();
                    ((JsonObject)validationNode["dotnetSuites"]!)[suite] = new JsonObject
                    {
                        ["passed"] = row.GetProperty("passed").GetInt32(), ["failed"] = row.GetProperty("failed").GetInt32(),
                        ["skipped"] = row.GetProperty("skipped").GetInt32(), ["total"] = row.GetProperty("total").GetInt32(),
                        ["minimumPassed"] = row.GetProperty("minimumPassed").GetInt32(), ["maximumSkipped"] = row.GetProperty("maximumSkipped").GetInt32()
                    };
                }
                if (plan.Includes("foundation-transport"))
                {
                    var fixture = "tests/Engine/TiaMcpServer.TransportFixture/TransportFixture.csproj";
                    ProcessRunner.RequireSuccess(RunLoggedProcess(dotnet, ["build", fixture, "-c", "Release", "-v:q", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:NuGetAudit=false"], logs, "fixture-build.log", nuget), "Transport fixture build failed");
                    WithIsolatedHost(runTemp, "foundation-transport", approvalEnabled: false, _ =>
                    {
                        var result = RunLoggedProcess(python, ["scripts/checks/Test-FoundationTransport.py", "--fixture", "tests/Engine/TiaMcpServer.TransportFixture/bin/Release/net10.0/TransportFixture.exe", "--output", Path.Combine(logs, "transport")], logs, "transport.log", null);
                        ProcessRunner.RequireSuccess(result, "Foundation transport test failed");
                        return 0;
                    });
                    validationNode["foundationTransportExecuted"] = true;
                }
            }

            if (File.Exists(pendingPath)) File.Delete(pendingPath);
            var adapterEvidence = Directory.Exists(Path.Combine(logs, "adapters"))
                ? Directory.EnumerateFiles(Path.Combine(logs, "adapters"), "coverage-*.json").ToArray() : [];
            var evidencePaths = new List<string>(adapterEvidence);
            evidencePaths.AddRange(Directory.EnumerateFiles(logs, "tools-*.json"));
            evidencePaths.AddRange(releaseCheckEvidence);
            if (test && plan.Includes("foundation-transport")) evidencePaths.Add(Path.Combine(logs, "transport/tool-usage.json"));

            if (!options.Has("CompleteOnly") && plan.Includes("foundation-approval"))
            {
                var approvalOutput = Path.Combine(logs, "foundation-approval-default-" + Guid.NewGuid().ToString("N"));
                WithIsolatedHost(runTemp, "foundation-default-approval", approvalEnabled: true, _ =>
                {
                    var result = RunLoggedProcess(python, ["scripts/checks/Test-ReleaseApprovalGate.py", "--product", "foundation", "--runtime-root", Path.Combine(Root, "runtime"), "--public-api", api, "--temp-root", runTemp, "--output", approvalOutput], logs, "foundation-approval-default.log", null);
                    ProcessRunner.RequireSuccess(result, "Foundation default-approval gate failed");
                    return 0;
                });
                using var approval = JsonDocument.Parse(File.ReadAllText(Path.Combine(approvalOutput, "result.json")));
                var approvalRoot = approval.RootElement;
                var unsupported = approvalRoot.GetProperty("callToolUnsupportedReleases").EnumerateArray().Select(item => item.GetString() ?? "").Order(StringComparer.Ordinal).ToArray();
                if (GetJsonString(approvalRoot, "status") != "passed" || GetJsonInt(approvalRoot, "checksPassed") != 18 || GetJsonInt(approvalRoot, "checksExpected") != 18 ||
                    GetJsonBool(approvalRoot, "workbenchConnected") || GetJsonBool(approvalRoot, "tiaConnected") || GetJsonString(approvalRoot, "approvalSettings") != "explicit enabled=true; timeoutSeconds=120" ||
                    !unsupported.SequenceEqual(new[] { "14sp1", "15.1", "16", "17", "18", "19" }, StringComparer.Ordinal))
                    throw new ReleaseException("Foundation default-approval check count, state, or roster evidence is invalid");
                foreach (var row in releaseRows)
                {
                    var key = (string)row.GetType().GetProperty("releaseKey")!.GetValue(row)!;
                    var releaseApproval = approvalRoot.GetProperty("releases").GetProperty(key);
                    if (GetJsonInt(releaseApproval, "checksPassed") != 3 || GetJsonString(releaseApproval, "directWrite") != "refused-before-dispatch" ||
                        GetJsonString(releaseApproval, "read") != "succeeded" || GetJsonString(releaseApproval, "CallTool") != "not-advertised-by-Foundation-V4")
                        throw new ReleaseException("Foundation default-approval result is incomplete: " + key);
                }
                validationNode["approvalSafetyExecuted"] = true;
                validationNode["foundationApprovalSafetyChecksPassed"] = 18;
                validationNode["foundationCallToolUnsupportedReleases"] = JsonSerializer.SerializeToNode(unsupported);
                validationNode["foundationApprovalResultPath"] = Path.GetRelativePath(Root, approvalOutput).Replace('\\', '/') + "/result.json";
                validationNode["approvalSafetyScriptSha256"] = ReleaseRecords.HashFile(Path.Combine(Root, "scripts/checks/Test-ReleaseApprovalGate.py"));
                foreach (var index in Enumerable.Range(0, releaseRows.Count))
                {
                    var row = releaseRows[index];
                    var key = (string)row.GetType().GetProperty("releaseKey")!.GetValue(row)!;
                    releaseRows[index] = new
                    {
                        releaseKey = key, profile = "plc-foundation", toolCount = (int)row.GetType().GetProperty("toolCount")!.GetValue(row)!, nativeAcceptance = "NOT RUN",
                        approvalSafetyChecksPassed = 3, approvalDefaultEnabled = true,
                        approvalSafety = new { status = "passed", checksPassed = 3, defaultEnabled = true, directWriteRefusedBeforeDispatch = true, readSucceeded = true, callTool = "not-advertised-by-Foundation-V4" }
                    };
                }
                evidencePaths.Add(Path.Combine(approvalOutput, "result.json"));
            }

            var preparedFiles = ReleaseRecords.GetRuntimeFiles(Root, prepared: true);
            var evidence = evidencePaths.Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => new { path = Path.GetRelativePath(Root, path).Replace('\\', '/'), sha256 = ReleaseRecords.HashFile(path) }).ToArray();
            var prepared = new
            {
                tier = plan.Tier, checkPlan = plan,
                release, fileVersion = release + ".0", publicApiRoot = api, test,
                releases = releaseRows, validation = validationNode, sourceFiles = sources,
                validationInputs, files = preparedFiles, evidence
            };
            using var preparedJson = JsonDocument.Parse(JsonSerializer.Serialize(prepared, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            ReleaseRecords.AssertMultiVersionPreparation(Root, preparedJson.RootElement, release, sources, validationInputs, preparedFiles, api);
            WriteJson(pendingPath, prepared);
            if (options.Has("PrepareOnly"))
            {
                Console.WriteLine("Multi-version preparation passed; build/reuse the full engines, then run -CompleteOnly with the same -Test setting.");
                return 0;
            }
            if (!options.Has("SkipFullEngines"))
            {
                var v20 = Path.Combine(api, "TIA_V20_PublicAPI/V20");
                var v21 = Path.Combine(api, "TIA_V21_PublicAPI/V21/net48");
                var engineArgs = new List<string> { "build-release", "-V20ReferenceRoot", v20, "-V21ReferenceRoot", v21, "-Dotnet", dotnet, "-Python", python };
                if (!string.IsNullOrWhiteSpace(nuget)) engineArgs.AddRange(["-NuGetConfig", nuget]);
                var engineCode = RunSelfCommand(engineArgs.ToArray(), "Full-engine build");
                if (engineCode != 0) throw new ReleaseException("Full-engine build failed");
            }
            pending?.Dispose();
            pending = JsonDocument.Parse(File.ReadAllText(pendingPath));
            if (JsonString(pending.RootElement, "tier") != plan.Tier) throw new ReleaseException("Prepared tier differs from completion tier.");
            validation = pending.RootElement.GetProperty("validation");
        }

        using (pending)
        {
            var engineRecordPath = Path.Combine(Root, "manifest/release-build.json");
            if (!File.Exists(engineRecordPath)) throw new ReleaseException("Full-engine build record missing; run build-release before multi-version completion");
            using var engineRecord = JsonDocument.Parse(File.ReadAllText(engineRecordPath));
            var engineReuse = ReleaseRecords.EngineReuseReason(Root, release, engineRecord.RootElement, ReleaseRecords.GetSources(Root, "engine"), plan.Tier);
            if (engineReuse.Length != 0)
                throw new ReleaseException("Full-engine inputs, binaries or audit evidence do not match: " + engineReuse + "; run build-release before multi-version completion");
            var deliveryPath = Path.Combine(Root, "manifest/delivery.json");
            if (!File.Exists(deliveryPath)) throw new ReleaseException("Delivery record missing; run prepare-delivery before multi-version completion");
            using var delivery = JsonDocument.Parse(File.ReadAllText(deliveryPath));
            if (GetJsonString(delivery.RootElement, "release") != release || GetJsonString(delivery.RootElement, "fileVersion") != release + ".0" ||
                !string.Equals(GetJsonString(delivery.RootElement, "engineBuildSha256"), ReleaseRecords.HashFile(engineRecordPath), StringComparison.OrdinalIgnoreCase))
                throw new ReleaseException("Delivery does not bind the current engine record; run prepare-delivery before multi-version completion");

            var audit = RunLoggedProcess(python, ["scripts/diagnostics/Audit-VersionTools.py", "--public-api-root", api], logs, "api-audit.log", null);
            ProcessRunner.RequireSuccess(audit, "Per-version tool/API audit failed; build full engines first");
            var validationAfter = JsonNode.Parse(validation.GetRawText())!.AsObject();
            if (test && plan.Includes("foundation-transport"))
            {
                var usage = RunLoggedProcess(python, ["scripts/diagnostics/Audit-ToolUsage.py"], logs, "tool-usage.log", null);
                ProcessRunner.RequireSuccess(usage, "All-release usage coverage failed");
                validationAfter["toolUsageCoverageExecuted"] = true;
            }
            var files = ReleaseRecords.GetRuntimeFiles(Root);
            var currentValidation = ReleaseRecords.GetValidationInputs(Root, "multi");
            if (!SameArtifacts(ReleaseRecords.ReadRows(pending!.RootElement, "validationInputs"), currentValidation)) throw new ReleaseException("Validation inputs changed during the build; refusing to record results");
            if (!SameArtifacts(ReleaseRecords.ReadRows(pending.RootElement, "sourceFiles"), ReleaseRecords.GetSources(Root, "multi"))) throw new ReleaseException("Multi-version inputs changed during validation; refusing to record results");
            if (!SameArtifacts(files, ReleaseRecords.GetRuntimeFiles(Root))) throw new ReleaseException("Runtime files changed during multi-version audits; refusing to record results");
            engineReuse = ReleaseRecords.EngineReuseReason(Root, release, engineRecord.RootElement, ReleaseRecords.GetSources(Root, "engine"), plan.Tier);
            if (engineReuse.Length != 0) throw new ReleaseException("Full-engine inputs or evidence changed during multi-version audits: " + engineReuse);
            var record = new
            {
                tier = plan.Tier, checkPlan = plan,
                createdAt = DateTimeOffset.UtcNow.ToString("o"), release, fileVersion = release + ".0",
                releases = pending.RootElement.GetProperty("releases"),
                studioReleaseKeys = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                nativeAcceptance = "NOT RUN", validation = validationAfter, files,
                sourceFiles = sources, validationInputs
            };
            var recordPath = Path.Combine(Root, "manifest/multi-version-build.json");
            WriteJson(recordPath, record);
            var deliveryNode = JsonNode.Parse(File.ReadAllText(deliveryPath))!.AsObject();
            deliveryNode["configuratorBuildSha256"] = ReleaseRecords.HashFile(Path.Combine(Root, "manifest/configurator-build.json"));
            deliveryNode["multiVersionBuildSha256"] = ReleaseRecords.HashFile(recordPath);
            deliveryNode["releaseKeys"] = JsonSerializer.SerializeToNode(new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" });
            WriteJson(deliveryPath, deliveryNode);
            Console.WriteLine("Multi-version build record and delivery binding written.");
            return 0;
        }
    }

    private static int Publish(Options options)
    {
        var version = options.Get("Version") ?? throw new ReleaseException("-Version is required");
        var token = options.Get("Token") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";
        var publisherOptions = new PublishOptions(
            version,
            token,
            options.Get("Repository", "asckye/TIA_Portal_Openness_MCP"),
            options.Get("ApiBaseUrl", "https://api.github.com"),
            options.Has("DraftOnly"),
            options.Has("DeleteDraft"),
            int.Parse(options.Get("RetryCount", "6"), System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(options.Get("RetryDelaySeconds", "15"), System.Globalization.CultureInfo.InvariantCulture),
            options.Get("Git", "git"));
        return ReleasePublisher.PublishAsync(Root, publisherOptions).GetAwaiter().GetResult();
    }

    private static CommandResult RunLoggedProcess(string executable, IEnumerable<string> arguments, string logDirectory, string logName, string? nugetConfig,
        IDictionary<string, string?>? additionalEnvironment = null)
    {
        var args = arguments.ToList();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (args.FirstOrDefault() is "build" or "restore" or "publish")
            {
                if (!string.IsNullOrWhiteSpace(nugetConfig)) args.Add("-p:RestoreConfigFile=" + Path.GetFullPath(nugetConfig));
                args.Add("-p:NuGetAudit=false");
                args.Add("-p:UseSharedCompilation=false");
                args.Add("-m:1");
                args.Add("-nodeReuse:false");
            }
        }
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["UseSharedCompilation"] = "false",
            ["NuGetAudit"] = "false"
        };
        if (additionalEnvironment is not null)
            foreach (var (key, value) in additionalEnvironment) environment[key] = value;
        var result = ProcessRunner.Run(executable, args, Root, environment);
        WriteLog(Path.Combine(logDirectory, logName), result);
        return result;
    }

    private static (string Root, Dictionary<string, string?> Environment) CreateIsolatedHost(string runTemp, string name, bool approvalEnabled, string apiRoot)
    {
        var hostRoot = Path.Combine(runTemp, Guid.NewGuid().ToString("N")[..8]);
        foreach (var directory in new[] { "config", "temp", "local-app-data", "app-data" }) Directory.CreateDirectory(Path.Combine(hostRoot, directory));
        File.WriteAllText(Path.Combine(hostRoot, "check.txt"), name, new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(hostRoot, "config/approval.settings"), $"enabled={approvalEnabled.ToString().ToLowerInvariant()}\ntimeoutSeconds=120\n", new System.Text.UTF8Encoding(false));
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TIA_MCP_DATA_DIRECTORY"] = hostRoot,
            ["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(hostRoot, "diagnostics"),
            ["TIA_MCP_RELEASE_TEMP_ROOT"] = runTemp,
            ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = apiRoot,
            ["LOCALAPPDATA"] = Path.Combine(hostRoot, "local-app-data"),
            ["APPDATA"] = Path.Combine(hostRoot, "app-data"),
            ["TEMP"] = Path.Combine(hostRoot, "temp"),
            ["TMP"] = Path.Combine(hostRoot, "temp")
        };
        return (hostRoot, environment);
    }

    private static int GetMaxParallelism(Options options)
    {
        var configured = options.Get("MaxParallelism");
        if (configured is null) return Math.Max(1, Environment.ProcessorCount);
        if (!int.TryParse(configured, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1)
            throw new ReleaseException("-MaxParallelism must be a positive integer.", 64);
        return value;
    }

    private static T WithIsolatedHost<T>(string runTemp, string name, bool approvalEnabled, Func<string, T> action)
    {
        var hostRoot = Path.Combine(runTemp, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(hostRoot, "config"));
        Directory.CreateDirectory(Path.Combine(hostRoot, "temp"));
        Directory.CreateDirectory(Path.Combine(hostRoot, "local-app-data"));
        Directory.CreateDirectory(Path.Combine(hostRoot, "app-data"));
        File.WriteAllText(Path.Combine(hostRoot, "check.txt"), name, new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(hostRoot, "config/approval.settings"), $"enabled={approvalEnabled.ToString().ToLowerInvariant()}\ntimeoutSeconds=120\n", new System.Text.UTF8Encoding(false));
        var paths = new Dictionary<string, string?>
        {
            ["TIA_MCP_DATA_DIRECTORY"] = hostRoot,
            ["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(hostRoot, "diagnostics"),
            ["LOCALAPPDATA"] = Path.Combine(hostRoot, "local-app-data"),
            ["APPDATA"] = Path.Combine(hostRoot, "app-data"),
            ["TEMP"] = Path.Combine(hostRoot, "temp"),
            ["TMP"] = Path.Combine(hostRoot, "temp")
        };
        var previous = paths.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var completed = false;
        try
        {
            foreach (var (key, value) in paths) Environment.SetEnvironmentVariable(key, value);
            var result = action(hostRoot);
            completed = true;
            return result;
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            if (completed)
            {
                try { Directory.Delete(hostRoot, true); }
                catch (Exception ex) { Console.Error.WriteLine("WARNING release check data still in use (kept): " + hostRoot + " (" + ex.Message + ")"); }
            }
            else Console.Error.WriteLine($"Retained failed release check data ({name}): {hostRoot}");
        }
    }

    private static void AssertBundledRuntime(string executable, string[] arguments, string name, string logs)
    {
        var trace = Path.Combine(logs, "corehost-" + name + ".txt");
        File.Delete(trace);
        var env = new Dictionary<string, string?>
        {
            ["COREHOST_TRACE"] = "1", ["COREHOST_TRACEFILE"] = trace,
            ["TIA_OPENNESS_NETWORK_NO_DIALOG"] = "1"
        };
        var result = ProcessRunner.Run(executable, arguments, Root, env);
        WriteLog(Path.Combine(logs, "corehost-" + name + ".out.log"), new CommandResult(result.ExitCode, result.StandardOutput, ""));
        WriteLog(Path.Combine(logs, "corehost-" + name + ".err.log"), new CommandResult(result.ExitCode, "", result.StandardError));
        var fxr = Path.GetFullPath(Path.Combine(Root, "runtime/dotnet/host/fxr")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!File.Exists(trace) || !File.ReadAllText(trace).Contains("Resolved fxr [" + fxr, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseException($"{name} does not load the bundled .NET runtime; see {trace}");
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectoryContents(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string GetJsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int GetJsonInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static bool GetJsonBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool SameArtifacts(IReadOnlyList<ReleaseArtifact> left, IReadOnlyList<ReleaseArtifact> right)
    {
        var first = left.OrderBy(row => row.Path, StringComparer.Ordinal).ToArray();
        var second = right.OrderBy(row => row.Path, StringComparer.Ordinal).ToArray();
        return first.Length == second.Length && first.Zip(second).All(pair => pair.First.Path == pair.Second.Path && pair.First.Sha256 == pair.Second.Sha256);
    }

    private static int RunChild(string exe, string[] args, string cwd, string description)
    {
        var result = ProcessRunner.Run(exe, AddOfflineNuGetConfig(exe, args), cwd);
        Console.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
        if (result.ExitCode != 0) throw new ReleaseException($"{description} failed with exit code {result.ExitCode}");
        return 0;
    }

    private static int RunPython(params string[] args) => RunChild(Py, args, Root, string.Join(' ', args));

    private static string[] AddOfflineNuGetConfig(string executable, string[] arguments)
    {
        var config = Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arguments.Length == 0 || arguments[0] != "test" || string.IsNullOrWhiteSpace(config)) return arguments;
        return [.. arguments, "-p:RestoreConfigFile=" + Path.GetFullPath(config), "-p:NuGetAudit=false"];
    }

    private static int RunSelfCommand(string[] args, string description)
    {
        var command = new List<string> { typeof(ReleaseCommands).Assembly.Location };
        command.AddRange(args);
        var result = ProcessRunner.Run(Dotnet, command, Root);
        Console.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
        if (result.ExitCode != 0) Console.Error.WriteLine($"{description} exited {result.ExitCode}");
        return result.ExitCode;
    }

    private static CommandResult RunLogged(string executable, IEnumerable<string> args, string relativeLog)
    {
        var result = ProcessRunner.Run(executable, args, Root);
        WriteLog(Path.Combine(Root, relativeLog), result);
        return result;
    }

    private static void RunDotnetLogged(Options options, string logDirectory, List<string> args, string logName, bool? sharedAdapterPaths = null)
    {
        if (args.Count > 0 && args[0] is "build" or "restore")
        {
            args.Add("-m:1");
            args.Add("-nodeReuse:false");
            args.Add("-p:UseSharedCompilation=false");
        }
        if (sharedAdapterPaths.HasValue) args.Add($"-p:TiaSharedAdapterPaths={sharedAdapterPaths.Value.ToString().ToLowerInvariant()}");
        var config = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG") ?? Environment.GetEnvironmentVariable("RestoreConfigFile");
        if (!string.IsNullOrWhiteSpace(config)) args.Add("-p:RestoreConfigFile=" + Path.GetFullPath(config));
        var environment = new Dictionary<string, string?> { ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false", ["NuGetAudit"] = "false" };
        var result = ProcessRunner.Run(options.Get("Dotnet", Dotnet), args, Root, environment);
        WriteLog(Path.Combine(logDirectory, logName), result);
        ProcessRunner.RequireSuccess(result, $"Studio build/test failed; see {logDirectory}/{logName}");
    }

    private static void AddNugetConfig(List<string> args, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) args.AddRange(["--configfile", Path.GetFullPath(path)]);
    }

    private static void RequireFile(string root, string name, string message)
    {
        if (!File.Exists(Path.Combine(root, name))) throw new ReleaseException(message);
    }

    private static string ReleaseRecordsProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool IsBuildSource(string path) => Path.GetExtension(path) is ".cs" or ".xaml" or ".csproj";

    private static bool IsBuildOutput(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part is "obj" or "obj-v20" or "bin" or "bin-v20");

    private static void WriteLog(string path, CommandResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, result.StandardOutput + result.StandardError, new System.Text.UTF8Encoding(false));
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(path, json.Replace("\r\n", "\n", StringComparison.Ordinal), new System.Text.UTF8Encoding(false));
    }

    private static void EnsureWindows(string command)
    {
        if (!OperatingSystem.IsWindows()) throw new ReleaseException($"{command} is Windows-only and cannot run on this operating system.");
    }

    private static string FindRoot(string start)
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TiaPortalOpenness.slnx")) && Directory.Exists(Path.Combine(current.FullName, "scripts"))) return current.FullName;
            current = current.Parent;
        }
        throw new ReleaseException("Could not find repository root (TiaPortalOpenness.slnx).");
    }
}
