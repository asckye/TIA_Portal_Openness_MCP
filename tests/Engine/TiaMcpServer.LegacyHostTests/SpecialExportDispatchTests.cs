using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class SpecialExportDispatchTests
{
    private sealed class SpecialWorker:IFoundationWorker
    {
        internal int Calls,Publications;
        internal bool Fail,WrongTarget;
        internal readonly WorkerOutcomeState State=new();
        public Task<JsonNode?> Call(string operation,JsonObject args,CancellationToken token)
        {
            State.RequireUsable(); Calls++;
            try
            {
                bool dry=args["dryRun"]!.GetValue<bool>();
                if(!dry) MutationIdentityPolicy.RequireSameProject(args["expectedProjectFile"]!.GetValue<string>(),"C:/Projects/project.ap17");
                string kind=operation=="ExportPlcWatchTable"?"watch-table":"technology-object";
                var result=PlcSpecialExportPolicy.Run(kind,"17","C:/Projects/project.ap17",args["softwarePath"]!.GetValue<string>(),args[kind=="watch-table"?"watchTableName":"toName"]!.GetValue<string>(),args["exportPath"]!.GetValue<string>(),true,dry,args["expectedPlanHash"]!.GetValue<string>(),()=>{},_=>throw new Exception("Native export forbidden in dispatch fixture"),(_,_)=>{Publications++; if(Fail)throw new IOException("private failure");return null;});
                var payload=JsonSerializer.SerializeToNode(result)!.AsObject();
                if(WrongTarget)payload["ObjectPath"]="different";
                State.AcceptResult(operation,args,payload);
                return Task.FromResult<JsonNode?>(payload);
            }
            catch(Exception error) {State.Failed(true,error); throw;}
        }
        public void Dispose(){}
    }
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-special-dispatch-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach(var operation in new[]{"ExportPlcWatchTable","ExportTechnologyObject"})
            {
                var worker=new SpecialWorker();
                var tool=FoundationTools.Create(worker).Single(t=>t.ProtocolTool.Name==operation);
                var args=new Dictionary<string,JsonElement> { ["softwarePath"]=JsonSerializer.SerializeToElement("devices/PLC"),[operation=="ExportPlcWatchTable"?"watchTableName":"toName"]=JsonSerializer.SerializeToElement("folder/object"),["exportPath"]=JsonSerializer.SerializeToElement(Path.Combine(root,"object.xml")) };
                RequestContext<CallToolRequestParams> Request()=>new(server){Params=new(){Name=operation,Arguments=args}};
                async Task Refuse(string reason) {try {await tool.InvokeAsync(Request());throw new Exception("Accepted "+reason);}catch(McpException){check(true,operation+": "+reason);}}
                var preview=await tool.InvokeAsync(Request());
                var plan=JsonNode.Parse(preview.Content.OfType<TextContentBlock>().Single().Text)!;
                check(worker.Publications==0 && worker.Calls==1 && !plan["Executed"]!.GetValue<bool>(),operation+": MCP default preview writes nothing");
                args["dryRun"]=JsonSerializer.SerializeToElement(false);
                await Refuse("unconfirmed apply blocked");
                args["confirm"]=JsonSerializer.SerializeToElement(true);
                await Refuse("missing exact project blocked");
                args["expectedProjectFile"]=JsonSerializer.SerializeToElement("C:/Projects/project.ap17");
                await Refuse("missing reviewed plan hash blocked");
                check(worker.Calls==1,operation+": incomplete apply never dispatches");
                args["expectedPlanHash"]=JsonSerializer.SerializeToElement(plan["PlanHash"]!.GetValue<string>());
                worker.Fail=true;
                var failed=await tool.InvokeAsync(Request());
                check(JsonNode.Parse(failed.Content.OfType<TextContentBlock>().Single().Text)!["RequiresSessionReset"]!.GetValue<bool>() && worker.State.Poisoned && worker.Publications==1,operation+": uncertain outcome returned once and session poisoned");
                await Refuse("poisoned session blocks subsequent apply without retry");
                check(worker.Calls==2 && worker.Publications==1,operation+": poison is enforced before another call");
                worker=new SpecialWorker{WrongTarget=true};tool=FoundationTools.Create(worker).Single(t=>t.ProtocolTool.Name==operation);
                args["dryRun"]=JsonSerializer.SerializeToElement(true);
                await Refuse("conflicting response target is never accepted");
                check(worker.State.Poisoned && worker.Publications==0,operation+": response identity mismatch poisons transport");
            }
        }
        finally {Directory.Delete(root,true);}
    }
}
