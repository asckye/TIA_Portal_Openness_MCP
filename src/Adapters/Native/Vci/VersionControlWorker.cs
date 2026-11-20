using System;
using TiaMcp.Adapters.Contracts;
#if STUDIO_VCI
using TiaMcp.Adapters.Native.Vci;
#endif

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
#if STUDIO_VCI
    private VersionControlAdapter? versionControl;
    private VersionControlAdapter VersionControl => versionControl ??= new VersionControlAdapter(() => project);

#endif

    public VersionControlResult ExecuteVersionControl(VersionControlRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun)
            throw new ArgumentException("Conflicting VCI preview mode.");
#if STUDIO_VCI
        var reply = request.Operation switch
        {
            "ListVersionControlWorkspaces" => VersionControl.GetVersionControlWorkspaces(),
            "GetVersionControlStatus" => VersionControl.GetVersionControlStatus(request.WorkspaceName, request.ChangedOnly),
            "CreateVersionControlWorkspace" => VersionControl.CreateVersionControlWorkspace(request.WorkspaceName, request.FolderPath, dryRun),
            "ConnectProjectToWorkspace" => VersionControl
                .ConnectProjectToWorkspace(request.WorkspaceName, dryRun, request.DeviceFilter, request.MaxObjects, request.WalkTrace),
            "SynchronizeVersionControlWorkspace" => VersionControl
                .SyncVersionControlWorkspace(request.Direction, request.WorkspaceName, dryRun, request.ChangedOnly),
            _ => throw new ArgumentException("Unregistered VCI operation: " + request.Operation)
        };
        return VersionControlAdapter.WithWriteOutcome(request, reply);
#else
        throw new NotSupportedException("VersionControlInterface requires V16 or later.");
#endif
    }
}
