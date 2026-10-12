using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class HmiInspectionService : UnifiedHmiPortService
    {
        public HmiInspectionService() { }
        internal HmiInspectionService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage ReadUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType)
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "GetUnifiedHmiButtonEvent", SoftwarePath = softwarePath, ScreenPath = screenPath, ButtonName = buttonName, EventType = eventType, DryRun = true });
        public ResponseMessage DeleteUnifiedHmiButtonEvent(string softwarePath,string screenPath,string buttonName,string eventType,bool dryRun=true,string expectedToken="",bool onlyIfEmpty=true)
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "DeleteUnifiedHmiButtonEvent", SoftwarePath = softwarePath, ScreenPath = screenPath, ButtonName = buttonName, EventType = eventType, DryRun = dryRun, ExpectedToken = expectedToken, OnlyIfEmpty = onlyIfEmpty });
        public ResponseMessage ReadUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName)
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "GetUnifiedHmiDynamization", SoftwarePath = softwarePath, ScreenPath = screenPath, ItemName = itemName, PropertyName = propertyName, DryRun = true });
        public ResponseMessage DeleteUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "DeleteUnifiedHmiDynamization", SoftwarePath = softwarePath, ScreenPath = screenPath, ItemName = itemName, PropertyName = propertyName, DryRun = dryRun, ExpectedToken = expectedToken });
        public ResponseMessage DeleteEmptyUnifiedHmiScreenGroup(string softwarePath,string groupPath,bool dryRun=true)
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "DeleteEmptyUnifiedHmiScreenGroup", SoftwarePath = softwarePath, GroupPath = groupPath, DryRun = dryRun });
    }
}
