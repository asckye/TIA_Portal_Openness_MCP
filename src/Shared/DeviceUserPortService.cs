using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services;
internal sealed class DeviceUserPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<bool> hasProject;
    private readonly Func<string> identity;
    public DeviceUserPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal DeviceUserPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> identity)
    { this.call = call; this.hasProject = hasProject; this.identity = identity; }
    private static void Capture(DeviceUserRequest request, string stage, Action validation)
    {
        try { validation(); }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is JsonException)
        { request.Faults[stage] = new SecurityValidationFault { Kind = error is InvalidOperationException ? "state" : "argument", Message = error.Message }; }
    }
    public ResponseMessage ManageDeviceUsers(string devicePathJson, string itemPathJson, string family, string action = "read", string userName = "", string password = "", string permissionsJson = "[]", bool active = true, string newName = "", bool confirmDelete = false, bool dryRun = true)
    {
        if (family == "simpleWebserver" && HardwareContract.ReleaseKey is "16" or "17")
            return new ResponseMessage { Message = "Simple web users require V18 or later.", Meta = new JsonObject { ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false, ["status"] = "NotSupportedOnVersion" } };
        if (!hasProject()) return JsonSerializer.Deserialize<ResponseMessage>(HardwarePortPreconditions.NoProject("ManageDeviceUsers").ToJsonString())!;
        var request = new DeviceUserRequest { Family = family, Action = action, UserName = userName, Password = password, Active = active, NewName = newName, ConfirmDelete = confirmDelete, DryRun = dryRun };
        Capture(request, "locator", () => { request.DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!; request.ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!; });
        Capture(request, "permissions", () => request.Permissions = HardwareNetworkLogic.ParseNames(permissionsJson, "permissionsJson", 32));
        Capture(request, "user", () => HardwareNetworkLogic.ValidateUserRequest(family, action, userName, password, request.Permissions, newName));
        Capture(request, "confirmation", () => HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun));
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = dryRun };
        if (!dryRun && action != "read") { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
        var reply = call("project-security.ExecuteDeviceUsers", arguments) ?? throw new InvalidOperationException("Missing device user worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }
}
