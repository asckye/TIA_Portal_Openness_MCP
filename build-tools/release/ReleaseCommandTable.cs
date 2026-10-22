namespace TiaMcp.ReleaseTool;

// Each release host check and generator has one binding here. The pipeline resolves
// {major} in log names and keeps count rules and record keys beside the command.
internal sealed record ReleaseCommandSpec(string Name, string Command, string LogFile, string CountRule, string RecordKey);

internal static class ReleaseCommandTable
{
    internal static readonly string[] OfflineSuites = ["offline", "offline-v20", "kept-code", "kept-code-v20"];

    internal static readonly IReadOnlyList<ReleaseCommandSpec> Entries =
    [
        new("build-solutions", "build-solutions -PublicApiRoot <SDK-root>", "solutions.log", "both solutions; exit 0", "developerSolutions"),
        new("build-file-programs", "build-file-programs", "file-programs.log", "all in-scope file apps; exit 0", "filePrograms"),
        new("publish-vci-watch", "publish-vci-watch", "vci-watch-publish.log", "WinExe in bin-build/vci-watch; exit 0", "vciWatchPublish"),
        new("test-suites", "test-suites -Suite <name> | -SelfTest", "test-suites.log", "registered TRX gate", "dotnetSuites"),
        new("host-parity", "host-parity -EngineMajor <20|21>", "host-behavior-parity.log", "minimum 103; zero skips", "hostBehaviorParity"),
        new("check-ratchet", "check-ratchet -Kind swallowed|comments|mcp-text|envelopes", "ratchet.log", "shrinking baseline; zero issues", "ratchets"),
        new("check-analyzer-warnings", "check-analyzer-warnings -Check [-Complete] | -UpdateBaseline -Complete", "analyzer-warnings.log", "reviewed compiler warnings; no new or stale entries", "analyzerWarnings"),
        new("check-envelope-rewrite", "check-envelope-rewrite -Base <commit> -Path <pathspec>", "envelope-rewrite.log", "closed templates; exit 0", "envelopeRewrite"),
        new("check-adapter-boundary", "check-adapter-boundary", "adapter-boundary.log", "zero forbidden dependencies", "adapterBoundary"),
        new("check-bundle-layout", "check-bundle-layout", "bundle-layout.log", "complete resource table", "bundleLayout"),
        new("check-repository", "check-repository -NoBinaries | -PackageMode -Root <bundle>", "repository.log", "zero issues", "repository"),
        new("check-dead-tool-references", "check-dead-tool-references [-Fix]", "dead-references.log", "sentinels and zero dead references", "deadReferences"),
        new("check-tia-features", "check-tia-features [-Capture <path>]", "tia-features.log", "reviewed defines identical", "tiaFeatures"),
        new("check-script-tool-calls", "check-script-tool-calls", "script-tool-calls.log", "registered C# and JSON calls", "scriptToolCalls"),
        new("approval-gate-self-test", "Test-ReleaseApprovalGate.py --self-test", "approval-gate-self-test.log", "exit 0", "approvalGateSelfTest"),
        new("native-supervisor", "Test-NativeLifecycle.py --self-test", "native-supervisor.log", "count > 0; live TIA not run", "nativeSupervisor"),
        new("native-mcp-safety", "Test-NativeMcpSession.py --self-test", "native-mcp-safety.log", "minimum 8", "nativeMcpSafety"),
        new("write-guard", "test-suites -Suite write-guard", "write-guard.log", "minimum 3", "writeGuardChecksPassed"),
        new("crash-evidence", "test-suites -Suite crash-evidence", "crash-evidence-tests.log", "minimum 7", "crashEvidenceChecksPassed"),
        new("updater", "test-suites -Suite updater", "updater.log", "minimum 20", "updaterPassed"),
        new("tool-counts", "generate-tool-counts -Check", "tool-counts.log", "every documentation block matches catalogs", "toolCounts"),
        new("tool-usage", "generate-tool-usage -Check", "tool-usage-catalog.log", "exit 0", "toolUsageCatalog"),
        new("version-catalog", "test-suites -Suite source-contracts", "version-catalog-wiring.log", "registered TRX gate", "versionCatalogWiring"),
        new("offline", "test-suites -Suite offline", "offline.log", "minimum 303 (Linux 302)", "offlinePassed"),
        new("offline-v20", "test-suites -Suite offline-v20", "offline-v20.log", "minimum 303 (Linux 302)", "offlineV20Passed"),
        new("kept-code", "test-suites -Suite kept-code", "kept-code.log", "minimum 4854; zero skips", "keptCodePassed"),
        new("kept-code-v20", "test-suites -Suite kept-code-v20", "kept-code-v20.log", "minimum 4854; zero skips", "keptCodeV20Passed"),
        new("worker-progress-foundation", "test-suites -Suite worker-progress-foundation", "worker-progress-foundation.log", "minimum 3; zero skips", "foundationProgressPassed"),
        new("shared-lifecycle-plc", "test-suites -Suite shared-lifecycle-plc", "shared-lifecycle-plc.log", "minimum 46; zero skips", "plcSharedLifecyclePassed"),
        new("plc-exchange-worker-v20", "test-suites -Suite plc-exchange-worker-v20", "plc-exchange-worker-v20.log", "minimum 128; zero skips", "plcExchangeV20Passed"),
        new("plc-exchange-worker-v21", "test-suites -Suite plc-exchange-worker-v21", "plc-exchange-worker-v21.log", "minimum 128; zero skips", "plcExchangeV21Passed"),
        new("version-policy", "test-suites -Suite version-policy", "version-policy-sdk.log", "minimum 10", "versionPolicySdkPassed"),
        new("native-diagnostics-fixture", "Test-NativeDiagnostics.py --fixture", "native-diagnostics-tests.log", "behavior >= 36; rejection >= 5", "diagnosticTests"),
        new("tool-capability-matrix", "Generate-ToolCapabilityMatrix.cs", "tool-matrix.log", "generated bytes match", "toolCapabilityMatrix"),
        .. HarnessEntries(),
        .. PlcReleaseEntries()
    ];

