using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;

internal static class BatchExportDispatchTests
{
    // No processes, SDK or native calls. Uses the same response acceptance/state helper as WorkerClient.
    private sealed class BatchWorker(string root):IFoundationWorker
    {
        internal int Calls,Publications;
        internal bool FailSecond;
        internal Action<JsonObject>? AlterResponse;
        internal readonly WorkerOutcomeState State=new();
        public Task<JsonNode?> Call(string operation,JsonObject arguments,CancellationToken token)
        {
            State.RequireUsable(); Calls++;
            try
            {
                var dry=arguments["dryRun"]!.GetValue<bool>();
                if(!dry) MutationIdentityPolicy.RequireSameProject(arguments["expectedProjectFile"]!.GetValue<string>(),"C:/Projects/project.ap17");
                var result=PlcBatchExportPolicy.Run(operation=="ExportBlocks"?"blocks":"types","C:/Projects/project.ap17","devices/PLC","",false,root,128,dry,arguments["expectedInventoryHash"]!.GetValue<string>(),
                    new[]{"A","B","C"}.Select(path=>new PlcBatchExportSource {Path=path,Consistent=true,Export=_=>throw new Exception("Native callback forbidden in dispatch tests")}),()=>{},
                    (_,_)=>{if(++Publications==2 && FailSecond)throw new IOException("unknown fake publication");return null;});
                var payload=JsonSerializer.SerializeToNode(result)!.AsObject(); AlterResponse?.Invoke(payload);
                State.AcceptResult(operation,arguments,payload);
                return Task.FromResult<JsonNode?>(payload);
            }
            catch(Exception error) {State.Failed(true,error);throw;}
        }
        public void Dispose(){}
    }
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-batch-dispatch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            foreach(var name in new[]{"ExportBlocks","ExportTypes"})
            {
                Dictionary<string,JsonElement> Args()=>new() { ["softwarePath"]=JsonSerializer.SerializeToElement("devices/PLC"),["groupPath"]=JsonSerializer.SerializeToElement(""),["exportPath"]=JsonSerializer.SerializeToElement(root) };
                RequestContext<CallToolRequestParams> Request(Dictionary<string,JsonElement> args)=>new(server) {Params=new(){Name=name,Arguments=args}};
                McpServerTool Tool(BatchWorker worker)=>FoundationTools.Create(worker).Single(t=>t.ProtocolTool.Name==name);
                JsonObject Payload(CallToolResult response)=>JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!.AsObject();
                void Execute(Dictionary<string,JsonElement> args,string? hash)
                {
                    args["dryRun"]=JsonSerializer.SerializeToElement(false);args["confirm"]=JsonSerializer.SerializeToElement(true);args["expectedProjectFile"]=JsonSerializer.SerializeToElement("C:/Projects/project.ap17");
                    if(hash!=null)args["expectedInventoryHash"]=JsonSerializer.SerializeToElement(hash);
                }
                async Task Refused(McpServerTool tool,Dictionary<string,JsonElement> args,McpErrorCode code,string reason)
                {try{await tool.InvokeAsync(Request(args));throw new Exception("Accepted "+reason);}catch(McpException error){check(error.ErrorCode==code,name+": "+reason);}}
                var worker=new BatchWorker(root);var tool=Tool(worker);var args=Args();
                var preview=Payload(await tool.InvokeAsync(Request(args)));var hash=preview["InventoryHash"]!.GetValue<string>();
                check(worker.Calls==1 && worker.Publications==0 && !preview["Executed"]!.GetValue<bool>() && preview["Items"]!.AsArray().Count==3,name+": MCP preview returns complete plan with no publications");
                Execute(args,null);
                await Refused(tool,args,McpErrorCode.InvalidParams,"missing inventory hash rejected by wrapper");
                check(worker.Calls==1,name+": missing hash never dispatched to fake worker");
                args["expectedInventoryHash"]=JsonSerializer.SerializeToElement(" ");
                await Refused(tool,args,McpErrorCode.InvalidParams,"blank inventory hash rejected by wrapper");
                check(worker.Calls==1,name+": blank hash never dispatched");
                args["expectedInventoryHash"]=JsonSerializer.SerializeToElement(hash);
                var success=Payload(await tool.InvokeAsync(Request(args)));
                check(worker.Calls==2 && worker.Publications==3 && !success["RequiresSessionReset"]!.GetValue<bool>(),name+": confirmed exact preview hash dispatches once and returns success items");
                var mismatch=new BatchWorker(root);var mismatchArgs=Args();Execute(mismatchArgs,new string('f',64));
                await Refused(Tool(mismatch),mismatchArgs,McpErrorCode.InvalidParams,"mismatching preview hash rejected by plan");
                check(mismatch.Calls==1 && mismatch.Publications==0,name+": mismatching hash cannot reach publisher");
                var wrongProject=new BatchWorker(root);var projectArgs=Args();Execute(projectArgs,hash);projectArgs["expectedProjectFile"]=JsonSerializer.SerializeToElement("C:/Projects/other.ap17");
                await Refused(Tool(wrongProject),projectArgs,McpErrorCode.InvalidParams,"exact project mismatch rejected");
                check(wrongProject.Publications==0,name+": project mismatch cannot publish");
                var wrongScope=new BatchWorker(root){AlterResponse=data=>data["SoftwarePath"]="devices/Other"};
                await Refused(Tool(wrongScope),Args(),McpErrorCode.InternalError,"different returned software scope rejected");
                check(wrongScope.State.Poisoned,name+": conflicting response poisons transport state");
                var partialWorker=new BatchWorker(root){FailSecond=true};var partialTool=Tool(partialWorker);var partialArgs=Args();Execute(partialArgs,hash);
                var partial=Payload(await partialTool.InvokeAsync(Request(partialArgs)));
                check(partial["RequiresSessionReset"]!.GetValue<bool>() && partial["Items"]!.AsArray().Select(x=>x!["Status"]!.GetValue<string>()).SequenceEqual(new[]{"exported","failed","not-attempted"}),name+": MCP retains explicit partial progress");
                check(partialWorker.State.Poisoned && partialWorker.Publications==2,name+": production outcome helper poisons after partial result without third attempt");
                await Refused(partialTool,partialArgs,McpErrorCode.InternalError,"replay rejected after unknown partial outcome");
                check(partialWorker.Calls==1 && partialWorker.Publications==2,name+": replay cannot dispatch or republish");
            }
        }
        finally{Directory.Delete(root,true);}
    }
}
