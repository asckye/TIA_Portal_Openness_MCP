using System;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedExchangeService : UnifiedPortService
    {
        public UnifiedExchangeService() { }
        internal UnifiedExchangeService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ExchangeUnifiedTags(string softwarePath, string action, string directory, string tagTable = "", string fileName = "", string expectedTagNamesJson = "[]", bool dryRun = true, bool overwrite = false)
            => Invoke(new UnifiedRequest { Operation = "ExchangeUnifiedTags", SoftwarePath = softwarePath, Action = action, Directory = directory, TagTable = tagTable, FileName = fileName, ExpectedTagNamesJson = expectedTagNamesJson, DryRun = dryRun, Overwrite = overwrite }, dryRun);
        public ResponseMessage ExchangeUnifiedScriptModules(string softwarePath, string action, string directory, string moduleName = "", string fileName = "", bool dryRun = true, bool overwrite = false)
            => Invoke(new UnifiedRequest { Operation = "ExchangeUnifiedScriptModules", SoftwarePath = softwarePath, Action = action, Directory = directory, ModuleName = moduleName, FileName = fileName, DryRun = dryRun, Overwrite = overwrite }, dryRun);
        public ResponseMessage ImportUnifiedOpcUaAlarms(string softwarePath, string connectionName, string action = "read", string xmlPath = "", string displayName = "", bool dryRun = true, bool overwrite = false)
            => Invoke(new UnifiedRequest { Operation = "ImportUnifiedOpcUaAlarms", SoftwarePath = softwarePath, ConnectionName = connectionName, Action = action, XmlPath = xmlPath, DisplayName = displayName, DryRun = dryRun, Overwrite = overwrite }, dryRun);
    }
}
