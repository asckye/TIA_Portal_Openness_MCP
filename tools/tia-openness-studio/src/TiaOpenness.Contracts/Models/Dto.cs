using System;
using System.Collections.Generic;

namespace TiaOpenness.Contracts.Models
{
    /// <summary>One TIA Portal / Openness installation discovered on the machine.</summary>
    public class OpennessInstallation
    {
        /// <summary>Openness API version, e.g. "21.0".</summary>
        public string Version { get; set; }
        /// <summary>Full path to the assembly that anchors the API for this version.</summary>
        public string EngineeringDllPath { get; set; }
        /// <summary>Directory holding the public API assemblies.</summary>
        public string PublicApiDirectory { get; set; }
        /// <summary>How the entry was found: "Registry" or "Probe".</summary>
        public string DiscoveredBy { get; set; }

        /// <summary>
        /// True from V21 on, where Siemens split the monolithic Siemens.Engineering.dll into
        /// Siemens.Engineering.Base.dll, .Step7.dll, .WinCC.dll and friends, moved them under a
        /// <c>net48</c> subfolder, and re-signed them with a new public key token. Code built
        /// against V20 or earlier does not run on V21 and vice versa.
        /// </summary>
        public bool IsModular { get; set; }

        /// <summary>File name of the anchor assembly: Siemens.Engineering.Base.dll from V21, else Siemens.Engineering.dll.</summary>
        public string PrimaryAssembly { get; set; }

        /// <summary>Every Siemens.Engineering* assembly in <see cref="PublicApiDirectory"/>.</summary>
        public List<string> Assemblies { get; set; } = new List<string>();
    }

    /// <summary>Result of one environment precondition check.</summary>
    public class DoctorCheck
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public CheckStatus Status { get; set; }
        public string Detail { get; set; }
        /// <summary>Actionable remedy shown to the engineer when Status is not Pass.</summary>
        public string Remedy { get; set; }
    }

    /// <summary>Full environment report produced by the Doctor.</summary>
    public class DoctorReport
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public string MachineName { get; set; }
        public string UserName { get; set; }
        public bool Is64BitProcess { get; set; }
        public string ClrVersion { get; set; }
        public List<DoctorCheck> Checks { get; set; } = new List<DoctorCheck>();
        public List<OpennessInstallation> Installations { get; set; } = new List<OpennessInstallation>();

        /// <summary>True when no check failed outright.</summary>
        public bool CanRunOpenness
        {
            get
            {
                foreach (var c in Checks) { if (c.Status == CheckStatus.Fail) return false; }
                return true;
            }
        }
    }

    /// <summary>State of the bridge's connection to TIA Portal.</summary>
    public class SessionState
    {
        public bool Connected { get; set; }
        public SessionMode Mode { get; set; }
        public string OpennessVersion { get; set; }
        /// <summary>True when the TIA Portal window is visible (WithUserInterface).</summary>
        public bool WithUserInterface { get; set; }
        public ProjectInfo OpenProject { get; set; }
    }

    /// <summary>An open TIA Portal project.</summary>
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

    /// <summary>A device (PLC station, HMI, drive) in the project tree.</summary>
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

    /// <summary>A software block or a UDT in a PLC program.</summary>
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

    /// <summary>A PLC tag table.</summary>
    public class TagTableInfo
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public int TagCount { get; set; }
        public bool IsDefault { get; set; }
    }

    /// <summary>A single PLC tag.</summary>
    public class TagInfo
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string LogicalAddress { get; set; }
        public string Comment { get; set; }
        public string TableName { get; set; }
    }

    /// <summary>One line of compiler output.</summary>
    public class CompileMessage
    {
        public CompileSeverity Severity { get; set; }
        public string Description { get; set; }
        /// <summary>Object the message refers to, e.g. "PLC_1/Program blocks/FB_Axis".</summary>
        public string Target { get; set; }
        public string ErrorCode { get; set; }
        public List<CompileMessage> Children { get; set; } = new List<CompileMessage>();
    }

    /// <summary>Aggregate result of a compile run.</summary>
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

    /// <summary>One exported file produced by an export run.</summary>
    public class ExportedItem
    {
        public string BlockPath { get; set; }
        public string FilePath { get; set; }
        public bool Succeeded { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Aggregate result of a batch export.</summary>
    public class ExportResult
    {
        public string OutputDirectory { get; set; }
        public int Requested { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public List<ExportedItem> Items { get; set; } = new List<ExportedItem>();
    }

    /// <summary>One finding from a project inspection rule.</summary>
    public class InspectionFinding
    {
        /// <summary>Rule that produced the finding, e.g. "NAMING-001".</summary>
        public string RuleId { get; set; }
        public CheckStatus Severity { get; set; }
        public string Target { get; set; }
        public string Message { get; set; }
        public string Suggestion { get; set; }
    }

    /// <summary>Aggregate result of a project inspection.</summary>
    public class InspectionReport
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public string DeviceId { get; set; }
        public int BlocksScanned { get; set; }
        public List<InspectionFinding> Findings { get; set; } = new List<InspectionFinding>();
    }
}

namespace TiaOpenness.Contracts.Models
{
    /// <summary>Outcome of building the Openness adapter against the local TIA installation.</summary>
    public class AdapterBuildResult
    {
        public bool Succeeded { get; set; }
        /// <summary>Openness version the adapter was built against, e.g. "21.0".</summary>
        public string OpennessVersion { get; set; }
        /// <summary>Directory the Siemens assemblies were referenced from.</summary>
        public string ReferenceDirectory { get; set; }
        /// <summary>Where the adapter was written.</summary>
        public string OutputPath { get; set; }
        /// <summary>Number of Siemens assemblies referenced.</summary>
        public int ReferencedAssemblies { get; set; }
        /// <summary>
        /// Compiler diagnostics when it failed. Each names a file and line in the adapter sources,
        /// so a missing Siemens member is reported as the API mismatch it is.
        /// </summary>
        public List<string> Errors { get; set; } = new List<string>();
    }
}
