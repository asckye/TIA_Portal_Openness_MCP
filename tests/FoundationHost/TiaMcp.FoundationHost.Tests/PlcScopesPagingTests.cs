extern alias enginehost;
using System.Text.Json.Nodes;
using Xunit;

public sealed class PlcScopesPagingTests
{
    [Fact]
    public void Expected_count_fills_meta_paging()
    {
        var response = new enginehost::TiaMcpServer.ModelContextProtocol.ResponseJsonReport {
            Ok = true, Message = "Scopes read", Meta = JsonNode.Parse(
                "{\"success\":true,\"records\":[],\"expectedCount\":195,\"actualCount\":8,\"offset\":0,"
                + "\"limit\":8,\"nextOffset\":8,\"truncated\":true,\"dataComplete\":false}")!.AsObject() };
        var result = enginehost::TiaMcpServer.ModelContextProtocol.OfflineContracts.Map("GetPlcBlockScopes", response).StructuredContent!;
        Assert.Equal(195, (int?)result["meta"]?["paging"]?["total"]);
        Assert.Equal(8, (int?)result["meta"]?["paging"]?["nextOffset"]);
        Assert.Null(result["data"]?["nextOffset"]);
    }
}
