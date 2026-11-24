using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Native.Hmi;
using TiaMcp.Engine.Tests.Shared;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.Engine.Tests;

public sealed class ServedScreenItemPolicyTests
{
    [Theory, InlineData("19"), InlineData("20"), InlineData("21")]
    public void Served_screen_item_operations_pass_the_native_request_policy(string release)
    {
        var requests = new ServedToolOperations(release).For("ManageUnifiedScreenItem").ToArray();
        Assert.NotEmpty(requests);
        int validated = 0;
        var service = new UnifiedScreenItemsService((_, payload) =>
        {
            var request = payload["request"]!.Deserialize<UnifiedHmiRequest>()!;
            var previous = UnifiedHmiAccess.Inputs;
            try
            {
                UnifiedHmiAccess.Inputs = request.Inputs;
                var changes = UnifiedScreenItemLogic.ParseProperties(request.PropertiesJson);
                UnifiedScreenItemLogic.ValidateRequest(request.Action, request.ItemName, request.ItemType, changes,
                    request.ConfirmDelete, request.DryRun, request.ContainedType);
                validated++;
            }
            finally { UnifiedHmiAccess.Inputs = previous; }
            return new JsonObject();
        }, () => "");
        foreach (var (_, usage) in requests)
        {
            var args = usage["example"]!["request"]!["params"]!["arguments"]!;
            string Text(string field) => (string?)(args[field] ?? usage["inputSchema"]!["properties"]![field]?["default"]) ?? "";
            service.ManageUnifiedScreenItem(Text("softwarePath"), Text("screenPath"), Text("action"), Text("itemName"), Text("itemType"),
                args["properties"]?.ToJsonString() ?? "{}", confirmDelete: (bool?)args["confirmDelete"] ?? false,
                dryRun: (bool)args["dryRun"]!, containedType: Text("containedType"));
        }
        Assert.Equal(requests.Length, validated);
    }
}
