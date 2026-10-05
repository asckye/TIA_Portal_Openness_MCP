using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.PlcFoundation;
using TiaMcp.LegacyHost;
internal static class ExternalSourceDeleteTests
{
    internal static JsonObject Payload(JsonObject? args=null)=>JsonSerializer.SerializeToNode(new PlcExternalSourceDeleteResult {Release="21",ProjectFile=@"C:\Projects\Demo.ap21",ProcessId=123,SoftwarePath=args?["softwarePath"]?.GetValue<string>()??"Device/PLC",SourceName=args?["externalSourceName"]?.GetValue<string>()??"Pump.scl",RootIdentity="root",TargetIdentity="source",PlanHash=new string('b',64),Inventory=new[]{args?["externalSourceName"]?.GetValue<string>()??"Pump.scl"}})!.AsObject();
    internal static void Run(Action<bool,string> check)
    {
        JsonObject Args()=>new(){["softwarePath"]="Device/PLC",["groupPath"]="",["externalSourceName"]="Pump.scl",["dryRun"]=true};
        var payload=Payload();ExternalSourceDeleteContract.Validate(payload,Args());check(true,"Delete preview validates");
        void Reject(Action a,string label){bool rejected=false;try{a();}catch(InvalidDataException){rejected=true;}check(rejected,label);}
        foreach(var pair in new (string,JsonNode?)[]{("Executed",JsonValue.Create(true)),("Deleted",JsonValue.Create(true)),("Attempted",JsonValue.Create(true)),("RequiresSessionReset",JsonValue.Create(true)),("Generation",JsonValue.Create("success")),("Status",JsonValue.Create("deleted-verified")),("Release",JsonValue.Create("14")),("ProcessId",JsonValue.Create(0)),("PlanHash",JsonValue.Create("bad")),("GroupPath",JsonValue.Create("folder")),("SourceName",JsonValue.Create("Other.scl")),("TargetIdentity",JsonValue.Create("")),("Recovery",JsonValue.Create("rollback-guaranteed"))})
        {var bad=(JsonObject)payload.DeepClone();bad[pair.Item1]=pair.Item2;Reject(()=>ExternalSourceDeleteContract.Validate(bad,Args()),"Delete rejects "+pair.Item1);}
        foreach(var key in payload.Select(x=>x.Key).ToArray()) {var bad=(JsonObject)payload.DeepClone();bad.Remove(key);Reject(()=>ExternalSourceDeleteContract.Validate(bad,Args()),"Delete requires "+key);}
        var apply=Args();apply["dryRun"]=false;apply["confirm"]=true;apply["expectedProjectFile"]=payload["ProjectFile"]!.DeepClone();apply["expectedPlanHash"]=payload["PlanHash"]!.DeepClone();
        var unknown=(JsonObject)payload.DeepClone();unknown["Status"]="outcome-unknown";unknown["Attempted"]=true;unknown["RequiresSessionReset"]=true;unknown["Error"]="transport lost";
        var state=new WorkerOutcomeState();state.AcceptResult("DeletePlcExternalSource",apply,unknown);check(state.Poisoned,"Delete unknown outcome poisons host session");
        var lost=new WorkerOutcomeState();lost.Failed(true,new IOException("transport loss"));check(lost.Poisoned,"Delete lost transport poisons without retry");
        Reject(()=>ExternalSourceDeleteContract.Validate(null,apply),"Missing delete outcome rejected");
        var success=(JsonObject)payload.DeepClone();success["Status"]="deleted-verified";success["Attempted"]=true;success["Executed"]=true;success["Deleted"]=true;ExternalSourceDeleteContract.Validate(success,apply);check(true,"Verified delete result accepted");
        foreach(var key in new[]{"expectedProjectFile","expectedPlanHash","externalSourceName","groupPath","softwarePath"}) {var bad=(JsonObject)apply.DeepClone();bad[key]="changed";Reject(()=>ExternalSourceDeleteContract.Validate(success,bad),"Delete request binds "+key);}
        check(TiaMcp.PlcWorker.WorkerOperations.Names.Contains("DeletePlcExternalSource"),"Delete worker operation registered");
    }
}
