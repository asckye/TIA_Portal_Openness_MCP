using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services {
    internal sealed class HmiInspectionPortService : HmiPortService {
        public HmiInspectionPortService() { }
        internal HmiInspectionPortService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ReadHmiScreenSnapshot(string softwarePath,string screenPath,int maxDepth=6,int maxNodes=2000) => Invoke<ResponseMessage>(new HmiRequest { Operation = "GetHmiScreenSnapshot", SoftwarePath = softwarePath, ScreenPath = screenPath, MaxDepth = maxDepth, MaxNodes = maxNodes, DryRun = true });
        public ResponseMessage ListHmiScreenPaths(string softwarePath,int offset=0,int limit=100) => Invoke<ResponseMessage>(new HmiRequest { Operation = "ListHmiScreenPaths", SoftwarePath = softwarePath, Offset = offset, Limit = limit, DryRun = true });
    }
}
