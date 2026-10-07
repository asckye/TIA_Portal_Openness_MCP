using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    private static readonly IReadOnlyDictionary<string, (int Count, bool Exact)> BuildReleaseCountRules =
        new Dictionary<string, (int, bool)>(StringComparer.Ordinal)
        {
            ["nativeMcpSafety"] = (8, false), ["diagnosticBehavior"] = (36, false), ["diagnosticRejection"] = (5, false),
            ["nativeJournalReader"] = (3, false), ["adapterJournal"] = (8, false), ["processLeases"] = (2, false),
            ["workerFaults"] = (25, false), ["workerProtocol"] = (58, false), ["approvalSafety"] = (4, true),
            ["softwareLookup"] = (45, false), ["engineeringApiV20"] = (2840, false), ["engineeringApiV21"] = (3126, false),
            ["v21Ecosystem"] = (75, false), ["globalScriptV21"] = (8, true), ["graphicSelection"] = (8, false),
            ["runtimeSettingsV20"] = (8, false), ["runtimeSettingsV21"] = (9, false), ["ecosystem"] = (31, false)
        };

    private static int BuildReleasePipeline(Options options)
    {
        if (options.Has("SelfTest"))
            return RunChild(Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", "--filter", "Category=Pipeline"], Root, "Parallel pipeline tests");

        EnsureWindows("build-release");
        var v20Api = RequiredDirectory(options.Get("V20ReferenceRoot"), "-V20ReferenceRoot");
        var v21Api = RequiredDirectory(options.Get("V21ReferenceRoot"), "-V21ReferenceRoot");
        var dotnet = options.Get("Dotnet", Dotnet);
        var python = options.Get("Python", Py);
        var nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("TIA_MCP_OFFLINE_NUGET_CONFIG") ?? Environment.GetEnvironmentVariable("RestoreConfigFile");
        if (!options.Has("NoRestore") && string.IsNullOrWhiteSpace(nuget))
            throw new ReleaseException("build-release requires an offline NuGet config; pass -NuGetConfig or set TIA_MCP_OFFLINE_NUGET_CONFIG");
        if (!string.IsNullOrWhiteSpace(nuget))
        {
            nuget = Path.GetFullPath(nuget);
            var error = ReleaseValidation.OfflineNuGetError(nuget);
            if (error is not null) throw new ReleaseException(error);
        }

        var versionDoc = XDocument.Load(Path.Combine(Root, "Version.props"));
        var release = (string?)versionDoc.Root?.Element("PropertyGroup")?.Element("TiaMcpRelease") ?? "";
        if (!Regex.IsMatch(release, "^\\d+\\.\\d+\\.\\d+$")) throw new ReleaseException("Public release version must be X.Y.Z without fork or feature suffixes");
        var fileVersion = release + ".0";
        var pipelineMajor = int.Parse(options.Get("PipelineMajor", "0"), System.Globalization.CultureInfo.InvariantCulture);
        if (pipelineMajor is not (0 or 20 or 21)) throw new ReleaseException("-PipelineMajor must be 0, 20 or 21");
        if (pipelineMajor == 0) ReleaseRecords.AssertRuntimePreparation(Root);
        var releaseDate = options.Get("ReleaseDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        if (!DateTime.TryParseExact(releaseDate, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new ReleaseException("-ReleaseDate must be a valid yyyyMMdd date");

        var package = $"TIA_MCP_Delivery_v{release}_{releaseDate}";
        var sharedOut = Path.Combine(Root, "bin-build/releases", "v" + release);
        var outputDirectory = pipelineMajor == 0 ? sharedOut : Path.Combine(sharedOut, "v" + pipelineMajor);
        Directory.CreateDirectory(outputDirectory);
        var runTempRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_MCP_RELEASE_TEMP_ROOT") ?? Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8]));
        var repoPrefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (runTempRoot.StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Release temp root must be outside the repository: " + runTempRoot);
        var runTemp = Path.Combine(runTempRoot, "br");
        Directory.CreateDirectory(runTemp);
        var apiRoot = Directory.GetParent(Directory.GetParent(v20Api)!.FullName)!.FullName;
        var cliHome = Path.Combine(outputDirectory, "dotnet-home");
        Directory.CreateDirectory(cliHome);

        var sourceFiles = pipelineMajor == 0 ? ReleaseRecords.GetSources(Root, "engine") : [];
        var validationInputs = pipelineMajor == 0 ? ReleaseRecords.GetValidationInputs(Root, "engine") : [];
        var commonPath = Path.Combine(sharedOut, "common.json");
        JsonElement common;
        if (pipelineMajor == 0)
        {
            common = RunBuildReleaseCommon(options, dotnet, python, nuget, v20Api, apiRoot, sharedOut, runTemp, cliHome);
        }
        else
        {
            if (!File.Exists(commonPath)) throw new ReleaseException("Common build evidence missing; run build-release without -PipelineMajor first");
            using var commonDoc = JsonDocument.Parse(File.ReadAllText(commonPath));
            common = commonDoc.RootElement.Clone();
        }

        var majors = pipelineMajor == 0 ? new[] { 20, 21 } : new[] { pipelineMajor };
        var jobs = majors.Select(major => ($"release-v{major}", (Func<PipelineResult>)(() =>
        {
            var majorOut = Path.Combine(sharedOut, "v" + major);
            var majorTemp = Path.Combine(runTemp, "v" + major);
            var majorCliHome = Path.Combine(outputDirectory, "dotnet-home-v" + major);
            Directory.CreateDirectory(majorOut);
            Directory.CreateDirectory(majorTemp);
            Directory.CreateDirectory(majorCliHome);
            try
            {
                var record = RunBuildReleaseMajor(options, dotnet, python, nuget, major, major == 20 ? v20Api : v21Api,
                    v21Api, apiRoot, release, fileVersion, common, majorOut, majorTemp, majorCliHome);
                WriteJson(Path.Combine(majorOut, "pipeline-result.json"), record);
                return new PipelineResult("release-v" + major, 0, $"V{major} pipeline passed; logs: {majorOut}{Environment.NewLine}", "");
            }
            catch (Exception ex)
            {
                return new PipelineResult("release-v" + major, 1, "", ex.ToString());
            }
        }))).ToArray();
        ParallelPipeline.Run(jobs, Path.Combine(outputDirectory, "pipeline-logs"), GetMaxParallelism(options));

        if (pipelineMajor != 0)
        {
            Console.WriteLine($"V{pipelineMajor} pipeline passed; logs: {outputDirectory}");
            return 0;
        }

        var runtimeRecords = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var major in new[] { 20, 21 })
        {
            var path = Path.Combine(sharedOut, "v" + major, "pipeline-result.json");
            if (!File.Exists(path)) throw new ReleaseException($"V{major} pipeline returned no validation record");
            using var result = JsonDocument.Parse(File.ReadAllText(path));
            runtimeRecords.Add("V" + major, result.RootElement.Clone());
        }

        RunShippedRuntimeChecks(dotnet, v21Api, apiRoot, package, sharedOut, runTemp, cliHome, ReleasePlan(options));
        RefreshPackageManifest(release, fileVersion, releaseDate, package);
        if (!SameArtifacts(sourceFiles, ReleaseRecords.GetSources(Root, "engine"))) throw new ReleaseException("Build inputs changed while validation was running; refusing to record results");
        if (!SameArtifacts(validationInputs, ReleaseRecords.GetValidationInputs(Root, "engine"))) throw new ReleaseException("Validation inputs changed during the build; refusing to record results");

        // Same roots as ReleaseRecords' engine inventory check (the PowerShell Build-Release recorded these four only);
        // Foundation, Studio and bundled .NET runtimes belong to the multi-version record.
        var runtimeFiles = new[] { "runtime/v20", "runtime/v21", "runtime/tools", "runtime/verification" }
            .Select(relative => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".exe" or ".dll" or ".config" ||
                Path.GetFileName(path) is "NativeCallWeaver.deps.json" or "NativeCallWeaver.runtimeconfig.json")
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { path = Path.GetRelativePath(Root, path).Replace('\\', '/'), length = new FileInfo(path).Length, sha256 = ReleaseRecords.HashFile(path) }).ToArray();
        var artifacts = new List<object>();
        foreach (var major in new[] { 20, 21 })
            foreach (var name in new[] { $"native-call-coverage-v{major}.json", $"tool-usage-v{major}.json" })
            {
                var path = Path.Combine(sharedOut, "v" + major, name);
                if (!File.Exists(path)) throw new ReleaseException("Validation artifact missing: " + path);
                artifacts.Add(new { path = Path.GetRelativePath(Root, path).Replace('\\', '/'), sha256 = ReleaseRecords.HashFile(path) });
            }
        var record = new
        {
            tier = ReleasePlan(options).Tier, checkPlan = ReleasePlan(options),
            release, releaseDate, fileVersion, package, generatedAt = DateTimeOffset.UtcNow.ToString("o"),
            validation = new
            {
                offlinePassed = GetJsonInt(common, "offlinePassed"), offlineV20Passed = GetJsonInt(common, "offlineV20Passed"),
                writeGuardPassed = GetJsonInt(common, "writeGuardChecksPassed"),
                crashEvidencePassed = GetJsonInt(common, "crashEvidenceChecksPassed"),
                versionPolicySdkPassed = GetJsonInt(common, "versionPolicySdkPassed"),
                updaterPassed = GetJsonInt(common, "updaterPassed"), runtimes = runtimeRecords
            },
            runtimeFiles, sourceFiles, validationInputs, validationArtifacts = artifacts
        };
        WriteJson(Path.Combine(Root, "manifest/release-build.json"), record);
        var deliveryCode = RunSelfCommand(["prepare-delivery", "-Release", release, "-ReleaseDate", releaseDate], "Delivery preparation");
        if (deliveryCode != 0) return deliveryCode;
        Console.WriteLine($"Built and checked both runtimes: {fileVersion}. Review and commit changes, then run scripts/build/Package-Release.py. Real TIA acceptance is separate.");
        return 0;
    }

    private static JsonElement RunBuildReleaseCommon(Options options, string dotnet, string python, string? nuget, string v20Api,
        string apiRoot, string outputDirectory, string runTemp, string cliHome)
    {
        var suiteResults = Path.Combine(outputDirectory, "dotnet-suites");
        Directory.CreateDirectory(suiteResults);
        RunBuildSpec("approval-gate-self-test", python, [Path.Combine(Root, "scripts/checks/Test-ReleaseApprovalGate.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        var lifecycle = RunBuildSpec("native-supervisor", python, [Path.Combine(Root, "scripts/checks/Test-NativeLifecycle.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        var lifecycleMatch = Regex.Match(lifecycle, "COMPLETE: (\\d+) native supervisor checks passed; live TIA tests NOT RUN");
        if (!lifecycleMatch.Success || int.Parse(lifecycleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) < 1)
            throw new ReleaseException("Native supervisor offline checks incomplete");
        var mcpSafetyText = RunBuildSpec("native-mcp-safety", python, [Path.Combine(Root, "scripts/checks/Test-NativeMcpSession.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
        var nativeMcpSafety = RequireCount("nativeMcpSafety", mcpSafetyText, "COMPLETE: (\\d+) native MCP safety checks passed");
        RestoreBuildProject(dotnet, Path.Combine(Root, "tests/Tools/TiaMcp.ShippedTools.Tests/TiaMcp.ShippedTools.Tests.csproj"), nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RestoreBuildProject(dotnet, Path.Combine(Root, "tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj"), nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunDotnetSuiteForRelease("write-guard", "write-guard", dotnet, python, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
        var writeGuardPassed = ReadSuitePassed(suiteResults, "write-guard");
        RunDotnetSuiteForRelease("crash-evidence", "crash-evidence", dotnet, python, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
        var crashEvidencePassed = ReadSuitePassed(suiteResults, "crash-evidence");
        RunBuildRaw(dotnet, ["publish", Path.Combine(Root, "src/Tools/WriteGuard/TiaMcp.WriteGuard.csproj"), "-c", "Release", "-o", Path.Combine(Root, "runtime/tools"), "--no-restore", "-p:PublishAot=false", "-p:UseAppHost=true"], Path.Combine(outputDirectory, "write-guard-publish.log"), runTemp, cliHome, apiRoot, nuget, "write-guard publish");
        RunBuildSpec("tool-usage", python, [Path.Combine(Root, "scripts/generate/Generate-ToolUsage.py"), "--check"], outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildSpec("version-catalog", python, [Path.Combine(Root, "scripts/checks/Test-VersionCatalogWiring.py")], outputDirectory, runTemp, cliHome, apiRoot);

        var offlineProject = Path.Combine(Root, "tests/Engine/TiaMcpServer.Tests/TiaMcpServer.Tests.csproj");
        RestoreBuildProject(dotnet, offlineProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunDotnetSuiteForRelease("offline", "offline", dotnet, python, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
        var offlinePassed = ReadSuitePassed(suiteResults, "offline");
        RunDotnetSuiteForRelease("offline-v20", "offline-v20", dotnet, python, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
        var offlineV20Passed = ReadSuitePassed(suiteResults, "offline-v20");
        var versionPolicyProject = Path.Combine(Root, "tests/Engine/TiaMcpServer.VersionPolicyTests/TiaMcpServer.VersionPolicyTests.csproj");
        RestoreBuildProject(dotnet, versionPolicyProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunDotnetSuiteForRelease("version-policy", "version-policy", dotnet, python, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
        var versionPolicyPassed = ReadSuitePassed(suiteResults, "version-policy");
        var updaterSuiteProject = Path.Combine(Root, "tests/Updater/TiaMcp.Updater.Tests.csproj");
        RestoreBuildProject(dotnet, updaterSuiteProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunDotnetSuiteForRelease("updater", "updater", dotnet, python, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
        var updaterPassed = ReadSuitePassed(suiteResults, "updater");
        var updaterProject = Path.Combine(Root, "src/Updater/TiaMcp.Updater.csproj");
        RestoreBuildProject(dotnet, updaterProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["build", updaterProject, "-c", "Release", "-f", "net48", "--no-restore", "-v:q"],
            Path.Combine(outputDirectory, "build-updater.log"), runTemp, cliHome, apiRoot, nuget, "Updater build");
        RequireFile(Path.Combine(Root, "bin-build/updater/TiaMcp.Updater.exe"), "The .NET Framework updater output is incomplete");
        RequireFile(Path.Combine(Root, "bin-build/updater/TiaMcp.Updater.exe.config"), "The .NET Framework updater output is incomplete");

        var harnessProject = Path.Combine(Root, "tests/Engine/TiaMcpServer.HttpTests/TiaMcpServer.HttpTests.csproj");
        RestoreBuildProject(dotnet, harnessProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["build", harnessProject, "-c", "Release", "--no-restore", "-v:q"], Path.Combine(outputDirectory, "build-harness.log"), runTemp, cliHome, apiRoot, nuget, "HTTP harness build");
        var weaverProject = Path.Combine(Root, "build-tools/native-call-weaver/NativeCallWeaver.csproj");
        RestoreBuildProject(dotnet, weaverProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["build", weaverProject, "-c", "Release", "--no-restore", "-v:q"], Path.Combine(outputDirectory, "native-weaver-build.log"), runTemp, cliHome, apiRoot, nuget, "native call weaver build");
        var weaverOutput = Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0");
        var verifierDirectory = Path.Combine(Root, "runtime/verification");
        Directory.CreateDirectory(verifierDirectory);
        foreach (var name in new[] { "NativeCallWeaver.dll", "NativeCallWeaver.deps.json", "NativeCallWeaver.runtimeconfig.json", "Mono.Cecil.dll" })
        {
            var source = Path.Combine(weaverOutput, name);
            if (!File.Exists(source)) throw new ReleaseException("Native weaver build output missing: " + source);
            File.Copy(source, Path.Combine(verifierDirectory, name), true);
        }

        var diagnosticProject = Path.Combine(Root, "tests/Engine/TiaMcpServer.DiagnosticsTests/DiagnosticsTests.csproj");
        RestoreBuildProject(dotnet, diagnosticProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["build", diagnosticProject, "-c", "Release", "--no-restore", "-v:q"], Path.Combine(outputDirectory, "native-diagnostics-build.log"), runTemp, cliHome, apiRoot, nuget, "native diagnostics fixture build");
        var diagnosticFixture = Path.Combine(Path.GetDirectoryName(diagnosticProject)!, "bin/Release/net48/DiagnosticsTests.exe");
        var diagnosticOut = Path.Combine(outputDirectory, "native-diagnostics-" + Guid.NewGuid().ToString("N"));
        RunBuildSpec("native-diagnostics-fixture", python, [Path.Combine(Root, "scripts/checks/Test-NativeDiagnostics.py"), "--fixture", diagnosticFixture,
            "--weaver", Path.Combine(weaverOutput, "NativeCallWeaver.dll"), "--output", diagnosticOut], outputDirectory, runTemp, cliHome, apiRoot);
        using var diagnosticsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(diagnosticOut, "result.json")));
        var diagnostics = diagnosticsDoc.RootElement.Clone();
        CheckReleaseCount("diagnosticBehavior", GetJsonInt(diagnostics, "behaviorChecks"), "Native diagnostic fixture behavior checks incomplete");
        CheckReleaseCount("diagnosticRejection", GetJsonInt(diagnostics, "rejectionChecks"), "Native diagnostic fixture rejection checks incomplete");
        if (GetJsonBool(diagnostics, "nativeTiaExecuted")) throw new ReleaseException("Native diagnostic fixture gate ran live TIA tests");
        var common = new { diagnosticTests = diagnostics, crashEvidenceChecksPassed = crashEvidencePassed, writeGuardChecksPassed = writeGuardPassed, updaterPassed,
            nativeMcpSafetyChecksPassed = nativeMcpSafety, nativeSupervisorChecksPassed = int.Parse(lifecycleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            offlinePassed, offlineV20Passed, versionPolicySdkPassed = versionPolicyPassed };
        WriteJson(Path.Combine(outputDirectory, "common.json"), common);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(common, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return result.RootElement.Clone();
    }

    private static object RunBuildReleaseMajor(Options options, string dotnet, string python, string? nuget, int major, string api, string v21Api,
        string apiRoot, string release, string fileVersion, JsonElement common, string outputDirectory, string runTemp, string cliHome)
    {
        var plan = ReleasePlan(options);
        var engineSource = Path.Combine(Root, "src/Engine");
        var nativeProject = Path.Combine(Root, $"tests/Engine/TiaMcpServer.NativeTests/V{major}/NativeTests.V{major}.csproj");
        var project = Path.Combine(engineSource, $"TiaMcpServer.V{major}.csproj");
        var harness = Path.Combine(Root, "tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe");
        var weaver = Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll");
        var packagedWeaver = Path.Combine(Root, "runtime/verification/NativeCallWeaver.dll");
        RequireFile(harness, "HTTP harness has not been built");
        RequireFile(weaver, "Native call weaver has not been built");
        RequireFile(packagedWeaver, "Packaged native call weaver has not been built");
        var properties = new[] { $"-p:SiemensEngineeringDirectory={api}" };

        var mutexHash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).ToLowerInvariant()));
        var mutexName = "Local\\TIA-Release-" + Convert.ToHexString(mutexHash);
        var nativeSafety = 0;
        using (var buildMutex = new Mutex(false, mutexName))
        {
            var locked = false;
            try
            {
                try { locked = buildMutex.WaitOne(); } catch (AbandonedMutexException) { locked = true; }
                RestoreBuildProject(dotnet, nativeProject, nuget, outputDirectory, runTemp, cliHome, apiRoot, properties);
                RunBuildRaw(dotnet, ["build", nativeProject, "-c", "Release", "--no-restore", "-v:q", .. properties], Path.Combine(outputDirectory, $"native-build-v{major}.log"), runTemp, cliHome, apiRoot, nuget, $"V{major} native tests build");
                var nativeOutput = Path.Combine(Path.GetDirectoryName(nativeProject)!, "bin/Release/net48");
                var nativeExe = Path.Combine(nativeOutput, $"NativeTests.V{major}.exe");
                if (Directory.Exists(nativeOutput) && Directory.EnumerateFiles(nativeOutput, "Siemens.Engineering*.dll").Any())
                    throw new ReleaseException("Native test harness must not copy Siemens assemblies locally");
                var nativeSafetyText = RunBuildSpec("native-safety", nativeExe, ["--self-test"], outputDirectory, runTemp, cliHome, apiRoot, major);
                nativeSafety = RequireCount("native safety", nativeSafetyText, "COMPLETE: (\\d+) native harness safety checks passed; live TIA tests NOT RUN");
                RestoreBuildProject(dotnet, project, nuget, outputDirectory, runTemp, cliHome, apiRoot, properties);
                RunBuildRaw(dotnet, ["build", project, "-c", "Release", "--no-restore", "-v:q", .. properties], Path.Combine(outputDirectory, $"build-v{major}.log"), runTemp, cliHome, apiRoot, nuget, $"V{major} engine build");

                var built = Path.Combine(engineSource, major == 20 ? "bin-v20/Release/net48" : "bin/Release/net48");
                var runtime = Path.Combine(Root, $"runtime/v{major}");
                Directory.CreateDirectory(runtime);
                var payload = Directory.EnumerateFiles(built).Where(path =>
                {
                    var extension = Path.GetExtension(path);
                    var name = Path.GetFileName(path);
                    return (extension is ".exe" or ".dll" or ".config") && !name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase) &&
                        (extension != ".exe" || name == $"TiaMcp.Engine.V{major}.exe") && (extension != ".config" || name == $"TiaMcp.Engine.V{major}.exe.config");
                }).ToArray();
                var names = payload.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var required in new[] { "TiaMcp.Runtime.dll", $"TiaMcp.Adapter.{major}.dll", "TiaMcp.Adapters.Contracts.dll" })
                    if (!names.Contains(required)) throw new ReleaseException($"V{major} runtime dependency missing: {required}");
                if (payload.Count(path => Path.GetFileName(path).StartsWith("TiaMcp.Adapter.", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new ReleaseException($"V{major} must ship exactly its own shared adapter");
                foreach (var old in Directory.EnumerateFiles(runtime).Where(path => Path.GetExtension(path) is ".exe" or ".dll" or ".config"))
                    if (!names.Contains(Path.GetFileName(old))) throw new ReleaseException("Review obsolete runtime file: " + old);
                foreach (var source in payload) File.Copy(source, Path.Combine(runtime, Path.GetFileName(source)), true);
            }
            finally { if (locked) buildMutex.ReleaseMutex(); }
        }

        var exe = Path.Combine(Root, $"runtime/v{major}/TiaMcp.Engine.V{major}.exe");
        RequireFile(exe, "Built engine EXE missing");
        if (FileVersionInfo.GetVersionInfo(exe).FileVersion != fileVersion) throw new ReleaseException($"V{major} runtime version mismatch");
        if (plan.Includes("engine-functional"))
            RunBuildSpec("example-library", harness, [exe, "example-library-only"], outputDirectory, runTemp, cliHome, apiRoot, major);
        var coveragePath = Path.Combine(outputDirectory, $"native-call-coverage-v{major}.json");
        RunBuildSpec("native-coverage", dotnet, [weaver, "verify", exe, coveragePath], outputDirectory, runTemp, cliHome, apiRoot, major);
        using var coverageDoc = JsonDocument.Parse(File.ReadAllText(coveragePath));
        var coverage = coverageDoc.RootElement.Clone();
        var adapterCoveragePath = Path.Combine(outputDirectory, $"adapter-native-call-coverage-v{major}.json");
        var adapter = Path.Combine(Root, $"runtime/v{major}/TiaMcp.Adapter.{major}.dll");
        RunBuildSpec("adapter-native-coverage", dotnet, [packagedWeaver, "verify", adapter, adapterCoveragePath], outputDirectory, runTemp, cliHome, apiRoot, major);
        using var adapterCoverageDoc = JsonDocument.Parse(File.ReadAllText(adapterCoveragePath));
        var adapterCoverage = adapterCoverageDoc.RootElement.Clone();

        var nativeJitText = RunBuildSpec("native-diagnostics-jit", harness, [exe, "native-diagnostics-only", api], outputDirectory, runTemp, cliHome, apiRoot, major);
        var nativeJit = Regex.Match(nativeJitText, "COMPLETE: (\\d+) native diagnostic wrappers JIT prepared; (\\d+) open generic wrappers");
        if (!nativeJit.Success || int.Parse(nativeJit.Groups[1].Value) + int.Parse(nativeJit.Groups[2].Value) != GetJsonInt(coverage, "count")) throw new ReleaseException("Diagnostic wrapper JIT/inventory mismatch");
        var adapterJit = Regex.Match(nativeJitText, "COMPLETE: (\\d+) adapter native diagnostic wrappers JIT prepared; (\\d+) open generic wrappers");
        if (!adapterJit.Success || int.Parse(adapterJit.Groups[1].Value) + int.Parse(adapterJit.Groups[2].Value) != GetJsonInt(adapterCoverage, "count")) throw new ReleaseException("Adapter diagnostic wrapper JIT/inventory mismatch");
        var adapterJournal = RequireCount("adapter journal", nativeJitText, "COMPLETE: (\\d+) adapter integration diagnostic checks passed", "adapterJournal");
        var nativeJournal = RequireCount("native journal", nativeJitText, "COMPLETE: (\\d+) native journal reader checks passed", "nativeJournalReader");

        var processLeases = 0;
        if (plan.Includes("engine-functional"))
        {
            var leaseText = RunBuildSpec("process-leases", harness, [exe, "process-leases-only"], outputDirectory, runTemp, cliHome, apiRoot, major);
            processLeases = RequireCount("process lease", leaseText, "COMPLETE: (\\d+) process lease checks passed", "processLeases");
        }

        var workerFaults = 0;
        var workerProtocol = 0;
        if (plan.Includes("engine-worker-isolation"))
        {
            var workerText = RunBuildSpec("worker-supervisor", harness, [exe, "worker-supervisor-only"], outputDirectory, runTemp, cliHome, apiRoot, major);
            workerFaults = RequireCount("worker supervisor", workerText, "COMPLETE: (\\d+) worker supervisor checks passed; no TIA connection attempted", "workerFaults");
            var protocolText = RunBuildSpec("worker-protocol", python, [Path.Combine(Root, "scripts/checks/Test-WorkerIsolation.py"), "--exe", exe,
                "--major", major.ToString(), "--host-harness", harness, "--public-api", api], outputDirectory, runTemp, cliHome, apiRoot, major);
            workerProtocol = RequireCount("worker protocol", protocolText, "COMPLETE: (\\d+) isolated MCP checks passed; no TIA connection attempted", "workerProtocol");

        }

        JsonElement approval = default;
        if (plan.Includes("engine-approval"))
        {
            var approvalOut = Path.Combine(outputDirectory, "approval-default-v" + major + "-" + Guid.NewGuid().ToString("N"));
            var approvalTemp = Path.Combine(runTemp, "approval-v" + major);
            Directory.CreateDirectory(approvalTemp);
            var approvalText = RunBuildSpec("approval-safety", python, [Path.Combine(Root, "scripts/checks/Test-ReleaseApprovalGate.py"), "--product", "engine", "--major", major.ToString(),
                "--exe", exe, "--portal-root", api, "--host-harness", harness, "--public-api", api, "--temp-root", approvalTemp, "--output", approvalOut], outputDirectory, runTemp, cliHome, apiRoot, major, approval: true);
            using var approvalDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(approvalOut, "result.json")));
            approval = approvalDoc.RootElement.Clone();
            CheckReleaseCount("approvalSafety", GetJsonInt(approval, "checksPassed"), "Default approval checks incomplete");
            var approvalResults = approval.GetProperty("results");
            if (GetJsonString(approval, "status") != "passed" || GetJsonInt(approval, "checksExpected") != 4 || GetJsonBool(approval, "workbenchConnected") ||
                GetJsonBool(approval, "tiaConnected") || GetJsonString(approvalResults, "direct") != "refused-before-dispatch; read-succeeded" ||
                GetJsonString(approvalResults, "CallTool") != "refused-before-dispatch") throw new ReleaseException($"V{major} default-approval gate result is invalid");

        }

        int software = 0, engineering = 0, http = 0, hmi = 0;
        if (plan.Includes("engine-functional"))
        {
            var softwareText = RunBuildSpec("software-lookup", harness, [exe, "software-lookup-only", api], outputDirectory, runTemp, cliHome, apiRoot, major);
            software = RequireCount("software lookup", softwareText, "COMPLETE: (\\d+) software lookup checks passed", "softwareLookup");
            var engineeringText = RunBuildSpec("engineering-api", harness, [exe, "engineering-api-only", api], outputDirectory, runTemp, cliHome, apiRoot, major);
            engineering = RequireCount("engineering API", engineeringText, "COMPLETE: (\\d+) engineering API checks passed", major == 20 ? "engineeringApiV20" : "engineeringApiV21");
            var httpText = RunBuildSpec("http", harness, [exe], outputDirectory, runTemp, cliHome, apiRoot, major);
            http = RequireCount("HTTP regression", httpText, "COMPLETE: (\\d+) passed");
            var hmiText = RunBuildSpec("hmi", harness, [exe, "hmi-only", major.ToString(), fileVersion], outputDirectory, runTemp, cliHome, apiRoot, major);
            var hmiMatch = Regex.Match(hmiText, "(\\d+) HMI traversal assertions, 0 failed");
            if (!hmiMatch.Success) throw new ReleaseException("HMI traversal regression did not report complete success");
            hmi = int.Parse(hmiMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        }

        var usagePath = Path.Combine(outputDirectory, $"tool-usage-v{major}.json");
        var resourcesText = RunBuildSpec("resource-discovery", python, [Path.Combine(Root, "scripts/checks/Test-ResourceDiscovery.py"), "--exe", exe,
            "--portal-root", api, "--major", major.ToString(), "--host-harness", harness, "--public-api", api, "--usage-output", usagePath], outputDirectory, runTemp, cliHome, apiRoot, major);
        var resources = RequireCount("resource discovery", resourcesText, "COMPLETE: (\\d+) resource discovery checks passed");
        JsonElement v21Ecosystem = default;
        if (plan.Includes("engine-ecosystem"))
        {
            var schemas = Path.Combine(Path.GetDirectoryName(v21Api)!, "Schemas");
            var ecosystemOut = Path.Combine(outputDirectory, "v21-ecosystem-v" + major + "-" + Guid.NewGuid().ToString("N"));
            var ecosystemPython = Environment.GetEnvironmentVariable("TIA_MCP_PLC_TOOLS_PYTHON") ?? python;
            RunBuildSpec("v21-ecosystem", ecosystemPython, [Path.Combine(Root, "scripts/checks/Test-V21Ecosystem.py"), "--exe", exe,
                "--major", major.ToString(), "--host-harness", harness, "--public-api", api, "--schema-root", schemas, "--output", ecosystemOut], outputDirectory, runTemp, cliHome, apiRoot, major);
            var ecosystemFiles = Directory.Exists(ecosystemOut) ? Directory.GetFiles(ecosystemOut, "result.json", SearchOption.AllDirectories) : [];
            if (ecosystemFiles.Length != 1) throw new ReleaseException("V21 ecosystem evidence missing or ambiguous");
            using var v21EcosystemDoc = JsonDocument.Parse(File.ReadAllText(ecosystemFiles[0]));
            v21Ecosystem = v21EcosystemDoc.RootElement.Clone();
            CheckReleaseCount("v21Ecosystem", GetJsonInt(v21Ecosystem, "checks"), "V21 ecosystem adapter checks incomplete");
            if (GetJsonString(v21Ecosystem, "status") != "passed" || GetJsonBool(v21Ecosystem, "selfTestOnly") || GetJsonBool(v21Ecosystem, "nativeTiaExecuted") ||
                GetJsonString(v21Ecosystem, "runtimeSha256") != ReleaseRecords.HashFile(exe)) throw new ReleaseException("V21 ecosystem adapter evidence does not match the selected EXE");

        }

        var stabilityRounds = options.Get("LocalStabilityRounds", "50");
        JsonElement stability = default, isolatedStability = default;
        if (plan.Includes("engine-stability"))
        {
            var stabilityOut = Path.Combine(outputDirectory, "stability-v" + major + "-" + Guid.NewGuid().ToString("N"));
            RunBuildSpec("local-stability", python, [Path.Combine(Root, "scripts/checks/Test-LocalStability.py"), "--exe", exe, "--major", major.ToString(),
                "--host-harness", harness, "--public-api", api, "--rounds", stabilityRounds, "--output", stabilityOut], outputDirectory, runTemp, cliHome, apiRoot, major);
            using var stabilityDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(stabilityOut, "result.json")));
            stability = stabilityDoc.RootElement.Clone();
            if (GetJsonString(stability, "status") != "passed" || GetArrayLength(stability, "runs") != 4 || GetJsonString(stability, "runtimeSha256") != ReleaseRecords.HashFile(exe))
                throw new ReleaseException("Local stability validation incomplete or used a different EXE");
        }
        if (plan.Includes("engine-isolated-stability"))
        {
            var isolatedOut = Path.Combine(outputDirectory, "isolated-stability-v" + major + "-" + Guid.NewGuid().ToString("N"));
            RunBuildSpec("isolated-local-stability", python, [Path.Combine(Root, "scripts/checks/Test-LocalStability.py"), "--exe", exe, "--major", major.ToString(),
                "--host-harness", harness, "--public-api", api, "--rounds", stabilityRounds, "--output", isolatedOut, "--isolate-openness"], outputDirectory, runTemp, cliHome, apiRoot, major);
            using var isolatedDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(isolatedOut, "result.json")));
            isolatedStability = isolatedDoc.RootElement.Clone();
            if (GetJsonString(isolatedStability, "status") != "passed" || GetArrayLength(isolatedStability, "runs") != 4 || !GetJsonBool(isolatedStability, "isolatedWorker") || GetJsonString(isolatedStability, "runtimeSha256") != ReleaseRecords.HashFile(exe))
                throw new ReleaseException("Isolated local stability checks failed or used a different EXE");
        }

        int nativeExport = 0, snapshot = 0, globalScripts = 0, graphic = 0, settings = 0, ecosystemCount = 0;
        if (plan.Includes("engine-functional"))
        {
            var nativeExportText = RunBuildSpec("native-export", harness, [exe, "native-export-only"], outputDirectory, runTemp, cliHome, apiRoot, major);
            nativeExport = RequireCount("native export remoting", nativeExportText, "COMPLETE: (\\d+) native export remoting checks passed");
            var snapshotText = RunBuildSpec("hmi-snapshot", harness, [exe, "hmi-snapshot-only"], outputDirectory, runTemp, cliHome, apiRoot, major);
            snapshot = RequireCount("HMI snapshot", snapshotText, "COMPLETE: (\\d+) HMI snapshot remoting checks passed");
            var globalText = RunBuildSpec("global-script", harness, [exe, "global-script-only", api], outputDirectory, runTemp, cliHome, apiRoot, major);
            globalScripts = RequireCount("global script bridge", globalText, "COMPLETE: (\\d+) global script bridge checks passed", major == 21 ? "globalScriptV21" : null);
            var graphicText = RunBuildSpec("graphic-selection", harness, [exe, "graphic-selection-only", api], outputDirectory, runTemp, cliHome, apiRoot, major);
            graphic = RequireCount("graphic selection", graphicText, "COMPLETE: (\\d+) graphical selection checks passed", "graphicSelection");
            var settingsText = RunBuildSpec("runtime-settings", harness, [exe, "runtime-settings-only", api], outputDirectory, runTemp, cliHome, apiRoot, major);
            settings = RequireCount("runtime settings", settingsText, "COMPLETE: (\\d+) runtime settings checks passed", major == 20 ? "runtimeSettingsV20" : "runtimeSettingsV21");
            var migrationText = RunBuildSpec("migration-assembly", harness, [exe, "test-migration-read-assembly", api], outputDirectory, runTemp, cliHome, apiRoot, major);
            if (!migrationText.Contains("migration assembly checks passed", StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Migration assembly file checks did not complete");
            var ecosystemText = RunBuildSpec("ecosystem-assembly", harness, [exe, "test-ecosystem-assembly", api], outputDirectory, runTemp, cliHome, apiRoot, major);
            ecosystemCount = RequireCount("ecosystem assembly", ecosystemText, "COMPLETE: (\\d+) ecosystem assembly checks passed", "ecosystem");

        }

        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        if (coverage.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Array)
            foreach (var group in sites.EnumerateArray().GroupBy(site => GetJsonString(site, "category"), StringComparer.Ordinal)) categories[group.Key] = group.Count();
        var nativeDiagnostics = new
        {
            status = "passed", sites = GetJsonInt(coverage, "count"), categories, uncoveredSupportedBoundaries = 0,
            coverageSha256 = ReleaseRecords.HashFile(Path.Combine(outputDirectory, $"native-call-coverage-v{major}.json")),
            instrumenterSha256 = GetJsonString(coverage, "instrumenterSha256"), jitPrepared = int.Parse(nativeJit.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            openGenericWrappers = int.Parse(nativeJit.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), fixture = common.GetProperty("diagnosticTests").Clone(),
            scriptSha256 = ReleaseRecords.HashFile(Path.Combine(Root, "scripts/checks/Test-NativeDiagnostics.py")), liveTiaExecuted = false,
            scope = "Engine-owned Openness call sites; not SDK/server internals or a native stability claim"
        };
        return new
        {
            httpPassed = http, hmiPassed = hmi, resourceDiscoveryPassed = resources, nativeExportRemotingPassed = nativeExport,
            migrationAssembly = plan.Includes("engine-functional") ? "passed" : "skipped", realProjectAcceptance = "NOT PERFORMED for this release", hmiSnapshotRemotingPassed = snapshot,
            softwareLookupPassed = software, engineeringApiShapePassed = engineering,
            nativeHarness = new { compiled = true, safetyChecksPassed = nativeSafety, supervisorChecksPassed = GetJsonInt(common, "nativeSupervisorChecksPassed"), exeSha256 = ReleaseRecords.HashFile(Path.Combine(Path.GetDirectoryName(nativeProject)!, $"bin/Release/net48/NativeTests.V{major}.exe")), supervisorSha256 = ReleaseRecords.HashFile(Path.Combine(Root, "scripts/checks/Test-NativeLifecycle.py")), liveAcceptance = "NOT RUN; explicit opt-in required" },
            localStability = plan.Includes("engine-stability") ? (JsonElement?)stability : null,
            v21EcosystemAdapters = plan.Includes("engine-ecosystem") ? (JsonElement?)v21Ecosystem : null,
            isolatedLocalStability = plan.Includes("engine-isolated-stability") ? (JsonElement?)isolatedStability : null,
            sessionStability = new { processLeaseChecksPassed = processLeases, nativeMcpSafetyChecksPassed = GetJsonInt(common, "nativeMcpSafetyChecksPassed"), crashEvidenceChecksPassed = GetJsonInt(common, "crashEvidenceChecksPassed"), nativeMcpExecuted = false },
            nativeDiagnostics, workerIsolation = plan.Includes("engine-worker-isolation") ? new { enabledByDefault = false, faultChecksPassed = workerFaults, protocolChecksPassed = workerProtocol, nativeAcceptance = "NOT RUN", protocolScriptSha256 = ReleaseRecords.HashFile(Path.Combine(Root, "scripts/checks/Test-WorkerIsolation.py")) } : null,
            approvalSafety = plan.Includes("engine-approval") ? new { status = "passed", checksPassed = GetJsonInt(approval, "checksPassed"), defaultEnabled = true, directWriteRefusedBeforeDispatch = true, callToolWriteRefusedBeforeDispatch = true, readSucceeded = true, workbenchConnected = false, tiaConnected = false, scriptSha256 = ReleaseRecords.HashFile(Path.Combine(Root, "scripts/checks/Test-ReleaseApprovalGate.py")) } : null,
            engineeringLiveEdits = "NOT TESTED; preview/API shape and offline behavior only",
            unifiedGraphicLists = major == 21 ? "API present; native import not live-tested" : "not exposed by supplied V20 API",
            globalScriptBridgePassed = globalScripts, graphicSelectionPassed = graphic, runtimeSettingsPassed = settings,
            ecosystemAssemblyPassed = ecosystemCount, globalScriptNativeApiSignature = major == 21 ? "verified in referenced V21 DLL; live import not tested" : "not established; bridge checks only"
        };
    }

    private static void RunShippedRuntimeChecks(string dotnet, string v21Api, string apiRoot, string package, string outputDirectory, string runTemp, string cliHome, ReleaseCheckPlan plan)
    {
        var exe = Path.Combine(Root, "runtime/v21/TiaMcp.Engine.V21.exe");
        var harness = Path.Combine(Root, "tests/Engine/TiaMcpServer.HttpTests/bin/Release/net48/HttpTests.exe");
        if (plan.Includes("engine-functional")) RunBuildSpec("download-route", harness, [exe, "test-download-route", v21Api], outputDirectory, runTemp, cliHome, apiRoot);
        if (plan.Includes("engine-functional")) RunBuildSpec("match-plc-name", harness, [exe, "test-match-plc-name"], outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildSpec("generate-tools-list", harness, [exe, "generate-tools-list", v21Api, Path.Combine(Root, "manifest/tools-list.json"), package], outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildSpec("tool-capability-matrix", dotnet, ["run", Path.Combine(Root, "scripts/generate/Generate-ToolCapabilityMatrix.cs"), "--", "--tools-list", Path.Combine(Root, "manifest/tools-list.json"), "--out-file", Path.Combine(Root, "docs/reference/tool-matrix.md")], outputDirectory, runTemp, cliHome, apiRoot);
    }

    private static void RefreshPackageManifest(string release, string fileVersion, string releaseDate, string package)
    {
        var rosterPath = Path.Combine(Root, "manifest/tools-list.json");
        var roster = JsonNode.Parse(File.ReadAllText(rosterPath, Encoding.UTF8))?.AsObject() ?? throw new ReleaseException("Generated tools-list.json is invalid");
        var manifestPath = Path.Combine(Root, "manifest/package-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))?.AsObject() ?? throw new ReleaseException("package-manifest.json is invalid");
        manifest["packageName"] = package;
        manifest["bundleVersion"] = release;
        manifest["fileVersion"] = fileVersion;
        manifest["refreshedAt"] = DateTimeOffset.UtcNow.ToString("o");
        var capabilities = manifest["capabilities"]?.AsObject() ?? throw new ReleaseException("package-manifest.json lacks capabilities");
        var tools = roster["tools"]?.AsArray() ?? throw new ReleaseException("tools-list.json lacks tools");
        var toolCount = roster["toolCount"]?.GetValue<int>() ?? -1;
        if (toolCount != tools.Count) throw new ReleaseException("Generated tools-list roster count is inconsistent");
        capabilities["mcpToolCount"] = toolCount;
        var layers = new JsonObject();
        foreach (var group in tools.Select(tool => tool?["layer"]?.GetValue<string>() ?? "").GroupBy(layer => layer, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            layers[group.Key] = group.Count();
        capabilities["mcpToolLayers"] = layers;

        var profiles = XDocument.Load(Path.Combine(Root, "src/Logic/ModelContextProtocol/ToolProfiles.resx"));
        var catalogValue = profiles.Root?.Elements("data").FirstOrDefault(row => (string?)row.Attribute("name") == "Catalog")?.Element("value")?.Value;
        if (string.IsNullOrWhiteSpace(catalogValue)) throw new ReleaseException("Could not locate the generated tool catalog");
        var catalog = JsonNode.Parse(catalogValue) ?? throw new ReleaseException("Generated tool catalog is invalid");
        var lite = catalog["releases"]?["21"]?.AsArray().Where(row => row?["profiles"]?.AsArray().Any(profile => profile?.GetValue<string>() == "lite") == true)
            .Select(row => row?["currentName"]?.GetValue<string>() ?? "").Where(name => name.Length != 0).ToArray() ?? [];
        if (lite.Length == 0 || lite.Distinct(StringComparer.Ordinal).Count() != lite.Length) throw new ReleaseException("Generated lite tool roster is missing or duplicated");
        var available = tools.Select(tool => tool?["name"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var name in lite) if (!available.Contains(name)) throw new ReleaseException("Lite tool missing from compiled roster: " + name);
        var liteProfile = capabilities["liteProfile"]?.AsObject() ?? throw new ReleaseException("package-manifest.json lacks liteProfile");
        liteProfile["toolCount"] = lite.Length;
        liteProfile["note"] = "Other available tools remain reachable through FindTools + CallTool; per-version admission excludes unsupported routes and runtime tools/list is authoritative.";
        manifest["validationStatus"] = "Both runtimes compiled and tested locally; new real-project acceptance remains pending";
        WriteJson(manifestPath, manifest);
    }

    private static void RunDotnetSuiteForRelease(string name, string tableName, string dotnet, string python, string resultDirectory,
        string outputDirectory, string runTemp, string cliHome, string apiRoot, string? nuget)
    {
        var spec = ReleaseCommandTable.Get(tableName);
        var args = new[] { Path.Combine(Root, "scripts/checks/Test-DotnetSuites.py"), "--suite", name, "--dotnet", dotnet, "--no-restore", "--results-directory", resultDirectory };
        RunBuildSpec(tableName, python, args, outputDirectory, runTemp, cliHome, apiRoot);
    }

    private static int ReadSuitePassed(string directory, string suite)
    {
        var path = Path.Combine(directory, suite + ".json");
        if (!File.Exists(path)) throw new ReleaseException($"Suite {suite} did not write its summary");
        using var summary = JsonDocument.Parse(File.ReadAllText(path));
        var passed = GetJsonInt(summary.RootElement, "passed");
        if (passed <= 0 || GetJsonInt(summary.RootElement, "failed") != 0 || GetJsonInt(summary.RootElement, "skipped") != 0)
            throw new ReleaseException($"Suite {suite} did not meet its passed/failed/skipped gates");
        return passed;
    }

    private static void RestoreBuildProject(string dotnet, string project, string? nuget, string outputDirectory, string runTemp, string cliHome, string apiRoot, string[]? properties = null)
    {
        var args = new List<string> { "restore", project, "-v:q" };
        if (!string.IsNullOrWhiteSpace(nuget)) args.AddRange(["--configfile", nuget]);
        if (properties is not null) args.AddRange(properties);
        RunBuildRaw(dotnet, args, Path.Combine(outputDirectory, "restore-" + Path.GetFileName(project) + ".log"), runTemp, cliHome, apiRoot, nuget, "Restore " + Path.GetFileName(project));
    }

    private static string RunBuildSpec(string specName, string executable, IEnumerable<string> args, string outputDirectory, string runTemp, string cliHome, string apiRoot,
        int? major = null, bool approval = false)
    {
        var spec = ReleaseCommandTable.Get(specName);
        var logName = major.HasValue ? spec.LogFile.Replace("{major}", major.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) : spec.LogFile;
        var result = RunBuildIsolated(executable, args, Path.Combine(outputDirectory, logName), runTemp, cliHome, apiRoot, null, approval, specName);
        ProcessRunner.RequireSuccess(result, specName + " failed; see " + Path.Combine(outputDirectory, logName));
        return result.StandardOutput + result.StandardError;
    }

    private static void RunBuildRaw(string executable, IEnumerable<string> args, string log, string runTemp, string cliHome, string apiRoot,
        string? nuget, string description, bool approval = false)
    {
        var result = RunBuildIsolated(executable, args, log, runTemp, cliHome, apiRoot, nuget, approval, Path.GetFileNameWithoutExtension(log));
        ProcessRunner.RequireSuccess(result, description + " failed; see " + log);
    }

    private static CommandResult RunBuildIsolated(string executable, IEnumerable<string> arguments, string log, string runTemp, string cliHome,
        string apiRoot, string? nuget, bool approval, string name)
    {
        var args = arguments.ToList();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var command = args.FirstOrDefault();
            if (command is "build" or "restore" or "publish")
            {
                args.Add("-nodeReuse:false"); args.Add("-p:UseSharedCompilation=false"); args.Add("-p:NuGetAudit=false"); args.Add("-m:1");
                if (!string.IsNullOrWhiteSpace(nuget)) args.Add("-p:RestoreConfigFile=" + nuget);
            }
        }
        var host = Path.Combine(runTemp, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(host, "config"));
        Directory.CreateDirectory(Path.Combine(host, "temp"));
        Directory.CreateDirectory(Path.Combine(host, "local-app-data"));
        Directory.CreateDirectory(Path.Combine(host, "app-data"));
        File.WriteAllText(Path.Combine(host, "check.txt"), name, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(host, "config/approval.settings"), $"enabled={approval.ToString().ToLowerInvariant()}\ntimeoutSeconds=120\n", new UTF8Encoding(false));
        var env = new Dictionary<string, string?>
        {
            ["TIA_MCP_DATA_DIRECTORY"] = host,
            ["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(host, "diagnostics"),
            ["TIA_MCP_RELEASE_TEMP_ROOT"] = runTemp,
            ["TIA_MCP_TEST_PUBLIC_API_ROOT"] = apiRoot,
            ["LOCALAPPDATA"] = Path.Combine(host, "local-app-data"),
            ["APPDATA"] = Path.Combine(host, "app-data"),
            ["TEMP"] = Path.Combine(host, "temp"), ["TMP"] = Path.Combine(host, "temp"),
            ["DOTNET_CLI_HOME"] = cliHome, ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1", ["UseSharedCompilation"] = "false", ["NuGetAudit"] = "false"
        };
        var result = ProcessRunner.Run(executable, args, Root, env);
        WriteLog(log, result);
        if (result.ExitCode == 0)
        {
            try { Directory.Delete(host, true); }
            catch (Exception ex) { Console.Error.WriteLine("WARNING release check data still in use (kept): " + host + " (" + ex.Message + ")"); }
        }
        else Console.Error.WriteLine($"Retained failed release check data ({name}): {host}");
        return result;
    }

    private static int RequireCount(string name, string text, string pattern, string? rule = null)
    {
        var match = Regex.Match(text, pattern);
        if (!match.Success) throw new ReleaseException(name + " did not report a complete check count");
        var actual = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (rule is not null) CheckReleaseCount(rule, actual, name + " checks incomplete");
        return actual;
    }

    private static void CheckReleaseCount(string key, int actual, string message)
    {
        if (!BuildReleaseCountRules.TryGetValue(key, out var expected)) throw new ReleaseException("Unknown check-count gate: " + key);
        if ((expected.Exact && actual != expected.Count) || (!expected.Exact && actual < expected.Count))
            throw new ReleaseException($"{message}; expected {(expected.Exact ? "exactly" : "at least")} {expected.Count}, actual {actual}");
    }

    private static int GetArrayLength(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
    private static string RequiredDirectory(string? input, string option)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ReleaseException(option + " is required");
        var path = Path.GetFullPath(input);
        if (!Directory.Exists(path)) throw new ReleaseException(option + " does not exist: " + path);
        return path;
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path)) throw new ReleaseException(message + ": " + path);
    }
}
