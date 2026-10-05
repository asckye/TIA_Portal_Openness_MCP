using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.PlcFoundation;
using TiaMcp.LegacyHost;
internal static class DocumentExportTests
{
    internal static JsonNode Payload(JsonObject args)
    {
        var block=args["blockPath"]!.GetValue<string>(); var name=Uri.UnescapeDataString(block.Split('/').Last());
        return JsonSerializer.SerializeToNode(new PlcDocumentExportResult {
            ReleaseKey="20", ProjectFile="C:/Projects/Example.ap20", SoftwarePath=args["softwarePath"]!.GetValue<string>(),
            BlockPath=block, OutputDirectory=args["exportPath"]!.GetValue<string>(), Language="LAD",PlanHash=new string('a',64),
            Files=new[]{name+".s7dcl",name+".s7res"}
        })!;
    }

    private sealed class PreviewWorker(JsonObject result):IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation,JsonObject args,CancellationToken token) { Calls++; return Task.FromResult<JsonNode?>(result.DeepClone()); }
        public void Dispose() { }
    }
    private static void Dispatch(PlcDocumentExportResult plan,Action<bool,string> check)
    {
        var worker=new PreviewWorker(JsonSerializer.SerializeToNode(plan)!.AsObject());
        var tool=FoundationTools.Create(worker).Single(t=>t.ProtocolTool.Name=="ExportAsDocuments");
        var server=System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer,ServerProxy>();
        var args=new Dictionary<string,JsonElement> { ["softwarePath"]=JsonSerializer.SerializeToElement(plan.SoftwarePath),["blockPath"]=JsonSerializer.SerializeToElement(plan.BlockPath),["exportPath"]=JsonSerializer.SerializeToElement(plan.OutputDirectory) };
        ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> Request()=>new(server) { Params=new() { Name="ExportAsDocuments",Arguments=args } };
        var preview=tool.InvokeAsync(Request()).AsTask().GetAwaiter().GetResult();
        check(preview.IsError!=true && worker.Calls==1,"Document MCP default preview uses dedicated result envelope");
        void Refuse(string label) { bool refused=false; try {refused=tool.InvokeAsync(Request()).AsTask().GetAwaiter().GetResult().IsError==true;} catch(ModelContextProtocol.McpException){refused=true;} check(refused && worker.Calls==1,label); }
        args["dryRun"]=JsonSerializer.SerializeToElement(false); Refuse("Document MCP refuses unconfirmed apply before worker dispatch");
        args["confirm"]=JsonSerializer.SerializeToElement(true); Refuse("Document MCP refuses missing exact project before worker dispatch");
        args["expectedProjectFile"]=JsonSerializer.SerializeToElement(plan.ProjectFile); Refuse("Document MCP refuses missing reviewed hash before worker dispatch");
    }

    internal static void Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-doc-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        int calls=0,offline=0;
        void Reject(Action action,string name) { bool rejected=false; try {action();} catch(ArgumentException){rejected=true;} catch(NotSupportedException){rejected=true;} catch(IOException){rejected=true;} catch(InvalidDataException){rejected=true;} check(rejected,name); }
        PlcDocumentExportResult Run(string output,bool dry=true,string hash="",string release="20",string language="LAD",bool consistent=true,Func<DirectoryInfo,string,string[]>? export=null,Action<DirectoryInfo,DirectoryInfo>? move=null) => PlcDocumentExportPolicy.Run(release,"/project.ap"+release,"device/PLC","group/Block",output,language,consistent,dry,hash,()=>offline++,export??((dir,name)=>{calls++;var paths=new[]{Path.Combine(dir.FullName,name+".s7dcl"),Path.Combine(dir.FullName,name+".s7res")};foreach(var path in paths) File.WriteAllText(path,"native document bytes");return paths;}),move);
        JsonObject Wire(PlcDocumentExportResult result)=>JsonSerializer.SerializeToNode(result)!.AsObject();
        try
        {
            check(PlcDocumentExportPolicy.Exact(new[]{new[]{"other","target"},Array.Empty<string>()},x=>x,"target")=="target","Document selection finds exact complete bounded inventory target");
            Reject(()=>PlcDocumentExportPolicy.Exact(Enumerable.Repeat(Array.Empty<string>(),1025),x=>x,"missing"),"Document empty-group traversal bound fails closed");
            Reject(()=>PlcDocumentExportPolicy.Exact(new[]{Enumerable.Repeat("target",10001)},x=>x,"target"),"Document block traversal bound fails closed");
            Reject(()=>PlcDocumentExportPolicy.Exact(new[]{new[]{"target","target"}},x=>x,"target"),"Document ambiguous identity refused");
            Reject(()=>PlcDocumentExportPolicy.Exact(new[]{new[]{"other"}},x=>x,"target"),"Document missing identity refused");
            var output=Path.Combine(root,"pair"); var plan=Run(output);
            DocumentExportContract.Validate(Wire(plan),true);
            Dispatch(plan,check);
            check(calls==0 && offline==0 && Directory.GetFileSystemEntries(root).Length==0,"Document preview performs zero writes/native/offline callbacks");
            foreach(var release in new[]{"14sp1","15.1","16","17","18","19","V20","20.0","22"}) Reject(()=>Run(output,release:release),"Document exact release refuses "+release);
            foreach(var lang in new[]{"SCL","FBD","STL","GRAPH","F_LAD","unknown"}) Reject(()=>Run(output,language:lang),"Document bounded language refuses "+lang);
            foreach(var language in new[]{"LAD","DB"}) foreach(var release in new[]{"20","21"}) check(Run(output,release:release,language:language).Status=="planned","Document pair preview supported exact scope "+release+"/"+language);
            Reject(()=>Run(output,false,"wrong"),"Document stale hash rejected before calls");
            Reject(()=>Run(Path.Combine(root,"changed"),false,plan.PlanHash),"Document output bound to hash");
            Reject(()=>Run(output,false,plan.PlanHash,release:"21"),"Document release bound to hash");
            var inconsistent=Run(output,consistent:false); Reject(()=>Run(output,false,inconsistent.PlanHash,consistent:false),"Document inconsistent apply refused");
            foreach(var name in new[]{"CON","LPT1","COM¹","bad:stream","a.","a ","../escape"}) Reject(()=>PlcDocumentExportPolicy.Name(name),"Document ordinary filename refuses "+name);
            Directory.CreateDirectory(output); Reject(()=>Run(output),"Document existing directory refuses overwrite"); Directory.Delete(output);
            File.WriteAllText(output,"original"); Reject(()=>Run(output),"Document existing file refuses overwrite"); File.Delete(output);
            if(!OperatingSystem.IsWindows()) Reject(()=>Run(output,false,plan.PlanHash),"Document production publication refuses non-Windows before native callback");
            var applied=Run(output,false,plan.PlanHash,move:(from,to)=>Directory.Move(from.FullName,to.FullName));
            DocumentExportContract.Validate(Wire(applied),false);
            check(applied.Status=="exported" && !applied.RequiresSessionReset && Directory.GetFiles(output).Length==2,"Document pair publishes together to fresh output");
            foreach(var kind in new[]{"missing","extra","escaped","duplicate","empty","native-failure","publish-failure","race"})
            {
                var dest=Path.Combine(root,kind);var preview=Run(dest);
                var failed=Run(dest,false,preview.PlanHash,export:(dir,name)=>{
                    if(kind=="native-failure") throw new Exception("private native message");
                    var paths=new[]{Path.Combine(dir.FullName,name+".s7dcl"),Path.Combine(dir.FullName,name+".s7res")};
                    File.WriteAllText(paths[0],kind=="empty"?"":"code"); if(kind!="missing") File.WriteAllText(paths[1],"resource");
                    if(kind=="extra") File.WriteAllText(Path.Combine(dir.FullName,"extra"),"unexpected");
                    if(kind=="escaped") paths[1]=Path.Combine(root,"outside.s7res");
                    if(kind=="duplicate") paths[1]=paths[0];
                    if(kind=="race") Directory.CreateDirectory(dest);
                    return paths;
                },move:(from,to)=>{if(kind=="publish-failure") throw new IOException("ambiguous");Directory.Move(from.FullName,to.FullName);});
                check(failed.Status=="failed" && failed.RequiresSessionReset && Directory.Exists(failed.RecoveryDirectory),"Document "+kind+" retains evidence and poisons session");
                DocumentExportContract.Validate(Wire(failed),false);
                var outcome=new WorkerOutcomeState();
                outcome.AcceptResult("ExportAsDocuments",new JsonObject { ["dryRun"]=false,["softwarePath"]="device/PLC",["blockPath"]="group/Block",["exportPath"]=dest,["expectedPlanHash"]=preview.PlanHash,["expectedProjectFile"]="/project.ap20" },Wire(failed));
                check(outcome.Poisoned,"Document failed result poisons host outcome: "+kind);
                check(kind=="race" || !Directory.Exists(dest),"Document invalid pair never published: "+kind);
            }
            var optionalPath=Path.Combine(root,"v21-no-comments"); var optionalPlan=Run(optionalPath,release:"21");
            var optional=Run(optionalPath,false,optionalPlan.PlanHash,release:"21",export:(dir,name)=>{var file=Path.Combine(dir.FullName,name+".s7dcl");File.WriteAllText(file,"code");return new[]{file};},move:(from,to)=>Directory.Move(from.FullName,to.FullName));
            DocumentExportContract.Validate(Wire(optional),false);
            check(optional.Status=="exported" && optional.Files.Length==1,"V21 code-only native export accepted when optional comment resource is absent");
            var request=new JsonObject { ["dryRun"]=true,["softwarePath"]="device/PLC",["blockPath"]="group/Block",["exportPath"]=plan.OutputDirectory,["preservePath"]=false };
            DocumentExportContract.ValidateRequest(request,Wire(plan));
            var bad=Wire(plan);bad["Language"]="SCL";Reject(()=>DocumentExportContract.Validate(bad,true),"Document forged language rejected");
            bad=Wire(plan);bad["Executed"]=true;Reject(()=>DocumentExportContract.Validate(bad,true),"Document forged execution rejected");
            request["blockPath"]="different";Reject(()=>DocumentExportContract.ValidateRequest(request,Wire(plan)),"Document wrong response identity rejected");
            var definition=FoundationTools.Definitions.Single(d=>d.Name=="ExportAsDocuments");
            check(definition.ResponseMember=="DocumentExport" && TiaMcp.PlcWorker.WorkerOperations.Names.Contains("ExportAsDocuments"),"Document tool and worker operation explicitly registered");
            check(calls==1,"Only valid injected pair invokes test export once; preflight refusals have no callback");
        }
        finally { Directory.Delete(root,true); }
    }
}
