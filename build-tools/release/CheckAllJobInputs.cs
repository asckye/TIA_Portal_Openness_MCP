using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.ReleaseTool;

internal sealed record CheckJobInputs(string Driver, string[] Paths, string[] Projects, string Arguments,
    string[] Outputs, string? Release = null, bool Aggregate = false, string[]? BinaryRoots = null, string[]? FilePrograms = null,
    bool SourceOnly = false, bool DefaultPython = false);

// Declarative inputs for checks outside the suite registry. Paths are additive:
// script helpers and data directories are intentionally conservative closures.
internal static class CheckAllJobInputs
{
    internal static readonly string[] Controls = ["Version.props", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", ".editorconfig", "NuGet.Config"];
    private const string Host = "src/FoundationHost/TiaMcp.FoundationHost.csproj";
    private const string Worker = "src/PlcWorker/TiaMcp.PlcWorker.csproj";
    private const string Harness = "tests/Engine/TiaMcp.Engine.Harness/TiaMcp.Engine.Harness.csproj";
    private const string Transport = "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/TiaMcp.WorkerChannel.TransportFixture.csproj";
    private const string ReleaseTool = "build-tools/release/TiaMcp.ReleaseTool.csproj";
    private static readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal)
    {
        ["campaign-runner-self-tests"] = "Test-CampaignInputs.py --self-test",
        ["check-all-speedup-self-tests"] = "Test-CheckAllSpeedups.py",
        ["plc-script-self-tests"] = "Test-PlcScriptSuccessors.py",
        ["plc-harness-self-tests"] = "Test-PlcHarnessSuccessors.py",
        ["campaign-validator-self-tests"] = "Test-CampaignInputs.py --validator-self-test",
        ["domain-source"] = "Test-DomainTools.py --source-only",
        ["contract-self-tests"] = "Snapshot-ToolContracts.py self-test",
        ["contract-verify"] = "Snapshot-ToolContracts.py verify",
        ["response-verify"] = "Snapshot-ToolResponses.py verify",
        ["native-lifecycle"] = "Test-NativeLifecycle.py --self-test",
        ["native-mcp-session"] = "Test-NativeMcpSession.py --self-test",
        ["approval-self-tests"] = "Test-ReleaseApprovalGate.py --self-test",
        ["relocation-self-tests"] = "Test-RelocatedBundle.py --self-test"
    };
    internal static readonly string[] ToolJobs = ["generate-catalogs", "prepare-decoder-python", "generate-tool-usage", "generate-release-migration", "build-once",
        "analyzer-warnings", "tracked-tree-drift", "catalog-drift", "tool-counts-drift", "usage-drift", "migration-drift",
        "tools-list-self-tests", "tools-list", "script-calls", "dead-references", "repository", "repository-self-tests", "suite-runner-self-tests",
        "adapter-boundary", "tia-features", "bundle", "bundle-self-tests", "release-documentation", "release-dry-run", "preflight",
        "production-compile-only", "match-plc-name-source", "configuration-page", "affected-inputs-self-test"];

