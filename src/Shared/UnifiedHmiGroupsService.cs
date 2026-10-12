using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedHmiGroupsService : UnifiedHmiPortService
    {
        public UnifiedHmiGroupsService() { }
        internal UnifiedHmiGroupsService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ManageUnifiedHmiGroup(string softwarePath, string family, string groupPath, string action, string newName = "", bool dryRun = true)
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "ManageUnifiedHmiGroup", SoftwarePath = softwarePath, Family = family, GroupPath = groupPath, Action = action, NewName = newName, DryRun = dryRun });
    }
}
