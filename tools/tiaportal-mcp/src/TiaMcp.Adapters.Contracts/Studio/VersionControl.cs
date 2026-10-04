#nullable disable
using System;
using System.Collections.Generic;

namespace TiaMcp.Adapters.Contracts.Studio
{
    public enum SyncDirection
    {
        /// <summary>Write the project's objects out as text files. Safe: nothing in the project changes.</summary>
        ProjectToWorkspace = 0,
        /// <summary>Read the text files back into the project. Destructive: overwrites blocks.</summary>
        WorkspaceToProject = 1,
    }

    public enum VcCompareState
    {
        /// <summary>Project and file agree; nothing to synchronize.</summary>
        Equal = 0,
        /// <summary>They differ. This is the thing worth committing.</summary>
        Unequal = 1,
        /// <summary>The object is mapped but was never written out.</summary>
        WorkspaceFileMissing = 2,
        /// <summary>TIA could not decide. Reported rather than guessed at.</summary>
        Unknown = 3,
    }

    public class WorkspaceInfo
    {
        public string Name { get; set; }
        /// <summary>Folder the text files live in - normally a Git working tree.</summary>
        public string RootPath { get; set; }
        public string Language { get; set; }
        public int MappedObjectCount { get; set; }
    }

    public class MappedObjectInfo
    {
        public string Name { get; set; }
        /// <summary>Path of the text file, without its extension.</summary>
        public string FilePath { get; set; }
        /// <summary>The VCI file format, e.g. "s7dcl" or a SimaticML flavour.</summary>
        public string FileFormat { get; set; }
        public VcCompareState CompareState { get; set; }
        /// <summary>Present when the state could not be determined.</summary>
        public string Error { get; set; }
    }

    public class WorkspaceStatusReport
    {
        public string WorkspaceName { get; set; }
        public string RootPath { get; set; }
        public int Total { get; set; }
        /// <summary>Objects whose state is not <see cref="VcCompareState.Equal"/>.</summary>
        public int Differing { get; set; }
        public List<MappedObjectInfo> Items { get; set; } = new List<MappedObjectInfo>();
        public bool InSync { get { return Differing == 0; } }
    }

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

    public class SyncItem
    {
        public string Name { get; set; }
        public string Outcome { get; set; }
        public string Error { get; set; }
    }

    public class SyncResult
    {
        public string WorkspaceName { get; set; }
        public string RootPath { get; set; }
        public SyncDirection Direction { get; set; }
        public bool DryRun { get; set; }
        public int Synchronized { get; set; }
        public int Failed { get; set; }
        /// <summary>Objects already in sync. TIA refuses to synchronize these, so they are skipped.</summary>
        public int SkippedEqual { get; set; }
        public List<SyncItem> Items { get; set; } = new List<SyncItem>();
    }
}
