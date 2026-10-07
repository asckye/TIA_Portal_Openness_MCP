using TiaMcp.PlcFoundation;
using TiaMcp.LegacyHost;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
internal static class DocumentImportTests
{
    private static readonly string FixtureRoot=Path.Combine(Path.GetTempPath(),"tia-document-import-tests");
    internal static JsonNode Payload(JsonObject args) { var result=JsonSerializer.SerializeToNode(new PlcDocumentImportResult { Release="21",ProjectFile=Path.Combine(FixtureRoot,"Example.ap21"),ProcessId=123,TargetIdentity="session/project/plc/group",SoftwarePath=args["softwarePath"]!.GetValue<string>(),GroupPath=args["groupPath"]!.GetValue<string>(),InputDirectory=args["importPath"]!.GetValue<string>(),DeclaredName=args["fileNameWithoutExtension"]!.GetValue<string>(),CodeSha256=new string('a',64),PlanHash=new string('b',64) })!.AsObject();result["PlanHash"]=DocumentImportContract.PlanHash(result);return result; }
    private sealed class DisposeFailureStream(byte[] bytes):MemoryStream(bytes,false)
    { protected override void Dispose(bool disposing) {base.Dispose(disposing);if(disposing)throw new IOException("Injected cleanup failure.");} }
    internal static void Run(Action<bool,string> Check)
    {
string projectFile=Path.Combine(FixtureRoot,"p.ap21"), documentDirectory=Path.Combine(FixtureRoot,"documents");
void Reject(Action a,string n) {bool rejected=false;try{a();}catch(ArgumentException){rejected=true;}catch(NotSupportedException){rejected=true;}catch(InvalidDataException){rejected=true;}Check(rejected,n);}
string code="{ S7_Optimized := \"TRUE\"; S7_Version := \"0.1\" } DATA_BLOCK \"Demo\" VAR Flag : Bool := true; Count : DInt := -12; END_VAR END_DATA_BLOCK";
Check(PlcDocumentDeclaration.Parse(Encoding.ASCII.GetBytes(code))=="Demo","identity");
Check(PlcDocumentDeclaration.Parse(Encoding.ASCII.GetBytes("//DATA_BLOCK Fake\n"+code+"// DATA_BLOCK Other"))=="Demo","comments ignored");
foreach(var bad in new[]{code+" DATA_BLOCK Other VAR x:Bool; END_VAR END_DATA_BLOCK",code.Replace("DATA_BLOCK \"Demo\"","DATA_BLOCK Demo FB1"),code.Replace("DInt","String"),code.Replace("Count :","Flag :"),code.Replace("S7_Optimized","S7_Safety"),code.Replace("S7_Optimized","S7_Number"),code.Replace("S7_Optimized","S7_Language"),code.Replace("-12","1 + 2"),code.Replace("VAR Flag","(* comment *) VAR Flag"),code.Replace("END_DATA_BLOCK","END_FUNCTION_BLOCK"),code+" \"\"",code.Replace("\"Demo\"","\"De$\"mo\""),code.Replace("true","'true'"),code.Replace("VAR Flag","VAR Flag:Bool; //\n Flag"),code.Replace("DATA_BLOCK","FUNCTION_BLOCK"),code.Replace("Flag : Bool","Flag : Array[0..1] of Bool"),code.Replace("END_VAR","END_VAR BEGIN"),code.Replace("S7_Version := \"0.1\"","S7_Optimized := \"TRUE\"")})Reject(()=>PlcDocumentDeclaration.Parse(Encoding.ASCII.GetBytes(bad)),"reject grammar");
var r=new PlcDocumentImportRequest {Release="21",Project=projectFile,Software="PLC",Group="",Directory=documentDirectory,Name="Demo",ProcessId=1,TargetIdentity="object"};
int calls=0,checks=0;string[] files={Path.Combine(documentDirectory,"Demo.s7dcl")};byte[] input=Encoding.ASCII.GetBytes(code);
PlcDocumentImportNative Native()=>new(){State="Success",Success=true,ExistsVerified=true,Identities=new[]{PlcDocumentImportPolicy.InventoryItem("","Demo","GlobalDB","returned")}};
PlcDocumentImportResult Run(Func<PlcDocumentImportNative>? native=null,Func<IEnumerable<string>>? inventory=null)=>PlcDocumentImportPolicy.Run(r,()=>files,_=>new MemoryStream(input,false),inventory??(()=>Array.Empty<string>()),_=>false,()=>checks++,()=>{calls++;return (native??Native)();});
var preview=Run();Check(calls==0&&checks==0&&!preview.Attempted&&!preview.MayHaveChanged,"preview nonmutating");
r.DryRun=false;r.Confirm=true;r.ExpectedProject=r.Project;r.ExpectedHash=preview.PlanHash;
Check(Run().Status=="imported"&&calls==1,"single successful import");
foreach(var kind in new[]{"null","exception","partial","failure","zero","extra","wrong","readback"}) {
 var result=Run(()=>{var n=Native();if(kind=="null")return null!;if(kind=="exception")throw new IOException();if(kind=="partial"||kind=="failure"){n.State=kind;n.Success=false;}if(kind=="zero")n.Identities=Array.Empty<string>();if(kind=="extra")n.Identities=new[]{"x","y"};if(kind=="wrong")n.Identities=new[]{"wrong"};if(kind=="readback")n.ExistsVerified=false;return n;});
 Check(result.Status=="unknown"&&result.Attempted&&result.MayHaveChanged&&result.RequiresSessionReset&&!result.ExistsVerified,"uncertain "+kind);
}
var before=calls;r.ExpectedHash=new string('0',64);Reject(()=>Run(),"stale plan");Check(calls==before,"stale zero calls");r.ExpectedHash=preview.PlanHash;
r.ExpectedProject="other";Reject(()=>Run(),"wrong project");r.ExpectedProject=r.Project;
r.Name="Other";files=new[]{Path.Combine(documentDirectory,"Other.s7dcl")};Reject(()=>Run(),"filename is not identity");r.Name="Demo";files=new[]{Path.Combine(documentDirectory,"Demo.s7dcl")};
r.Overwrite=true;Reject(()=>Run(),"override rejected");r.Overwrite=false;
r.Release="20";Reject(()=>Run(),"v20 pair required");r.Release="21";
r.DryRun=true;Reject(()=>Run(inventory:()=>Enumerable.Repeat("item",4097)),"inventory bound");
Reject(()=>PlcDocumentImportPolicy.Read(new MemoryStream(new byte[PlcDocumentImportPolicy.MaximumBytes+1])),"input bound");


// Every plan-affecting identity field changes the plan; failed reviews never dispatch.
r.DryRun=true;
var originalHash=Run().PlanHash;
foreach(var mutate in new Action[]{()=>r.ProcessId++,()=>r.TargetIdentity+="x",()=>r.Project+="x",()=>r.Software+="x",()=>r.Group="Group"}) { mutate();Check(Run().PlanHash!=originalHash,"Document plan binds exact target identity");originalHash=Run().PlanHash; }
r.Group="";r.Project=projectFile;r.Software="PLC";r.ProcessId=1;r.TargetIdentity="object";
var inventoryPlan=Run(inventory:()=>new[]{"item"});Check(inventoryPlan.PlanHash!=Run().PlanHash,"Document plan binds complete inventory");
input=Encoding.ASCII.GetBytes(code+" //changed bytes");Check(Run().PlanHash!=preview.PlanHash,"Document plan binds unchanged code bytes including comments");input=Encoding.ASCII.GetBytes(code);
files=new[]{Path.Combine(documentDirectory,"Demo.s7dcl"),Path.Combine(documentDirectory,"Demo.s7res")};Check(Run().ResourceSha256==PlcDocumentImportPolicy.Hash(input),"Optional V21 resources remain hashed unchanged");r.Release="20";Check(Run().Status=="planned","V20 pair admitted");r.Release="21";files=new[]{Path.Combine(documentDirectory,"Demo.s7dcl")};
foreach(var release in new[]{"14sp1","15.1","16","17","18","19","V20","22"}) {r.Release=release;Reject(()=>Run(),"Document release gate "+release);}r.Release="21";
if(!OperatingSystem.IsWindows())Reject(()=>PlcDocumentImportPolicy.Scan(r),"Document native path scan refuses non-Windows");
r.DryRun=false;r.ExpectedHash=preview.PlanHash;
int reads=0;var countBefore=calls;Reject(()=>Run(inventory:()=>++reads==1?Array.Empty<string>():new[]{"changed"}),"Inventory changed between preview and apply");Check(calls==countBefore,"Changed inventory invokes no native call");
var request=new JsonObject { ["softwarePath"]="PLC",["groupPath"]="",["importPath"]=documentDirectory,["fileNameWithoutExtension"]="Demo",["dryRun"]=true };
JsonObject Wire(PlcDocumentImportResult x)=>JsonSerializer.SerializeToNode(x)!.AsObject();
DocumentImportContract.Validate(Wire(preview),request);
foreach(var field in new[]{"Executed","Attempted","MayHaveChanged","RequiresSessionReset","ExistsVerified","ContentVerified"}) {var bad=Wire(preview);bad[field]=true;Reject(()=>DocumentImportContract.Validate(bad,request),"Forged preview "+field);}
foreach(var field in new[]{"ProjectFile","TargetIdentity","SoftwarePath","DeclaredName","CodeSha256","PlanHash","NativeOptions","Kind","Language","Policy","Validation","InstalledUpdate","Recovery","Compilation","Save","Download"}) {var bad=Wire(preview);bad[field]="";Reject(()=>DocumentImportContract.Validate(bad,request),"Missing evidence "+field);}
var valid=Run();request["dryRun"]=false;request["confirm"]=true;request["expectedProjectFile"]=r.Project;request["expectedPlanHash"]=preview.PlanHash;DocumentImportContract.Validate(Wire(valid),request);
foreach(var field in new[]{"Attempted","MayHaveChanged","ExistsVerified"}) {var bad=Wire(valid);bad[field]=false;Reject(()=>DocumentImportContract.Validate(bad,request),"Forged successful outcome "+field);}
var unknown=Run(()=>throw new IOException());DocumentImportContract.Validate(Wire(unknown),request);
var state=new WorkerOutcomeState();state.AcceptResult("ImportFromDocuments",request,Wire(unknown));Check(state.Poisoned,"Document unknown outcome poisons host session");
var forged=Wire(unknown);forged["MayHaveChanged"]=false;Reject(()=>DocumentImportContract.Validate(forged,request),"Unknown cannot claim no mutation");
foreach(var field in new[]{"softwarePath","groupPath","importPath","fileNameWithoutExtension","expectedProjectFile","expectedPlanHash"}) {var bad=request.DeepClone().AsObject();bad[field]="other";Reject(()=>DocumentImportContract.Validate(Wire(valid),bad),"Request identity mismatch "+field);}
foreach(var path in new[]{@"C:\dir.",@"C:\dir ",@"C:\dir:ads",@"C:\CON",@"C:\dir\..\other",@"C:\dir\\sub",@"C:\dir\",@"\\server\share",@"relative"})Reject(()=>PlcDocumentImportPolicy.ValidateDirectory(path),"Document directory rejects Win32 ambiguity "+path);
PlcDocumentImportPolicy.ValidateDirectory(@"C:/Documents");
PlcDocumentImportPolicy.ValidateDirectory(@"C:\Documents");Check(true,"Canonical local Windows directory admitted lexically");
var overflow=Encoding.ASCII.GetBytes(code.Replace("-12","18446744073709551616"));Check(PlcDocumentDeclaration.Parse(overflow)=="Demo","Lexical admission does not certify native integer range validity");
var cleanup=PlcDocumentImportPolicy.Run(r,()=>files,_=>new DisposeFailureStream(input),()=>Array.Empty<string>(),_=>false,()=>{},Native);
Check(cleanup.Status=="unknown" && cleanup.RequiresSessionReset && cleanup.Attempted && cleanup.MayHaveChanged && !cleanup.ExistsVerified,"Post-native cleanup failure returns poisoned uncertain result");
var fakeEvidence=Wire(valid);fakeEvidence["TargetIdentity"]="different-target";Reject(()=>DocumentImportContract.Validate(fakeEvidence,request),"Plan hash recomputed against response evidence");
var definition=FoundationTools.Definitions.Single(x=>x.Name=="ImportFromDocuments");Check(definition.ResponseMember=="DocumentImport"&&TiaMcp.PlcWorker.WorkerOperations.Names.Contains("ImportFromDocuments"),"Document route explicitly registered");
    }
}
