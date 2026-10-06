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
    internal sealed class SaveCloseCandidateTools
    {
        private readonly Portal portal;
        public SaveCloseCandidateTools(Portal portal) { this.portal = portal; }
        private CallToolResult Run(string tool, string mode, bool confirm, string hash, string project, bool save = false, bool discard = false, bool confirmDiscard = false, string directory = "")
        {
            Envelope result;
            if (BehaviorCapabilities.Select(typeof(SaveCloseCandidateTools).Assembly, McpServer.ReleaseKey, "P6-CLOSE") != BehaviorPolicy.SafeV4)
                result = SaveCloseSession.Result(McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), null,
                    new Error("Save/close candidate is not selected.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "P6-CLOSE", "candidate")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            else result = portal.RunSaveCloseCandidate(tool, new SaveCloseRequest { Action = SaveCloseContract.Action(tool), SaveChanges = save,
                DiscardChanges = discard, NewProjectPath = directory }, mode, confirm, hash, project, confirmDiscard);
            var wire = McpResult.From(result);
            return new CallToolResult { IsError = wire.IsError, StructuredContent = JsonNode.Parse(wire.StructuredContent.GetRawText()), Content = new[] { new TextContentBlock { Text = wire.Content[0].Text } } };
        }
        [BehaviorCandidate("SaveProject", "P6-CLOSE", typeof(SaveCloseContract))]
        [Description(SaveCloseContract.Description)]
        public CallToolResult SaveProject(string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("SaveProject", mode, confirm, expectedPlanHash, expectedProjectFile);
        [BehaviorCandidate("SaveProjectCopy", "P6-CLOSE", typeof(SaveCopyContract))]
        [Description(SaveCloseContract.Description)]
        public CallToolResult SaveProjectCopy(string newProjectPath, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("SaveProjectCopy", mode, confirm, expectedPlanHash, expectedProjectFile, directory: newProjectPath);
        [BehaviorCandidate("CloseProject", "P6-CLOSE", typeof(CloseCandidateContract))]
        [Description(SaveCloseContract.Description)]
        public CallToolResult CloseProject(bool saveChanges = false, bool discardChanges = false, bool confirmDiscard = false, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("CloseProject", mode, confirm, expectedPlanHash, expectedProjectFile, saveChanges, discardChanges, confirmDiscard);
        [BehaviorCandidate("DisconnectPortal", "P6-CLOSE", typeof(DisconnectCandidateContract))]
        [Description(SaveCloseContract.Description)]
        public CallToolResult DisconnectPortal(string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("DisconnectPortal", mode, confirm, expectedPlanHash, expectedProjectFile);
    }
}
