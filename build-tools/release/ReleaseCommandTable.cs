namespace TiaMcp.ReleaseTool;

// Each release host check and generator has one binding here. The pipeline resolves
// {major} in log names and keeps count rules and record keys beside the command.
internal sealed record ReleaseCommandSpec(string Name, string Command, string LogFile, string CountRule, string RecordKey);

internal static class ReleaseCommandTable
{
    internal static readonly IReadOnlyList<ReleaseCommandSpec> Entries =
    [
        new("approval-gate-self-test", "Test-ReleaseApprovalGate.py --self-test", "approval-gate-self-test.log", "exit 0", "approvalGateSelfTest"),
        new("native-supervisor", "Test-NativeLifecycle.py --self-test", "native-supervisor.log", "count > 0; live TIA not run", "nativeSupervisor"),
        new("native-mcp-safety", "Test-NativeMcpSession.py --self-test", "native-mcp-safety.log", "minimum 8", "nativeMcpSafety"),
        new("write-guard", "Test-DotnetSuites.py --suite write-guard", "write-guard.log", "minimum 3", "writeGuardChecksPassed"),
        new("crash-evidence", "Test-DotnetSuites.py --suite crash-evidence", "crash-evidence-tests.log", "minimum 7", "crashEvidenceChecksPassed"),
        new("updater", "Test-DotnetSuites.py --suite updater", "updater.log", "minimum 20", "updaterPassed"),
        new("tool-usage", "Generate-ToolUsage.py --check", "tool-usage-catalog.log", "exit 0", "toolUsageCatalog"),
        new("version-catalog", "Test-VersionCatalogWiring.py", "version-catalog-wiring.log", "exit 0", "versionCatalogWiring"),
        new("offline", "Test-DotnetSuites.py --suite offline", "offline.log", "minimum 5153 (Linux 5152)", "offlinePassed"),
        new("offline-v20", "Test-DotnetSuites.py --suite offline-v20", "offline-v20.log", "minimum 5153 (Linux 5152)", "offlineV20Passed"),
        new("version-policy", "Test-DotnetSuites.py --suite version-policy", "version-policy-sdk.log", "minimum 10", "versionPolicySdkPassed"),
        new("native-diagnostics-fixture", "Test-NativeDiagnostics.py --fixture", "native-diagnostics-tests.log", "behavior >= 36; rejection >= 5", "diagnosticTests"),
        new("native-safety", "NativeTests.V{major}.exe --self-test", "native-safety-v{major}.log", "live TIA not run", "nativeHarness.safetyChecksPassed"),
        new("example-library", "HttpTests.exe example-library-only", "example-library-v{major}.log", "exit 0", "exampleLibrary"),
        new("native-coverage", "NativeCallWeaver.dll verify engine", "native-coverage-v{major}.log", "complete inventory", "nativeDiagnostics"),
        new("adapter-native-coverage", "NativeCallWeaver.dll verify adapter", "adapter-native-coverage-v{major}.log", "complete inventory", "nativeDiagnostics.adapter"),
        new("native-diagnostics-jit", "HttpTests.exe native-diagnostics-only", "native-jit-v{major}.log", "JIT + open generic equals inventory; adapter/native journals >= 8/3", "nativeDiagnostics"),
        new("process-leases", "HttpTests.exe process-leases-only", "process-leases-v{major}.log", "minimum 2", "sessionStability.processLeaseChecksPassed"),
        new("worker-supervisor", "HttpTests.exe worker-supervisor-only", "worker-supervisor-v{major}.log", "minimum 25; no TIA", "workerIsolation.faultChecksPassed"),
        new("worker-protocol", "Test-FoundationTransport.py", "worker-protocol-v{major}.log", "minimum 58; no TIA", "workerIsolation.protocolChecksPassed"),
        new("approval-safety", "Test-ReleaseApprovalGate.py --product engine", "approval-safety-v{major}.log", "exactly 7; no workbench/TIA", "approvalSafety"),
        new("software-lookup", "HttpTests.exe software-lookup-only", "software-lookup-v{major}.log", "minimum 45", "softwareLookupPassed"),
        new("engineering-api", "HttpTests.exe engineering-api-only", "engineering-api-v{major}.log", "V20 >= 2840; V21 >= 3126", "engineeringApiShapePassed"),
        new("http-concurrency", "HttpTests.exe concurrency-only", "concurrency-v{major}.log", "exit 0; classification and concurrent audit", "httpConcurrency"),
        new("http", "HttpTests.exe", "http-v{major}.log", "complete runtime regressions", "httpPassed"),
        new("hmi", "HttpTests.exe hmi-only", "hmi-v{major}.log", "18 assertions", "hmiPassed"),
        new("resource-discovery", "Test-ResourceDiscovery.py", "resources-v{major}.log", "complete result", "resourceDiscoveryPassed"),
        new("v21-ecosystem", "Test-V21Ecosystem.py", "v21-ecosystem-v{major}.log", "minimum 75; matching runtime hash; no TIA", "v21EcosystemAdapters"),
        new("local-stability", "Test-LocalStability.py", "stability-v{major}.log", "four runs; matching runtime hash", "localStability"),
        new("isolated-local-stability", "Test-LocalStability.py --isolate-openness", "isolated-stability-v{major}.log", "four isolated runs; matching runtime hash", "isolatedLocalStability"),
        new("native-export", "HttpTests.exe native-export-only", "native-export-v{major}.log", "complete result", "nativeExportRemotingPassed"),
        new("hmi-snapshot", "HttpTests.exe hmi-snapshot-only", "hmi-snapshot-v{major}.log", "complete result", "hmiSnapshotRemotingPassed"),
        new("global-script", "HttpTests.exe global-script-only", "global-script-v{major}.log", "V21 exactly 8", "globalScriptBridgePassed"),
        new("graphic-selection", "HttpTests.exe graphic-selection-only", "graphic-selection-v{major}.log", "minimum 8", "graphicSelectionPassed"),
        new("runtime-settings", "HttpTests.exe runtime-settings-only", "runtime-settings-v{major}.log", "V20 >= 8; V21 >= 9", "runtimeSettingsPassed"),
        new("migration-assembly", "HttpTests.exe test-migration-read-assembly", "assembly-v{major}.log", "exit 0", "migrationAssembly"),
        new("ecosystem-assembly", "HttpTests.exe test-ecosystem-assembly", "ecosystem-v{major}.log", "minimum 31", "ecosystemAssemblyPassed"),
        new("download-route", "HttpTests.exe test-download-route", "route-selection-v21.log", "exit 0", "downloadRouteSelection"),
        new("match-plc-name", "HttpTests.exe test-match-plc-name", "match-plc-name-v21.log", "exit 0", "matchPlcName"),
        new("generate-tools-list", "HttpTests.exe generate-tools-list", "tools-list.log", "compiled roster generated", "toolList"),
        new("tool-capability-matrix", "Generate-ToolCapabilityMatrix.cs", "tool-matrix.log", "generated bytes match", "toolCapabilityMatrix")
    ];

    internal static ReleaseCommandSpec Get(string name) => Entries.Single(entry => entry.Name == name);
}
