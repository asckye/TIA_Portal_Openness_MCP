using System;
using System.Collections.Generic;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class VersionControlTools
    {
        private readonly VersionControlService _versionControl;

        public VersionControlTools(VersionControlService versionControl) => _versionControl = versionControl;

        [McpServerTool(Name = "ListVersionControlWorkspaces"), Description(
            "[L1][VersionControl] List this project's version control (VCI) workspaces: name, folder on disk, " +
            "language, and how many objects are mapped. A workspace is the plain-text mirror of the project " +
            "that Git can actually diff and commit. Read-only. Requires TIA V21+ and an open project. Current native policy; V4 native acceptance is pending." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ListVersionControlWorkspacesV4()
            => LibraryToolContract.Run("ListVersionControlWorkspaces", false, true, () =>
            {
                return GetVersionControlWorkspaces();
            });

        internal ResponseStringList GetVersionControlWorkspaces()
        => _versionControl.GetVersionControlWorkspaces();

        [McpServerTool(Name = "CreateVersionControlWorkspace"), Description(
            "[L2][VersionControl] Create a VCI workspace pointing at a folder on disk — normally the working " +
            "tree of a Git repository, so every synchronized export lands where Git can commit it. " +
            "Creating the workspace does NOT map any objects into it — call ConnectProjectToWorkspace " +
            "afterwards to map the whole project (or one device) automatically. " +
            "Requires TIA V21+ and an open project. Current native policy; V4 native acceptance is pending." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult CreateVersionControlWorkspaceV4(
            [Description("workspaceName: name shown in the TIA project tree, e.g. 'git'.")] string workspaceName,
            [Description("folderPath: existing folder the text files are written to, e.g. 'D:\\\\repos\\\\crane-plc'. Use your Git working tree.")] string folderPath)
            => LibraryToolContract.Run("CreateVersionControlWorkspace", true, true, () =>
            {
                return CreateVersionControlWorkspace(workspaceName, folderPath);
            });

        internal ResponseMessage CreateVersionControlWorkspace(
            string workspaceName,
            string folderPath)
        => _versionControl.CreateVersionControlWorkspace(workspaceName, folderPath);

        [McpServerTool(Name = "GetVersionControlStatus"), Description(
            "[L1][VersionControl] Per-object status of a VCI workspace: which mapped objects differ between the " +
            "TIA project and the text files on disk. This is the input for a change log — it names exactly what " +
            "changed before you commit. Read-only, changes nothing. " +
            "Status values: Equal (in sync), Unequal (project and file differ), WorkspaceFileMissing (never exported), Unknown. Current native policy; V4 native acceptance is pending." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult GetVersionControlStatusV4(
            [Description("workspaceName: which workspace. Empty = the first one in the project.")] string workspaceName = "",
            [Description("changedOnly: default true — list only objects that are NOT in sync. false lists every mapped object.")] bool changedOnly = true)
            => LibraryToolContract.Run("GetVersionControlStatus", false, true, () =>
            {
                return GetVersionControlStatus(workspaceName, changedOnly);
            });

        internal ResponseStringList GetVersionControlStatus(
            string workspaceName = "",
            bool changedOnly = true)
        => _versionControl.GetVersionControlStatus(workspaceName, changedOnly);

        [McpServerTool(Name = "SynchronizeVersionControlWorkspace"), Description(
            "[L1][VersionControl] Synchronize a VCI workspace. direction='ProjectToWorkspace' writes the TIA " +
            "project's objects out as text files (do this before `git commit`); 'WorkspaceToProject' reads the " +
            "text files back INTO the project (do this after `git pull` / to restore a reviewed version). " +
            "DEFAULTS TO dryRun=true: the default call only reports what WOULD be synchronized. " +
            "WorkspaceToProject OVERWRITES blocks in the open project — compile and save afterwards, " +
            "and it requires a Pro license (exporting is free). Current native policy; V4 native acceptance is pending." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult SynchronizeVersionControlWorkspaceV4(
            [Description("direction: 'ProjectToWorkspace' (export, for committing) or 'WorkspaceToProject' (import, for restoring).")] string direction = "ProjectToWorkspace",
            [Description("workspaceName: which workspace. Empty = the first one in the project.")] string workspaceName = "",
            [Description("dryRun: DEFAULT true — only reports what would change. Pass false to actually synchronize.")] bool dryRun = true,
            [Description("changedOnly: default true — synchronize only objects whose status is not Equal. false forces every mapped object.")] bool changedOnly = true)
            => LibraryToolContract.Run("SynchronizeVersionControlWorkspace", !dryRun, true, () =>
            {
                return SyncVersionControlWorkspace(direction, workspaceName, dryRun, changedOnly);
            });

        internal ResponseStringList SyncVersionControlWorkspace(
            string direction = "ProjectToWorkspace",
            string workspaceName = "",
            bool dryRun = true,
            bool changedOnly = true)
        => _versionControl.SyncVersionControlWorkspace(direction, workspaceName, dryRun, changedOnly);

        [McpServerTool(Name = "ConnectProjectToWorkspace"), Description(
            "[L2][VersionControl] Put a WHOLE project under version control automatically - no TIA UI clicks. " +
            "Walks the project tree, asks every object whether VCI can map it (Workspace.GetSupportedFileFormats) " +
            "and maps each supported object with Workspace.ConnectObject. COARSE-FIRST: when a device or PLC " +
            "software object is mappable as one unit it is mapped whole and its children are not visited, so you " +
            "get the fewest mappings that still cover everything. Objects VCI does not support (typically hardware " +
            "configuration) are reported, never silently dropped. " +
            "DEFAULTS TO dryRun=true: the default call only reports what it WOULD map. " +
            "After a real run call SynchronizeVersionControlWorkspace(ProjectToWorkspace, dryRun=false), then git commit. Current native policy; V4 native acceptance is pending." + " Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ConnectProjectToWorkspaceV4(
            [Description("workspaceName: which workspace to map into. Empty = the first one in the project.")] string workspaceName = "",
            [Description("dryRun: DEFAULT true - reports what would be mapped and changes nothing. Pass false to actually map.")] bool dryRun = true,
            [Description("deviceFilter: map only this device (exact name, e.g. 'PLC_1'). Empty = the whole project.")] string deviceFilter = "",
            [Description("maxObjects: safety cap on how many tree nodes are visited. Default 3000.")] int maxObjects = 3000,
            [Description("walkTrace: write one stderr line per visited node. Diagnostic only.")] bool walkTrace = false)
            => LibraryToolContract.Run("ConnectProjectToWorkspace", !dryRun, true, () =>
            {
                return ConnectProjectToWorkspace(workspaceName, dryRun, deviceFilter, maxObjects, walkTrace);
            });

        internal ResponseStringList ConnectProjectToWorkspace(
            string workspaceName = "",
            bool dryRun = true,
            string deviceFilter = "",
            int maxObjects = 3000,
            bool walkTrace = false)
        => _versionControl.ConnectProjectToWorkspace(workspaceName, dryRun, deviceFilter, maxObjects, walkTrace);
    }
}
