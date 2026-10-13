using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens.Services;

internal sealed class OptionalEngineeringService
{
    private readonly OptionalEngineeringPort _port;
    public OptionalEngineeringService() : this(HardwareAddressWorkerBridge.Call, HardwareAddressWorkerBridge.ProjectIdentity) { }
    internal OptionalEngineeringService(Func<string, JsonObject, JsonNode?> call, Func<string> identity)
    {
        _port = new OptionalEngineeringPort(call, identity);
    }
    public ResponseMessage ReadSiVArcRules(string category,string objectPathJson="[]",int offset=0,int limit=100)
    {
        var request = new OptionalEngineeringRequest { Operation = "ListSivarcRules", IsWrite = false, Category = category, ObjectPathJson = objectPathJson, Offset = offset, Limit = limit };
        OptionalEngineeringPort.Input(request, objectPathJson);
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
    public ResponseMessage ManageSiVArcRule(string category,string collectionPathJson,string name,string action,string propertiesJson="{}",bool dryRun=true)
    {
        var request = new OptionalEngineeringRequest { Operation = "ManageSivarcRule", IsWrite = !dryRun && !(action == "read" || action == "checkValidity" || action == "checkTraceValidity"), Category = category, CollectionPathJson = collectionPathJson, Name = name, Action = action, PropertiesJson = propertiesJson, DryRun = dryRun };
        OptionalEngineeringPort.Input(request, collectionPathJson);
        OptionalEngineeringPort.Input(request, propertiesJson);
        return _port.Invoke("sivarc.ExecuteSivarc", request);
    }
}
