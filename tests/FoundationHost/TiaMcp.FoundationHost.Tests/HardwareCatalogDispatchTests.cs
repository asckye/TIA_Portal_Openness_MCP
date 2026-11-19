using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;
using System.Text.Json.Nodes;

internal static class HardwareCatalogDispatchTests
{
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        var worker=new FakeWorker();
        var tool=FoundationTools.Create(worker).Single(t=>t.ProtocolTool.Name=="SearchHardwareCatalog");
        var args=new Dictionary<string,JsonElement> { ["keyword"]=JsonSerializer.SerializeToElement("CPU") };
        RequestContext<CallToolRequestParams> Request()=>new(server) { Params=new() { Name="SearchHardwareCatalog",Arguments=args } };
        var result=await tool.InvokeAsync(Request());
        check(result.IsError!=true && worker.Calls==1 && worker.Operation=="SearchHardwareCatalog","Catalog actual MCP route single dispatch");
        check(worker.Arguments!["limit"]!.GetValue<int>()==50,"Catalog default result bound");
        var candidates = Enumerable.Range(0, 81).Select(index => new PlcHardwareCatalogCandidate {
            TypeIdentifier = "OrderNumber:CPU/" + index, Description = new string('x', 820) }).ToArray();
        var definition = FoundationTools.Definitions.Single(d => d.Name == "SearchHardwareCatalog");
        var identifiers = new List<string>();
        int? offset = 0;
        do
        {
            var pageArgs = new JsonObject { ["keyword"] = "CPU", ["limit"] = 50, ["offset"] = offset.Value };
            var rows = PlcHardwareCatalogPolicy.Search("20", "CPU", 50, _ => candidates, offset.Value);
            var raw = JsonSerializer.SerializeToNode(rows)!;
            var response = FoundationV4Result.Worker("20", definition, Guid.NewGuid().ToString("N"), pageArgs, raw,
                HardwareCatalogContract.Validate(raw, pageArgs));
            var body = response.StructuredContent!;
            check((bool?)body["ok"] == true && ((TextContentBlock)response.Content.Single()).Text.Length < 40000,
                "Catalog serialized page stays within the client response budget at offset " + offset);
            identifiers.AddRange(body["data"]!["items"]!.AsArray().Select(item => (string)item!["typeIdentifier"]!));
            offset = (int?)body["meta"]!["paging"]!["nextOffset"];
        } while (offset != null);
        check(identifiers.SequenceEqual(candidates.Select(row => row.TypeIdentifier)), "Catalog pages return every candidate exactly once");
        foreach(var query in new[]{"","* ?","CPU\n",new string('a',257)})
        {
            args["keyword"]=JsonSerializer.SerializeToElement(query); var before=worker.Calls; var rejected=false;
            try { await tool.InvokeAsync(Request()); } catch(McpException e) { rejected=e.ErrorCode==McpErrorCode.InvalidParams; }
            check(rejected && worker.Calls==before,"Invalid catalog query rejected before worker");
        }
        args["keyword"]=JsonSerializer.SerializeToElement("CPU");
        foreach(var limit in new[]{0,101})
        {
            args["limit"]=JsonSerializer.SerializeToElement(limit); var before=worker.Calls; var rejected=false;
            try { await tool.InvokeAsync(Request()); } catch(McpException e) { rejected=e.ErrorCode==McpErrorCode.InvalidParams; }
            check(rejected && worker.Calls==before,"Invalid catalog bound rejected before worker");
        }
    }
}
