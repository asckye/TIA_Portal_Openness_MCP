using System;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Collaboration;

namespace TiaMcp.Adapters;

public sealed partial class PlcFoundationEngine
{
#if COLLABORATION_TEAMCENTER
    private TeamcenterAdapter? teamcenter;
#endif
    public VersionControlResult ExecuteTeamcenter(TeamcenterRequest request, bool dryRun = true)
    {
        Check();
        if (request.DryRun != dryRun) throw new ArgumentException("Conflicting Teamcenter preview mode.");
#if COLLABORATION_TEAMCENTER
        var adapter = teamcenter ??= new TeamcenterAdapter(new CollaborationSession(this));
        adapter.Request = request;
        try
        {
            return request.Operation switch
            {
                "ManageTeamcenterConnection" => adapter.ManageTeamcenterConnection(request.Action, request.UserName, request.Password, request.Group, request.Role, request.HostUrl, request.Instance, request.LoginUrl, request.ApplicationId, request.DryRun),
                "ManageTeamcenterDataset" => adapter.ManageTeamcenterDataset(request.Action, request.ItemId, request.RevisionId, request.DatasetType, request.DatasetName, request.ItemType, request.TiaObjectName, request.ItemName, request.LocalCacheOption, request.DryRun),
                "ManageTeamcenterWorkflow" => adapter.ManageTeamcenterWorkflow(request.Action, request.Target, request.LibraryName, request.ItemType, request.ItemId, request.RevisionId, request.LocalCacheOption, "{}", "{}", "{}", request.ConfirmSave, request.DryRun),
                _ => throw new ArgumentException("Unregistered Teamcenter operation: " + request.Operation)
            };
        }
        finally { adapter.Request = null; }
#else
        throw new NotSupportedException("TeamcenterGateway requires V18 or later.");
#endif
    }
}
