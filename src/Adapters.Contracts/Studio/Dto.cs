#nullable disable
using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts.Studio
{
    public class SessionState
    {
        public bool Connected { get; set; }
        public SessionMode Mode { get; set; }
        public string OpennessVersion { get; set; }
        /// <summary>True when the TIA Portal window is visible (WithUserInterface).</summary>
        public bool WithUserInterface { get; set; }
        public ProjectInfo OpenProject { get; set; }
    }

    public class ProjectInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Author { get; set; }
        public string Comment { get; set; }
        public DateTimeOffset? CreationTime { get; set; }
        public DateTimeOffset? LastModified { get; set; }
        public bool IsModified { get; set; }
    }

    public class DeviceInfo
    {
        /// <summary>Stable address used by later calls. The station name, which never changes shape.</summary>
        public string Id { get; set; }
        public string Name { get; set; }

        /// <summary>
        /// What TIA shows in its project tree: the name of the module that carries the software,
        /// not the station. A station is called "S71500/ET200MP-Station_1" while the CPU inside it
        /// is called "FA3572", and it is the CPU name an engineer recognises.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>The type TIA prints in brackets after the name, e.g. "CPU 1517F-3 PN/DP".</summary>
        public string TypeName { get; set; }
        public string TypeIdentifier { get; set; }
        public string ArticleNumber { get; set; }
        public string FirmwareVersion { get; set; }
        /// <summary>"Plc", "Hmi", "Drive" or "Other".</summary>
        public string Category { get; set; }

        /// <summary>
        /// Slash-separated device groups this device sits in, empty at the project root. TIA lets
        /// an engineer file devices into folders, and a flat list loses that.
        /// </summary>
        public string GroupPath { get; set; }
        public List<string> ItemNames { get; set; } = new List<string>();
    }

    public class BlockInfo
    {
        /// <summary>Slash-separated path inside the block folder, e.g. "Motion/FB_Axis".</summary>
        public string Path { get; set; }

        /// <summary>
        /// The folder alone, without the name. Kept separate because a block name may itself
        /// contain a slash - splitting Path would invent a folder that does not exist.
        /// </summary>
        public string FolderPath { get; set; }
        public string Name { get; set; }
        public BlockKind Kind { get; set; }
        public int? Number { get; set; }
        public string ProgrammingLanguage { get; set; }
        public bool IsConsistent { get; set; }
        public bool IsKnowHowProtected { get; set; }
        public DateTimeOffset? ModifiedDate { get; set; }
        public string HeaderAuthor { get; set; }
        public string HeaderVersion { get; set; }
    }

    public class TagTableInfo
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public int TagCount { get; set; }
        public bool IsDefault { get; set; }
    }

    public class TagInfo
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string LogicalAddress { get; set; }
        public string Comment { get; set; }
        public string TableName { get; set; }
    }

    public class CompileMessage
    {
        public CompileSeverity Severity { get; set; }
        public string Description { get; set; }
        /// <summary>Object the message refers to, e.g. "PLC_1/Program blocks/FB_Axis".</summary>
        public string Target { get; set; }
        public string ErrorCode { get; set; }
        public List<CompileMessage> Children { get; set; } = new List<CompileMessage>();
    }

    public class CompileResult
    {
        /// <summary>TIA's own verdict, e.g. "Success", "Warning", "Error".</summary>
        public string State { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public TimeSpan Duration { get; set; }
        public List<CompileMessage> Messages { get; set; } = new List<CompileMessage>();
        public bool Succeeded { get { return ErrorCount == 0; } }
    }

    public class ExportedItem
    {
        public string BlockPath { get; set; }
        public string FilePath { get; set; }
        public bool Succeeded { get; set; }
        public string Error { get; set; }
    }

    public class ExportResult
    {
        public string OutputDirectory { get; set; }
        public int Requested { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public List<ExportedItem> Items { get; set; } = new List<ExportedItem>();
    }
}
