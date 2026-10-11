using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services;
internal sealed class CertificateManagementPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<bool> hasProject;
    private readonly Func<string> identity;
    public CertificateManagementPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal CertificateManagementPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject, Func<string> identity)
    { this.call = call; this.hasProject = hasProject; this.identity = identity; }
    private static void Capture(CertificateRequest request, string stage, Action validation)
    {
        try { validation(); }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is JsonException)
        { request.Faults[stage] = new SecurityValidationFault { Kind = error is InvalidOperationException ? "state" : "argument", Message = error.Message }; }
    }
    public ResponseMessage ManagePlcCertificate(string devicePathJson, string itemPathJson, string action, string certificateId = "", string filePath = "", string usage = "", string propertiesJson = "{}", string assignment = "", bool dryRun = true, string assignmentItemPathJson = "", string subjectAlternativeNamesJson = "[]", string password = "")
    {
        if (action == "export" && HardwareContract.ReleaseKey is "16" or "17" or "18"
            && !System.IO.Path.GetExtension(filePath).Equals(".cer", StringComparison.OrdinalIgnoreCase)
            && !System.IO.Path.GetExtension(filePath).Equals(".der", StringComparison.OrdinalIgnoreCase))
            return new ResponseMessage { Message = "V16-V18 certificate export requires a .cer or .der output file.", Meta = new JsonObject {
                ["operationSuccess"] = false, ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false, ["status"] = "NotSupportedOnVersion" } };
        if (!hasProject()) return JsonSerializer.Deserialize<ResponseMessage>(HardwarePortPreconditions.NoProject("ManagePlcCertificate").ToJsonString())!;
        var request = new CertificateRequest { Action = action, CertificateId = certificateId, FilePath = filePath, Usage = usage, Assignment = assignment, DryRun = dryRun, Password = password };
        Capture(request, "locator", () => { request.DevicePath = JsonSerializer.Deserialize<string[]>(devicePathJson)!; request.ItemPath = JsonSerializer.Deserialize<string[]>(itemPathJson)!; });
        Capture(request, "assignmentLocator", () => { if (!string.IsNullOrEmpty(assignmentItemPathJson)) request.AssignmentItemPath = JsonSerializer.Deserialize<string[]>(assignmentItemPathJson)!; });
        Capture(request, "san", () => { request.SubjectAlternativeNames = JsonSerializer.Deserialize<CertificateSubjectName[]>(JsonSerializer.Serialize(SecurityDeepLogic.ParseSubjectAlternativeNames(subjectAlternativeNamesJson), new JsonSerializerOptions { IncludeFields = true }))!; });
        JsonObject? properties = null;
        Capture(request, "properties", () => { properties = JsonNode.Parse(propertiesJson) as JsonObject ?? throw new ArgumentException("propertiesJson must be an object."); request.Properties = HardwareAddressingService.Scalars(propertiesJson); });
        Capture(request, "template", () => { if (properties != null) SecurityDeepLogic.ValidateTemplateProperties(properties); });
        bool writing = action != "list" && action != "read" && action != "template" && !dryRun;
        var arguments = new JsonObject { ["request"] = JsonSerializer.SerializeToNode(request), ["dryRun"] = dryRun };
        if (writing) { arguments["confirm"] = true; arguments["expectedProjectFile"] = identity(); }
        var reply = call("project-security.ExecuteCertificate", arguments) ?? throw new InvalidOperationException("Missing certificate worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }
}
