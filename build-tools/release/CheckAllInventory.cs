using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal static partial class ReleaseCommands
{
    internal static JsonObject CheckAllPreflightSuite(JsonObject catalog)
    {
        var suite = catalog["release-tool"]!.DeepClone().AsObject();
        suite["arguments"] = new JsonArray("-p:UseAppHost=false", "-p:UseSharedCompilation=false");
        return suite;
    }

    private static void AddCheckAllPreflightJobs(List<CheckJob> jobs, JsonObject catalog, string output, string dotnet, int shards)
    {
        // Flatten all fifteen preflight checks into the same CPU budget. A nested
        // full-suite scheduler would either oversubscribe or hold a global barrier.
        var checks = PreflightChecks(); var dependencies = new List<string>();
        for (int i = 0; i < checks.Count; i++)
        {
            var check = checks[i]; var name = "preflight-" + i;
            if (check.Name == "release tool test suite")
            {
                var history = Path.Combine(Root, "bin-build/check-timings/preflight"); Directory.CreateDirectory(history);
                var measured = Path.Combine(Root, "bin-build/check-timings/release-tool.json");
                var seed = Path.Combine(history, "release-tool.json");
                if (File.Exists(measured) && !File.Exists(seed)) File.Copy(measured, seed);
                FastSuites.AddJobs(jobs, Root, "release-tool", CheckAllPreflightSuite(catalog), Path.Combine(output, "preflight-suites"), dotnet, shards, null, [],
                    history: history, jobPrefix: "preflight-", manifest: Path.Combine(output, "build/runtime-paths.tsv"));
                dependencies.Add("preflight-release-tool-gate"); continue;
            }
            dependencies.Add(name);
            jobs.Add(new(name, () =>
            {
                var args = AddOfflineNuGetConfig(check.Exe, check.Args).ToList();
                if (check.Args.FirstOrDefault() == "test")
                {
                    args.AddRange(["--no-build", "--no-restore"]);
                    if (CheckAllBuild.TestAssembly(Root, CheckAllBuild.Suite(CheckAllPreflightSuite(catalog)), Path.Combine(output, "build/runtime-paths.tsv"), "release-tool") is { } assembly) args.Add("-p:TargetPath=" + assembly);
                }
                if (check.Args.Contains("-SelfTest") && check.Exe != "test-suites") args.Add("-NoBuild");
                var command = check.Exe == "test-suites" || check.Exe.StartsWith("check-", StringComparison.Ordinal)
                    ? new[] { typeof(ReleaseCommands).Assembly.Location, check.Exe }.Concat(args) : args;
                return SuiteHang.RunProcess(check.Exe == "test-suites" || check.Exe.StartsWith("check-", StringComparison.Ordinal) || check.Exe == Dotnet ? dotnet : check.Exe,
                    command, Root, FastSuites.EnvironmentFor(Path.Combine(output, "isolation", name)));
            }, []));
        }
        jobs.Add(new("preflight", () => new(0, $"PREFLIGHT: {checks.Count} passed, 0 failed\n", ""), dependencies.ToArray()));
    }

    private static void AddCheckAllStaticJobs(Action<string, Func<CommandResult>, string[]?, bool> add,
        Func<string, string[], CommandResult> tool, Func<string, string, string[], CommandResult> python,
        Func<string, string, string[], bool, CommandResult> isolated, string output, string api, string nuget, int parallelism, int shards, string dotnet)
    {
        void Tool(string name, params string[] arguments) => add(name, () => tool(name, arguments), null, false);
        void Python(string name, string script, params string[] arguments) => add(name, () => python(name, script, arguments), null, false);
        Tool("catalog-drift", "generate-catalogs", "-Check");
        Tool("usage-drift", "generate-tool-usage", "-Check");
        Tool("migration-drift", "generate-release-migration", "-Check");
        Tool("tools-list-self-tests", "check-tools-list", "-SelfTest", "-NoBuild");
        Tool("tools-list", "check-tools-list");
        Tool("script-calls", "check-script-tool-calls");
        Tool("dead-references", "check-dead-tool-references");
        Tool("repository", "check-repository", "-NoBinaries");
        Tool("repository-self-tests", "check-repository", "-SelfTest", "-NoBuild");
        Tool("suite-runner-self-tests", "test-suites", "-SelfTest");
        Tool("adapter-boundary", "check-adapter-boundary");
        Tool("tia-features", "check-tia-features");
        Tool("bundle", "validate-bundle", "-Strict", "-NoBinaries", "-SkipSourceHashes");
        Tool("bundle-self-tests", "validate-bundle", "-SelfTest", "-NoBuild");
        Tool("release-documentation", "release", "-DocumentationOnly");
        Tool("release-dry-run", "run-release-build", "-Tier", "full", "-DryRun");
        add("preflight", () => tool("preflight", ["preflight", "-NoBuild", "-MaxParallelism", parallelism.ToString(), "-Shards", shards.ToString()]), null, true);
        Tool("production-compile-only", "build-release", "-CompileOnly", "-NoBuild", "-NuGetConfig", nuget,
            "-V20ReferenceRoot", ResolveReleaseApi(null, api, 20), "-V21ReferenceRoot", ResolveReleaseApi(null, api, 21));
        Python("python-repository", "scripts/checks/Check-Repository.py", "--no-binaries");
        Python("python-script-self-tests", "scripts/checks/Check-ScriptToolCalls.py", "--self-test");
        Python("python-script-calls", "scripts/checks/Check-ScriptToolCalls.py");
        Python("campaign-runner-self-tests", "scripts/checks/Test-CampaignInputs.py", "--self-test");
        Python("domain-source", "scripts/checks/Test-DomainTools.py", "--source-only");
        Python("contract-self-tests", "scripts/checks/Snapshot-ToolContracts.py", "self-test");
        Python("contract-verify", "scripts/checks/Snapshot-ToolContracts.py", "verify");
        Python("response-verify", "scripts/checks/Snapshot-ToolResponses.py", "verify");
        Python("native-lifecycle", "scripts/checks/Test-NativeLifecycle.py", "--self-test");
        Python("native-mcp-session", "scripts/checks/Test-NativeMcpSession.py", "--self-test");
        Python("approval-self-tests", "scripts/checks/Test-ReleaseApprovalGate.py", "--self-test");
        Python("relocation-self-tests", "scripts/checks/Test-RelocatedBundle.py", "--self-test");
        add("static-python-csharp-parity", () =>
        {
            var directory = Path.Combine(Root, "bin-build/p", Guid.NewGuid().ToString("N")[..8]);
            var result = isolated("static-python-csharp-parity", dotnet,
                [Path.Combine(Root, "bin-build/check-scripts/Test-StaticCheckParity/Test-StaticCheckParity.dll"), "-Python", Py, "-NoBuild", "-ReleaseTool", typeof(ReleaseCommands).Assembly.Location, "-Output", directory], true);
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(output, "static-parity", Path.GetRelativePath(directory, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination, true);
                }
            return result;
        }, null, false);
        add("match-plc-name-source", () => isolated("match-plc-name-source", dotnet,
            [Path.Combine(Root, "bin-build/check-scripts/Test-MatchPlcName/Test-MatchPlcName.dll"), "-SourceOnly"], true), null, false);
        // This console project isn't a registered xUnit suite; dotnet test would
        // silently execute none of its configuration-page assertions.
        add("configuration-page", () => isolated("configuration-page", dotnet,
            ["run", "--project", Path.Combine(Root, "tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj"), "-c", "Release", "--no-build", "--no-restore"], true), null, true);
    }

    internal static string[] CheckAllHarnessModes(string release) =>
    ["example-library-only", "software-lookup-only", "engineering-api-only", "", "hmi-only", "native-diagnostics-only", "process-leases-only",
        "native-export-only", "hmi-snapshot-only", "global-script-only", "graphic-selection-only", "runtime-settings-only", "test-migration-read-assembly", "test-ecosystem-assembly",
        "descriptor-catalog-only", "check-modes-self-test",
        .. release == "21" ? new[] { "test-download-route", "test-match-plc-name" } : []];

    private static string[] HarnessArguments(string mode, string key, string api) => mode switch
    {
        "" => [],
        "hmi-only" => [mode, key, ((string?)System.Xml.Linq.XDocument.Load(Path.Combine(Root, "Version.props")).Root!.Element("PropertyGroup")!.Element("TiaMcpRelease")) + ".0"],
        "example-library-only" or "process-leases-only" or "native-export-only" or "hmi-snapshot-only" or "test-match-plc-name" => [mode],
        _ => [mode, api]
    };

    internal static string[] CheckAllCandidateModes() => ["device", "import", "session", "save-close", "compile", "fallback", "source", "export"];

    internal static CheckBuild CheckAllCandidateBuild(string release, string mode)
    {
        var family = mode == "save-close" ? "CLOSE" : mode.ToUpperInvariant();
        return new($"src/Engine/TiaMcp.Engine.V{release}.csproj", "candidate-" + mode + "-" + release,
            ["-p:TiaMcpTestPolicy=P6-" + family + ":safe-v4"]);
    }

    private static CommandResult CheckAllHarness(CommandResult result, string mode, string key)
    {
        if (result.ExitCode != 0) return result;
        switch (mode)
        {
            case "software-lookup-only": RequireCount("software lookup", result.StandardOutput, "COMPLETE: (\\d+) software lookup checks passed", "softwareLookup"); break;
            case "engineering-api-only": RequireCount("engineering API", result.StandardOutput, "COMPLETE: (\\d+) engineering API checks passed", key == "20" ? "engineeringApiV20" : "engineeringApiV21"); break;
            case "": RequireCount("HTTP", result.StandardOutput, "COMPLETE: (\\d+) Foundation transport checks passed"); break;
            case "hmi-only": if (!Regex.IsMatch(result.StandardOutput, "(\\d+) HMI traversal assertions, 0 failed")) throw new ReleaseException("Incomplete HMI traversal"); break;
            case "process-leases-only": RequireCount("process leases", result.StandardOutput, "COMPLETE: (\\d+) process lease checks passed", "processLeases"); break;
            case "native-export-only": RequireCount("native export", result.StandardOutput, "COMPLETE: (\\d+) native export remoting checks passed"); break;
            case "hmi-snapshot-only": RequireCount("HMI snapshot", result.StandardOutput, "COMPLETE: (\\d+) HMI snapshot remoting checks passed"); break;
            case "global-script-only": RequireCount("global script", result.StandardOutput, "COMPLETE: (\\d+) global script bridge checks passed", key == "21" ? "globalScriptV21" : null); break;
            case "graphic-selection-only": RequireCount("graphic selection", result.StandardOutput, "COMPLETE: (\\d+) graphical selection checks passed", "graphicSelection"); break;
            case "runtime-settings-only": RequireCount("runtime settings", result.StandardOutput, "COMPLETE: (\\d+) runtime settings checks passed", key == "20" ? "runtimeSettingsV20" : "runtimeSettingsV21"); break;
            case "test-ecosystem-assembly": RequireCount("ecosystem assembly", result.StandardOutput, "COMPLETE: (\\d+) ecosystem assembly checks passed", "ecosystem"); break;
            case "test-migration-read-assembly": if (!result.StandardOutput.Contains("migration assembly checks passed", StringComparison.OrdinalIgnoreCase)) throw new ReleaseException("Incomplete migration assembly"); break;
            // The fixture checks JIT, open generics and native journal assertions
            // internally; its release wrapper's inventory equality is added below.
        }
        return result;
    }

    private static void ValidateCheckAllApproval(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var evidence = doc.RootElement;
        CheckReleaseCount("approvalSafety", GetJsonInt(evidence, "checksPassed"), "Default approval checks incomplete");
        var results = evidence.GetProperty("results");
        if (GetJsonString(evidence, "status") != "passed" || GetJsonInt(evidence, "checksExpected") != 7 || GetJsonBool(evidence, "workbenchConnected") ||
            GetJsonBool(evidence, "tiaConnected") || GetJsonString(results, "direct") != "refused-before-dispatch; read-succeeded" ||
            GetJsonString(results, "CallTool") != "refused-before-dispatch") throw new ReleaseException("Default approval evidence mismatch");
    }

    private static void ValidateCheckAllJit(string text, string enginePath, string adapterPath)
    {
        using var engine = JsonDocument.Parse(File.ReadAllText(enginePath)); using var adapter = JsonDocument.Parse(File.ReadAllText(adapterPath));
        foreach (var (prefix, evidence) in new[] { ("", engine.RootElement), ("adapter ", adapter.RootElement) })
        {
            var match = Regex.Match(text, "COMPLETE: (\\d+) " + prefix + "native diagnostic wrappers JIT prepared; (\\d+) open generic wrappers");
            if (!match.Success || int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) != GetJsonInt(evidence, "count"))
                throw new ReleaseException(prefix + "diagnostic wrapper JIT/inventory mismatch");
        }
        RequireCount("adapter journal", text, "COMPLETE: (\\d+) adapter integration diagnostic checks passed", "adapterJournal");
        RequireCount("native journal", text, "COMPLETE: (\\d+) native journal reader checks passed", "nativeJournalReader");
    }
}