    internal static CheckJobInputs? Find(CheckJob job, JsonObject catalog)
    {
        var suite = catalog.Select(pair => pair.Key).OrderByDescending(name => name.Length).FirstOrDefault(name =>
            // "-reference" binds the suite's merged result to its runtime binaries (engine-host, SPD-1); same inputs as the suite.
            job.Name == name || job.Name == name + "-gate" || job.Name == name + "-coverage-proof" || job.Name == name + "-reference" ||
            Regex.IsMatch(job.Name, "^" + Regex.Escape(name) + @"-(?:shard-\d+|sequential)$"));
        if (suite != null)
            return new("registered suite", ["tests/test-suites.json"], [(string)catalog[suite]!["project"]!],
                catalog[suite]!.ToJsonString(), [], Release: SuiteRelease(catalog[suite]!.AsObject()), Aggregate: job.ResultInputs == null);
        if (Scripts.TryGetValue(job.Name, out var command))
            return new("scripts/checks/" + command.Split(' ')[0],
                [.. CheckAllScriptInputs.Closure("scripts/checks/" + command.Split(' ')[0]), .. ScriptData(job.Name)],
                job.Name == "campaign-validator-self-tests" ? ["src/Logic/TiaMcp.Logic.csproj"] : [], command, [],
                FilePrograms: job.Name == "campaign-validator-self-tests" ? ["scripts/checks/Test-CampaignInputs.cs"] : null,
                SourceOnly: ReleaseCommands.CheckAllSourceOnly(job.Name));
        if (ToolJobs.Contains(job.Name))
            return new("release-tool", ["build-tools/release", "scripts", "src", "tests", "reference", "manifest", "templates", "catalog", "third_party"],
                job.Name == "configuration-page" ? ["tests/Studio/TiaOpenness.Configuration.Tests/TiaOpenness.Configuration.Tests.csproj"]
                : job.Name == "match-plc-name-source" ? ["scripts/checks/Test-MatchPlcName.cs"] : [], job.Name, [],
                Aggregate: job.Name is "preflight" or "build-once",
                BinaryRoots: job.Name == "match-plc-name-source" ? ["bin-build/check-scripts/Test-MatchPlcName"] : null,
                SourceOnly: ReleaseCommands.CheckAllSourceOnly(job.Name));
        if (job.Name == "usage-audit-plc")
            return new("scripts/checks/Test-ToolUsage.py", [.. CheckAllScriptInputs.Closure("scripts/checks/Test-ToolUsage.py"), .. CheckAllScriptInputs.Data("usage"), "manifest/tool-usage-coverage.json"], [Host, Worker],
                "coverage; CatalogAudits.Usage; exact manifest bytes", [], Aggregate: true);
        var match = Regex.Match(job.Name, @"^(?<family>contracts|responses|transport-usage|usage|campaign-inputs|approval|resources|ecosystem|stability|harness|adapter-coverage|host-behavior-parity|native-safety|native-coverage)-v(?<key>14sp1|15\.1|16|17|18|19|20|21)(?<suffix>.*)$");
        if (!match.Success) return null;
        string family = match.Groups["family"].Value, key = match.Groups["key"].Value;
        if (family is "host-behavior-parity" or "native-safety" or "native-coverage")
        {
            if (key is not ("20" or "21") || match.Groups["suffix"].Value != (family == "host-behavior-parity" ? "" : "-plc")) return null;
            var receipt = "binary-receipts/" + job.Name + ".json";
            return family switch
            {
                "host-behavior-parity" => new("build-tools/release/HostParity.cs",
                    ["build-tools/release/HostParity.cs", "build-tools/release/BinaryCheckReceipts.cs"],
                    [ReleaseTool, SuiteWorkerCopies.FoundationProject, Host, "src/EngineHost/TiaMcp.EngineHost.csproj",
                        "tests/Fixtures/TiaMcp.Offline.Fixtures/TiaMcp.Offline.Fixtures.csproj"],
                    "-EngineMajor " + key + " -NoBuild; variant=" + HostParity.Unit(key).Variant + "; FullyQualifiedName~BehaviorParityTests",
                    [job.Name + "/host-behavior-parity.json", job.Name + "/host-behavior-parity.trx"], key),
                "native-safety" => new($"tests/Engine/TiaMcp.Engine.Native.Tests/V{key}/bin/Release/net48/TiaMcp.Engine.Native.Tests.V{key}.exe",
                    ["build-tools/release/BinaryCheckReceipts.cs"],
                    [ReleaseTool, $"tests/Engine/TiaMcp.Engine.Native.Tests/V{key}/TiaMcp.Engine.Native.Tests.V{key}.csproj"],
                    "--self-test", [receipt], key),
                _ => new("build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll",
                    ["build-tools/release/BinaryCheckReceipts.cs"], [ReleaseTool, Worker, "build-tools/native-call-weaver/NativeCallWeaver.csproj"],
                    "verify TiaMcp.Adapter." + key + ".dll", [job.Name + ".json", receipt], key)
            };
        }
        string? script = family switch
        {
            "contracts" => "Snapshot-ToolContracts.py", "responses" => "Snapshot-ToolResponses.py",
            "transport-usage" => "Test-FoundationTransport.py", "usage" => "Test-ToolUsage.py",
            "campaign-inputs" => "Test-CampaignInputs.py", "approval" => "Test-ReleaseApprovalGate.py",
            "resources" => "Test-ResourceDiscovery.py", "ecosystem" => "Test-V21Ecosystem.py",
            "stability" => "Test-LocalStability.py", "harness" => "Test-PlcHarness.py", "adapter-coverage" => null, _ => null
        };
        var projects = new List<string> { Host };
        if (key is "20" or "21") projects.Add(Worker);
        else projects.Add($"src/Adapters/V{(key == "15.1" ? "15_1" : key == "14sp1" ? "14Sp1" : key)}/Adapter.{key}.csproj");
        if (family is "harness" or "stability" or "ecosystem" or "approval") projects.Add(Harness);
        if (family == "transport-usage") projects.Add(Transport);
        if (family == "adapter-coverage") projects.Add("build-tools/native-call-weaver/NativeCallWeaver.csproj");
        var paths = new List<string>(CheckAllScriptInputs.Data(family));
        if (script != null) paths.AddRange(CheckAllScriptInputs.Closure("scripts/checks/" + script));
        if (family == "harness" && job.Name.Contains("native-diagnostics-only", StringComparison.Ordinal))
        {
            projects.AddRange(["tests/FoundationHost/TiaMcp.FoundationHost.Tests/TiaMcp.FoundationHost.Tests.csproj", Transport,
                "tests/WorkerChannel/TiaMcp.PlcWorker.FaultFixture/TiaMcp.PlcWorker.FaultFixture.csproj"]);
            paths.AddRange(["src", "tests"]);
        }
        string[] outputs = family switch
        {
            "responses" => [(key is "20" or "21" ? "responses-plc/" : "responses/") + key + ".json",
                (key is "20" or "21" ? "responses-plc" : "responses") + "-latency-" + key + ".json"],
            "contracts" => [(key is "20" or "21" ? "contracts-plc/" : "contracts/") + key + ".json"],
            "usage" or "resources" or "adapter-coverage" => [job.Name + ".json"], _ => [job.Name]
        };
        return new(script == null ? "native-call-weaver verify" : "scripts/checks/" + script,
            paths.ToArray(),
            projects.ToArray(), job.Name + (family == "stability" ? "; rounds=50; original growth/time limits" : ""), outputs, key,
            FilePrograms: family == "campaign-inputs" || family == "harness" &&
                (job.Name.Contains("script-inputs-only", StringComparison.Ordinal) || job.Name.Contains("check-modes-self-test", StringComparison.Ordinal))
                ? ["scripts/checks/Test-CampaignInputs.cs"] : null);
    }

