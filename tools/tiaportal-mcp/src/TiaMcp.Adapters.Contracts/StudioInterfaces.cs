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
    // Studio's compatibility profile: device names and block paths retain their existing
    // spelling and lookup rules. Its results and mutation policy differ from Foundation's
    // IPortalSession/IPlcProgram/IPlcData, so hosts must select this profile explicitly.
    // No native handles escape. The host installs the matching SDK resolver before creation.
    public interface IStudioSession : IDisposable
    {
        SessionMode Mode { get; }
        bool IsConnected { get; }
        bool HasProject { get; }
        SessionState Connect(bool withUserInterface, bool attachToRunning, string version);
        void Disconnect();
        SessionState GetState();
        ProjectInfo OpenProject(string path);
        ProjectInfo GetProjectInfo();
        void SaveProject();
        void CloseProject();
        // Canonical device name, or null for a known non-PLC device. Unknown names
        // retain the existing lookup error. Inspection uses this before ListBlocks.
        string FindPlcDeviceId(string deviceId);
        IReadOnlyList<BlockInfo> ListBlocks(string deviceId, bool includeSystemBlocks);
        // Source uses GenerateSource for PLC blocks/types; HMI retains its XML export path.
        ExportResult ExportBlocks(string deviceId, IReadOnlyList<string> blockPaths, string outputDirectory,
            ExportFormat format, bool preserveFolders, ProgressCallback progress);
        ExportResult ImportBlocks(string deviceId, IReadOnlyList<string> files, bool overwrite, ProgressCallback progress);
        IReadOnlyList<TagTableInfo> ListTagTables(string deviceId);
        IReadOnlyList<TagInfo> ListTags(string deviceId, string tableName);
        CompileResult CompileDevice(string deviceId, bool softwareOnly);
    }

    public interface IHardware
    {
        IReadOnlyList<DeviceInfo> ListDevices();
    }

    public interface IHmiExport
    {
        IReadOnlyList<BlockInfo> ListItems(string deviceId);
        ExportResult ExportItems(string deviceId, IReadOnlyList<string> paths, string outputDirectory,
            bool preserveFolders, ProgressCallback progress);
    }

    public interface IVersionControl
    {
        IReadOnlyList<WorkspaceInfo> ListWorkspaces();
        WorkspaceInfo CreateWorkspace(string name, string folderPath);
        MappingResult MapProject(string workspaceName, string deviceFilter, bool dryRun, ProgressCallback progress);
        WorkspaceStatusReport GetStatus(string workspaceName, bool changedOnly);
        SyncResult Sync(string workspaceName, SyncDirection direction, bool dryRun, ProgressCallback progress);
    }
}
