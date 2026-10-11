using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services {
    internal sealed class HmiTagDeletionService : HmiPortService {
        public HmiTagDeletionService() { }
        internal HmiTagDeletionService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage DeleteHmiTag(string softwarePath, string tagTablePath, string tagName, bool dryRun = true, bool confirmDelete = false) => Invoke<ResponseMessage>(new HmiRequest { Operation = "DeleteHmiTag", SoftwarePath = softwarePath, TagTablePath = tagTablePath, TagName = tagName, DryRun = dryRun, ConfirmDelete = confirmDelete });
    }
}
