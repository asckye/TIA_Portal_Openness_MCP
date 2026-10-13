using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class SafetyValidationService
{
    private readonly Func<string, JsonObject, JsonNode?> _call;
    private readonly Func<string> _identity;
    public SafetyValidationService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal SafetyValidationService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _call = call;
        _identity = identity;
    }
    private static void Capture(SafetyValidationRequest request, string stage, Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException || error is JsonException || error is InvalidOperationException)
        {
            request.Faults[stage] = error.Message;
        }
    }
    private static SafetyValidationPlan Plan(object plan, JsonObject? properties = null)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        var node = JsonSerializer.SerializeToNode(plan, plan.GetType(), options)!.AsObject();
        node.Remove("Properties");
        var result = JsonSerializer.Deserialize<SafetyValidationPlan>(node.ToJsonString())!;
        if (properties != null)
        {
            properties = (JsonObject)properties.DeepClone();
            if (properties["signals"] is JsonArray signals)
            {
                result.Signals = JsonSerializer.Deserialize<string[]>(signals.ToJsonString())!;
                properties.Remove("signals");
            }
            result.Properties = HardwareAddressingService.Scalars(properties.ToJsonString());
        }
        return result;
    }
    private ResponseMessage Invoke(SafetyValidationRequest request)
    {
        if (request.Faults.Count != 0) request.Plan.Writes = OptionalEngineeringMutations.Writes(request);
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = !(request.Plan.Writes || request.Plan.WritesFile) };
        if (request.Plan.Writes || request.Plan.WritesFile)
        {
            arguments["confirm"] = true;
            arguments["expectedProjectFile"] = _identity();
        }
        var reply = _call("plc-safety.ExecuteSafetyValidation", arguments) ?? throw new InvalidOperationException("Missing Safety Validation worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }
    public ResponseMessage ReadSafetyActivationTests(string groupPathJson = "[]", string name = "", bool includeSafetyFunctions = false, bool includeConditions = false, int offset = 0, int limit = 100)
    {
        var request = new SafetyValidationRequest { Operation = "ListSafetyActivationTests", Name = name, IncludeSafetyFunctions = includeSafetyFunctions, IncludeConditions = includeConditions, Offset = offset, Limit = limit };
        Capture(request, "group", () => request.GroupPath = SafetyValidationLogic.ParseGroupPath(groupPathJson));
        return Invoke(request);
    }
    public ResponseMessage ManageSafetyActivationTest(string name, string action = "read", string groupPathJson = "[]", string evaluationDeviceName = "", string sourceName = "", string masterCopyPath = "", string libraryName = "", string newValue = "", string filePath = "", string exportOptions = "", string documentInfoOptions = "", string importOptions = "", bool confirmDelete = false, bool dryRun = true)
    {
        var request = new SafetyValidationRequest { Operation = "ManageSafetyActivationTest", Name = name, Action = action, EvaluationDeviceName = evaluationDeviceName, SourceName = sourceName, MasterCopyPath = masterCopyPath, LibraryName = libraryName, NewValue = newValue, FilePath = filePath, ExportOptions = exportOptions, DocumentInfoOptions = documentInfoOptions, ImportOptions = importOptions, ConfirmDelete = confirmDelete, DryRun = dryRun };
        Capture(request, "plan", () => { var plan = SafetyValidationLogic.ValidateActivationTestRequest(action, name, evaluationDeviceName, sourceName, masterCopyPath, newValue, filePath, exportOptions, documentInfoOptions, importOptions, confirmDelete, dryRun); request.Plan = Plan(plan); });
        Capture(request, "group", () => request.GroupPath = SafetyValidationLogic.ParseGroupPath(groupPathJson));
        return Invoke(request);
    }
    public ResponseMessage ManageSafetyActivationTestGroup(string action = "read", string groupPathJson = "[]", string name = "", string masterCopyPath = "", string libraryName = "", string newName = "", bool confirmDelete = false, bool dryRun = true)
    {
        var request = new SafetyValidationRequest { Operation = "ManageSafetyActivationTestGroup", Action = action, Name = name, MasterCopyPath = masterCopyPath, LibraryName = libraryName, NewName = newName, ConfirmDelete = confirmDelete, DryRun = dryRun };
        Capture(request, "plan", () => { var plan = SafetyValidationLogic.ValidateGroupRequest(action, name, masterCopyPath, newName, confirmDelete, dryRun); request.Plan = Plan(plan); });
        Capture(request, "group", () => request.GroupPath = SafetyValidationLogic.ParseGroupPath(groupPathJson));
        return Invoke(request);
    }
    public ResponseMessage ManageSafetyFunction(string activationTest, string action = "read", string groupPathJson = "[]", string name = "", string sourceName = "", string propertiesJson = "{}", string filePath = "", string exportOptions = "", string documentInfoOptions = "", string importOptions = "", bool confirmDelete = false, bool dryRun = true)
    {
        var request = new SafetyValidationRequest { Operation = "ManageSafetyFunction", ActivationTest = activationTest, Action = action, Name = name, SourceName = sourceName, FilePath = filePath, ExportOptions = exportOptions, DocumentInfoOptions = documentInfoOptions, ImportOptions = importOptions, ConfirmDelete = confirmDelete, DryRun = dryRun };
        Capture(request, "plan", () => { var plan = SafetyValidationLogic.ValidateSafetyFunctionRequest(action, name, sourceName, propertiesJson, filePath, exportOptions, documentInfoOptions, importOptions, confirmDelete, dryRun); request.Plan = Plan(plan, plan.Properties); });
        Capture(request, "group", () => request.GroupPath = SafetyValidationLogic.ParseGroupPath(groupPathJson));
        return Invoke(request);
    }
    public ResponseMessage ManageSafetyFunctionCondition(string activationTest, string safetyFunction, string action = "read", string groupPathJson = "[]", int index = -1, string deviceName = "", string signalUsage = "", string signalName = "", string propertiesJson = "{}", bool dryRun = true)
    {
        var request = new SafetyValidationRequest { Operation = "ManageSafetyFunctionCondition", ActivationTest = activationTest, SafetyFunction = safetyFunction, Action = action, Index = index, DeviceName = deviceName, SignalUsage = signalUsage, SignalName = signalName, DryRun = dryRun };
        Capture(request, "plan", () => { var plan = SafetyValidationLogic.ValidateConditionRequest(action, index, deviceName, signalUsage, signalName, propertiesJson, dryRun); request.Plan = Plan(plan, plan.Properties); });
        Capture(request, "group", () => request.GroupPath = SafetyValidationLogic.ParseGroupPath(groupPathJson));
        return Invoke(request);
    }
}
