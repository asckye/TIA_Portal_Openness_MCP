using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class TeamcenterPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<string> identity;
    public TeamcenterPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal TeamcenterPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    { this.call = call; this.identity = identity; }
    private ResponseMessage Invoke(TeamcenterRequest request)
    {
        if (!PortedFamilies.Available(HardwareContract.ReleaseKey, request.Operation))
            throw new NotSupportedException("TeamcenterGateway requires V18 or later.");
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
        if (!request.DryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
        var reply = call("version-control.ExecuteTeamcenter", arguments)
            ?? throw new InvalidOperationException("Missing Teamcenter worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }
    public ResponseMessage ManageTeamcenterConnection(string action = "read", string userName = "", string password = "", string group = "", string role = "", string hostUrl = "", string instance = "", string loginUrl = "", string applicationId = "", bool dryRun = true)
    {
        var request = new TeamcenterRequest { Operation = "ManageTeamcenterConnection", Action = action, UserName = userName, Password = password, Group = group, Role = role, HostUrl = hostUrl, Instance = instance, LoginUrl = loginUrl, ApplicationId = applicationId, DryRun = dryRun };
        try
        {
            var plan = TeamcenterLogic.ValidateConnectionRequest(action, userName, password, group, role, hostUrl, instance, loginUrl, applicationId, dryRun);
            request.Plan = JsonSerializer.Deserialize<TeamcenterPlan>(JsonSerializer.Serialize(plan, new JsonSerializerOptions { IncludeFields = true }))!;
        }
        catch (ArgumentException error) { request.ValidationError = error.Message; }
        return Invoke(request);
    }

    public ResponseMessage ManageTeamcenterDataset(string action, string itemId = "", string revisionId = "", string datasetType = "", string datasetName = "", string itemType = "", string tiaObjectName = "", string itemName = "", string localCacheOption = "", bool dryRun = true)
    {
        var request = new TeamcenterRequest { Operation = "ManageTeamcenterDataset", Action = action, ItemId = itemId, RevisionId = revisionId, DatasetType = datasetType, DatasetName = datasetName, ItemType = itemType, TiaObjectName = tiaObjectName, ItemName = itemName, LocalCacheOption = localCacheOption, DryRun = dryRun };
        try
        {
            var plan = TeamcenterLogic.ValidateDatasetRequest(action, itemId, revisionId, datasetType, datasetName, itemType, tiaObjectName, itemName, localCacheOption, dryRun);
            request.Plan = JsonSerializer.Deserialize<TeamcenterPlan>(JsonSerializer.Serialize(plan, new JsonSerializerOptions { IncludeFields = true }))!;
        }
        catch (ArgumentException error) { request.ValidationError = error.Message; }
        return Invoke(request);
    }

    public ResponseMessage ManageTeamcenterWorkflow(string action, string target = "project", string libraryName = "", string itemType = "", string itemId = "", string revisionId = "", string localCacheOption = "", string itemDetailsJson = "{}", string revisionDetailsJson = "{}", string customAttributesJson = "{}", bool confirmSave = false, bool dryRun = true)
    {
        var request = new TeamcenterRequest { Operation = "ManageTeamcenterWorkflow", Action = action, Target = target, LibraryName = libraryName, ItemType = itemType, ItemId = itemId, RevisionId = revisionId, LocalCacheOption = localCacheOption, ConfirmSave = confirmSave, DryRun = dryRun };
        try
        {
            var plan = TeamcenterLogic.ValidateWorkflowRequest(action, target, libraryName, itemType, itemId, revisionId, localCacheOption, itemDetailsJson, revisionDetailsJson, customAttributesJson, confirmSave, dryRun);
            request.Plan = JsonSerializer.Deserialize<TeamcenterPlan>(JsonSerializer.Serialize(plan, new JsonSerializerOptions { IncludeFields = true }))!;
        }
        catch (ArgumentException error) { request.ValidationError = error.Message; }
        return Invoke(request);
    }
}
