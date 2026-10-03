using System;
using System.Collections.Generic;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Core.Abstractions
{
    /// <summary>Reports progress of a long-running bridge operation back to the caller.</summary>
    /// <param name="current">Items finished so far.</param>
    /// <param name="total">Total items, or 0 when unknown.</param>
    public delegate void ProgressCallback(string operation, int current, int total, string message);

    /// <summary>
    /// Everything the bridge can do against TIA Portal. Implemented twice: once over
    /// Siemens.Engineering (<c>TiaOpenness.Openness</c>) and once in memory
    /// (<see cref="TiaOpenness.Core.Mock.MockTiaSession"/>), so the whole stack above
    /// this interface can be built and tested on a machine without TIA Portal.
    /// </summary>
    public interface ITiaSession : IDisposable
    {
        SessionMode Mode { get; }
        bool IsConnected { get; }
        bool HasProject { get; }

        /// <summary>Attach to a running TIA Portal, or start one.</summary>
        /// <param name="withUserInterface">Show the TIA window. Headless is faster but cannot be watched.</param>
        /// <param name="attachToRunning">Prefer an already-running instance over starting a new one.</param>
        /// <param name="version">Openness version to bind, e.g. "21.0". Null picks the newest installed.</param>
        SessionState Connect(bool withUserInterface, bool attachToRunning, string version);

        void Disconnect();
        SessionState GetState();

        ProjectInfo OpenProject(string path);
        ProjectInfo GetProjectInfo();
        void SaveProject();
        void CloseProject();

        IReadOnlyList<DeviceInfo> ListDevices();
        IReadOnlyList<BlockInfo> ListBlocks(string deviceId, bool includeSystemBlocks);

        ExportResult ExportBlocks(string deviceId, IReadOnlyList<string> blockPaths, string outputDirectory,
            ExportFormat format, bool preserveFolders, ProgressCallback progress);

        ExportResult ImportBlocks(string deviceId, IReadOnlyList<string> files, bool overwrite, ProgressCallback progress);

        IReadOnlyList<TagTableInfo> ListTagTables(string deviceId);
        IReadOnlyList<TagInfo> ListTags(string deviceId, string tableName);

        CompileResult CompileDevice(string deviceId, bool softwareOnly);

        InspectionReport Inspect(string deviceId, InspectionOptions options);

        /// <summary>
        /// The project's Version Control Interface, or null when this TIA Portal is older than
        /// V21 and has none. Modelled as a nullable capability rather than methods that throw,
        /// so a front end can hide the feature instead of offering it and failing.
        /// </summary>
        IVersionControl VersionControl { get; }
    }

    /// <summary>Which inspection rules to run and how strict they are.</summary>
    public class InspectionOptions
    {
        /// <summary>Regex every block name must match. Null disables the rule.</summary>
        public string BlockNamePattern { get; set; }
        /// <summary>Flag blocks whose header author is empty (not the block comment).</summary>
        public bool RequireBlockComment { get; set; } = true;
        /// <summary>Flag blocks that no other block calls.</summary>
        public bool FindUnusedBlocks { get; set; } = true;
        /// <summary>Flag blocks that are not consistent (need recompiling).</summary>
        public bool FlagInconsistentBlocks { get; set; } = true;
    }

    /// <summary>Creates sessions. The bridge picks an implementation at startup.</summary>
    public interface ITiaSessionFactory
    {
        SessionMode Mode { get; }

        /// <summary>
        /// Binds the process to one Openness version before any session exists. The CLR caches
        /// resolved assemblies per AppDomain, so this can only happen once per bridge process;
        /// a second version needs a second bridge.
        /// </summary>
        /// <param name="opennessVersion">e.g. "21.0". Null selects the newest installed.</param>
        void Configure(string opennessVersion);

        ITiaSession Create();
    }
}