    // P8-04l: the engine harness entries retired with the engine; each mode runs as its PLC job.
    private static IEnumerable<ReleaseCommandSpec> HarnessEntries()
    {
        foreach (var key in new[] { "20", "21" })
        foreach (var mode in ReleaseCommands.CheckAllPlcHarnessModes(key))
        {
            string selected = mode.Length == 0 ? "http" : mode;
            string plc = ReleaseCommands.CheckAllPlcHarnessJob(key, mode);
            yield return new(plc, "Test-PlcHarness.py --release " + key + " --mode " + selected
                + " --host <FoundationHost> --worker <SDK-ready-PlcWorker> --harness <harness> --api <PublicAPI> --public-api-root <SDK-root> --output <fresh-output>"
                + (mode == "native-diagnostics-only" ? " --adapter-inventory <adapter-coverage.json>" : ""),
                plc + ".log", "mode assertions on the host/adapter/worker; the retired engine job's count gates retained", "harness." + plc);
        }
    }

    internal static ReleaseCommandSpec Get(string name) => Entries.Single(entry => entry.Name == name);

    private static IEnumerable<ReleaseCommandSpec> PlcReleaseEntries() => ReleaseCommands.PlcReleaseJobs(
        "<worktree>", "<SDK-root>", "<logs>", "<temp>", "dotnet", "python")
        .Where(job => !job.Name.StartsWith("harness-", StringComparison.Ordinal))
        .Select(job => new ReleaseCommandSpec(job.Name, job.Executable + " " + string.Join(' ', job.Arguments), job.Name + ".log",
            "registered/count/evidence gates of the retired engine job; exact snapshot bytes; no TIA", "plcChecks." + job.Name));
}
