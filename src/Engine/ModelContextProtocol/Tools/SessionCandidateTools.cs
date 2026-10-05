using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class SessionCandidateTools
    {
        private readonly Portal portal;
        public SessionCandidateTools(Portal portal) { this.portal = portal; }
        private CallToolResult Run(string tool, int processId, string processStartUtc, string projectPath, bool startNew, bool reuseOpen, string upgrade, string copyPath,
            bool confirmUpgrade, string mode, bool confirm, string expectedPlanHash, string expectedProjectFile)
        {
            Envelope result;
            try
            {
                if (BehaviorCapabilities.Select(typeof(SessionCandidateTools).Assembly, McpServer.ReleaseKey, "P6-SESSION") != BehaviorPolicy.SafeV4)
                    throw new SessionCandidateRejection(new Error("Session candidate is not selected.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "P6-SESSION", "candidate")));
                result = portal.RunSessionCandidate(tool, new SessionRequest { Action = SessionCandidateContract.Action(tool), ProcessId = processId,
                    ProcessStartUtc = SessionCandidateSession.Start(processStartUtc), ProjectPath = projectPath, StartNew = startNew, ReuseOpen = reuseOpen,
                    Upgrade = upgrade, CopyPath = copyPath }, mode, confirm, expectedPlanHash, expectedProjectFile, confirmUpgrade);
            }
            catch (SessionCandidateRejection error)
            { result = SessionCandidateSession.Result(McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), null, error.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted); }
            var wire = McpResult.From(result);
            return new CallToolResult { IsError = wire.IsError, StructuredContent = JsonNode.Parse(wire.StructuredContent.GetRawText()), Content = new[] { new TextContentBlock { Text = wire.Content[0].Text } } };
        }

        [BehaviorCandidate("ConnectPortal", "P6-SESSION", typeof(SessionCandidateContract))]
        [Description(SessionCandidateContract.Description)]
        public CallToolResult ConnectPortal(int processId, string processStartUtc, bool startNew = false, bool reuseOpen = false, string upgrade = "reject", string copyPath = "", bool confirmUpgrade = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ConnectPortal", processId, processStartUtc, "", startNew, reuseOpen, upgrade, copyPath, confirmUpgrade, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ConnectIsolatedPortal", "P6-SESSION", typeof(SessionIsolatedContract))]
        [Description(SessionCandidateContract.Description)]
        public CallToolResult ConnectIsolatedPortal(int processId, string processStartUtc, bool startNew = false, bool reuseOpen = false, string upgrade = "reject", string copyPath = "", bool confirmUpgrade = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ConnectIsolatedPortal", processId, processStartUtc, "", startNew, reuseOpen, upgrade, copyPath, confirmUpgrade, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ConnectProject", "P6-SESSION", typeof(SessionBindContract))]
        [Description(SessionCandidateContract.Description)]
        public CallToolResult ConnectProject(int processId, string processStartUtc, string projectPath, bool startNew = false, bool reuseOpen = false, string upgrade = "reject", string copyPath = "", bool confirmUpgrade = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ConnectProject", processId, processStartUtc, projectPath, startNew, reuseOpen, upgrade, copyPath, confirmUpgrade, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("AttachOpenProject", "P6-SESSION", typeof(SessionAttachProjectContract))]
        [Description(SessionCandidateContract.Description)]
        public CallToolResult AttachOpenProject(int processId, string processStartUtc, string projectPath, bool startNew = false, bool reuseOpen = false, string upgrade = "reject", string copyPath = "", bool confirmUpgrade = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("AttachOpenProject", processId, processStartUtc, projectPath, startNew, reuseOpen, upgrade, copyPath, confirmUpgrade, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("OpenProject", "P6-SESSION", typeof(SessionOpenContract))]
        [Description(SessionCandidateContract.Description)]
        public CallToolResult OpenProject(int processId, string processStartUtc, string projectPath, bool startNew = false, bool reuseOpen = false, string upgrade = "reject", string copyPath = "", bool confirmUpgrade = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("OpenProject", processId, processStartUtc, projectPath, startNew, reuseOpen, upgrade, copyPath, confirmUpgrade, mode, confirm, expectedPlanHash, expectedProjectFile);
    }
}
