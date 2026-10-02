using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.PlcFoundation;
using TiaMcp.LegacyHost;
internal static class ExternalSourcePlanTests
{
    internal static JsonObject Payload(JsonObject? args=null)=>JsonSerializer.SerializeToNode(new PlcExternalSourceImportPlan {Release="21",ProjectFile=@"C:\Projects\Demo.ap21",ProcessId=123,SoftwarePath=args?["softwarePath"]?.GetValue<string>()??"Device/PLC",FilePath=args?["filePath"]?.GetValue<string>()??@"C:\Sources\Pump.scl",SourceName="Pump.scl",Extension=".scl",ByteCount=1,InputSha256=new string('a',64),PlanHash=new string('b',64)})!.AsObject();
    internal static void Run(Action<bool,string> check)
    {
        JsonObject Args()=>new(){["softwarePath"]="Device/PLC",["groupPath"]="",["filePath"]=@"C:\Sources\Pump.scl",["allowedFilePath"]=@"C:\Sources\Pump.scl",["dryRun"]=true};
        var payload=Payload();ExternalSourcePlanContract.ValidateRequest(Args(),payload);check(true,"External-source plan accepted without mutation claims");
        void Reject(Action action,string label){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}check(rejected,label);}
        foreach(var pair in new (string,JsonNode?)[]{("Executed",JsonValue.Create(true)),("Attempted",JsonValue.Create(true)),("CreatedCount",JsonValue.Create(1)),("ApplyBlocked",JsonValue.Create(false)),("Generation",JsonValue.Create("success")),("Status",JsonValue.Create("created")),("Release",JsonValue.Create("14")),("ProcessId",JsonValue.Create(0)),("InputSha256",JsonValue.Create("bad")),("ByteCount",JsonValue.Create(4194305)),("InventoryCount",JsonValue.Create(4097)),("GroupPath",JsonValue.Create("folder")),("SourceName",JsonValue.Create("Other.scl")),("CollisionStatus",JsonValue.Create("race-safe"))})
        {var bad=(JsonObject)payload.DeepClone();bad[pair.Item1]=pair.Item2;Reject(()=>ExternalSourcePlanContract.Validate(bad),"External-source response rejects "+pair.Item1);}
        foreach(var key in payload.Select(x=>x.Key).ToArray()) {var bad=(JsonObject)payload.DeepClone();bad.Remove(key);Reject(()=>ExternalSourcePlanContract.Validate(bad),"External-source required field "+key);}
        foreach(var key in new[]{"softwarePath","groupPath","filePath","allowedFilePath","expectedProjectFile","expectedPlanHash"}) {var args=Args();args[key]="changed";Reject(()=>ExternalSourcePlanContract.ValidateRequest(args,payload),"External-source request binds "+key);}
        var apply=Args();apply["dryRun"]=false;apply["confirm"]=true;apply["expectedProjectFile"]=payload["ProjectFile"]!.DeepClone();apply["expectedPlanHash"]=payload["PlanHash"]!.DeepClone();Reject(()=>ExternalSourcePlanContract.ValidateRequest(apply,payload),"External-source apply refused even with matching confirmation");
        var fake=new FakeWorker();
        var tool=FoundationTools.Create(fake).Single(t=>t.ProtocolTool.Name=="PlanPlcExternalSourceImport");
        var server=System.Reflection.DispatchProxy.Create<ModelContextProtocol.Server.IMcpServer,ServerProxy>();
        var request=new ModelContextProtocol.Server.RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams>(server) { Params=new ModelContextProtocol.Protocol.CallToolRequestParams {Name="PlanPlcExternalSourceImport",Arguments=apply.ToDictionary(p=>p.Key,p=>JsonSerializer.SerializeToElement(p.Value))} };
        bool blocked=false;try{tool.InvokeAsync(request).AsTask().GetAwaiter().GetResult();}catch(ModelContextProtocol.McpException e){blocked=e.ErrorCode==ModelContextProtocol.McpErrorCode.InvalidParams;}
        check(blocked && fake.Calls==0,"Confirmed external-source apply never dispatches worker");
        check(TiaMcp.PlcWorker.WorkerOperations.Names.Contains("PlanPlcExternalSourceImport"),"External-source explicit worker operation registered");
        check(!TiaMcp.PlcWorker.WorkerOperations.Names.Contains("ImportPlcExternalSource"),"Legacy native external-source import remains unavailable");
    }
}
