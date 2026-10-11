using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    public sealed class LibrarySelection { public bool IsFolder { get; set; } public string Path { get; set; } = ""; }
    public sealed class LibraryRequest
    {
        public string[] SourceDevicePath { get; set; } = Array.Empty<string>();
        public string Operation { get; set; } = "";
        public string? ValidationError { get; set; }
        public string TypePath { get; set; } = "";
        public string Version { get; set; } = "";
        public string Action { get; set; } = "";
        public string LibraryName { get; set; } = "";
        public string NewVersion { get; set; } = "";
        public string DependenciesMode { get; set; } = "";
        public string Author { get; set; } = "";
        public string Comment { get; set; } = "";
        public string TargetSoftwarePath { get; set; } = "";
        public bool DryRun { get; set; } = false;
        public string SourceKind { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string SoftwarePath { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public bool IncludeTypes { get; set; } = false;
        public bool IncludeMasterCopies { get; set; } = false;
        public int MaxDepth { get; set; } = 0;
        public int MaxItems { get; set; } = 0;
        public string Guid { get; set; } = "";
        public int Offset { get; set; } = 0;
        public int Limit { get; set; } = 0;
        public string UpdateCheckMode { get; set; } = "";
        public Dictionary<string, HardwareScalar> Properties { get; set; } = new Dictionary<string, HardwareScalar>();
        public string TargetLibraryName { get; set; } = "";
        public string[] ScopeSoftwarePaths { get; set; } = Array.Empty<string>();
        public string DeleteUnusedVersionsMode { get; set; } = "";
        public string StructureConflictResolutionMode { get; set; } = "";
        public string ForceUpdateMode { get; set; } = "";
        public bool ConfirmDelete { get; set; } = false;
        public LibrarySelection[] Selection { get; set; } = Array.Empty<LibrarySelection>();
        public string[] HarmonizeOptions { get; set; } = Array.Empty<string>();
        public string CleanUpMode { get; set; } = "";
        public bool ConfirmChange { get; set; } = false;
        public string Kind { get; set; } = "";
        public string LeftPath { get; set; } = "";
        public string RightPath { get; set; } = "";
        public string LeftLibraryName { get; set; } = "";
        public string RightLibraryName { get; set; } = "";
        public string LeftVersion { get; set; } = "";
        public string RightVersion { get; set; } = "";
        public bool IncludeIdentical { get; set; } = false;
        public string DestinationLibraryName { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ImportOptions { get; set; } = "";
        public string CreateOptions { get; set; } = "";
        public string TargetGroupKind { get; set; } = "";
        public string TargetGroupPath { get; set; } = "";
        public string DestinationDirectory { get; set; } = "";
        public string OpenMode { get; set; } = "";
        public bool Upgrade { get; set; } = false;
        public string ArchiveName { get; set; } = "";
        public string ArchiveMode { get; set; } = "";
        public string FolderKind { get; set; } = "";
        public string NewName { get; set; } = "";
        public string LibraryPath { get; set; } = "";
        public string MasterCopyName { get; set; } = "";
        public string HmiSoftwarePath { get; set; } = "";
        public string ScreenName { get; set; } = "";
        public string ImportedItemName { get; set; } = "";
        public int Left { get; set; } = 0;
        public int Top { get; set; } = 0;
    }
    public class GlobalLibraryProbeEvidence
    {
        public string? Message { get; set; }
        public bool? Ok { get; set; }
        public string? LibraryPath { get; set; }
        public string? ResolvedLibraryFile { get; set; }
        public string? LibraryType { get; set; }
        public IEnumerable<string>? Members { get; set; }
        public IEnumerable<string>? MasterCopies { get; set; }
        public IEnumerable<string>? Types { get; set; }
        public IEnumerable<string>? Folders { get; set; }
        public IEnumerable<string>? Warnings { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, object?>? Raw { get; set; }
    }

    public class GlobalLibraryImportEvidence
    {
        public string? Message { get; set; }
        public bool? Ok { get; set; }
        public string? LibraryPath { get; set; }
        public string? ResolvedLibraryFile { get; set; }
        public string? MasterCopyName { get; set; }
        public string? HmiSoftwarePath { get; set; }
        public string? ScreenName { get; set; }
        public string? ImportedItemName { get; set; }
        public IEnumerable<string>? Attempts { get; set; }
        public IEnumerable<string>? ReadbackItems { get; set; }
        public IEnumerable<string>? Warnings { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, object?>? Raw { get; set; }
    }

    public sealed class LibraryReply : IWorkerOperationReply
    {
        public HardwareAddressingReply? Step { get; set; }
        public GlobalLibraryProbeEvidence? Probe { get; set; }
        public GlobalLibraryImportEvidence? Import { get; set; }
        public bool MayHaveChanged => Step?.MayHaveChanged == true || Import?.Ok == true || Import?.Attempts?.Any(a => a.StartsWith("Try ", StringComparison.Ordinal)) == true;
        public bool RequiresSessionReset => Step?.RequiresSessionReset == true || MayHaveChanged && Import?.Ok == false;
        public bool BlockReadsAfterUncertain => true;
    }
}
