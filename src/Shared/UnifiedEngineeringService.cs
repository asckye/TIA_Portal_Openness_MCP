using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedEngineeringService : UnifiedPortService
    {
        public UnifiedEngineeringService() { }
        internal UnifiedEngineeringService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ImportUnifiedEngineeringList(string softwarePath, string category, string filePath, string expectedNamesJson, bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ImportUnifiedEngineeringList", SoftwarePath = softwarePath, Category = category, FilePath = filePath, ExpectedNamesJson = expectedNamesJson, DryRun = dryRun }, dryRun);
        public ResponseMessage ReadUnifiedEngineeringObjects(string softwarePath, string category, string name = "", int offset = 0, int limit = 100)
            => Invoke(new UnifiedRequest { Operation = "ListUnifiedEngineeringObjects", SoftwarePath = softwarePath, Category = category, Name = name, Offset = offset, Limit = limit }, true);
        public ResponseMessage ManageUnifiedEngineeringObject(string softwarePath, string category, string name, string action,
            string propertiesJson = "{}", bool dryRun = true)
            => Invoke(new UnifiedRequest { Operation = "ManageUnifiedEngineeringObject", SoftwarePath = softwarePath, Category = category, Name = name, Action = action, PropertiesJson = propertiesJson, DryRun = dryRun }, dryRun);
    }
}
