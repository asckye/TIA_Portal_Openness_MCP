using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcExportCandidateTools
    {
        private readonly IEngineeringSession session;
        public PlcExportCandidateTools(IEngineeringSession session) { this.session = session; }
        private CallToolResult Run(string tool, PlcExportRequest request, string mode, bool confirm, string hash, string project)
        {
            var result = McpResult.From(PlcExportCandidateService.For(session).Run(tool, request, mode, confirm, hash, project));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }

        [BehaviorCandidate("ExportPlcBlock", "P6-EXPORT", typeof(PlcExportContract))]
        [Description("[PLC-Software][WRITE] Reviewed export candidate. Preview lists objects, readable content hashes and destination identities without writing. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. One export per item into sibling staging, atomic publication, content/path readback, stop on first failure. Retain staging on failure; unknown requires session reset. Document output uses new directories; object/version boundaries remain restricted. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ExportPlcBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false, bool overwrite = false, string onError = "stop", string workspaceRoot = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ExportPlcBlock", new PlcExportRequest { SoftwarePath = softwarePath, ObjectPath = blockPath, OutputPath = exportPath, PreservePath = preservePath,
                RegexName = "", Recursive = false, MaxItems = 1, Overwrite = overwrite, OnError = onError, WorkspaceRoot = workspaceRoot }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ExportPlcType", "P6-EXPORT", typeof(PlcTypeExportContract))]
        [Description("[PLC-Software][WRITE] Reviewed export candidate. Preview lists objects, readable content hashes and destination identities without writing. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. One export per item into sibling staging, atomic publication, content/path readback, stop on first failure. Retain staging on failure; unknown requires session reset. Document output uses new directories; object/version boundaries remain restricted. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ExportPlcType(string softwarePath, string typePath, string exportPath, bool preservePath = false, bool overwrite = false, string onError = "stop", string workspaceRoot = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ExportPlcType", new PlcExportRequest { SoftwarePath = softwarePath, ObjectPath = typePath, OutputPath = exportPath, PreservePath = preservePath,
                RegexName = "", Recursive = false, MaxItems = 1, Overwrite = overwrite, OnError = onError, WorkspaceRoot = workspaceRoot }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ExportPlcTagTable", "P6-EXPORT", typeof(PlcTagExportContract))]
        [Description("[PLC-Software][WRITE] Reviewed export candidate. Preview lists objects, readable content hashes and destination identities without writing. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. One export per item into sibling staging, atomic publication, content/path readback, stop on first failure. Retain staging on failure; unknown requires session reset. Document output uses new directories; object/version boundaries remain restricted. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ExportPlcTagTable(string softwarePath, string tagTableName, string exportPath, bool overwrite = false, string onError = "stop", string workspaceRoot = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ExportPlcTagTable", new PlcExportRequest { SoftwarePath = softwarePath, ObjectPath = tagTableName, OutputPath = exportPath, PreservePath = false,
                RegexName = "", Recursive = false, MaxItems = 1, Overwrite = overwrite, OnError = onError, WorkspaceRoot = workspaceRoot }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ExportPlcBlocks", "P6-EXPORT", typeof(PlcBlocksExportContract))]
        [Description("[PLC-Software][WRITE] Reviewed export candidate. Preview lists objects, readable content hashes and destination identities without writing. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. One export per item into sibling staging, atomic publication, content/path readback, stop on first failure. Retain staging on failure; unknown requires session reset. Document output uses new directories; object/version boundaries remain restricted. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ExportPlcBlocks(string softwarePath, string groupPath, string exportPath, bool preservePath = false, string regexName = "", bool recursive = false, int maxItems = 128, bool overwrite = false, string onError = "stop", string workspaceRoot = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ExportPlcBlocks", new PlcExportRequest { SoftwarePath = softwarePath, GroupPath = groupPath, OutputPath = exportPath, PreservePath = preservePath,
                RegexName = regexName, Recursive = recursive, MaxItems = maxItems, Overwrite = overwrite, OnError = onError, WorkspaceRoot = workspaceRoot }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ExportPlcTypes", "P6-EXPORT", typeof(PlcTypesExportContract))]
        [Description("[PLC-Software][WRITE] Reviewed export candidate. Preview lists objects, readable content hashes and destination identities without writing. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. One export per item into sibling staging, atomic publication, content/path readback, stop on first failure. Retain staging on failure; unknown requires session reset. Document output uses new directories; object/version boundaries remain restricted. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ExportPlcTypes(string softwarePath, string groupPath, string exportPath, bool preservePath = false, string regexName = "", bool recursive = false, int maxItems = 128, bool overwrite = false, string onError = "stop", string workspaceRoot = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ExportPlcTypes", new PlcExportRequest { SoftwarePath = softwarePath, GroupPath = groupPath, OutputPath = exportPath, PreservePath = preservePath,
                RegexName = regexName, Recursive = recursive, MaxItems = maxItems, Overwrite = overwrite, OnError = onError, WorkspaceRoot = workspaceRoot }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ExportPlcBlockDocuments", "P6-EXPORT", typeof(PlcDocumentExportContract))]
        [Description("[PLC-Software][WRITE] Reviewed export candidate. Preview lists objects, readable content hashes and destination identities without writing. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. One export per item into sibling staging, atomic publication, content/path readback, stop on first failure. Retain staging on failure; unknown requires session reset. Document output uses new directories; object/version boundaries remain restricted. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ExportPlcBlockDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false, bool overwrite = false, string onError = "stop", string workspaceRoot = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ExportPlcBlockDocuments", new PlcExportRequest { SoftwarePath = softwarePath, ObjectPath = blockPath, OutputPath = exportPath, PreservePath = preservePath,
                RegexName = "", Recursive = false, MaxItems = 1, Overwrite = overwrite, OnError = onError, WorkspaceRoot = workspaceRoot }, mode, confirm, expectedPlanHash, expectedProjectFile);

        [BehaviorCandidate("ExportPlcBlocksDocuments", "P6-EXPORT", typeof(PlcDocumentsExportContract))]
        [Description("[PLC-Software][WRITE] Reviewed export candidate. Preview lists objects, readable content hashes and destination identities without writing. Apply requires confirm=true, expectedPlanHash and expectedProjectFile. One export per item into sibling staging, atomic publication, content/path readback, stop on first failure. Retain staging on failure; unknown requires session reset. Document output uses new directories; object/version boundaries remain restricted. Test/accepted behaviorPolicy=safe-v4.")]
        public CallToolResult ExportPlcBlocksDocuments(string softwarePath, string groupPath, string exportPath, bool preservePath = false, string regexName = "", bool recursive = false, int maxItems = 128, bool overwrite = false, string onError = "stop", string workspaceRoot = "",
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ExportPlcBlocksDocuments", new PlcExportRequest { SoftwarePath = softwarePath, GroupPath = groupPath, OutputPath = exportPath, PreservePath = preservePath,
                RegexName = regexName, Recursive = recursive, MaxItems = maxItems, Overwrite = overwrite, OnError = onError, WorkspaceRoot = workspaceRoot }, mode, confirm, expectedPlanHash, expectedProjectFile);

    }
}
