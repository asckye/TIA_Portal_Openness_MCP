using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class BatchImportDispatchTests
{
    private sealed class Worker(string root):IFoundationWorker
    {
        internal int Calls,Writes;
        internal bool FailSecond;
        internal readonly WorkerOutcomeState State=new();
        public Task<JsonNode?> Call(string operation,JsonObject args,CancellationToken token)
        {
            State.RequireUsable();Calls++;
            try
            {
                var request=new PlcBatchImportRequest {Release="17",ProcessId=123,Project="C:/Projects/project.ap17",Software=args["softwarePath"]!.GetValue<string>(),Directory=root,Program=operation=="ImportPlcProgramFromDirectory",DryRun=args["dryRun"]!.GetValue<bool>(),Confirm=args["confirm"]!.GetValue<bool>(),ExpectedProject=args["expectedProjectFile"]!.GetValue<string>(),ExpectedHash=args["expectedPlanHash"]!.GetValue<string>(),Order=args["importOrder"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray()};
                var result=PlcBatchImportPolicy.Run(request,Array.Empty<PlcBatchImportObject>(),()=>{},(_,item)=>{Writes++;if(FailSecond && Writes==2)throw new IOException("fake native uncertainty");return new[]{new PlcBatchImportObject {Name=item.Name,Kind=item.Kind,GroupPath=item.GroupPath,Number=item.Number ?? 900+Writes}};});
                var payload=JsonSerializer.SerializeToNode(result)!;State.AcceptResult(operation,args,payload);return Task.FromResult<JsonNode?>(payload);
            }
            catch(Exception error){State.Failed(true,error);throw;}
        }
        public void Dispose(){}
    }
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-import-dispatch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            foreach(var name in new[]{"A","B","C"})File.WriteAllText(Path.Combine(root,name+".xml"),BatchImportTests.Xml(name));
            foreach(var name in new[]{"ImportBlocksFromDirectory","ImportPlcProgramFromDirectory"})
            {
                var worker=new Worker(root);var tool=FoundationTools.Create(worker).Single(t=>t.ProtocolTool.Name==name);
                var args=new Dictionary<string,JsonElement> {["softwarePath"]=JsonSerializer.SerializeToElement("devices/PLC"),[name=="ImportBlocksFromDirectory" ? "dir" : "sourceDir"]=JsonSerializer.SerializeToElement(root)};
                if(name=="ImportBlocksFromDirectory") args["groupPath"]=JsonSerializer.SerializeToElement("");
                RequestContext<CallToolRequestParams> Request()=>new(server){Params=new(){Name=name,Arguments=args}};
                async Task Reject(string reason)
                {try{await tool.InvokeAsync(Request());throw new Exception("Accepted "+reason);}catch(McpException){check(true,name+": "+reason);}}
                JsonObject Payload(CallToolResult result)=>JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!.AsObject();
                var preview=Payload(await tool.InvokeAsync(Request()));
                check(worker.Calls==1 && worker.Writes==0 && preview["Items"]!.AsArray().Count==3,name+": actual facade preview has no fake writes");
                check(tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("importOrder").GetProperty("items").GetProperty("type").GetString()=="string",name+": explicit string-array schema");
                args["overwrite"]=JsonSerializer.SerializeToElement(true);var overwritePreview=Payload(await tool.InvokeAsync(Request()));check(worker.Writes==0, name+": overwrite preview dispatches without writes");args.Remove("overwrite");
                if(name=="ImportPlcProgramFromDirectory")
                {
                    args["compileAfter"]=JsonSerializer.SerializeToElement(true);await Reject("compileAfter rejected before dispatch");args.Remove("compileAfter");
                    args["stopOnImportFailure"]=JsonSerializer.SerializeToElement(false);await Reject("continuation rejected before dispatch");args.Remove("stopOnImportFailure");
                    args["technologyFolderPath"]=JsonSerializer.SerializeToElement("tech");await Reject("technology rejected before dispatch");args.Remove("technologyFolderPath");
                }
                args["importOrder"]=JsonSerializer.SerializeToElement(new[]{1,2});await Reject("nonstring manifest rejected");args.Remove("importOrder");
                args["dryRun"]=JsonSerializer.SerializeToElement(false);args["confirm"]=JsonSerializer.SerializeToElement(true);args["expectedProjectFile"]=JsonSerializer.SerializeToElement("C:/Projects/project.ap17");await Reject("missing manifest/hash refused");
                check(worker.Calls==2 && worker.Writes==0,name+": every wrapper rejection avoided dispatch");
                args["importOrder"]=JsonSerializer.SerializeToElement(new[]{"A.xml","B.xml","C.xml"});args["expectedPlanHash"]=JsonSerializer.SerializeToElement(preview["PlanHash"]!.GetValue<string>());
                worker.FailSecond=true;var partial=Payload(await tool.InvokeAsync(Request()));
                check(partial["ImportedCount"]!.GetValue<int>()==1 && partial["FailedCount"]!.GetValue<int>()==1 && worker.Writes==2 && worker.State.Poisoned,name+": partial result retained and host poisoned");
                await Reject("replay denied while worker remains available");check(worker.Calls==3 && worker.Writes==2,name+": no replay or third write after uncertainty");
            }
        }
        finally{Directory.Delete(root,true);}
    }
}
