using System;
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
            ["workerProtocol"] = (58, false), ["approvalSafety"] = (7, true),
            ["softwareLookup"] = (45, false),
            ["v21Ecosystem"] = (43, false), ["globalScriptV21"] = (8, true), ["graphicSelection"] = (8, false),
            ["runtimeSettingsV20"] = (8, false), ["runtimeSettingsV21"] = (9, false), ["ecosystem"] = (31, false)
        };

    private static int BuildReleasePipeline(Options options)
    {
        if (options.Has("CompileOnly")) return BuildReleaseCompilation(options);
        if (options.Has("SelfTest"))
            return RunChild(Dotnet, ["test", "tests/Release/TiaMcp.ReleaseTool.Tests/TiaMcp.ReleaseTool.Tests.csproj", "-c", "Release", "--filter", "Category=Pipeline"], Root, "Parallel pipeline tests");

        EnsureWindows("build-release");
        var v20Api = RequiredDirectory(options.Get("V20ReferenceRoot"), "-V20ReferenceRoot");
        // Release callers still pass both reference roots; the V21 root is validated, the V20 root locates the SDK root.
        _ = RequiredDirectory(options.Get("V21ReferenceRoot"), "-V21ReferenceRoot");
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

        var releaseVersion = ReleaseVersions.ReadVersionProps(Root);
        var release = releaseVersion.Label;
        var fileVersion = releaseVersion.FileVersion;
        ReleaseRecords.AssertRuntimePreparation(Root);
        var releaseDate = options.Get("ReleaseDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        if (!DateTime.TryParseExact(releaseDate, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new ReleaseException("-ReleaseDate must be a valid yyyyMMdd date");

        var package = $"TIA_MCP_Delivery_v{release}_{releaseDate}";
        var outputDirectory = Path.Combine(Root, "bin-build/releases", "v" + release);
        Directory.CreateDirectory(outputDirectory);
        var runTempRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_MCP_RELEASE_TEMP_ROOT") ?? Path.Combine(Path.GetTempPath(), "tmr-" + Guid.NewGuid().ToString("N")[..8]));
        var repoPrefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (runTempRoot.StartsWith(repoPrefix, StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Release temp root must be outside the repository: " + runTempRoot);
        var runTemp = Path.Combine(runTempRoot, "br");
        Directory.CreateDirectory(runTemp);
        var apiRoot = Directory.GetParent(Directory.GetParent(v20Api)!.FullName)!.FullName;
        var cliHome = Path.Combine(outputDirectory, "dotnet-home");
        Directory.CreateDirectory(cliHome);

        // P8-04l: the V20/V21 engine is deleted. build-release runs the release-wide checks and builds the shipped tools;
        // the V20/V21 checks run on the PLC workers in build-multi-version (PlcReleaseChecks).
        var sourceFiles = ReleaseRecords.GetSources(Root, "engine");
        var validationInputs = ReleaseRecords.GetValidationInputs(Root, "engine");
        var common = RunBuildReleaseCommon(options, dotnet, python, nuget, v20Api, apiRoot, outputDirectory, runTemp, cliHome);

        RunShippedRuntimeChecks(dotnet, apiRoot, package, outputDirectory, runTemp, cliHome);
        RefreshPackageManifest(release, fileVersion, releaseDate, package);
        if (!SameArtifacts(sourceFiles, ReleaseRecords.GetSources(Root, "engine"))) throw new ReleaseException("Build inputs changed while validation was running; refusing to record results");
        if (!SameArtifacts(validationInputs, ReleaseRecords.GetValidationInputs(Root, "engine"))) throw new ReleaseException("Validation inputs changed during the build; refusing to record results");
        var updaterFiles = ReleaseRecords.HashUpdaterOutput(Root);
        if (!common.TryGetProperty("updaterFiles", out var builtUpdater) ||
            !JsonElement.DeepEquals(builtUpdater, JsonSerializer.SerializeToElement(updaterFiles, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })))
            throw new ReleaseException("Updater build output changed during validation; refusing to record results");

        // Same roots as ReleaseRecords' release-build inventory check; Foundation hosts, PLC workers, Studio and bundled
        // .NET runtimes belong to the multi-version record.
        var runtimeFiles = ReleaseRecords.ReleaseBuildRuntimeRoots
            .Select(relative => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".exe" or ".dll" or ".config" ||
                Path.GetFileName(path) is "release-key.txt" or "NativeCallWeaver.deps.json" or "NativeCallWeaver.runtimeconfig.json")
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { path = Path.GetRelativePath(Root, path).Replace('\\', '/'), length = new FileInfo(path).Length, sha256 = ReleaseRecords.HashFile(path) }).ToArray();
        var record = new
        {
            tier = ReleasePlan(options).Tier, checkPlan = ReleasePlan(options),
            release, releaseDate, fileVersion, package, generatedAt = DateTimeOffset.UtcNow.ToString("o"),
            validation = new
            {
                offlinePassed = GetJsonInt(common, "offlinePassed"), offlineV20Passed = GetJsonInt(common, "offlineV20Passed"),
                keptCodePassed = GetJsonInt(common, "keptCodePassed"), keptCodeV20Passed = GetJsonInt(common, "keptCodeV20Passed"),
                writeGuardPassed = GetJsonInt(common, "writeGuardChecksPassed"),
                crashEvidencePassed = GetJsonInt(common, "crashEvidenceChecksPassed"),
                versionPolicySdkPassed = GetJsonInt(common, "versionPolicySdkPassed"),
                updaterPassed = GetJsonInt(common, "updaterPassed"),
                // Formerly copied into each engine runtime's sessionStability; the checks are release-wide.
                nativeMcpSafetyPassed = GetJsonInt(common, "nativeMcpSafetyChecksPassed"),
                nativeSupervisorPassed = GetJsonInt(common, "nativeSupervisorChecksPassed"),
                diagnosticTests = common.GetProperty("diagnosticTests")
            },
            runtimeFiles, updaterFiles, sourceFiles, validationInputs, validationArtifacts = Array.Empty<object>()
        };
        WriteJson(Path.Combine(Root, "manifest/release-build.json"), record);
        // Completion binds the eight-release record before delivery preparation.
        Console.WriteLine($"Built and checked the release-wide tools: {fileVersion}. The V20/V21 checks run on the PLC workers in build-multi-version. Real TIA acceptance is separate.");
        return 0;
    }

    // Compile refactor proofs without refreshing release manifests or publication evidence.
    private static int BuildReleaseCompilation(Options options)
    {
        EnsureWindows("build-release -CompileOnly");
        _ = RequiredDirectory(options.Get("V20ReferenceRoot"), "-V20ReferenceRoot");
        _ = RequiredDirectory(options.Get("V21ReferenceRoot"), "-V21ReferenceRoot");
        string? nuget = options.Get("NuGetConfig") ?? Environment.GetEnvironmentVariable("RestoreConfigFile");
        if (string.IsNullOrWhiteSpace(nuget)) throw new ReleaseException("-CompileOnly requires an offline -NuGetConfig");
        nuget = Path.GetFullPath(nuget);
        var error = ReleaseValidation.OfflineNuGetError(nuget);
        if (error != null) throw new ReleaseException(error);
        string dotnet = options.Get("Dotnet", Dotnet);
        string logs = Path.Combine(Root, "bin-build/release-compile");
        Directory.CreateDirectory(logs);
        string weaver = Path.Combine(Root, "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll");
        if (options.Has("NoBuild"))
        {
            foreach (var path in new[] { weaver,
                "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe", "tests/Engine/TiaMcp.Engine.Harness/bin/Release/net48/TiaMcp.Engine.Harness.exe" })
                RequireFile(Path.IsPathRooted(path) ? path : Path.Combine(Root, path), "CompileOnly -NoBuild requires the completed check-all build graph");
            Console.WriteLine("PASS production compilation outputs from the completed build graph");
            return 0;
        }
        void Build(string name, string project)
        {
            var args = new List<string> { "build", project, "-c", "Release", "--disable-build-servers", "-m:1", "-nodeReuse:false",
                "-p:UseSharedCompilation=false", "-p:NuGetAudit=false" };
            var result = RunLoggedProcess(dotnet, args, logs, name + ".log", nuget);
            ProcessRunner.RequireSuccess(result, name + " compilation failed");
            Console.WriteLine("PASS " + name + " compilation; " + Path.Combine(logs, name + ".log"));
        }
        Build("native-call-weaver", "build-tools/native-call-weaver/NativeCallWeaver.csproj");
        Build("foundation-host", "src/FoundationHost/TiaMcp.FoundationHost.csproj");
        Build("http-tests", "tests/Engine/TiaMcp.Engine.Harness/TiaMcp.Engine.Harness.csproj");
        return 0;
    }

    private static JsonElement RunBuildReleaseCommon(Options options, string dotnet, string python, string? nuget, string v20Api,
        string apiRoot, string outputDirectory, string runTemp, string cliHome)
    {
        var plan = ReleasePlan(options);
        var suiteResults = Path.Combine(outputDirectory, "dotnet-suites");
        Directory.CreateDirectory(suiteResults);
        int writeGuardPassed = 0, crashEvidencePassed = 0, offlinePassed = 0, offlineV20Passed = 0, keptCodePassed = 0, keptCodeV20Passed = 0, versionPolicyPassed = 0, updaterPassed = 0, nativeMcpSafety = 0;
        Match lifecycleMatch = Match.Empty;
        // An adopted publication (TIA_MCP_RELEASE_ADOPTION) takes this stage's suite counts from the verified evidence of its commit.
        var adopted = ReleaseEvidence.LoadAdoption();
        if (plan.Includes("offline-suites") && adopted is not null)
            (writeGuardPassed, crashEvidencePassed, offlinePassed, offlineV20Passed, keptCodePassed, keptCodeV20Passed, versionPolicyPassed, updaterPassed, nativeMcpSafety, lifecycleMatch) =
                AdoptedCommonValidation(adopted, dotnet, python, outputDirectory, runTemp, cliHome, apiRoot);
        if (plan.Includes("offline-suites") && adopted is null)
        {
            RunBuildSpec("approval-gate-self-test", python, [Path.Combine(Root, "scripts/checks/Test-ReleaseApprovalGate.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
            var lifecycle = RunBuildSpec("native-supervisor", python, [Path.Combine(Root, "scripts/checks/Test-NativeLifecycle.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
            lifecycleMatch = Regex.Match(lifecycle, "COMPLETE: (\\d+) native supervisor checks passed; live TIA tests NOT RUN");
            if (!lifecycleMatch.Success || (lifecycleMatch.Success ? int.Parse(lifecycleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0) < 1)
                throw new ReleaseException("Native supervisor offline checks incomplete");
            var mcpSafetyText = RunBuildSpec("native-mcp-safety", python, [Path.Combine(Root, "scripts/checks/Test-NativeMcpSession.py"), "--self-test"], outputDirectory, runTemp, cliHome, apiRoot);
            nativeMcpSafety = RequireCount("nativeMcpSafety", mcpSafetyText, "COMPLETE: (\\d+) native MCP safety checks passed");
            RestoreBuildProject(dotnet, Path.Combine(Root, "tests/Tools/TiaMcp.ShippedTools.Tests/TiaMcp.ShippedTools.Tests.csproj"), nuget, outputDirectory, runTemp, cliHome, apiRoot);
            RestoreBuildProject(dotnet, Path.Combine(Root, "tests/Studio/TiaOpenness.Gui.Tests/TiaOpenness.Gui.Tests.csproj"), nuget, outputDirectory, runTemp, cliHome, apiRoot);
            RunDotnetSuiteForRelease("write-guard", "write-guard", dotnet, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
            writeGuardPassed = ReadSuitePassed(suiteResults, "write-guard");
            RunDotnetSuiteForRelease("crash-evidence", "crash-evidence", dotnet, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
            crashEvidencePassed = ReadSuitePassed(suiteResults, "crash-evidence");
            RunBuildSpec("tool-usage", dotnet, [typeof(ReleaseCommands).Assembly.Location, "generate-tool-usage", "-Check"], outputDirectory, runTemp, cliHome, apiRoot);
            RestoreBuildProject(dotnet, Path.Combine(Root, "tests/Repository/TiaMcp.SourceContracts.Tests/TiaMcp.SourceContracts.Tests.csproj"), nuget, outputDirectory, runTemp, cliHome, apiRoot);
            RunDotnetSuiteForRelease("source-contracts", "version-catalog", dotnet, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);

            var offlineSuites = DotnetSuites.LoadCatalog(Path.Combine(Root, "tests/test-suites.json"), Root);
            foreach (var project in ReleaseCommandTable.OfflineSuites.Select(name => offlineSuites[name]!["project"]!.GetValue<string>()).Distinct(StringComparer.Ordinal))
                RestoreBuildProject(dotnet, Path.Combine(Root, project), nuget, outputDirectory, runTemp, cliHome, apiRoot);
            foreach (var name in ReleaseCommandTable.OfflineSuites)
                RunDotnetSuiteForRelease(name, name, dotnet, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
            offlinePassed = ReadSuitePassed(suiteResults, "offline");
            offlineV20Passed = ReadSuitePassed(suiteResults, "offline-v20");
            keptCodePassed = ReadSuitePassed(suiteResults, "kept-code");
            keptCodeV20Passed = ReadSuitePassed(suiteResults, "kept-code-v20");
            var versionPolicyProject = Path.Combine(Root, "tests/Engine/TiaMcp.Engine.VersionPolicy.Tests/TiaMcp.Engine.VersionPolicy.Tests.csproj");
            RestoreBuildProject(dotnet, versionPolicyProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
            RunDotnetSuiteForRelease("version-policy", "version-policy", dotnet, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
            versionPolicyPassed = ReadSuitePassed(suiteResults, "version-policy");
            var updaterSuiteProject = Path.Combine(Root, "tests/Updater/TiaMcp.Updater.Tests/TiaMcp.Updater.Tests.csproj");
            RestoreBuildProject(dotnet, updaterSuiteProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
            RunDotnetSuiteForRelease("updater", "updater", dotnet, suiteResults, outputDirectory, runTemp, cliHome, apiRoot, nuget);
            updaterPassed = ReadSuitePassed(suiteResults, "updater");
        }
        var writeGuardProject = Path.Combine(Root, "src/Tools/WriteGuard/TiaMcp.WriteGuard.csproj");
        RestoreBuildProject(dotnet, writeGuardProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["publish", writeGuardProject, "-c", "Release", "-o", Path.Combine(Root, "runtime/tools"), "--no-restore", "-p:PublishAot=false", "-p:UseAppHost=true"], Path.Combine(outputDirectory, "write-guard-publish.log"), runTemp, cliHome, apiRoot, nuget, "write-guard publish");
        var pluginLauncherProject = Path.Combine(Root, "src/Tools/PluginLauncher/TiaMcp.PluginLauncher.csproj");
        RestoreBuildProject(dotnet, pluginLauncherProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["publish", pluginLauncherProject, "-c", "Release", "-o", Path.Combine(Root, "runtime/tools"), "--no-restore", "-p:UseAppHost=true"], Path.Combine(outputDirectory, "plugin-launcher-publish.log"), runTemp, cliHome, apiRoot, nuget, "plugin launcher publish");
        var updaterProject = Path.Combine(Root, "src/Updater/TiaMcp.Updater.csproj");
        RestoreBuildProject(dotnet, updaterProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["build", updaterProject, "-c", "Release", "-f", "net48", "--no-restore", "-v:q"], Path.Combine(outputDirectory, "build-updater.log"), runTemp, cliHome, apiRoot, nuget, "Updater build");
        // The package takes the updater from bin-build/updater, outside the runtime inventory; the record binds these bytes (P8-upd-bind).
        var updaterFiles = ReleaseRecords.HashUpdaterOutput(Root);
        var harnessProject = Path.Combine(Root, "tests/Engine/TiaMcp.Engine.Harness/TiaMcp.Engine.Harness.csproj");
        RestoreBuildProject(dotnet, harnessProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["build", harnessProject, "-c", "Release", "--no-restore", "-v:q"], Path.Combine(outputDirectory, "build-harness.log"), runTemp, cliHome, apiRoot, nuget, "HTTP harness build");
        var foundationHostProject = Path.Combine(Root, "src/FoundationHost/TiaMcp.FoundationHost.csproj");
        RestoreBuildProject(dotnet, foundationHostProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
        RunBuildRaw(dotnet, ["build", foundationHostProject, "-c", "Release", "--no-restore", "-v:q"], Path.Combine(outputDirectory, "build-foundation-host.log"), runTemp, cliHome, apiRoot, nuget, "Foundation product catalog build");
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

        JsonElement diagnostics = JsonSerializer.SerializeToElement(new { status = "skipped" });
        if (plan.Includes("native-diagnostics"))
        {
            var diagnosticProject = Path.Combine(Root, "tests/Engine/TiaMcp.Engine.Diagnostics.Tests/TiaMcp.Engine.Diagnostics.Tests.csproj");
            RestoreBuildProject(dotnet, diagnosticProject, nuget, outputDirectory, runTemp, cliHome, apiRoot);
            RunBuildRaw(dotnet, ["build", diagnosticProject, "-c", "Release", "--no-restore", "-v:q"], Path.Combine(outputDirectory, "native-diagnostics-build.log"), runTemp, cliHome, apiRoot, nuget, "native diagnostics fixture build");
            var diagnosticFixture = Path.Combine(Path.GetDirectoryName(diagnosticProject)!, "bin/Release/net48/TiaMcp.Engine.Diagnostics.Tests.exe");
            diagnostics = VerifyDiagnosticFixture(python, diagnosticFixture, Path.Combine(weaverOutput, "NativeCallWeaver.dll"), outputDirectory, runTemp, cliHome, apiRoot);
        }
        var common = new { diagnosticTests = diagnostics, crashEvidenceChecksPassed = crashEvidencePassed, writeGuardChecksPassed = writeGuardPassed, updaterPassed,
            nativeMcpSafetyChecksPassed = nativeMcpSafety, nativeSupervisorChecksPassed = (lifecycleMatch.Success ? int.Parse(lifecycleMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0),
            offlinePassed, offlineV20Passed, keptCodePassed, keptCodeV20Passed, versionPolicySdkPassed = versionPolicyPassed, updaterFiles };
        WriteJson(Path.Combine(outputDirectory, "common.json"), common);
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(common, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return result.RootElement.Clone();
    }

    private static JsonElement VerifyDiagnosticFixture(string python, string diagnosticFixture, string weaver, string outputDirectory, string runTemp, string cliHome, string apiRoot)
    {
        var diagnosticOut = Path.Combine(outputDirectory, "native-diagnostics-" + Guid.NewGuid().ToString("N"));
        RunBuildSpec("native-diagnostics-fixture", python, [Path.Combine(Root, "scripts/checks/Test-NativeDiagnostics.py"), "--fixture", diagnosticFixture,
            "--weaver", weaver, "--output", diagnosticOut], outputDirectory, runTemp, cliHome, apiRoot);
        using var diagnosticsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(diagnosticOut, "result.json")));
        var diagnostics = diagnosticsDoc.RootElement.Clone();
        CheckReleaseCount("diagnosticBehavior", GetJsonInt(diagnostics, "behaviorChecks"), "Native diagnostic fixture behavior checks incomplete");
        CheckReleaseCount("diagnosticRejection", GetJsonInt(diagnostics, "rejectionChecks"), "Native diagnostic fixture rejection checks incomplete");
        if (GetJsonBool(diagnostics, "nativeTiaExecuted")) throw new ReleaseException("Native diagnostic fixture gate ran live TIA tests");
        return diagnostics;
    }

    // The delivered tools-list comes from the installed V21 FoundationHost's product catalog (P8-04l: the engine oracle and
    // its harness generator are gone; the host producer was byte-identical to it). The generator's undocumented-parameter
    // ratchet is kept here: the count may not grow over the committed roster.
    private static void RunShippedRuntimeChecks(string dotnet, string apiRoot, string package, string outputDirectory, string runTemp, string cliHome)
    {
        var foundationHost = Path.Combine(Root, "runtime/v21/TiaMcp.FoundationHost.exe");
        RequireFile(foundationHost, "Installed V21 FoundationHost missing; run build-multi-version -PrepareOnly first");
        var toolsList = Path.Combine(Root, "manifest/tools-list.json");
        var previous = Path.Combine(outputDirectory, "tools-list-previous.json");
        File.Copy(toolsList, previous, true);
        var hostToolsList = Path.Combine(outputDirectory, "tools-list-host.json");
        string[] producer = ["--catalog", "--offline", "--profile", "full", "--release-key", "21", "--bundle-root", Root,
            "--catalog-tools-list", hostToolsList, "--catalog-package", package, "--catalog-generated-at", DateTimeOffset.UtcNow.ToString("o")];
        RunBuildRaw(foundationHost, producer, Path.Combine(outputDirectory, "host-tools-list.log"), runTemp, cliHome, apiRoot, null, "Host tools-list producer");
        static int Undocumented(string path) => JsonNode.Parse(File.ReadAllText(path))?["callDiscipline"]?["parametersUndocumented"]?.GetValue<int>()
            ?? throw new ReleaseException("tools-list callDiscipline.parametersUndocumented missing: " + path);
        if (Undocumented(hostToolsList) > Undocumented(previous))
            throw new ReleaseException($"Parameters without a [Description] went up from {Undocumented(previous)} to {Undocumented(hostToolsList)}");
        File.Copy(hostToolsList, toolsList, true);
        RunBuildSpec("tool-capability-matrix", dotnet, ["run", Path.Combine(Root, "scripts/generate/Generate-ToolCapabilityMatrix.cs"), "--", "--tools-list", toolsList, "--out-file", Path.Combine(Root, "docs/reference/tool-matrix.md")], outputDirectory, runTemp, cliHome, apiRoot);
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

    // The V20 and V21 pipelines run in parallel and share each test project's output directory:
    // build and run one registered suite at a time so a running testhost never locks the other build.
    private static readonly object SuiteGate = new();

    private static void RunDotnetSuiteForRelease(string name, string tableName, string dotnet, string resultDirectory,
        string outputDirectory, string runTemp, string cliHome, string apiRoot, string? nuget)
    {
        lock (SuiteGate) RunDotnetSuite(name, tableName, dotnet, resultDirectory, outputDirectory, runTemp, cliHome, apiRoot, nuget);
    }

    private static void RunDotnetSuite(string name, string tableName, string dotnet, string resultDirectory,
        string outputDirectory, string runTemp, string cliHome, string apiRoot, string? nuget)
    {
        var spec = ReleaseCommandTable.Get(tableName);
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests/test-suites.json")));
        var suiteCatalog = JsonNode.Parse(catalog.RootElement.GetRawText())!.AsObject();
        foreach (var selected in DotnetSuites.WithCompanions(new[] { name }, suiteCatalog))
        {
            var suite = catalog.RootElement.GetProperty(selected);
            var buildProperties = suite.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).Where(arg => arg.StartsWith("-p:", StringComparison.OrdinalIgnoreCase)).ToArray();
            RestoreBuildProject(dotnet, Path.Combine(Root, suite.GetProperty("project").GetString()!), nuget, outputDirectory, runTemp, cliHome, apiRoot, buildProperties);
            RunBuildRaw(dotnet, ["build", suite.GetProperty("project").GetString()!, "-c", "Release", "--no-restore", "-v:q", .. buildProperties],
                Path.Combine(outputDirectory, selected + "-harness-build.log"), runTemp, cliHome, apiRoot, nuget, selected + " harness build");
        }
        var options = Options.Parse(["-Suite", name, "-Dotnet", dotnet, "-NoRestore", "-DotnetArg=--no-build", "-ResultsDirectory", resultDirectory], new HashSet<string> { "NoRestore" });
        var result = RunBuildIsolated(dotnet, [], Path.Combine(outputDirectory, spec.LogFile), runTemp, cliHome, apiRoot, null, false, name,
            env => DotnetSuites.Run(Root, options, env));
        ProcessRunner.RequireSuccess(result, spec.Command);
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
        string apiRoot, string? nuget, bool approval, string name, Func<IDictionary<string, string?>, CommandResult>? inProcess = null)
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
            ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1", ["UseSharedCompilation"] = "false", ["NuGetAudit"] = "false"
        };
        var result = inProcess is null ? CachedBuild(executable, args, () => ProcessRunner.Run(executable, args, Root, env), env) : inProcess(env);
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
