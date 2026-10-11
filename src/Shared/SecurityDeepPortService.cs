using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services;
internal sealed class SecurityDeepPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<bool> hasProject;
    private readonly Func<string> identity;
    public SecurityDeepPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal SecurityDeepPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> identity)
    { this.call = call; this.hasProject = hasProject; this.identity = identity; }
    private static void Capture(SecurityDeepRequest request, string stage, Action validation)
    {
        try { validation(); }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is JsonException)
        { request.Faults[stage] = new SecurityValidationFault { Kind = error is InvalidOperationException ? "state" : "argument", Message = error.Message }; }
    }
    private static JsonObject Properties(SecurityDeepRequest request, string json, bool attributes)
    {
        var properties = new JsonObject();
        Capture(request, attributes ? "attributes" : "properties", () => {
            properties = HardwareNetworkLogic.ParseObject(json, attributes ? "attributesJson" : "propertiesJson");
            if (attributes) request.Attributes = HardwareAddressingService.Scalars(json); else request.Properties = HardwareAddressingService.Scalars(json);
        });
        return properties;
    }
    private ResponseMessage Invoke(SecurityDeepRequest request)
    {
        if (request.Operation == "ManageUmcUsers"
            && (request.Action is "createOffline" or "rename" && HardwareContract.ReleaseKey is "17" or "18" or "19"
                || request.Kind == "server" && request.Action is "checkConsistency" or "synchronize" && HardwareContract.ReleaseKey == "17"))
            return new ResponseMessage { Message = "The requested UMC action is unavailable on this release.", Meta = new JsonObject {
                ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false, ["status"] = "NotSupportedOnVersion" } };
        if (request.Operation == "ManageSyslogServers" && request.Scope == "project" && HardwareContract.ReleaseKey == "19")
            return new ResponseMessage { Message = "Project-global syslog requires V20 or later.", Meta = new JsonObject {
                ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false, ["status"] = "NotSupportedOnVersion" } };
        if (!hasProject()) return JsonSerializer.Deserialize<ResponseMessage>(HardwarePortPreconditions.NoProject(request.Operation).ToJsonString())!;
        bool writing = !request.DryRun && request.Action != "read" && request.Action != "checkConsistency";
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
        if (writing) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
        var reply = call("project-security.ExecuteSecurityDeep", arguments) ?? throw new InvalidOperationException("Missing security worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }
    public ResponseMessage ManageSyslogServers(string scope = "project", string action = "read", string name = "", string propertiesJson = "{}", string attributesJson = "{}", string devicePathJson = "[]", string itemPathJson = "[]", string serverAddress = "", int serverPort = 0, bool confirmDelete = false, bool dryRun = true)
    {
        var request = new SecurityDeepRequest { Operation = "ManageSyslogServers", Scope = scope, Action = action, Name = name, ServerAddress = serverAddress, ServerPort = serverPort, ConfirmDelete = confirmDelete, DryRun = dryRun };
        var properties = Properties(request, propertiesJson, false); Properties(request, attributesJson, true);
        Capture(request, "syslog", () => SecurityDeepLogic.ValidateSyslogRequest(scope, action, name, properties, devicePathJson, serverAddress, serverPort, confirmDelete, dryRun));
        Capture(request, "locator", () => { request.DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!; request.ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!; });
        return Invoke(request);
    }
    public ResponseMessage ManagePasswordPolicy(string action = "read", string target = "", string propertiesJson = "{}", bool confirmChange = false, bool dryRun = true)
    {
        var request = new SecurityDeepRequest { Operation = "ManagePasswordPolicy", Action = action, Target = target, ConfirmChange = confirmChange, DryRun = dryRun };
        var properties = Properties(request, propertiesJson, false);
        Capture(request, "policy", () => SecurityDeepLogic.ValidatePolicyRequest(action, target, properties, confirmChange, dryRun));
        return Invoke(request);
    }
    public ResponseMessage ManageUmcUsers(string kind = "user", string action = "read", string name = "", string newName = "", string roleName = "", string serverUserName = "", string serverPassword = "", bool confirmChange = false, bool dryRun = true, int offset = 0, int limit = 100)
    {
        var request = new SecurityDeepRequest { Operation = "ManageUmcUsers", Kind = kind, Action = action, Name = name, NewName = newName, RoleName = roleName, ServerUserName = serverUserName, ServerPassword = serverPassword, ConfirmChange = confirmChange, DryRun = dryRun, Offset = offset, Limit = limit };
        Capture(request, "umc", () => { request.UmcPlan = JsonSerializer.Deserialize<UmcPlan>(JsonSerializer.Serialize(SecurityDeepLogic.ValidateUmcRequest(kind, action, name, newName, roleName, serverUserName, serverPassword, confirmChange, dryRun), new JsonSerializerOptions { IncludeFields = true }))!; });
        Capture(request, "pagination", () => HardwareServicesLogic.ValidatePagination(offset, limit));
        return Invoke(request);
    }
}
