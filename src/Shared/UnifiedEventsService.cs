using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedEventsService : UnifiedPortService
    {
        public UnifiedEventsService() { }
        internal UnifiedEventsService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ManageUnifiedEvent(string softwarePath,string objectPathJson,string eventType,string action="read",string propertyName="",string scriptPropertiesJson="{}",string expectedToken="",bool dryRun=true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedEvent", SoftwarePath = softwarePath, ObjectPathJson = objectPathJson, EventType = eventType, Action = action, PropertyName = propertyName, ScriptPropertiesJson = scriptPropertiesJson, ExpectedToken = expectedToken, DryRun = dryRun }, dryRun);
    }
}
