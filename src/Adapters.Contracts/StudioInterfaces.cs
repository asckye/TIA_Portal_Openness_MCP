#nullable disable
using System;
using System.Collections.Generic;
using TiaMcp.Adapters.Contracts.Studio;

namespace TiaMcp.Adapters.Contracts.Studio
{
    // Synchronous on the owning STA; callbacks must not reenter the adapter.
    public delegate void ProgressCallback(string operation, int current, int total, string message);
}

namespace TiaMcp.Adapters.Contracts
{
    public interface IStudioVciReader
    {
        public SessionState AttachVciReadOnly(int processId, long startUtcTicks, string projectFile);
    }
    // Studio's compatibility profile: device names and block paths retain their existing
    // spelling and lookup rules. Its results and mutation policy differ from Foundation's
    // IPortalSession/IPlcProgram/IPlcData, so hosts must select this profile explicitly.
    // No native handles escape. The host installs the matching SDK resolver before creation.
    public interface IStudioSession : IDisposable
    {
        public SessionMode Mode { get; }
        public bool IsConnected { get; }
        public bool HasProject { get; }
        public SessionState Connect(bool withUserInterface, bool attachToRunning, string version);
        public void Disconnect();
        public SessionState GetState();
        public ProjectInfo OpenProject(string path);
        public ProjectInfo GetProjectInfo();
        public void SaveProject();
        public void CloseProject();
        // Canonical device name, or null for a known non-PLC device. Unknown names
        // retain the existing lookup error. Inspection uses this before ListBlocks.
        public string FindPlcDeviceId(string deviceId);
        public IReadOnlyList<BlockInfo> ListBlocks(string deviceId, bool includeSystemBlocks);
        // Source uses GenerateSource for PLC blocks/types; HMI retains its XML export path.
        public ExportResult ExportBlocks(string deviceId, IReadOnlyList<string> blockPaths, string outputDirectory,
            ExportFormat format, bool preserveFolders, ProgressCallback progress);
        public ExportResult ImportBlocks(string deviceId, IReadOnlyList<string> files, bool overwrite, ProgressCallback progress);
        public IReadOnlyList<TagTableInfo> ListTagTables(string deviceId);
        public IReadOnlyList<TagInfo> ListTags(string deviceId, string tableName);
        public CompileResult CompileDevice(string deviceId, bool softwareOnly);
    }

    public interface IHardware
    {
        public IReadOnlyList<DeviceInfo> ListDevices();
    }

    public interface IHmiExport
    {
        public IReadOnlyList<BlockInfo> ListItems(string deviceId);
        public ExportResult ExportItems(string deviceId, IReadOnlyList<string> paths, string outputDirectory,
            bool preserveFolders, ProgressCallback progress);
    }

    public interface IVersionControl
    {
        public IReadOnlyList<WorkspaceInfo> ListWorkspaces();
        public WorkspaceInfo CreateWorkspace(string name, string folderPath);
        public MappingResult MapProject(string workspaceName, string deviceFilter, bool dryRun, ProgressCallback progress);
        public WorkspaceStatusReport GetStatus(string workspaceName, bool changedOnly);
        public SyncResult Sync(string workspaceName, SyncDirection direction, bool dryRun, ProgressCallback progress);
    }
}
