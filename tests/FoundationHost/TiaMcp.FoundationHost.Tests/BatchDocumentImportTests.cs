using TiaMcp.Adapters;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
internal static class BatchDocumentImportTests
{
 internal static JsonNode Payload(JsonObject args)
 {
  var items=new JsonArray();foreach(var name in args["fileNamesWithoutExtension"]!.AsArray()) {var single=(JsonObject)args.DeepClone();single["fileNameWithoutExtension"]=name!.DeepClone();items.Add(new JsonObject{["Name"]=name.DeepClone(),["Status"]="not-attempted",["Preview"]=DocumentImportTests.Payload(single),["Outcome"]=null,["PostInventory"]=new JsonArray()});}
  return new JsonObject{["Policy"]="batch-document-global-db-v1",["PlanHash"]=BatchDocumentImportContract.PlanHash(items),["Status"]="planned",["Attempted"]=false,["MayHaveChanged"]=false,["RequiresSessionReset"]=false,["Error"]="",["Items"]=items};
 }
 internal static void Run(Action<bool,string> Check)
 {
  string fixtureRoot=Path.Combine(Path.GetTempPath(),"tia-batch-document-import-tests"), projectFile=Path.Combine(fixtureRoot,"p.ap21"), documentDirectory=Path.Combine(fixtureRoot,"documents");
  int calls=0,closed=0;bool closeFailure=false;var inventory=new List<string>();var modes=new Dictionary<string,string>();
  PlcDocumentImportContext[] Contexts(params string[] names)=>names.Select(name=>new PlcDocumentImportContext {
   Request=new PlcDocumentImportRequest{Release="21",Project=projectFile,Software="PLC",Group="",Directory=documentDirectory,Name=name,ProcessId=1,TargetIdentity="same"},
   Scan=()=>new[]{Path.Combine(documentDirectory,name+".s7dcl")},Open=_=>new Tracking(Encoding.ASCII.GetBytes("DATA_BLOCK "+name+" VAR x:Bool; END_VAR END_DATA_BLOCK"),()=>{closed++;if(closeFailure)throw new IOException();}),
   Inventory=()=>inventory.ToArray(),Collision=n=>inventory.Any(x=>x==PlcDocumentImportPolicy.InventoryItem("",n,"GlobalDB",n+"-id")),Recheck=()=>{},
   Import=()=>{Check(closed==0,"all locks retained");calls++;var mode=modes.GetValueOrDefault(name,"");if(mode=="throw")throw new IOException();if(mode=="null")return null!;if(mode=="partial"||mode=="failure")return new(){State="PartialSuccess"};inventory.Add(PlcDocumentImportPolicy.InventoryItem("",name,"GlobalDB",name+"-id"));if(mode=="extra")inventory.Add("stray");return new(){State="Success",Success=true,ExistsVerified=true,Identities=new[]{PlcDocumentImportPolicy.InventoryItem("",name,"GlobalDB","returned")}};}
  }).ToArray();
  PlcBatchDocumentImportResult Preview(string[] names)=>PlcBatchDocumentImportPolicy.Run(Contexts(names),true,"",false,"");
  void Reject(Action action,string test){bool failed=false;try{action();}catch(ArgumentException){failed=true;}Check(failed,test);}
  string[] names={"A","B","C"};var p=Preview(names);
  JsonObject Request(bool dry,string hash)=>new(){["softwarePath"]="PLC",["groupPath"]="",["importPath"]=documentDirectory,["fileNamesWithoutExtension"]=new JsonArray(names.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()),["overwrite"]=false,["dryRun"]=dry,["confirm"]=!dry,["expectedPlanHash"]=hash,["expectedProjectFile"]=projectFile};
  BatchDocumentImportContract.Validate(JsonSerializer.SerializeToNode(p),Request(true,""));Check(true,"host preview");Check(calls==0&&closed==3&&p.Items.All(x=>x.Status=="not-attempted"),"preview locks only");closed=0;
  var ok=PlcBatchDocumentImportPolicy.Run(Contexts(names),false,p.PlanHash,true,projectFile);Check(ok.Status=="imported"&&calls==3&&closed==3&&ok.Items.All(x=>x.Status=="succeeded"),"all success");
  BatchDocumentImportContract.Validate(JsonSerializer.SerializeToNode(ok),Request(false,p.PlanHash));Check(true,"host apply");
  inventory.Clear();calls=0;closed=0;Reject(()=>Preview(new[]{"A","a"}),"case duplicate");Reject(()=>PlcBatchDocumentImportPolicy.Run(Contexts(names),false,new string('0',64),true,projectFile),"hash change");Check(calls==0,"invalid hash no native");
  foreach(var mode in new[]{"throw","partial","failure","null","extra"}) {inventory.Clear();calls=0;closed=0;modes["B"]=mode;var result=PlcBatchDocumentImportPolicy.Run(Contexts(names),false,p.PlanHash,true,projectFile);Check(calls==2&&closed==3&&result.RequiresSessionReset&&result.MayHaveChanged&&result.Items[0].Status=="succeeded"&&result.Items[1].Status=="failed"&&result.Items[2].Status=="not-attempted","stop "+mode);BatchDocumentImportContract.Validate(JsonSerializer.SerializeToNode(result),Request(false,p.PlanHash));Check(true,"host failure "+mode);}
  inventory.Clear();closed=0;var reversed=Preview(names.Reverse().ToArray());Check(p.PlanHash!=reversed.PlanHash,"manifest order bound");
  modes.Clear();inventory.Clear();calls=0;closed=0;closeFailure=true;var cleanup=PlcBatchDocumentImportPolicy.Run(Contexts(names),false,p.PlanHash,true,projectFile);Check(cleanup.Status=="unknown"&&cleanup.RequiresSessionReset&&cleanup.Items.All(x=>x.Status=="succeeded")&&calls==3,"cleanup after native preserves outcomes and poisons");BatchDocumentImportContract.Validate(JsonSerializer.SerializeToNode(cleanup),Request(false,p.PlanHash));
  inventory.Clear();calls=0;try{Preview(names);Check(false,"cleanup preview refused");}catch(IOException){Check(calls==0,"cleanup preview zero native");}closeFailure=false;
  inventory.Clear();calls=0;closed=0;var boundary=Contexts(names);int checks=0;boundary[0].Recheck=()=>{if(++checks==2)throw new ArgumentException("pre-native change");};Reject(()=>PlcBatchDocumentImportPolicy.Run(boundary,false,p.PlanHash,true,projectFile),"final preflight rejection");Check(calls==0&&closed==3,"final preflight zero native");
  inventory.Clear();calls=0;closed=0;boundary=Contexts(names);checks=0;boundary[0].Recheck=()=>{if(++checks==4)throw new ArgumentException("later change");};var stopped=PlcBatchDocumentImportPolicy.Run(boundary,false,p.PlanHash,true,projectFile);Check(calls==1&&stopped.RequiresSessionReset&&stopped.Items[0].Status=="succeeded"&&stopped.Items[1].Status=="not-attempted","later preflight preserves not attempted");BatchDocumentImportContract.Validate(JsonSerializer.SerializeToNode(stopped),Request(false,p.PlanHash));
  var host=new WorkerOutcomeState();host.AcceptResult("ImportBlocksFromDocuments",Request(false,p.PlanHash),JsonSerializer.SerializeToNode(stopped));Check(host.Poisoned,"unknown batch poisons whole host");bool unusable=false;try{host.RequireUsable();}catch(InvalidOperationException){unusable=true;}Check(unusable,"all later host operations blocked");
  var altered=JsonSerializer.SerializeToNode(ok)!.AsObject();var changed=altered["Items"]![0]!["Outcome"]!.AsObject();changed["Inventory"]=new JsonArray("fabricated-before-first-call");changed["PlanHash"]=DocumentImportContract.PlanHash(changed);bool rejectedInventory=false;try{BatchDocumentImportContract.Validate(altered,Request(false,p.PlanHash));}catch(InvalidDataException){rejectedInventory=true;}Check(rejectedInventory,"host rejects forged inventory with recomputed item hash");
  var forged=JsonSerializer.SerializeToNode(ok)!.AsObject();forged["Items"]![1]!["Status"]="not-attempted";bool refused=false;try{BatchDocumentImportContract.Validate(forged,Request(false,p.PlanHash));}catch(InvalidDataException){refused=true;}Check(refused,"host rejects forged continuation");
 }
 private sealed class Tracking(byte[] bytes,Action closed):MemoryStream(bytes,false){protected override void Dispose(bool disposing){if(disposing)closed();base.Dispose(disposing);}}
}
