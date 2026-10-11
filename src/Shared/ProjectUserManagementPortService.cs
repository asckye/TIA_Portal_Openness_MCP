using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services;
internal sealed class ProjectUserManagementPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<bool> hasProject;
    private readonly Func<string> identity;
    public ProjectUserManagementPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal ProjectUserManagementPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> identity)
    { this.call = call; this.hasProject = hasProject; this.identity = identity; }
    private static void Capture(ProjectUserRequest request, string stage, Action validation)
    {
        try { validation(); }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is JsonException)
        { request.Faults[stage] = new SecurityValidationFault { Kind = error is InvalidOperationException ? "state" : "argument", Message = error.Message }; }
    }
    private ResponseMessage Invoke(ProjectUserRequest request, string devicePathJson, string itemPathJson)
    {
        string release = HardwareContract.ReleaseKey;
        if (request.Operation == "GetProjectUserManagement" && request.Category == "passwordPolicy" && release == "17"
            || request.Action == "deleteDeviceRight" && release is "17" or "18" or "19")
            return new ResponseMessage { Message = "The requested security action is unavailable on this release.", Meta = new JsonObject {
                ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false, ["status"] = "NotSupportedOnVersion" } };
        if (!hasProject()) return JsonSerializer.Deserialize<ResponseMessage>(HardwarePortPreconditions.NoProject(request.Operation).ToJsonString())!;
        Capture(request, "locator", () => { request.DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!; request.ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!; });
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
        if (!request.DryRun) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
        var reply = call("project-security.ExecuteProjectUsers", arguments) ?? throw new InvalidOperationException("Missing project user worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }
    public ResponseMessage ReadProjectUserManagement(string category = "users", string name = "", string devicePathJson = "[]", string itemPathJson = "[]", int offset = 0, int limit = 100)
    {
        var request = new ProjectUserRequest { Operation = "GetProjectUserManagement", Category = category, Name = name, Offset = offset, Limit = limit };
        Capture(request, "category", () => ProjectSecurityLogic.ValidateCategory(category));
        Capture(request, "page", () => ProjectSecurityLogic.ValidatePage(offset, limit));
        Capture(request, "devicePath", () => ProjectSecurityLogic.ValidateDevicePath(devicePathJson));
        return Invoke(request, devicePathJson, itemPathJson);
    }
    public ResponseMessage ManageProjectUserManagement(string action, string name, string password = "", string roleName = "", string rightName = "", string comment = "", string group = "", string devicePathJson = "[]", string itemPathJson = "[]", bool confirmChange = false, bool dryRun = true)
    {
        var request = new ProjectUserRequest { Operation = "ManageProjectUserManagement", Action = action, Name = name, Password = password, RoleName = roleName, RightName = rightName, Comment = comment, Group = group, ConfirmChange = confirmChange, DryRun = dryRun };
        Capture(request, "userPlan", () => {
            var plan = ProjectSecurityLogic.ValidateUserManagement(action, name, password, roleName, rightName, group, devicePathJson);
            request.Plan = JsonSerializer.Deserialize<ProjectUserPlan>(JsonSerializer.Serialize(plan, new JsonSerializerOptions { IncludeFields = true }))!;
        });
        return Invoke(request, devicePathJson, itemPathJson);
    }
}