    private static string[] ScriptData(string name) => name switch
    {
        // These checks intentionally scan repository-wide source or documentation.
        "check-all-speedup-self-tests" => [],
        "contract-self-tests" => ["src/EngineHost", "src/FoundationHost", "src/Logic/V4", "src/Logic/ModelContextProtocol/ToolProfiles.resx", "src/Adapters.Contracts/PortedFamilies.cs", "manifest/contracts/v4/baseline"],
        "domain-source" => ["src", "tests", "reference"],
        "contract-verify" or "response-verify" => ["src", "reference", "manifest/contracts/v4"],
        "plc-script-self-tests" or "plc-harness-self-tests" => ["build-tools/release/CheckAll.cs", "build-tools/release/CheckAllInventory.cs", "tests/Engine/TiaMcp.Engine.Harness"],
        _ => CheckAllScriptInputs.Data(name)
    };

    private static string? SuiteRelease(JsonObject suite)
    {
        var keys = (suite["arguments"]?.AsArray() ?? []).Select(value => Regex.Match((string)value!,
            @"^-p:(?:TiaReleaseKey|EngineRelease|AdapterReleaseKey)=(?:V)?(14sp1|15[._]1|16|17|18|19|20|21)$", RegexOptions.IgnoreCase))
            .Where(match => match.Success).Select(match => match.Groups[1].Value.Replace('_', '.').ToLowerInvariant()).Distinct().ToArray();
        return keys.Length == 1 ? keys[0] : null;
    }

    internal static string[] Undeclared(IEnumerable<CheckJob> jobs, JsonObject catalog) => jobs.Where(job => Find(job, catalog) == null).Select(job => job.Name).ToArray();
    internal static CommandResult Validate(IEnumerable<CheckJob> jobs, JsonObject catalog)
    {
        var missing = Undeclared(jobs, catalog);
        return new(missing.Length == 0 ? 0 : 1, $"affected input declarations: {missing.Length} missing\n",
            string.Join("\n", missing.Select(name => "Undeclared job (always run): " + name)));
    }

    internal static bool AlwaysRun(string name) => name.StartsWith("generate-", StringComparison.Ordinal) || name is "prepare-decoder-python" or
        "catalog-drift" or "tool-counts-drift" or "usage-drift" or "migration-drift" or "tracked-tree-drift" or "repository" or
        "dead-references" or "script-calls" or "adapter-boundary" or "domain-source" or "release-documentation" or
        "analyzer-warnings" or "tools-list" or "contract-verify" or "response-verify" or "bundle" or
        "build-once" or "affected-inputs-self-test";
    internal static bool NightlyOnly(string name) => name.StartsWith("stability-", StringComparison.Ordinal) && name.Contains("-c16-", StringComparison.Ordinal);
}
