using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.PlcWorker;

internal static class SoftwareReadDispatchTests
{
    private sealed class Reader(JsonNode? payload,Exception? error=null) : IFoundationWorker
    {
        internal int Calls;
        internal string? Operation;
        public Task<JsonNode?> Call(string operation,JsonObject arguments,CancellationToken token)
        { Calls++; Operation=operation; if(error!=null) throw error; return Task.FromResult(payload?.DeepClone()); }
        public void Dispose() { }
    }
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        string Wire(string name)=>McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(name)??name;
        foreach(var dateKind in new[]{DateTimeKind.Unspecified,DateTimeKind.Utc,DateTimeKind.Local})
        {
            var date=DateTime.SpecifyKind(new DateTime(2026,1,2,3,4,5),dateKind);
            var values=new object?[]{date,123,true,1.25m,DayOfWeek.Friday,null,"literal"};
            var dto=new { Name="PLC",Attributes=values.Select((v,i)=>new {Name="A"+i,Value=v,AccessMode="Read"}).ToArray(),Description="PLC",Meta=new {softwarePath="devices/D/CPU",scope="ordinary PLC"} };
            var wireJson=Newtonsoft.Json.JsonConvert.SerializeObject(new { id=7,result=dto });
            var decoded=WorkerProtocol.Decode(wireJson,7)!;
            var wrapped=SoftwareReadContract.Wrap("GetSoftwareInfo",decoded,McpJsonUtilities.DefaultOptions);
            for(int i=0;i<values.Length;i++)
                check(JsonNode.DeepEquals(wrapped[Wire("Attributes")]![i]![Wire("Value")],decoded["Attributes"]![i]!["Value"]),"Actual Newtonsoft/Decode/software-envelope preserves "+dateKind+" value "+i);
        }
        var candidates=new[]{new {Path="G%2F1/B%25"}};
        var objectPath=TiaMcp.PlcFoundation.PlcExchangePolicy.ObjectPath("G%2F1/B%25");
        check(TiaMcp.PlcFoundation.PlcExchangePolicy.Exact(candidates,x=>x.Path,objectPath).Path=="G%2F1/B%25","Nested escaped software-tree selectors resolve through existing object policy");
        foreach(var pair in new[]{("GetSoftwareInfo","ReadSoftwareInfo"),("GetSoftwareTree","ReadSoftwareTree")})
        {
            check(WorkerOperations.Names.Contains(pair.Item2),pair.Item1+" worker allowlist");
            var definition=FoundationTools.Definitions.Single(d=>d.Name==pair.Item1);
            check(definition.Arguments.Length==1 && definition.Arguments[0].Name=="softwarePath" && definition.Arguments[0].Required,pair.Item1+" exact V17 parameter contract");
            var request=new RequestContext<CallToolRequestParams>(server) { Params=new() { Name=pair.Item1,Arguments=new Dictionary<string,JsonElement> { ["softwarePath"]=JsonSerializer.SerializeToElement("devices/D/CPU") } } };
            var meta=new JsonObject { ["softwarePath"]="devices/D/CPU",["scope"]="ordinary PLC",["paths"]=new JsonArray(new JsonObject { ["name"]="Program blocks",["path"]="devices/D/CPU/blocks",["kind"]="block-group",["objectPath"]="",["selectorParameter"]="groupPath" }) };
            var payload=pair.Item1=="GetSoftwareInfo" ? new JsonObject { ["Name"]="PLC",["Attributes"]=new JsonArray(),["Description"]="description",["Meta"]=meta } : new JsonObject { ["Tree"]="PLC [PLC Software]",["Meta"]=meta };
            var reader=new Reader(payload);
            var response=await new FoundationTool(definition,reader).InvokeAsync(request);
            var actual=JsonNode.Parse(((TextContentBlock)response.Content.Single()).Text)!.AsObject();
            var wire=McpJsonUtilities.DefaultOptions.PropertyNamingPolicy;
            check(reader.Calls==1 && reader.Operation==pair.Item2,pair.Item1+" single read-only dispatch");
            check(actual.ContainsKey(wire?.ConvertName(pair.Item1=="GetSoftwareInfo"?"Name":"Tree")??(pair.Item1=="GetSoftwareInfo"?"Name":"Tree")),pair.Item1+" SDK wire naming");
            foreach(var invalid in new JsonNode?[]{null,new JsonArray(),new JsonObject()})
            {
                try { await new FoundationTool(definition,new Reader(invalid)).InvokeAsync(request); throw new Exception("Malformed software payload accepted"); }
                catch(McpException ex) { check(ex.ErrorCode==McpErrorCode.InternalError,pair.Item1+" malformed worker response fails"); }
            }
            foreach(int code in new[]{-32602,-32603})
            {
                try { await new FoundationTool(definition,new Reader(null,new WorkerOperationException("read failed",code,"read-failed"))).InvokeAsync(request); throw new Exception("Read error ignored"); }
                catch(McpException ex) { check((int)ex.ErrorCode==code,pair.Item1+" preserves worker error category"); }
            }
            check(!WorkerProtocol.RequiresSessionReset(true,new WorkerOperationException("read failed",-32603,"read-failed")),pair.Item1+" known read error does not poison session");
        }
    }
}
