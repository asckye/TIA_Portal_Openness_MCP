using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    internal sealed class FallbackCandidateTools
    {
        private readonly Portal portal;
        private readonly OnlineDownloadService download;
        private readonly VersionControlService vci;
        private readonly DocumentsService documents;
        public FallbackCandidateTools(Portal portal, OnlineDownloadService download, VersionControlService vci, DocumentsService documents)
        { this.portal = portal; this.download = download; this.vci = vci; this.documents = documents; }
        private CallToolResult Run(string entry, string software, string route, string retry, bool refresh, string mode, bool confirm, string hash, string file,
            Dictionary<string, string>? parameters = null, string password = "")
        {
            var request = new FallbackRequest { Entry = entry, SoftwarePath = software, Route = route, RetryPolicy = retry, RefreshReadHandle = refresh,
                Parameters = parameters ?? new Dictionary<string, string>() };
            Envelope result;
            if (BehaviorCapabilities.Select(typeof(FallbackCandidateTools).Assembly, McpServer.ReleaseKey, "P6-FALLBACK") != BehaviorPolicy.SafeV4)
                result = FallbackSession.Result(McpServer.ReleaseKey, entry, Meta.Correlate(InvocationJournal.CorrelationId), null,
                    new Error("Fallback candidate is not selected.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "P6-FALLBACK", "candidate")), Outcome.RejectedBeforeOperation, Execution.NotStarted);
            else result = portal.RunFallbackCandidate(request, () => entry.Contains("VersionControl") || entry == "ConnectProjectToWorkspace"
                ? vci.FallbackAdapter(request, portal) : download.FallbackAdapter(request, password, portal, documents), password, mode, confirm, hash, file);
            var wire = McpResult.From(result);
            return new CallToolResult { IsError = wire.IsError, StructuredContent = JsonNode.Parse(wire.StructuredContent.GetRawText()), Content = new[] { new TextContentBlock { Text = wire.Content[0].Text } } };
        }
        private static string Flag(bool value) => value ? "true" : "false";
        [BehaviorCandidate("DownloadPlc", "P6-FALLBACK", typeof(FallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult DownloadPlc(string softwarePath = "", bool consistentBlocksOnly = true, bool keepActualValues = true, bool startAfterDownload = true, bool stopBeforeDownload = true,
            string password = "", string rhTarget = "", bool trustDeviceCertificate = false, string route = "", string retryPolicy = "never", bool refreshReadHandle = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("DownloadPlc", softwarePath, route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile,
                new Dictionary<string, string> { ["consistentBlocksOnly"] = Flag(consistentBlocksOnly), ["keepActualValues"] = Flag(keepActualValues), ["startAfterDownload"] = Flag(startAfterDownload),
                    ["stopBeforeDownload"] = Flag(stopBeforeDownload), ["rhTarget"] = rhTarget, ["trustDeviceCertificate"] = Flag(trustDeviceCertificate) }, password);
        [BehaviorCandidate("DownloadPlcToFolder", "P6-FALLBACK", typeof(FolderFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult DownloadPlcToFolder(string destinationDirectory, string softwarePath = "", string targetForSoftware = "CPU", string route = "", string retryPolicy = "never", bool refreshReadHandle = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("DownloadPlcToFolder", softwarePath, route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile,
                new Dictionary<string, string> { ["destinationDirectory"] = destinationDirectory, ["targetForSoftware"] = targetForSoftware });
        [BehaviorCandidate("CompilePlcSoftware", "P6-FALLBACK", typeof(CompileFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult CompilePlcSoftware(string softwarePath = "", string password = "", string route = "", string retryPolicy = "never", bool refreshReadHandle = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("CompilePlcSoftware", softwarePath, route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile, password: password);
        [BehaviorCandidate("ExportPlcBlockDocuments", "P6-FALLBACK", typeof(ExportDocumentsFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult ExportPlcBlockDocuments(string blockPath, string exportPath, string softwarePath = "", bool preservePath = false, string route = "", string retryPolicy = "never", bool refreshReadHandle = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ExportPlcBlockDocuments", softwarePath, route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile,
                new Dictionary<string, string> { ["blockPath"] = blockPath, ["exportPath"] = exportPath, ["preservePath"] = Flag(preservePath) });
        [BehaviorCandidate("ImportPlcBlockDocuments", "P6-FALLBACK", typeof(ImportDocumentsFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult ImportPlcBlockDocuments(string importPath, string fileNameWithoutExtension, string softwarePath = "", string groupPath = "", string route = "", string retryPolicy = "never", bool refreshReadHandle = false,
            string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ImportPlcBlockDocuments", softwarePath, route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile,
                new Dictionary<string, string> { ["groupPath"] = groupPath, ["importPath"] = importPath, ["fileNameWithoutExtension"] = fileNameWithoutExtension });
        [BehaviorCandidate("ListVersionControlWorkspaces", "P6-FALLBACK", typeof(ListVciFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult ListVersionControlWorkspaces(string route = "", string retryPolicy = "never", bool refreshReadHandle = false, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ListVersionControlWorkspaces", "", route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile);
        [BehaviorCandidate("GetVersionControlStatus", "P6-FALLBACK", typeof(StatusVciFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult GetVersionControlStatus(bool changedOnly = true, string route = "", string retryPolicy = "never", bool refreshReadHandle = false, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("GetVersionControlStatus", "", route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile, new Dictionary<string, string> { ["changedOnly"] = Flag(changedOnly) });
        [BehaviorCandidate("CreateVersionControlWorkspace", "P6-FALLBACK", typeof(CreateVciFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult CreateVersionControlWorkspace(string workspaceName, string folderPath, string route = "", string retryPolicy = "never", bool refreshReadHandle = false, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("CreateVersionControlWorkspace", "", route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile, new Dictionary<string, string> { ["workspaceName"] = workspaceName, ["folderPath"] = folderPath });
        [BehaviorCandidate("ConnectProjectToWorkspace", "P6-FALLBACK", typeof(MapVciFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult ConnectProjectToWorkspace(string deviceFilter = "", int maxObjects = 3000, string route = "", string retryPolicy = "never", bool refreshReadHandle = false, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("ConnectProjectToWorkspace", "", route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile, new Dictionary<string, string> { ["deviceFilter"] = deviceFilter, ["maxObjects"] = maxObjects.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        [BehaviorCandidate("SynchronizeVersionControlWorkspace", "P6-FALLBACK", typeof(SyncVciFallbackContract))]
        [Description(FallbackContract.Description)]
        public CallToolResult SynchronizeVersionControlWorkspace(string direction = "ProjectToWorkspace", string route = "", string retryPolicy = "never", bool refreshReadHandle = false, string mode = "preview", bool confirm = false, string expectedPlanHash = "", string expectedProjectFile = "")
            => Run("SynchronizeVersionControlWorkspace", "", route, retryPolicy, refreshReadHandle, mode, confirm, expectedPlanHash, expectedProjectFile, new Dictionary<string, string> { ["direction"] = direction });
    }
}
