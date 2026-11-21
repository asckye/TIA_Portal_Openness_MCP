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

    private static void AddChecks(Plan plan, string[] changes, string dotnet, string python, string api, string output)
    {
        void Tool(string name, params string[] args) => plan.Checks.Add(new(name, dotnet, [typeof(Program).Assembly.Location, name, .. args]));
        void Py(string name, string script, params string[] args) => plan.Checks.Add(new(name, python, ["scripts/checks/" + script, .. args]));
        foreach (var name in new[] { "generate-catalogs", "generate-tool-usage", "generate-release-migration", "generate-tool-capability-matrix" }) Tool(name, "-Check");
        foreach (var name in new[] { "check-tools-list", "check-script-tool-calls", "check-adapter-boundary", "check-tia-features" }) Tool(name);
        Tool("check-analyzer-warnings", "-Check", "-DiagnosticsDirectory", Path.Combine(output, "analyzers"));
        Py("contract-verify", "Snapshot-ToolContracts.py", "verify");
        plan.Checks.Add(new("match-plc-name", dotnet, ["run", "scripts/checks/Test-MatchPlcName.cs", "--", "-SourceOnly"]));
        bool Changed(params string[] names) => plan.Wide || changes.Any(path => names.Any(name => path == "scripts/checks/" + name));
        foreach (var script in new[] { "Test-ReleaseApprovalGate.py", "Test-RelocatedBundle.py", "Test-ReleaseSmoke.py", "Compare-NativeCallOrder.py",
            "Test-NativeLifecycle.py", "Test-NativeMcpSession.py", "Test-ResourceDiscovery.py" })
            if (Changed(script)) Py(Path.GetFileNameWithoutExtension(script), script, "--self-test");
        if (Changed("Test-FoundationTransport.py", "Test-ResourceDiscovery.py"))
            Py("Test-FoundationTransport", "Test-FoundationTransport.py", "--self-test");
        if (Changed("Snapshot-ToolContracts.py", "phase6_groups.py", "snapshot_text_migration.py", "offline_fixtures.py")) Py("contract-self-test", "Snapshot-ToolContracts.py", "self-test");
        if (Changed("Test-V21Ecosystem.py")) Py("ecosystem-self-test", "Test-V21Ecosystem.py", "--self-test", "--output", Path.Combine(output, "ecosystem"));
        if (Changed("Test-CampaignInputs.py", "Test-CampaignInputs.cs"))
        {
            foreach (var path in changes.Where(path => path is "scripts/checks/Test-CampaignInputs.py" or "scripts/checks/Test-CampaignInputs.cs"))
                Reason(plan.Builds, "file:scripts/checks/Test-CampaignInputs.cs", path);
            if (plan.Wide) Reason(plan.Builds, "file:scripts/checks/Test-CampaignInputs.cs", "wide change");
            Py("campaign-self-test", "Test-CampaignInputs.py", "--self-test");
            Py("campaign-validator-self-test", "Test-CampaignInputs.py", "--validator-self-test");
        }
        if (plan.Builds.ContainsKey(ConfigurationProject)) plan.Checks.Add(new("configuration-page", dotnet,
            ["run", "--project", ConfigurationProject, "-c", "Release", "--no-build", "--no-restore", "--", Path.Combine(output, "configuration")]));
        if (plan.Wide || changes.Any(path => new[] { "src/Adapters", "src/Adapters.Contracts", "src/PlcWorker" }.Any(dir => Under(path, dir))))
        {
            plan.Checks.Add(new("adapter-inputs", dotnet, ["run", "--no-build", "-c", "Release", "src/Adapters/build/Test-AdapterInputs.cs", "--", "--source-root", "src", "--public-api-root", api, "--evidence-directory", Path.Combine(output, "adapter-inputs")], true));
            plan.Checks.Add(new("worker-isolation", dotnet, ["run", "--no-build", "-c", "Release", "src/Adapters/build/Test-WorkerIsolation.cs", "--", "--evidence-directory", Path.Combine(output, "worker-isolation"), "--native-call-weaver", "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll"], true));
            Reason(plan.Builds, "file:src/Adapters/build/Test-AdapterInputs.cs", "adapter-inputs");
            Reason(plan.Builds, "file:src/Adapters/build/Test-WorkerIsolation.cs", "worker-isolation");
            Reason(plan.Builds, "eight-release-table", "worker-isolation");
        }
        if (plan.Wide || plan.Builds.ContainsKey(DiagnosticsProject) || changes.Any(path => Under(path, "src/Adapters") || Under(path, "build-tools/native-call-weaver")) || Changed("Test-NativeDiagnostics.py"))
        {
            Reason(plan.Builds, DiagnosticsProject, "native-diagnostics");
            Reason(plan.Builds, "build-tools/native-call-weaver/NativeCallWeaver.csproj", "native-diagnostics");
            plan.Checks.Add(new("native-diagnostics", python, ["scripts/checks/Test-NativeDiagnostics.py", "--fixture", "tests/FoundationHost/TiaMcp.Engine.Diagnostics.Tests/bin/Release/net48/TiaMcp.Engine.Diagnostics.Tests.exe",
                "--weaver", "build-tools/native-call-weaver/bin/Release/net10.0/NativeCallWeaver.dll", "--output", Path.Combine(output, "native-diagnostics")], true));
        }
    }

    private static void AddWorkerChecks(Plan plan, string[] changes, bool all, int parallelism, string dotnet, string python, string api, string output,
        string? transportFallback)
    {
        bool transport = plan.Wide || changes.Any(path =>
            new[] { "src/FoundationHost", "src/ToolPipeline", "src/WorkerChannel", "src/PlcWorker", "src/Shared",
                "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture" }.Any(dir => Under(path, dir))
            || path is "scripts/checks/Test-FoundationTransport.py" or "scripts/checks/Test-ResourceDiscovery.py"
                or "scripts/checks/Test-StdioFraming.py");
        bool worker = plan.Wide || changes.Any(path =>
            new[] { "src/PlcWorker", "src/WorkerChannel", "src/Adapters", "src/Adapters.Contracts", "src/ToolPipeline", "src/FoundationHost",
                "src/Shared", "reference/tool-catalog", "reference/tool-examples", "build-tools/native-call-weaver", "tests/FoundationHost/TiaMcp.Engine.Native.Tests",
                "tests/FoundationHost/TiaMcp.Engine.ApiCompileChecks", "tests/Adapters/TiaMcp.Adapters.Diagnostics.Tests" }.Any(dir => Under(path, dir))
            || path is "scripts/checks/Test-ToolUsage.py" or "scripts/checks/tool_usage_checks.py"
                or "scripts/checks/Test-CampaignInputs.py" or CampaignProgram
                or "build-tools/release/CatalogGenerator.cs" or "build-tools/release/CatalogData.cs"
                or "build-tools/release/ToolUsageGenerator.cs" or "build-tools/release/ToolCountGenerator.cs");
        bool approval = all || changes.Any(path =>
            new[] { "src/FoundationHost", "src/ToolPipeline", "src/WorkerChannel", "src/PlcWorker", "src/Adapters",
                "src/Adapters.Contracts", "src/Shared" }.Any(dir => Under(path, dir))
            || (Under(path, "src") || Under(path, "build-tools")) && path.Contains("Approval", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("build-tools/release/Prepare", StringComparison.Ordinal)
            || path.StartsWith("build-tools/release/Bundle", StringComparison.Ordinal)
            || path is "build-tools/release/PlcReleaseChecks.cs" or "build-tools/release/ReleaseCommands.cs"
                or "build-tools/release/PackageDependencies.cs");
        if (!transport && !worker && !approval) return;
        var reason = plan.Wide ? "wide change" : string.Join(", ", changes);
        Reason(plan.Builds, "eight-release-table", reason);
        Reason(plan.Builds, SourceHostProject, reason);
        Reason(plan.Builds, "plc-fixtures", reason);
        var host = "src/FoundationHost/bin/Release/net10.0/TiaMcp.FoundationHost.exe";
        void Py(string name, string script, bool post, params string[] args) =>
            plan.Checks.Add(new(name, python, ["scripts/checks/" + script, .. args], PostSuite: post, Isolated: true));
        if (approval)
        {
            foreach (var key in new[] { "20", "21" })
            {
                var name = "approval-v" + key + "-host";
                var sdk = Path.Combine(api, ReleaseCommands.PlcWorkerApi(key).Directory);
                Py(name, "Test-ReleaseApprovalGate.py", false, "--product", "engine", "--major", key, "--exe", host,
                    "--portal-root", sdk, "--public-api", sdk, "--plc-worker", ReleaseCommands.ReleasePlcFixture(output, key),
                    "--temp-root", Path.Combine(output, "checks", name, "temp"), "--output", Path.Combine(output, name));
            }
        }
        if (transport)
        {
            Reason(plan.Builds, TransportFixtureProject, reason);
            var maxParallel = TransportParallelism(transportFallback, parallelism).ToString(CultureInfo.InvariantCulture);
            const string fixture = "tests/WorkerChannel/TiaMcp.WorkerChannel.TransportFixture/bin/Release/net10.0/TiaMcp.WorkerChannel.TransportFixture.exe";
            Py("foundation-transport", "Test-FoundationTransport.py", true, "--max-parallel", maxParallel, "--host-exe", host, "--fixture", fixture,
                "--releases", "14sp1", "15.1", "16", "17", "18", "19", "--temp-root", Path.Combine(output, "transport-temp"),
                "--output", Path.Combine(output, "transport"));
            Py("foundation-transport-plc", "Test-FoundationTransport.py", true, "--max-parallel", maxParallel, "--host-exe", host, "--fixture", fixture,
                "--plc-worker", "--releases", "14sp1", "15.1", "16", "17", "18", "19", "20", "21",
                "--plc-sdk-worker", "20=" + ReleaseCommands.ReleasePlcFixture(output, "20"),
                "--plc-sdk-worker", "21=" + ReleaseCommands.ReleasePlcFixture(output, "21"),
                "--public-api-root", api, "--temp-root", Path.Combine(output, "transport-plc-temp"), "--output", Path.Combine(output, "transport-plc"));
            Py("stdio-framing", "Test-StdioFraming.py", true, "--exe", host, "--release", "19", "--iterations", "2000",
                "--output", Path.Combine(output, "stdio-framing"));
        }
        if (!worker) return;
        Reason(plan.Builds, ApiCompileProject, reason);
        Reason(plan.Builds, AdapterDiagnosticsProject, reason);
        Reason(plan.Builds, "campaign-file-program", reason);
        foreach (var key in new[] { "20", "21" })
        {
            var native = $"tests/FoundationHost/TiaMcp.Engine.Native.Tests/V{key}/TiaMcp.Engine.Native.Tests.V{key}.csproj";
            Reason(plan.Builds, native, reason);
            plan.Checks.Add(new("native-safety-v" + key + "-plc",
                $"tests/FoundationHost/TiaMcp.Engine.Native.Tests/V{key}/bin/Release/net48/TiaMcp.Engine.Native.Tests.V{key}.exe",
                ["--self-test"], Isolated: true));
            Py("usage-v" + key + "-plc", "Test-ToolUsage.py", false, "--foundation-host", host,
                "--plc-worker", key + "=" + ReleaseCommands.ReleasePlcFixture(output, key), "--public-api-root", api,
                "--releases", key, "--evidence", Path.Combine(output, "usage-v" + key + "-plc"),
                "--coverage-output", Path.Combine(output, "usage-v" + key + "-plc.json"));
            Py("campaign-inputs-v" + key + "-plc", "Test-CampaignInputs.py", false, "--exe", host, "--major", key,
                "--public-api", Path.Combine(api, ReleaseCommands.PlcWorkerApi(key).Directory),
                "--plc-worker", ReleaseCommands.ReleasePlcFixture(output, key), "--output", Path.Combine(output, "campaign-v" + key + "-plc"));
        }
    }

    internal static string TransportFallbackPath(string root, Func<string[], CommandResult> git) =>
        Path.Combine(Path.GetDirectoryName(TimingHistory(root, git))!, "transport-parallelism-4.txt");

    internal static int TransportParallelism(string? transportFallback, int parallelism) =>
        Math.Min(File.Exists(transportFallback) ? 4 : 8, parallelism);

    internal static IDictionary<string, string?> CheckEnvironment(Job job, string output, string api,
        IDictionary<string, string?> environment)
    {
        var result = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase);
        if (!job.Isolated) return result;
        var directory = Path.GetFullPath(Path.Combine(output, "checks", job.Name));
        foreach (var child in new[] { "config", "temp", "local-app-data", "app-data" }) Directory.CreateDirectory(Path.Combine(directory, child));
        File.WriteAllText(Path.Combine(directory, "config/approval.settings"), "enabled=true\ntimeoutSeconds=120\n");
        result["TIA_MCP_DATA_DIRECTORY"] = directory;
        result["TIA_MCP_DIAGNOSTICS_DIRECTORY"] = Path.Combine(directory, "diagnostics");
        result["TEMP"] = result["TMP"] = result["TMPDIR"] = Path.Combine(directory, "temp");
        result["LOCALAPPDATA"] = Path.Combine(directory, "local-app-data");
        result["APPDATA"] = Path.Combine(directory, "app-data");
        result["TIA_MCP_RELEASE_TEMP_ROOT"] = Path.GetFullPath(output);
        result["TIA_MCP_TEST_PUBLIC_API_ROOT"] = api;
        result["TIA_MCP_WRITE_GUARD"] = "1";
        result["TIA_MCP_BUNDLE_ROOT"] = result["CLAUDE_PLUGIN_ROOT"] = null;
        result[ReleaseCommands.PlcReleaseSystemTempVariable] = Path.GetFullPath(Path.Combine(output, "file-apps"));
        return result;
    }

    // Python is a bundle process name, so native diagnostics must finish outside the updater group.
    internal static IEnumerable<Job> UpdaterCompanionChecks(Plan plan) =>
        plan.Checks.Where(job => job.Serial && job.Name != "native-diagnostics");
}
