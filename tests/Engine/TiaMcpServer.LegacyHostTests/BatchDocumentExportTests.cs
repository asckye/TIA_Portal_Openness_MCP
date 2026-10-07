using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.PlcFoundation;
using TiaMcp.LegacyHost;
internal static class BatchDocumentExportTests
{
    internal static JsonNode Payload(JsonObject args)
    {
        var group=args["groupPath"]!.GetValue<string>();var block=group+"/Block";var output=args["exportPath"]!.GetValue<string>();
        var result=new PlcBatchDocumentExportResult {ReleaseKey="20",ProjectFile="C:/Projects/Example.ap20",SoftwarePath=args["softwarePath"]!.GetValue<string>(),GroupPath=group,OutputDirectory=output,MaxItems=args["maxItems"]?.GetValue<int>()??128,Recursive=args["recursive"]?.GetValue<bool>()??false,
            Items=new[]{new PlcBatchDocumentExportItem{BlockPath=block,OutputDirectory=Path.Combine(output,"block-"+PlcDocumentExportPolicy.Hash(block)),Language="LAD",Consistent=true,Files=new[]{"Block.s7dcl","Block.s7res"}}}};
        result.PlanHash=PlcBatchDocumentExportPolicy.PlanHash(result);return JsonSerializer.SerializeToNode(result)!;
    }
    private sealed class PreviewWorker(JsonObject result):IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation,JsonObject args,CancellationToken token) {Calls++;return Task.FromResult<JsonNode?>(result.DeepClone());}
        public void Dispose() { }
    }
    private static void Dispatch(PlcBatchDocumentExportResult plan,Action<bool,string> check)
    {
        var worker=new PreviewWorker(JsonSerializer.SerializeToNode(plan)!.AsObject());
        var tool=FoundationTools.Create(worker).Single(t=>t.ProtocolTool.Name=="ExportBlocksAsDocuments");
        var server=System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer,ServerProxy>();
        var args=new Dictionary<string,JsonElement>{["softwarePath"]=JsonSerializer.SerializeToElement(plan.SoftwarePath),["groupPath"]=JsonSerializer.SerializeToElement(plan.GroupPath),["exportPath"]=JsonSerializer.SerializeToElement(plan.OutputDirectory)};
        ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> Request()=>new(server){Params=new(){Name="ExportBlocksAsDocuments",Arguments=args}};
        check(tool.InvokeAsync(Request()).AsTask().GetAwaiter().GetResult().IsError!=true && worker.Calls==1,"Batch document MCP default preview reaches dedicated contract");
        void Refuse(string label){bool refused=false;try{refused=tool.InvokeAsync(Request()).AsTask().GetAwaiter().GetResult().IsError==true;}catch(ModelContextProtocol.McpException){refused=true;}check(refused && worker.Calls==1,label);}
        args["dryRun"]=JsonSerializer.SerializeToElement(false);Refuse("Batch document unconfirmed apply blocked before dispatch");
        args["confirm"]=JsonSerializer.SerializeToElement(true);Refuse("Batch document missing project blocked before dispatch");
        args["expectedProjectFile"]=JsonSerializer.SerializeToElement(plan.ProjectFile);Refuse("Batch document missing preview hash blocked before dispatch");
    }
    internal static void Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-batch-doc-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        int native=0,offline=0;
        PlcBatchDocumentExportSource Source(string path,string language="LAD",bool consistent=true,bool protection=false,Func<DirectoryInfo,string,string[]>? callback=null)=>new(){Path=path,Language=language,Consistent=consistent,Protected=protection,Export=callback??((directory,name)=>{native++;var files=new[]{Path.Combine(directory.FullName,name+".s7dcl"),Path.Combine(directory.FullName,name+".s7res")};foreach(var file in files)File.WriteAllText(file,"bytes");return files;})};
        PlcBatchDocumentExportResult Run(string output,IEnumerable<PlcBatchDocumentExportSource>? sources=null,bool dry=true,string hash="",string release="20",bool recursive=false,int max=128,Action<DirectoryInfo,DirectoryInfo>? move=null)=>PlcBatchDocumentExportPolicy.Run(release,"/project.ap"+release,"device/PLC","group",recursive,output,max,dry,hash,sources??new[]{Source("group/B"),Source("group/A")},()=>offline++,move);
        JsonObject Request(PlcBatchDocumentExportResult result,bool dry=true)=>new(){["softwarePath"]=result.SoftwarePath,["groupPath"]=result.GroupPath,["exportPath"]=result.OutputDirectory,["maxItems"]=result.MaxItems,["recursive"]=result.Recursive,["dryRun"]=dry,["expectedPlanHash"]=result.PlanHash,["expectedProjectFile"]=result.ProjectFile};
        JsonObject Wire(PlcBatchDocumentExportResult result)=>JsonSerializer.SerializeToNode(result)!.AsObject();
        void Reject(Action action,string message){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(NotSupportedException){rejected=true;}catch(IOException){rejected=true;}catch(InvalidDataException){rejected=true;}check(rejected,message);}
        void Validate(PlcBatchDocumentExportResult result,bool dry=true)=>BatchDocumentExportContract.Validate(Wire(result),Request(result,dry));
        try
        {
            var output=Path.Combine(root,"tree");var plan=Run(output);Validate(plan);Dispatch(plan,check);
            check(native==0 && offline==0 && Directory.GetFileSystemEntries(root).Length==0,"Batch document preview is zero-write/offline/native");
            check(plan.Items.Select(x=>x.BlockPath).SequenceEqual(new[]{"group/A","group/B"}),"Batch document inventory is ordinal deterministic");
            check(Run(output,new[]{Source("group/A"),Source("group/B")}).PlanHash==plan.PlanHash,"Batch document order independent plan hash");
            Reject(()=>Run(output,dry:false,hash:"wrong"),"Batch document stale hash refused");
            Reject(()=>Run(Path.Combine(root,"changed"),dry:false,hash:plan.PlanHash),"Batch document changed output refused");
            Reject(()=>Run(output,dry:false,hash:plan.PlanHash,release:"21"),"Batch document changed release refused");
            Reject(()=>Run(output,dry:false,hash:plan.PlanHash,max:3),"Batch document bound included in hash");
            foreach(var release in new[]{"17","18","19","V20","20.0","22"}) Reject(()=>Run(output,release:release),"Batch document release refused "+release);
            foreach(var language in new[]{"SCL","FBD","STL","GRAPH"}) Reject(()=>Run(output,new[]{Source("group/A",language)}),"Batch document language refused "+language);
            Reject(()=>Run(output,new[]{Source("group/A",protection:true)}),"Batch document protected inventory refused");
            Reject(()=>Run(output,Array.Empty<PlcBatchDocumentExportSource>()),"Batch document empty inventory refused");
            Reject(()=>Run(output,new[]{Source("group/A"),Source("group/A")}),"Batch document duplicate paths refused");
            Reject(()=>Run(output,new[]{Source("elsewhere/A")}),"Batch document outside group refused");
            Reject(()=>Run(output,new[]{Source("group/sub/A")}),"Batch document unexpected recursion refused");
            check(Run(output,new[]{Source("group/sub/A")},recursive:true).Items.Length==1,"Batch document recursion explicit");
            Reject(()=>Run(output,Enumerable.Range(0,257).Select(i=>Source("group/B"+i)),max:256),"Batch document complete over-bound inventory refused");
            foreach(var max in new[]{0,257}) Reject(()=>Run(output,max:max),"Batch document invalid max refused "+max);
            Reject(()=>Run(output,new[]{Source("group/CON")}),"Batch document device filename refused");
            foreach(bool dry in new[]{true,false})
            {
                try {Run(output,new[]{Source("group/A",consistent:false)},dry);throw new Exception("Inconsistent batch accepted");}
                catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
                {check(!error.IsArgument && error.Message.Contains("group/A") && native==0,"Batch document inconsistent inventory names its objects before preview/apply");}
            }
            Directory.CreateDirectory(output);Reject(()=>Run(output),"Batch document existing target refused");Directory.Delete(output);
            File.WriteAllText(output,"original");Reject(()=>Run(output),"Batch document existing file refused");File.Delete(output);
            if(!OperatingSystem.IsWindows())Reject(()=>Run(output,dry:false,hash:plan.PlanHash),"Batch document production non-Windows refused before calls");
            check(native==0 && offline==0,"Batch document all preflight failures make no native/offline calls");
            var success=Run(output,dry:false,hash:plan.PlanHash,move:(from,to)=>Directory.Move(from.FullName,to.FullName));Validate(success,false);
            check(success.Status=="exported" && success.Items.All(x=>x.Status=="exported") && Directory.GetDirectories(output).Length==2 && native==2,"Batch document entire validated tree publishes once");
            Reject(()=>Run(output,dry:false,hash:plan.PlanHash,move:(from,to)=>Directory.Move(from.FullName,to.FullName)),"Batch document replay cannot overwrite published tree");
            foreach(var kind in new[]{"native","missing-resource","extra","empty","duplicate","escaped","publish","race","late-tamper","extra-tree"})
            {
                var destination=Path.Combine(root,kind);int attempts=0;string? firstFile=null;
                var sources=new[]{"group/A","group/B","group/C"}.Select(path=>Source(path,callback:(directory,name)=>{
                    attempts++;if(name=="B" && kind=="native")throw new InvalidOperationException("native private message");
                    var files=new[]{Path.Combine(directory.FullName,name+".s7dcl"),Path.Combine(directory.FullName,name+".s7res")};
                    File.WriteAllText(files[0],name=="B" && kind=="empty"?"":"code");
                    if(!(name=="B" && kind=="missing-resource"))File.WriteAllText(files[1],"resource");
                    if(name=="A")firstFile=files[0];
                    if(name=="B") {
                        if(kind=="extra")File.WriteAllText(Path.Combine(directory.FullName,"extra"),"extra");
                        if(kind=="duplicate")files[1]=files[0];if(kind=="escaped")files[1]=Path.Combine(root,"escaped.s7res");
                        if(kind=="race")Directory.CreateDirectory(destination);
                        if(kind=="late-tamper")File.WriteAllText(firstFile!,"");
                        if(kind=="extra-tree")File.WriteAllText(Path.Combine(directory.Parent!.FullName,"extra"),"extra");
                    }
                    return files;
                })).ToArray();
                var preview=Run(destination,sources);var failed=Run(destination,sources,false,preview.PlanHash,move:(from,to)=>{if(kind=="publish")throw new IOException("unknown publication");Directory.Move(from.FullName,to.FullName);});Validate(failed,false);
                check(failed.Status=="failed" && failed.RequiresSessionReset && Directory.Exists(failed.RecoveryDirectory) && failed.Items.All(x=>x.Status!="exported"),"Batch document "+kind+" retains evidence without false published successes");
                var midFailure=kind is "native" or "missing-resource" or "extra" or "empty" or "duplicate" or "escaped";
                check(midFailure?attempts==2 && failed.Items[0].Status=="staged" && failed.Items[1].Status=="failed" && failed.Items[2].Status=="not-attempted":attempts==3,"Batch document "+kind+" stops without native replay");
                var outcome=new WorkerOutcomeState();outcome.AcceptResult("ExportBlocksAsDocuments",Request(failed,false),Wire(failed));check(outcome.Poisoned,"Batch document "+kind+" poisons host");
                check(kind=="race" || !Directory.Exists(destination),"Batch document "+kind+" never publishes invalid tree");
            }
            var duplicateNames=Run(Path.Combine(root,"same-name"),new[]{Source("group/one/A"),Source("group/two/A")},recursive:true);
            check(duplicateNames.Items.Select(x=>x.OutputDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count()==2 && duplicateNames.Items.All(x=>x.Files[0]=="A.s7dcl"),"Batch document same basenames receive distinct path-hashed directories");
            var optionalFailedOutput=Path.Combine(root,"optional-failed");
            var optionalFailedSources=new[]{Source("group/A",callback:(directory,name)=>{var file=Path.Combine(directory.FullName,name+".s7dcl");File.WriteAllText(file,"bytes");return new[]{file};}),Source("group/B",callback:(_,_)=>throw new IOException("native failure"))};
            var optionalFailedPlan=Run(optionalFailedOutput,optionalFailedSources,release:"21");
            var optionalFailed=Run(optionalFailedOutput,optionalFailedSources,false,optionalFailedPlan.PlanHash,release:"21",move:(from,to)=>Directory.Move(from.FullName,to.FullName));Validate(optionalFailed,false);
            check(optionalFailed.Status=="failed" && optionalFailed.Items[0].Status=="staged" && optionalFailed.Items[0].Files.Length==1 && optionalFailed.Items[1].Status=="failed","Batch document V21 code-only staged outcome survives later failure");
            if(!OperatingSystem.IsWindows())
            {
                var linkOutput=Path.Combine(root,"root-link");
                var linkSources=new[]{Source("group/A",callback:(directory,name)=> {
                    var files=new[]{Path.Combine(directory.FullName,name+".s7dcl"),Path.Combine(directory.FullName,name+".s7res")};foreach(var file in files)File.WriteAllText(file,"bytes");
                    var stage=directory.Parent!.FullName;Directory.Move(stage,stage+"-retained");Directory.CreateSymbolicLink(stage,stage+"-retained");return files;
                })};
                var linkPlan=Run(linkOutput,linkSources);var linked=Run(linkOutput,linkSources,false,linkPlan.PlanHash,move:(from,to)=>Directory.Move(from.FullName,to.FullName));Validate(linked,false);
                check(linked.Status=="failed" && linked.RequiresSessionReset && !Directory.Exists(linkOutput),"Batch document final root reparse refresh rejects replaced symlink");
            }
            var optionalOutput=Path.Combine(root,"optional");var optionalSources=new[]{Source("group/A",callback:(directory,name)=>{var file=Path.Combine(directory.FullName,name+".s7dcl");File.WriteAllText(file,"code");return new[]{file};})};
            var optionalPlan=Run(optionalOutput,optionalSources,release:"21");var optional=Run(optionalOutput,optionalSources,false,optionalPlan.PlanHash,release:"21",move:(from,to)=>Directory.Move(from.FullName,to.FullName));Validate(optional,false);
            check(optional.Status=="exported" && optional.Items[0].Files.Length==1,"Batch document V21 optional resources supported");
            foreach(var field in new[]{"SoftwarePath","GroupPath","OutputDirectory","PlanHash","ProjectFile","Options","Evidence"})
            {var bad=Wire(plan);bad[field]="forged";Reject(()=>BatchDocumentExportContract.Validate(bad,Request(plan)),"Batch document forged "+field+" rejected");}
            var malformed=Wire(plan);malformed["Unexpected"]=true;Reject(()=>BatchDocumentExportContract.Validate(malformed,Request(plan)),"Batch document extra envelope field rejected");
            malformed=Wire(success);malformed["Items"]![0]!["Status"]="staged";Reject(()=>BatchDocumentExportContract.Validate(malformed,Request(success,false)),"Batch document false all-success rejected");
            malformed=Wire(plan);malformed["Items"]![0]!["BlockPath"]="group/Z";Reject(()=>BatchDocumentExportContract.Validate(malformed,Request(plan)),"Batch document unordered forged inventory rejected");
            check(FoundationTools.Definitions.Single(d=>d.Name=="ExportBlocksAsDocuments").ResponseMember=="BatchDocumentExport" && TiaMcp.PlcWorker.WorkerOperations.Names.Contains("ExportBlocksAsDocuments"),"Batch document host/worker explicit registration");
            var refusedPath=Path.Combine(root,"policy-refused");
            var refusedSources=new[]{Source("group/A",callback:(_,_)=>throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Block eligibility changed after preview.","export-plan",false))};
            var refusedPlan=Run(refusedPath,refusedSources);
            try {Run(refusedPath,refusedSources,false,refusedPlan.PlanHash,move:(from,to)=>Directory.Move(from.FullName,to.FullName));throw new Exception("Policy refusal accepted");}
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            {check(!error.IsArgument && !Directory.Exists(refusedPath),"Batch document policy refusal before first export stays a precondition without publication");}
            var partialPath=Path.Combine(root,"policy-partial");var partialSources=new[]{Source("group/A"),refusedSources[0]};partialSources[1].Path="group/B";
            var partialPlan=Run(partialPath,partialSources);var partialResult=Run(partialPath,partialSources,false,partialPlan.PlanHash,move:(from,to)=>Directory.Move(from.FullName,to.FullName));
            check(partialResult.RequiresSessionReset && partialResult.Items[0].Status=="staged" && partialResult.Items[1].Status=="failed","Batch document policy refusal after native export retains staging evidence and requires reset");
        }
        finally {Directory.Delete(root,true);}
    }
}
