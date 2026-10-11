using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services;
internal sealed class ProjectProtectionPortService
{
    private readonly Func<string, JsonObject, JsonNode?> call;
    private readonly Func<bool> hasProject;
    public ProjectProtectionPortService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.HasProject) { }
    internal ProjectProtectionPortService(Func<string, JsonObject, JsonNode?> call, Func<bool> hasProject)
    { this.call = call; this.hasProject = hasProject; }
    public ResponseMessage ReadProjectProtection()
    {
        if (!hasProject()) return JsonSerializer.Deserialize<ResponseMessage>(HardwarePortPreconditions.NoProject("GetProjectProtection").ToJsonString())!;
        var reply = call("project-security.ReadProjectProtection", new JsonObject()) ?? throw new InvalidOperationException("Missing project protection worker reply.");
        return JsonSerializer.Deserialize<ResponseMessage>(reply.ToJsonString())!;
    }
}
