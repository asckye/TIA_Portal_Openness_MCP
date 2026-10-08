using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;

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
