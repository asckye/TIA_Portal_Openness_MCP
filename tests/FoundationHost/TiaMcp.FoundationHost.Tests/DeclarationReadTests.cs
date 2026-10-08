using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;

internal static class DeclarationReadTests
{
    private sealed class Reader(JsonNode? payload,Exception? error=null) : IFoundationWorker
    {
        internal int Calls;
        internal string? Operation;
        internal JsonObject? Arguments;
        public Task<JsonNode?> Call(string operation,JsonObject arguments,CancellationToken token)
        {
            Calls++; Operation=operation; Arguments=arguments.DeepClone().AsObject();
            if(error!=null) throw error;
            return Task.FromResult(payload?.DeepClone());
        }
        public void Dispose() { }
    }
    private static JsonObject Row(string table,string name,string kind,string dataType,string value) => new() {
        ["Name"]=name,["Path"]=table+"/"+Uri.EscapeDataString(name),["Kind"]=kind,["DataType"]=dataType,["Value"]=value };
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        const string table="Group%2F1/Table%20A";
        foreach(var entry in new[]{("ReadPlcTags","ListTags","tag"),("ReadPlcUserConstants","ListUserConstants","user-constant"),("ReadPlcSystemConstants","ListSystemConstants","system-constant")})
        {
            var definition=FoundationTools.Definitions.Single(d=>d.Name==entry.Item1);
            var request=new RequestContext<CallToolRequestParams>(server) { Params=new() { Name=entry.Item1,Arguments=new Dictionary<string,JsonElement> { ["plc"]=JsonSerializer.SerializeToElement("devices/PLC/CPU"),["table"]=JsonSerializer.SerializeToElement(table) } } };
            var rows=new JsonArray(Row(table,"Tag/ One",entry.Item3,"",""),Row(table,"Tag",entry.Item3,"String","'a%20b'"),Row(table,"tag",entry.Item3,"Word","W#16#00ff"));
            var reader=new Reader(rows);var tool=new FoundationTool(definition,reader);
            var result=await tool.InvokeAsync(request);
            var actual=JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text);
            check(result.IsError!=true && JsonNode.DeepEquals(actual,rows),entry.Item1+" real SDK preserves PascalCase array, order, case, empty address and unparsed value text");
            check(reader.Calls==1 && reader.Operation==entry.Item2 && reader.Arguments!["table"]!.GetValue<string>()==table,entry.Item1+" invokes exactly the selected read and table");
            result=await new FoundationTool(definition,new Reader(new JsonArray())).InvokeAsync(request);
            check(((TextContentBlock)result.Content.Single()).Text=="[]",entry.Item1+" genuine empty collection remains empty");
            var malformed=new List<JsonNode?> { null,new JsonObject(),new JsonArray(1),new JsonArray((JsonNode?)null),new JsonArray(rows[0]!.DeepClone(),rows[0]!.DeepClone()) };
            foreach(string field in new[]{"Name","Path","Kind","DataType","Value"})
            {
                var missing=rows[0]!.DeepClone().AsObject();missing.Remove(field);malformed.Add(new JsonArray(missing));
                var wrongType=rows[0]!.DeepClone().AsObject();wrongType[field]=123;malformed.Add(new JsonArray(wrongType));
            }
            foreach(var change in new[]{("Name",""),("Path","Other/Tag%2F%20One"),("Path",table+"/Tag/ One"),("Kind","wrong-kind")})
            {var row=rows[0]!.DeepClone().AsObject();row[change.Item1]=change.Item2;malformed.Add(new JsonArray(row));}
            var unknown=rows[0]!.DeepClone().AsObject();unknown["OnlineValue"]=true;malformed.Add(new JsonArray(unknown));
            foreach(var bad in malformed)
            {
                try { await new FoundationTool(definition,new Reader(bad)).InvokeAsync(request);throw new Exception("Invalid declaration accepted"); }
                catch(McpException ex){check(ex.ErrorCode==McpErrorCode.InternalError,entry.Item1+" malformed result is an explicit error, never partial/empty success");}
            }
            foreach(int code in new[]{-32602,-32603})
            {
                try { await new FoundationTool(definition,new Reader(null,new WorkerOperationException("known read failure",code,"read-failed"))).InvokeAsync(request);throw new Exception("Native failure ignored"); }
                catch(McpException ex){check((int)ex.ErrorCode==code,entry.Item1+" worker error preserved");}
            }
            foreach(string parameter in new[]{"plc","table"})
            {
                var original=request.Params.Arguments![parameter];
                ((Dictionary<string,JsonElement>)request.Params.Arguments)[parameter]=JsonSerializer.SerializeToElement(" ");reader=new Reader(rows);
                try {await new FoundationTool(definition,reader).InvokeAsync(request);throw new Exception("Blank selection accepted");}
                catch(McpException ex){check(ex.ErrorCode==McpErrorCode.InvalidParams && reader.Calls==0,entry.Item1+" rejects blank "+parameter+" before worker call");}
                ((Dictionary<string,JsonElement>)request.Params.Arguments)[parameter]=original;
            }
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();reader=new Reader(rows);
            try {await new FoundationTool(definition,reader).InvokeAsync(request,cancellation.Token);throw new Exception("Cancelled read executed");}
            catch(OperationCanceledException){check(reader.Calls==0,entry.Item1+" cancellation never calls worker");}
        }
    }
}
