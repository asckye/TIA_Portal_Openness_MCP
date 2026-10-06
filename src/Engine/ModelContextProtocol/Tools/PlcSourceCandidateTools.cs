using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Logic.V4;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class PlcSourceCandidateTools
    {
        private readonly IEngineeringSession session;
        public PlcSourceCandidateTools(IEngineeringSession session) { this.session = session; }
        private CallToolResult Run(string tool, string softwarePath, string action, string sourceName, string groupPath, string filePath, bool overwrite,
            string onError, string missingPolicy, string mode, bool confirm, string expectedPlanHash, string expectedProjectFile)
        {
            var request = new SourceRequest { SoftwarePath = softwarePath, ReadGroup = action == "list" ? groupPath : "", ReadSource = action == "list" ? sourceName : "", Overwrite = overwrite, OnError = onError, MissingPolicy = missingPolicy,
                Items = action == "list" ? Array.Empty<SourceItem>() : new[] { new SourceItem { Action = action, SourceName = sourceName, GroupPath = groupPath, FilePath = filePath } } };
            var result = McpResult.From(PlcSourceCandidateService.For(session).Run(tool, request, mode, confirm, expectedPlanHash, expectedProjectFile));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()), Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
        [BehaviorCandidate("ListPlcExternalSources", "P6-SOURCE", typeof(SourceListContract))]
        [Description(SourceContract.Description)]
        public CallToolResult ListPlcExternalSources(string softwarePath = "", string groupPath = "")
            => Run("ListPlcExternalSources", softwarePath, "list", "", groupPath, "", false, "stop", "reject", "preview", false, "", "");
        [BehaviorCandidate("ImportPlcExternalSource", "P6-SOURCE", typeof(SourceContract))]
        [Description(SourceContract.Description)]
        public CallToolResult ImportPlcExternalSource(string sourceName, string filePath, string softwarePath = "", string groupPath = "", bool overwrite = false,
            string onError = "stop", string missingPolicy = "reject", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcExternalSource", softwarePath, "import", sourceName, groupPath, filePath, overwrite, onError, missingPolicy, mode, confirm, expectedPlanHash, expectedProjectFile);
        [BehaviorCandidate("GenerateBlocksFromExternalSource", "P6-SOURCE", typeof(SourceGenerateContract))]
        [Description(SourceContract.Description)]
        public CallToolResult GenerateBlocksFromExternalSource(string sourceName, string softwarePath = "", string groupPath = "", bool overwrite = false,
            string onError = "stop", string missingPolicy = "reject", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("GenerateBlocksFromExternalSource", softwarePath, "generate", sourceName, groupPath, "", overwrite, onError, missingPolicy, mode, confirm, expectedPlanHash, expectedProjectFile);
        [BehaviorCandidate("DeletePlcExternalSource", "P6-SOURCE", typeof(SourceDeleteContract))]
        [Description(SourceContract.Description)]
        public CallToolResult DeletePlcExternalSource(string sourceName, string softwarePath = "", string groupPath = "", bool overwrite = false,
            string onError = "stop", string missingPolicy = "reject", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("DeletePlcExternalSource", softwarePath, "delete", sourceName, groupPath, "", overwrite, onError, missingPolicy, mode, confirm, expectedPlanHash, expectedProjectFile);
        [BehaviorCandidate("ManagePlcExternalSources", "P6-SOURCE", typeof(SourceManageContract))]
        [Description(SourceContract.Description)]
        public CallToolResult ManagePlcExternalSources(string action, string softwarePath = "", string sourceName = "", string groupPath = "", string filePath = "", bool overwrite = false,
            string onError = "stop", string missingPolicy = "reject", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ManagePlcExternalSources", softwarePath, action == "createFromFile" ? "import" : action == "generateBlocks" ? "generate" : action == "read" ? "list" : action,
                sourceName, groupPath, filePath, overwrite, onError, missingPolicy, mode, confirm, expectedPlanHash, expectedProjectFile);
        // Foundation-only planning entry participates in the same generated family inventory.
        [BehaviorCandidate("PlanPlcExternalSourceImport", "P6-SOURCE", typeof(SourcePlanContract))]
        [Description(SourceContract.Description)]
        public CallToolResult PlanPlcExternalSourceImport(string filePath, string allowedFilePath, string softwarePath = "", string groupPath = "", string sourceName = "", bool overwrite = false,
            string onError = "stop", string missingPolicy = "reject", string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("PlanPlcExternalSourceImport", softwarePath, "import", sourceName == "" ? Path.GetFileName(filePath) : sourceName, groupPath,
                filePath == allowedFilePath ? filePath : "", overwrite, onError, missingPolicy, mode, confirm, expectedPlanHash, expectedProjectFile);
    }
}
