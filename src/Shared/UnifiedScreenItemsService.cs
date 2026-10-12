using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcpServer.ModelContextProtocol;
namespace TiaMcpServer.Siemens.Services
{
    internal sealed class UnifiedScreenItemsService : UnifiedHmiPortService
    {
        public UnifiedScreenItemsService() { }
        internal UnifiedScreenItemsService(Func<string, JsonObject, JsonNode?> call, Func<string> identity) : base(call, identity) { }
        public ResponseMessage DescribeUnifiedScreenItemType(string itemType = "", int depth = 2)
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "DescribeUnifiedScreenItemType", ItemType = itemType, Depth = depth, DryRun = true });
        public ResponseMessage ManageUnifiedScreenItem(string softwarePath, string screenPath, string action = "read", string itemName = "", string itemType = "",
            string propertiesJson = "{}", int depth = 2, bool confirmDelete = false, int offset = 0, int limit = 100, bool dryRun = true, string containedType = "")
            => Invoke<ResponseMessage>(new UnifiedHmiRequest { Operation = "ManageUnifiedScreenItem", SoftwarePath = softwarePath, ScreenPath = screenPath, Action = action, ItemName = itemName, ItemType = itemType, PropertiesJson = propertiesJson, Depth = depth, ConfirmDelete = confirmDelete, Offset = offset, Limit = limit, DryRun = dryRun, ContainedType = containedType });
    }
}
