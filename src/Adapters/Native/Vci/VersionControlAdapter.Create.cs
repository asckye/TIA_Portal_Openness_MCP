#if STUDIO_VCI
using System;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
#if STUDIO_VCI_MODERN
using PortVci = TiaOpenness.Openness.VersionControlPrimitives;
#else
using PortVci = TiaMcp.Adapters.Native.Vci.LegacyVciPortPrimitives;
#endif

namespace TiaMcp.Adapters.Native.Vci
{
    internal sealed partial class VersionControlAdapter
    {
        public VersionControlResult CreateVersionControlWorkspace(
            string workspaceName,
            string folderPath,
            bool dryRun)
        {
            bool issued = false;
            try
            {
                var dir = VciWorkspaceLayout.CreationDirectory(workspaceName, folderPath, TiaOpenness.Shared.NativeInputPolicy.FullPath);
                var vci = RequireVci();
                string[] Names() => AllWorkspaces(vci).Select(PortVci.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                var before = Names();
                if (before.Contains(workspaceName.Trim(), StringComparer.OrdinalIgnoreCase))
                    return new VersionControlResult
                    {
                        Message = "A workspace named '" + workspaceName + "' already exists. " +
                                  "Use ListVersionControlWorkspaces to inspect it.",
                        Meta = HmiMeta.Basic(false, ("v4Rejection", "ALREADY_EXISTS")),
                    };

                if (dryRun)
                    return new VersionControlResult {
                        Message = "Version-control workspace '" + workspaceName.Trim() + "' would be created. Folder: " + dir.FullName,
                        Meta = HmiMeta.Basic(true, ("dryRun", true), ("mayHaveChanged", false), ("rootPath", dir.FullName)),
                    };

                var group = Keep(PortVci.Group(vci));
                var workspaces = Keep(PortVci.Workspaces(group));
                bool emptyBefore;
                try { emptyBefore = !dir.EnumerateFileSystemInfos().Any(); }
                catch (Exception) /* swallow(probe-optional): an unreadable folder cannot prove a refused registration. */
                { emptyBefore = false; }
                issued = true;
                Siemens.Engineering.VersionControl.Workspace ws;
                try { ws = PortVci.Create(workspaces, workspaceName.Trim(), dir); }
                catch (Exception error) when (emptyBefore && NativeRefusalProbe.ProvesUnchanged(error,
                    () => before.SequenceEqual(Names()) && !dir.EnumerateFileSystemInfos().Any()))
                {
                    var evidence = HmiMeta.Basic(false, ("mayHaveChanged", false), ("apiCallSuccess", false), ("nativeMessage", error.Message));
                    return new VersionControlResult { Message = "Workspace creation refused. Registration and the empty folder were verified.",
                        Meta = evidence };
                }
                Keep(ws);
                return new VersionControlResult
                {
                    Message = "Version-control workspace '" + PortVci.Name(ws) + "' was created. " +
                              "Call ConnectProjectToWorkspace to map objects, then SynchronizeVersionControlWorkspace to export them. Folder: " + dir.FullName,
                    Meta = HmiMeta.Basic(true),
                };
            }
            catch (Exception ex)
            {
                return new VersionControlResult
                {
                    Message = "CreateVersionControlWorkspace failed: " + ex.Message,
                    Meta = FailureEvidence(ex, issued),
                };
            }
        }

    }
}
#endif
