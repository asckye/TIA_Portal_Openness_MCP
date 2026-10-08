using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;

internal static class ExternalSourceWorkflowTests
{
    private sealed class GenerationDiagnosticException(string message) : Exception(message);
    private const string Project=@"C:\Projects\Example.ap17";
    private static PlcExternalSourceImportResult ImportResult(string release="17")=>new() {Operation="ImportPlcExternalSource",Release=release,ProjectFile=Project,ProcessId=123,SoftwarePath="devices/PLC_1/CPU",FilePath=@"C:\Sources\Pump.scl",RequestedSourceName="Pump.scl"};
    private static PlcExternalSourceGenerationResult GenerationResult(string release="17")=>new() {Operation="GenerateBlocksFromExternalSource",Release=release,ProjectFile=Project,ProcessId=123,SoftwarePath="devices/PLC_1/CPU",SourceName="Pump.scl",SourceIdentity="native-source-identity"};
    private static PlcExternalSourceObject Block(string name,string kind="block",int day=1)=>new() {Kind=kind,Path="Program/"+name,Name=name,TypeName=kind=="block"?"PlcBlock":"PlcType",ProgrammingLanguage=kind=="block"?"SCL":"",IsConsistent=true,ModifiedDate=new DateTime(2026,1,day)};
    internal static JsonObject Payload(string operation,JsonObject args)
    {
        PlcExternalSourceWorkflowResult result;
        if(operation=="ImportPlcExternalSource")
        {
            var import=ImportResult(); import.FilePath=args["filePath"]!.GetValue<string>();
            import.InputSha256=new string('a',64); import.ByteCount=32; import.SourceName=import.RequestedSourceName;
            result=import;
        }
        else { var generation=GenerationResult();generation.SourceName=args["externalSourceName"]!.GetValue<string>();generation.GenerationOption="None";generation.ResultBasis="native-returned-objects-with-path-readback";result=generation; }
        result.SoftwarePath=args["softwarePath"]!.GetValue<string>();result.PlanHash=new string('b',64);
        return JsonSerializer.SerializeToNode(result,result.GetType())!.AsObject();
    }
    internal static async Task Run(IMcpServer server,Action<bool,string> check)
    {
        void Reject(Action action,string label) {try {action();} catch(ArgumentException) {check(true,label);return;} catch(InvalidOperationException) {check(true,label);return;} throw new Exception("Accepted: "+label);}
        foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"})
        {
            var names=new List<string>(); var source=Encoding.ASCII.GetBytes("FUNCTION \"Pump\" : Void\r\nBEGIN\r\nEND_FUNCTION\r\n"); int imports=0;
            PlcExternalSourceImportResult Import(bool dry,string hash="")=>PlcExternalSourceWorkflowPolicy.Import(ImportResult(release),new MemoryStream(source),dry,hash,!dry,dry?"":Project,()=>names,()=>{},(name,path)=> {check(name=="Pump.scl" && path==@"C:\Sources\Pump.scl","Native name/path argument order "+release);imports++;names.Add(name);return name;});
            var preview=Import(true); check(imports==0 && !preview.Executed && preview.ByteCount==source.Length,"Source preview reads bytes without importing "+release);
            var imported=Import(false,preview.PlanHash);check(imports==1 && imported.Executed && imported.SourceName=="Pump.scl" && imported.SourceNamesAfter.Single()=="Pump.scl","Source import performs one create and readback "+release);
            var importRequest=new JsonObject { ["softwarePath"]=imported.SoftwarePath,["groupPath"]="",["filePath"]="C:/Sources/Pump.scl",["dryRun"]=false,["confirm"]=true,["expectedProjectFile"]="C:/Projects/Example.ap17",["expectedPlanHash"]=preview.PlanHash };
            ExternalSourceWorkflowContract.Validate("ImportPlcExternalSource",importRequest,JsonSerializer.SerializeToNode(imported));
            check(imported.Generation=="notRun","Import acknowledgement preserves actual source identity and Windows path semantics "+release);
            Reject(()=>Import(true),"Existing source is not recreated "+release);
            var before=new[]{Block("Existing")}; var current=before; int generations=0;
            PlcExternalSourceGenerationResult Generate(bool dry,string hash="")=>PlcExternalSourceWorkflowPolicy.Generate(GenerationResult(release),dry,hash,!dry,dry?"":Project,()=>current,()=>{},()=> {generations++;current=new[]{Block("Existing",day:2),Block("Pump"),Block("Motor","type")};},()=>release=="14sp1"?Array.Empty<PlcExternalSourceObject>():current,_=>false);
            var plan=Generate(true);check(generations==0 && plan.ObjectsBefore.Length==1 && plan.GeneratedObjects.Length==0,"Generation preview does not call native API "+release);
            var generated=Generate(false,plan.PlanHash);
            check(generations==1 && generated.Executed && generated.ObjectsAfter.Length==3 && generated.ObservedChanges.Length==3,"Generation observes new block, updated block and UDT "+release);
            check(generated.GeneratedObjects.Length==(release=="14sp1"?0:3) && generated.GenerationOption==(release=="14sp1"?"parameterless":"None"),"Generation distinguishes void and returned-object APIs "+release);
            var args=new JsonObject { ["softwarePath"]=generated.SoftwarePath,["externalSourceName"]="Pump.scl",["dryRun"]=false,["confirm"]=true,["expectedProjectFile"]=Project,["expectedPlanHash"]=plan.PlanHash };
            var payload=JsonSerializer.SerializeToNode(generated)!.AsObject();ExternalSourceWorkflowContract.Validate("GenerateBlocksFromExternalSource",args,payload);
            check(generated.Compilation=="notRun" && generated.Save=="notRun" && generated.Download=="notRun","Generation does not claim PLC compile or deployment "+release);
            var returned=(JsonObject)payload.DeepClone();returned["ObjectsAfter"]=new JsonArray();
            if(release!="14sp1") {try {ExternalSourceWorkflowContract.Validate("GenerateBlocksFromExternalSource",args,returned);throw new Exception("Missing generated objects accepted");} catch(InvalidDataException){check(true,"Host requires actual generated-object readback "+release);}}
        }
        Reject(()=>PlcExternalSourceWorkflowPolicy.RequireRoot("Group"),"Official external-source root-only contract");
        Reject(()=>PlcExternalSourceWorkflowPolicy.InputHash(new MemoryStream(Encoding.UTF8.GetBytes("//中文"))),"Non-ASCII source rejected without silently transcoding");
        var bytes=Encoding.ASCII.GetBytes("FUNCTION Pump : Void\nEND_FUNCTION\n");
        var state=new List<string>();int attempts=0;
        PlcExternalSourceImportResult PreviewImport()=>PlcExternalSourceWorkflowPolicy.Import(ImportResult(),new MemoryStream(bytes),true,"",false,"",()=>state,()=>{},(_,__)=>throw new Exception("Preview called create"));
        var importPlan=PreviewImport();
        Reject(()=>PlcExternalSourceWorkflowPolicy.Import(ImportResult(),new MemoryStream(bytes.Concat(new byte[]{32}).ToArray()),false,importPlan.PlanHash,true,Project,()=>state,()=>{},(_,__)=>{attempts++;return "Pump.scl";}),"Changed source bytes invalidate preview");
        check(attempts==0,"Changed input is rejected before native creation");
        var failedImport=PlcExternalSourceWorkflowPolicy.Import(ImportResult(),new MemoryStream(bytes),false,importPlan.PlanHash,true,Project,()=>state,()=>{},(_,__)=> {attempts++;state.Add("Pump.scl");throw new IOException("native failure after creation");});
        check(attempts==1 && failedImport.RequiresSessionReset && failedImport.Status=="outcome-unknown" && !failedImport.Executed,"Native import failure retains uncertainty and is not replayed");
        foreach(var release in new[]{"14sp1","15.1","16","17","18","19","20","21"})
        {
            var objects=new[]{Block("Existing")};int nativeCalls=0,readbacks=0;
            bool Recoverable(Exception ex)=>ex is GenerationDiagnosticException;
            var generationPlan=PlcExternalSourceWorkflowPolicy.Generate(GenerationResult(release),true,"",false,"",()=>objects,()=>{},()=>throw new Exception(),()=>throw new Exception(),Recoverable);
            PlcExternalSourceGenerationResult Apply(Action native,Func<PlcExternalSourceObject[]> readback)=>PlcExternalSourceWorkflowPolicy.Generate(GenerationResult(release),false,generationPlan.PlanHash,true,Project,()=>objects,()=>{},native,readback,Recoverable);
            var failedGeneration=Apply(()=>{nativeCalls++;throw new GenerationDiagnosticException("Source line 3: unknown symbol");},()=>{readbacks++;return objects;});
            check(failedGeneration.Status=="failed" && failedGeneration.Error.Contains("Source line 3") && !failedGeneration.RequiresSessionReset && !failedGeneration.Executed && nativeCalls==1 && readbacks==0,"Recoverable native generation failure preserves diagnostics without poisoning the session "+release);
            var request=new JsonObject { ["softwarePath"]=generationPlan.SoftwarePath,["externalSourceName"]="Pump.scl",["dryRun"]=false,["confirm"]=true,["expectedProjectFile"]=Project,["expectedPlanHash"]=generationPlan.PlanHash };
            var outcome=new WorkerOutcomeState();outcome.AcceptResult("GenerateBlocksFromExternalSource",request,JsonSerializer.SerializeToNode(failedGeneration));outcome.RequireUsable();
            check(!outcome.Poisoned,"Host accepts rolled-back generation failure and keeps the session usable "+release);
            var fixedGeneration=Apply(()=>{nativeCalls++;},()=>{readbacks++;return release=="14sp1"?Array.Empty<PlcExternalSourceObject>():objects;});
            check(fixedGeneration.Status=="completed" && fixedGeneration.Executed && nativeCalls==2 && readbacks==1,"User correction can run generation again after a normal source error "+release);
            var readbackFailure=Apply(()=>{nativeCalls++;},()=>throw new GenerationDiagnosticException("Native call succeeded; object readback failed"));
            check(readbackFailure.Status=="outcome-unknown" && readbackFailure.RequiresSessionReset,"Recoverable-class exception during readback is not mistaken for generation rollback "+release);
            outcome.AcceptResult("GenerateBlocksFromExternalSource",request,JsonSerializer.SerializeToNode(readbackFailure));check(outcome.Poisoned,"Uncertain readback stops further mutation dispatch "+release);
            var lost=Apply(()=>throw new IOException("Connection lost during generation"),()=>throw new Exception());
            check(lost.Status=="outcome-unknown" && lost.RequiresSessionReset,"Transport or fatal generation failure remains unknown "+release);
        }
        var fake=new FakeWorker();
        foreach(var name in new[]{"ImportPlcExternalSource","GenerateBlocksFromExternalSource"})
        {
            var tool=FoundationTools.Create(fake,"14sp1").Single(x=>x.ProtocolTool.Name==name);
            var args=new JsonObject { ["softwarePath"]="devices/PLC_1/CPU" };
            if(name=="ImportPlcExternalSource") {args["groupPath"]="";args["filePath"]=@"C:\Sources\Pump.scl";} else args["externalSourceName"]="Pump.scl";
            RequestContext<CallToolRequestParams> Request()=>new(server) {Params=new() {Name=name,Arguments=args.ToDictionary(x=>x.Key,x=>JsonSerializer.SerializeToElement(x.Value))}};
            var response=await tool.InvokeAsync(Request());check(response.IsError!=true && fake.Operation==name && fake.Arguments!["dryRun"]!.GetValue<bool>(),"Foundation host dispatches preview to the actual worker operation "+name);
            int calls=fake.Calls;args["dryRun"]=false;args["confirm"]=true;args["expectedProjectFile"]=Project;
            try {await tool.InvokeAsync(Request());throw new Exception("Apply without preview hash was dispatched");} catch(McpException ex) {check(ex.ErrorCode==McpErrorCode.InvalidParams && fake.Calls==calls,"Apply requires preview before worker dispatch "+name);}
            check(TiaMcp.PlcWorker.WorkerOperations.Names.Contains(name),"Typed worker exposes native external-source route "+name);
        }
    }
}
