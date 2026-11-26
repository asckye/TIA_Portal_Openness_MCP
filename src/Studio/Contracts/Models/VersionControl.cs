using System;
using System.Collections.Generic;

namespace TiaOpenness.Contracts.Models
{
    /// <summary>Which way a workspace synchronization moves data.</summary>
    public enum SyncDirection
    {
        /// <summary>Write the project's objects out as text files. Safe: nothing in the project changes.</summary>
        ProjectToWorkspace = 0,
        /// <summary>Read the text files back into the project. Destructive: overwrites blocks.</summary>
        WorkspaceToProject = 1,
    }

    /// <summary>How a mapped object compares against its file on disk.</summary>
    public enum VcCompareState
    {
        /// <summary>TIA reports no difference.</summary>
        Equal = 0,
        /// <summary>They differ. This is the thing worth committing.</summary>
        Unequal = 1,
        /// <summary>The object is mapped but was never written out.</summary>
        WorkspaceFileMissing = 2,
        /// <summary>TIA could not decide. Reported rather than guessed at.</summary>
        Unknown = 3,
    }

    /// <summary>A Version Control Interface workspace: the project's plain-text mirror on disk.</summary>
    public class WorkspaceInfo
    {
        public string Name { get; set; }
        /// <summary>Folder the text files live in - normally a Git working tree.</summary>
        public string RootPath { get; set; }
        public string Language { get; set; }
        public int MappedObjectCount { get; set; }
    }

    /// <summary>One project object mapped into a workspace.</summary>
    public class MappedObjectInfo
    {
        public string Name { get; set; }
        /// <summary>Path of the text file, without its extension.</summary>
        public string FilePath { get; set; }
        /// <summary>The VCI file format, e.g. "s7dcl" or a SimaticML flavour.</summary>
        public string FileFormat { get; set; }
        public VcCompareState CompareState { get; set; }
        public bool? Verified { get; set; }
        public string RefreshMessage { get; set; }
        // The legacy bridge codec uses the ShouldSerialize convention.
        public bool ShouldSerializeRefreshMessage() => RefreshMessage != null;
        /// <summary>Present when the state could not be determined.</summary>
        public string Error { get; set; }
    }

    /// <summary>What differs between the project and the workspace files.</summary>
    public class WorkspaceStatusReport
    {
        public string WorkspaceName { get; set; }
        public string RootPath { get; set; }
        public int Total { get; set; }
        /// <summary>Objects with a known difference, including missing workspace files.</summary>
        public int Differing { get; set; }
        public int Unknown { get; set; }
        public int UnverifiedEqual { get; set; }
        public List<MappedObjectInfo> Items { get; set; } = new List<MappedObjectInfo>();
        public bool DataComplete => Unknown == 0 && UnverifiedEqual == 0;
        public bool InSync { get { return Differing == 0 && Unknown == 0 && UnverifiedEqual == 0; } }
    }

    /// <summary>Outcome of mapping one project object into a workspace.</summary>
    public class MappingItem
    {
        /// <summary>Human-readable position in the project tree.</summary>
        public string Target { get; set; }
        /// <summary>"mapped", "already mapped", "would map", "unsupported" or "failed".</summary>
        public string Outcome { get; set; }
        public string FileFormat { get; set; }
        /// <summary>Folder inside the workspace; empty means the workspace root.</summary>
        public string Directory { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Aggregate result of putting project objects under version control.</summary>
    public class MappingResult
    {
        public string WorkspaceName { get; set; }
        public string RootPath { get; set; }
        public bool DryRun { get; set; }
        public int Visited { get; set; }
        public int Mapped { get; set; }
        public int AlreadyMapped { get; set; }
        public int Unsupported { get; set; }
        public int Failed { get; set; }
        /// <summary>True when the walk stopped at its node budget rather than finishing.</summary>
        public bool Truncated { get; set; }
        public List<MappingItem> Items { get; set; } = new List<MappingItem>();
    }

    /// <summary>Outcome of synchronizing one mapped object.</summary>
    public class SyncItem
    {
        public List<string> NotCompared { get; set; }
        public bool ShouldSerializeNotCompared() => NotCompared != null;
        public string FilePath { get; set; }
        public string StateBefore { get; set; }
        public string WorkspaceFileSha256 { get; set; }
        public string Verification { get; set; }
        public string NativeMessage { get; set; }
        public bool? FileChanged { get; set; }
        public string RefreshMessage { get; set; }
        public bool ShouldSerializeRefreshMessage() => RefreshMessage != null;
        public string Name { get; set; }
        public string Outcome { get; set; }
        public string Error { get; set; }
    }

    /// <summary>What a line of a unified diff is.</summary>
    public enum DiffLineKind
    {
        /// <summary>Unchanged, shown for context.</summary>
        Context = 0,
        Added = 1,
        Removed = 2,
        /// <summary>A hunk header, the @@ line.</summary>
        Hunk = 3,
        /// <summary>A file header: diff, index, ---, +++.</summary>
        Header = 4,
    }

    /// <summary>One line of a unified diff.</summary>
    public class DiffLine
    {
        public DiffLineKind Kind { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// The workspace's uncommitted changes, as Git sees them.
    ///
    /// The comparison has to run against the files rather than against the project: VCI reports
    /// whether a mapped object matches its file, but gives no way to read the project side without
    /// writing it out first. So the order is push, then review this, then commit.
    /// </summary>
    public class WorkspaceDiff
    {
        public string WorkspaceName { get; set; }
        public string RootPath { get; set; }
        /// <summary>False when the folder is not a Git repository, or Git is not installed.</summary>
        public bool Available { get; set; }
        /// <summary>Why it is unavailable, or what was run.</summary>
        public string Detail { get; set; }
        /// <summary>Files with uncommitted changes, whether or not a diff was requested for one.</summary>
        public List<string> ChangedFiles { get; set; } = new List<string>();
        public List<DiffLine> Lines { get; set; } = new List<DiffLine>();
    }

    /// <summary>Aggregate result of a workspace synchronization.</summary>
    public class SyncResult
    {
        public string WorkspaceName { get; set; }
        public string RootPath { get; set; }
        public SyncDirection Direction { get; set; }
        public bool DryRun { get; set; }
        public int Synchronized { get; set; }
        public int Failed { get; set; }
        /// <summary>Mappings skipped after an accepted Equal status or a matching content comparison.</summary>
        public int SkippedEqual { get; set; }
        public int VerifiedEqual { get; set; }
        public int NotVerified { get; set; }
        public int WouldVerify { get; set; }
        public int NotAttempted { get; set; }
        public int SkippedDisconnected { get; set; }
        public bool DataComplete { get; set; } = true;
        public int UnverifiedEqual { get; set; }
        public bool ComparisonInterrupted { get; set; }
        public List<SyncItem> Items { get; set; } = new List<SyncItem>();
    }
}
