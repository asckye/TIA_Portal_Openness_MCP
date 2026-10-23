using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services;
internal sealed class HardwareSecurityPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<bool> hasProject;
    private readonly Func<string> identity;
    public HardwareSecurityPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal HardwareSecurityPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> identity)
    { this.call = call; this.hasProject = hasProject; this.identity = identity; }
    private static void Capture(HardwareSecurityRequest request, string stage, Action validation)
    {
        try { validation(); }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is JsonException)
        { request.Faults[stage] = new SecurityValidationFault { Kind = error is InvalidOperationException ? "state" : "argument", Message = error.Message }; }
    }
    private JsonNode Invoke(HardwareSecurityRequest request)
    {
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = request.DryRun };
        if (!request.DryRun && request.Action != "read") { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
        return call("project-security.ExecuteHardwareSecurity", arguments) ?? throw new InvalidOperationException("Missing hardware security worker reply.");
    }
    public JsonObject GetPutGetAccess(string devicePath)
        => !hasProject() ? new JsonObject { ["found"] = false, ["message"] = "No project open." }
            : Invoke(new HardwareSecurityRequest { Operation = "GetPlcPutGetAccess", DeviceName = devicePath }).AsObject();
    public JsonObject SetPutGetAccess(string devicePath, bool enable, bool dryRun = true)
        => !hasProject() ? new JsonObject { ["ok"] = false, ["failureCode"] = "PROJECT_NOT_BOUND", ["message"] = "No project open." }
            : Invoke(new HardwareSecurityRequest { Operation = "SetPlcPutGetAccess", DeviceName = devicePath, Enable = enable, Action = "set", DryRun = dryRun }).AsObject();
    private ResponseMessage Run(HardwareSecurityRequest request, string devicePathJson, string itemPathJson)
    {
        if (request.Operation == "ManagePlcProtection" && (
            (request.NormalizedAction is "protectAllConfiguration" or "unprotectAllConfiguration" && HardwareContract.ReleaseKey != "21")
            || (PlcProtectionRules.MasterSecretActions.Contains(request.NormalizedAction) && HardwareContract.ReleaseKey is "15.1" or "16")))
            return new ResponseMessage { Message = "The requested PLC protection action is unavailable on this release.", Meta = new JsonObject { ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false, ["status"] = "NotSupportedOnVersion" } };
        if (!hasProject()) return JsonSerializer.Deserialize<ResponseMessage>(HardwarePortPreconditions.NoProject(request.Operation).ToJsonString())!;
        Capture(request, "locator", () => { request.DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!; request.ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!; });
        Capture(request, "confirmation", () => HardwareServicesLogic.RequireConfirmation(request.ConfirmChange, "confirmChange", request.DryRun));
        return JsonSerializer.Deserialize<ResponseMessage>(Invoke(request).ToJsonString())!;
    }
    public ResponseMessage ManageWatchForceTableWebAccess(string devicePathJson, string itemPathJson, string action = "read", string softwarePath = "", string tableKind = "watch", string tablePath = "", string access = "Read", bool confirmChange = false, bool dryRun = true)
        => Run(new HardwareSecurityRequest { Operation = "ManageWatchForceTableWebAccess", Action = action, SoftwarePath = softwarePath, TableKind = tableKind, TablePath = tablePath, Access = access, ConfirmChange = confirmChange, DryRun = dryRun }, devicePathJson, itemPathJson);
    public ResponseMessage ManagePlcProtection(string devicePathJson, string itemPathJson = "[]", string action = "read", string accessLevel = "", string password = "", string newPassword = "", bool confirmChange = false, bool dryRun = true)
    {
        var request = new HardwareSecurityRequest { Operation = "ManagePlcProtection", Action = action, AccessLevel = accessLevel, Password = password, NewPassword = newPassword, ConfirmChange = confirmChange, DryRun = dryRun };
        Capture(request, "protection", () => { var plan = PlcProtectionRules.Validate(action, accessLevel, password, newPassword); request.NormalizedAction = plan.Action; request.NormalizedLevel = plan.AccessLevel; });
        return Run(request, devicePathJson, itemPathJson);
    }
}
