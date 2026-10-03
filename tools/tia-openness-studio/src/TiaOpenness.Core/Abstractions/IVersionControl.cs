using System.Collections.Generic;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Core.Abstractions
{
    /// <summary>
    /// TIA Portal V21's Version Control Interface.
    ///
    /// A TIA project is a binary blob Git cannot diff. VCI fixes that: a <em>workspace</em> is an
    /// ordinary folder holding one text file per mapped object, so the project becomes reviewable
    /// and commitable. Unlike the export-based workflow, change <em>detection</em> works on
    /// uncompiled edits and reports per object rather than per file.
    ///
    /// A session exposes this only when the backend supports it; see
    /// <see cref="ITiaSession.VersionControl"/>, which is null on TIA Portal below V21.
    /// </summary>
    public interface IVersionControl
    {
        /// <summary>Every workspace in the open project, including those inside workspace groups.</summary>
        IReadOnlyList<WorkspaceInfo> ListWorkspaces();

        /// <summary>
        /// Creates a workspace pointing at an existing folder — normally a Git working tree.
        /// Creating it maps nothing; call <see cref="MapProject"/> afterwards.
        /// </summary>
        WorkspaceInfo CreateWorkspace(string name, string folderPath);

        /// <summary>
        /// Walks the project and maps every object VCI can handle into the workspace.
        /// Coarse-first: when a device or PLC can be mapped as a unit, its children are not
        /// visited separately, so one call covers hundreds of blocks without per-block clicking.
        /// </summary>
        /// <param name="workspaceName">Null or empty selects the project's first workspace.</param>
        /// <param name="deviceFilter">Map only this device. Null maps everything.</param>
        /// <param name="dryRun">Report what would be mapped without touching the project.</param>
        MappingResult MapProject(string workspaceName, string deviceFilter, bool dryRun, ProgressCallback progress);

        /// <summary>Per-object comparison between the project and the workspace files. Read-only.</summary>
        /// <param name="changedOnly">List only objects that are not in sync.</param>
        WorkspaceStatusReport GetStatus(string workspaceName, bool changedOnly);

        /// <summary>
        /// Synchronizes mapped objects. <see cref="SyncDirection.ProjectToWorkspace"/> writes text
        /// files out and changes nothing in the project;
        /// <see cref="SyncDirection.WorkspaceToProject"/> overwrites blocks in the open project.
        /// </summary>
        /// <param name="dryRun">Report what would move without moving it.</param>
        SyncResult Sync(string workspaceName, SyncDirection direction, bool dryRun, ProgressCallback progress);
    }
}
