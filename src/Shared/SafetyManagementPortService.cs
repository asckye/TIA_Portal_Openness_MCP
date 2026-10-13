using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class SafetyManagementService
{
    private readonly Func<string, JsonObject, JsonNode?> _call;
    private readonly Func<string> _identity;

    public SafetyManagementService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal SafetyManagementService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _call = call;
        _identity = identity;
    }

    private static void Changes(SafetyRequest request, string json)
    {
        try
        {
            if (request.Operation == "ManageSafetyGlobalSettings" && request.Action == "update")
            {
                var changes = SafetyLogic.ParseGlobalSettingChanges(json);
                request.Scalars = HardwareAddressingService.Scalars(JsonSerializer.Serialize(changes.ToDictionary(p => p.Key, p => p.Value)));
            }
            else if (request.Action == "updateRuntimeGroup")
            {
                var changes = SafetyLogic.SplitRuntimeGroupChanges(json);
                request.Scalars = HardwareAddressingService.Scalars(changes.Scalars.ToJsonString());
                request.Attributes = HardwareAddressingService.Scalars(changes.Attributes.ToJsonString());
            }
            else if (request.Action == "updateSettings")
            {
                var changes = SafetyLogic.SplitSettingsChanges(json);
                request.Scalars = HardwareAddressingService.Scalars(changes.Scalars.ToJsonString());
                request.Attributes = HardwareAddressingService.Scalars(changes.Attributes.ToJsonString());
                request.BlockNumbers = HardwareAddressingService.Scalars(changes.BlockNumbers.ToJsonString());
                request.SafetySystemVersion = changes.SafetySystemVersion;
            }
        }
        catch (Exception error) when (error is ArgumentException || error is JsonException || error is InvalidOperationException)
        {
            request.ChangesError = error.Message;
        }
    }

    private ResponseMessage Invoke(SafetyRequest request)
    {
        if (HardwareContract.ReleaseKey == "17" && request.Action is "createRuntimeGroup" or "deleteRuntimeGroup" or "generateGlobalFIOStatusBlock")
            return Unsupported("The requested runtime group action requires V18 or later.");
        if (HardwareContract.ReleaseKey == "16" && request.Operation == "ManageSafetyGlobalSettings" && request.Action == "read")
            return Unsupported("GlobalSettings getters require V17 or later.");
        if (HardwareContract.ReleaseKey != "21" && request.Action == "generateBaseId")
            return Unsupported("SafetyBaseIdProvider requires V21.");
        if (request.Operation == "ManageSafetyGlobalSettings" && HardwareContract.ReleaseKey is "16" or "17"
            && request.Action == "update" && request.Scalars.Keys.Any(k => k is "GenerationOfDefaultFailsafeProgram" or "ManagementOfFailsafeInSoftwareUnitsEnvironment"))
            return Unsupported("The requested GlobalSettings setter requires V18 or later.");
        bool writing = !request.DryRun && request.Action != "read";
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = !writing };
        if (writing)
        {
            arguments["confirm"] = true;
            arguments["expectedProjectFile"] = _identity();
        }
        var reply = _call("plc-safety.ExecuteSafety", arguments) ?? throw new InvalidOperationException("Missing Safety worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }

    private static ResponseMessage Unsupported(string message) => new ResponseMessage { Message = message,
        Meta = JsonSerializer.SerializeToNode(OptionalEngineeringCapabilities.Refusal(message).Meta)!.AsObject() };

    public ResponseMessage ManagePlcSafety(string softwarePath, string action = "read", string runtimeGroup = "", string propertiesJson = "{}", bool dryRun = true,
        string password = "", bool confirmSafetyChange = false, string mainSafetyBlockPath = "", string mainSafetyInstanceDbPath = "")
    {
        var request = new SafetyRequest { Operation = "ManagePlcSafety", SoftwarePath = softwarePath, Action = action, RuntimeGroup = runtimeGroup,
            DryRun = dryRun, Password = password, ConfirmSafetyChange = confirmSafetyChange, MainSafetyBlockPath = mainSafetyBlockPath, MainSafetyInstanceDbPath = mainSafetyInstanceDbPath };
        Changes(request, propertiesJson);
        return Invoke(request);
    }

    public ResponseMessage ManageSafetyGlobalSettings(string action = "read", string propertiesJson = "{}", bool dryRun = true)
    {
        var request = new SafetyRequest { Operation = "ManageSafetyGlobalSettings", Action = action, DryRun = dryRun };
        Changes(request, propertiesJson);
        return Invoke(request);
    }

    public ResponseMessage ReadSafetyBlockSignatures(string softwarePath, string blockPath = "", bool includeProgramSignatures = true, int offset = 0, int limit = 100)
        => Invoke(new SafetyRequest { Operation = "GetSafetyBlockSignatures", SoftwarePath = softwarePath, BlockPath = blockPath,
            IncludeProgramSignatures = includeProgramSignatures, Offset = offset, Limit = limit });

    public ResponseMessage ExportSafetyPrintout(string softwarePath, string filePath, string printer = "MicrosoftPrintToPdf", string option = "All", string documentLayout = "", bool dryRun = true)
        => Invoke(new SafetyRequest { Operation = "ExportSafetyPrintout", SoftwarePath = softwarePath, Action = "export", FilePath = filePath,
            Printer = printer, Option = option, DocumentLayout = documentLayout, DryRun = dryRun });
}
