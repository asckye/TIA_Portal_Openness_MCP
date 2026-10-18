using System;
using System.Collections.Generic;
using System.IO;

#if TIA_RELEASE_TEST_SCHEDULING
namespace TiaMcp.ReleaseTool
#else
namespace TiaMcp.Engine.Tests.Shared
#endif
{
    // Shared by the scheduler and executor so unknown/unmeasured cases receive
    // the same isolation as measured cases. These are execution partitions only.
    public static class TimeSensitiveTests
    {
        public static readonly IReadOnlyDictionary<string, string> Classes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EngineHttpSessionTests"] = "Worker startup with scaled deadlines; bounded HTTP readiness/shutdown.",
            ["WorkerRobustnessTests"] = "Scaled worker budgets, dispatch cancellation and real timeout measurements.",
            ["PlcWorkerRobustnessTests"] = "One-second startup/default budgets and 1.3-second synthetic native calls.",
            ["EngineWorkerClientTests"] = "Worker startup and disposal measured against ten/two-second bounds.",
            ["FoundationProcessLeaseTests"] = "Child readiness/exit bounds and durable-request overhead measurement.",
            ["FoundationChecks"] = "Indivisible legacy producer includes bounded adapter evaluation and FIFO fixture startup.",
            ["PortedPortalAdmissionTests"] = "Two-second queued worker lane acquisition.",
            ["ApprovalPrecheckTests"] = "Warm approval precheck latency samples.",
            ["FoundationCoverageRegressionTests"] = "Before/after symbol lookup latency samples.",
            ["PipelineTests"] = "Local tool p95 below 100 ms during a sixty-second worker call.",
            ["ConcurrencyTests"] = "Two-second queued worker lane acquisition.",
            ["OperatorProtocolTests"] = "One-second pipe connect and three-second graceful shutdown measurements.",
            ["OperatorSessionTests"] = "Bounded worker dispatch and MCP shutdown.",
            ["SaveCloseCandidateTests"] = "Synthetic channel connect within three seconds.",
            ["TiaMcp.FoundationHost.Tests.SessionCandidateTests"] = "Synthetic channel connect within three seconds.",
            ["WorkbenchControlHostTests"] = "Pipe connect/response deadlines and bounded request deadline assertions.",
            ["TiaMcp.Engine.Tests.ApprovalPrecheckTests"] = "Warm approval precheck latency samples.",
            ["TiaMcp.Engine.Tests.ApprovalTests"] = "Bounded approval pipe connection and response.",
            ["TiaMcp.Engine.Tests.ImportStagingTests"] = "Timed file-lock release and bounded publication retries.",
            ["TiaMcp.Engine.Tests.OfflineChecks"] = "Indivisible legacy producer includes one-second synthetic child termination.",
            ["PromptRegistrationChecks"] = "Collectible fixture unload after ten 50 ms GC waits.",
            ["TiaMcp.WorkerChannel.Tests.ProcessTests"] = "Real process handshake and 700 ms terminal timeout budgets.",
            ["TiaMcp.WorkerChannel.Tests.ProtocolTests"] = "40 ms protocol deadlines and three-second channel budgets.",
            ["TiaMcp.WorkerChannel.Tests.PreviewRuleTests"] = "40 ms dispatch deadline.",
            ["TiaMcp.WorkerChannel.Tests.EngineProfileTests"] = "Bounded channel handshake.",
            ["TiaMcp.WorkbenchControl.Tests.PipeSecurityTests"] = "250 ms real pipe connection.",
            ["TiaOpenness.Core.Tests.BridgeChannelTests"] = "Synthetic bridge process startup, 500 ms timeout and bounded shutdown.",
            ["TiaOpenness.Core.Tests.GitWorkspaceDiffTests"] = "Temporary repository child startup/exit within thirty seconds.",
            ["TiaOpenness.Core.Tests.CallJournalTests"] = "Five-second live audit notification completion.",
            ["TiaOpenness.Core.Tests.IntegrationTests"] = "Synthetic bridge process connect/dispatch budgets.",
            ["TiaOpenness.Gui.Tests.ResponsivenessTests"] = "Reader throttling, latency measurement and bounded lock recovery.",
            ["TiaOpenness.Gui.Tests.ResponsivenessHarness"] = "Opt-in dispatcher p95/max latency and idle CPU thresholds.",
            ["TiaOpenness.Gui.Tests.WorkbenchControlLoadTests"] = "Dispatcher p95 below 50 ms and maximum below 200 ms.",
            ["TiaOpenness.Gui.Tests.ApprovalServiceTests"] = "One-second pipe connect and bounded approval delivery.",
            ["TiaOpenness.Gui.Tests.WorkbenchControlPipeTests"] = "One-second pipe connect and bounded response.",
            ["TiaOpenness.Gui.Tests.WorkbenchControlTests"] = "Blocked-dispatcher ordering and pipe deadlines.",
            ["TiaOpenness.Gui.Tests.McpPanelTests"] = "Real session expiry and bounded command completion.",
            ["TiaOpenness.Gui.Tests.WorkbenchMcpPrefillTests"] = "Bounded command completion polling.",
            ["TiaOpenness.Gui.Tests.NetworkHelperTests"] = "Synthetic helper process exit within sixty seconds.",
            ["TiaOpenness.Gui.Tests.UnifiedDesktopTests"] = "Ten-second asynchronous approval persistence.",
            ["TiaOpenness.Gui.Tests.VciPanelTests"] = "Five-second preview command admission.",
            ["TiaMcp.ShippedTools.Tests.WriteGuardPortTests"] = "Cold hook startup/decision within fifteen/twenty seconds.",
            ["TiaMcp.Updater.Tests.UpdaterTests"] = "Synthetic launcher/installed-process startup and exit bounds.",
            ["TiaMcp.SourceContracts.Tests.ScriptClientTests"] = "Five-second client cancellation completion.",
            ["TiaMcp.SourceContracts.Tests.ReadOnlySourceContracts"] = "Bounded child MSBuild evaluation.",
            ["TiaMcp.ReleaseTool.Tests.CheckAllRound3Tests"] = "Thirty-second child/grandchild affinity probe and ten-second barriers.",
            ["TiaMcp.ReleaseTool.Tests.CheckAllTests"] = "Concurrent scheduler peak/resource assertions around timed synthetic work.",
            ["TiaMcp.ReleaseTool.Tests.ReleaseValidationTests"] = "Five-second scheduler barriers.",
        };

        public static bool Contains(string className) => Classes.ContainsKey(className);

        public static bool ReserveShard(string project) => Path.GetFileNameWithoutExtension(project) is
            "TiaMcp.FoundationHost.Tests" or "TiaMcp.EngineHost.Tests" or "TiaMcp.Engine.Tests" or "TiaMcp.KeptCode.Tests" or
            "TiaMcp.WorkerChannel.Tests" or "TiaMcp.WorkbenchControl.Tests" or "TiaOpenness.Core.Tests" or
            "TiaOpenness.Gui.Tests" or "TiaMcp.ShippedTools.Tests" or "TiaMcp.Updater.Tests" or
            "TiaMcp.SourceContracts.Tests" or "TiaMcp.ReleaseTool.Tests" or "TiaMcp.Engine.PromptRegistration.Tests";
    }
}
