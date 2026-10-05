using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;

// Runs only our API-independent diagnostic runtime. Workers and Siemens modules
// are inspected as PE files; they are never loaded or executed.
try {
if(args.Length!=2) throw new ArgumentException("Usage: <worker bin root> <new writable diagnostics directory>");
string output=Path.GetFullPath(args[0]), journal=Path.GetFullPath(args[1]);
if(Directory.Exists(journal)) throw new ArgumentException("Use a new directory so existing evidence is preserved.");
Directory.CreateDirectory(journal);
Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY",journal);
Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY",Path.Combine(journal,"data"));
InvocationJournalGoldenTests.Run(journal);
int checks=0;
void Check(bool condition,string name){if(!condition)throw new Exception(name);checks++;}
Check(!Assembly.GetExecutingAssembly().GetReferencedAssemblies().Any(a=>a.Name!.StartsWith("Siemens",StringComparison.Ordinal)),"diagnostic runtime has no Siemens dependency");
var receiver=new ExplosiveToString();
string correlation=InvocationJournal.Begin("diagnostic-test");
var token=NativeCallDiagnostics.Enter("site-a","Native::Read()","direct",receiver,null,null);
NativeCallDiagnostics.Returned(token,new List<int>{1,2,3});
var before=NativeCallDiagnostics.Enter("site-b","Native::Fail()","direct",receiver,null,null);
NativeCallDiagnostics.Threw(before,new InvalidOperationException("DO_NOT_LOG_PASSWORD_OR_EXCEPTION_MESSAGE"));
Check(receiver.Calls==0,"diagnostics never calls receiver ToString");
Check(NativeCallDiagnostics.Enter("non-native","Bcl::Read()","interface",new object(),null,null)==null,"unobserved non-native calls ignored");
var list=new List<int>{4,5};
var seed=NativeCallDiagnostics.Enter("site-list","Native::List()","direct",receiver,null,null);
NativeCallDiagnostics.Returned(seed,list);
var wrapped=NativeCallDiagnostics.EnumerateGeneric(list)!;
Check(!ReferenceEquals(wrapped,list),"observed enumeration wrapped");
Check(wrapped.SequenceEqual(list),"wrapped enumeration preserves values");
Check(wrapped is IList<int>,"list interface preserved");
var unobserved=new List<int>{7};
Check(ReferenceEquals(NativeCallDiagnostics.EnumerateGeneric(unobserved),unobserved),"unobserved enumeration unchanged");
IEnumerable<int> sequence=Enumerable.Range(4,2).Where(_=>true);
var sequenceSeed=NativeCallDiagnostics.Enter("site-sequence","Native::Sequence()","direct",receiver,null,null);
NativeCallDiagnostics.Returned(sequenceSeed,sequence);
int sum=0;
foreach(var value in NativeCallDiagnostics.EnumerateGeneric(sequence)!) sum+=value;
Check(sum==9,"reference enumerator values preserved");
var files=Directory.GetFiles(journal,"calls-*.jsonl");
Check(files.Length==1,"one process journal");
var text=File.ReadAllText(files[0]);
Check(!text.Contains("DO_NOT_LOG_PASSWORD_OR_EXCEPTION_MESSAGE"),"exception message redacted");
var rows=File.ReadAllLines(files[0]).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
Check(rows.All(r=>(string?)r["id"]==correlation),"journal correlation retained");
var nativeRows=rows.Where(r=>r["nativeCallId"]!=null).ToArray();
foreach(var group in nativeRows.GroupBy(r=>(string?)r["nativeCallId"])){
    Check(group.Count(r=>(string?)r["phase"]=="BEFORE")==1,"single BEFORE");
    Check(group.Count(r=>(string?)r["phase"] is "RETURNED" or "THREW")==1,"single completion");
}
Check(nativeRows.Any(r=>(string?)r["phase"]=="THREW" && (string?)r["exceptionType"]==typeof(InvalidOperationException).FullName),"exception type retained");
Check(nativeRows.Any(r=>((string?)r["member"])?.Contains("MoveNext")==true),"enumerator movement journaled");
Environment.SetEnvironmentVariable("TIA_MCP_DIAGNOSTICS_DIRECTORY","relative-path-rejected");
var badPath=NativeCallDiagnostics.Enter("site-badpath","Native::Read()","direct",receiver,null,null);
NativeCallDiagnostics.Returned(badPath,null);
Check((long)JsonNode.Parse(InvocationJournal.Health().ToString())!["failedWrites"]!>0,"journal failure observable without changing operation");

foreach(string key in new[]{"14sp1","15.1","16","17","18","19","20","21"}){
    string tfm="net48";
    string folder=Path.Combine(output,key,"Release",tfm);
    using var workerStream=File.OpenRead(Path.Combine(folder,$"TiaMcp.PlcWorker.{key}.exe"));
    using var workerPe=new PEReader(workerStream);
    var worker=workerPe.GetMetadataReader();
    var workerRefs=worker.AssemblyReferences.Select(h=>worker.GetString(worker.GetAssemblyReference(h).Name)).ToArray();
    Check(workerRefs.Count(n=>n.StartsWith("TiaMcp.Adapter.",StringComparison.Ordinal))==1 && workerRefs.Contains("TiaMcp.Adapter."+key),key+" exactly one adapter reference");
    Check(!workerRefs.Any(n=>n.StartsWith("Siemens",StringComparison.Ordinal)||n.StartsWith("TiaMcp.PlcFoundation",StringComparison.Ordinal)),key+" worker has no direct Siemens/old Foundation assembly reference");
    using var adapterStream=File.OpenRead(Path.Combine(folder,$"TiaMcp.Adapter.{key}.dll"));
    using var adapterPe=new PEReader(adapterStream);
    var adapter=adapterPe.GetMetadataReader();
    Check(!adapter.AssemblyReferences.Any(h=>adapter.GetString(adapter.GetAssemblyReference(h).Name) is "Newtonsoft.Json" or "System.Text.Json"),key+" adapter has no JSON library dependency");
    Check(!adapter.AssemblyReferences.Any(h=>adapter.GetString(adapter.GetAssemblyReference(h).Name)=="TiaMcp.Logic"),key+" adapter has no host policy dependency");
    Check(!workerRefs.Contains("TiaMcp.Logic"),key+" worker has no host policy dependency");
    Check(!workerRefs.Contains("Newtonsoft.Json") && !File.Exists(Path.Combine(folder,"Newtonsoft.Json.dll")),key+" worker payload has no Newtonsoft dependency");
    var types=adapter.TypeDefinitions.Select(h=>adapter.GetTypeDefinition(h)).ToArray();
    var engine=types.Single(t=>adapter.GetString(t.Name)=="PlcFoundationEngine");
    Check(engine.GetMethods().Any(h=>adapter.GetString(adapter.GetMethodDefinition(h).Name)=="ReadExternalSourceNames"),key+" latest external-source operation retained");
    Check(types.Any(t=>adapter.GetString(t.Name)=="NativeCallDiagnostics"),key+" runtime present");
    Check(types.Any(t=>adapter.GetString(t.Name)=="GeneratedNativeCalls"),key+" generated wrappers present");
    Check(adapter.ManifestResources.Any(h=>adapter.GetString(adapter.GetManifestResource(h).Name)=="TiaMcp.NativeCallCoverage.json"),key+" coverage embedded");
    var native=adapter.AssemblyReferences.Select(h=>adapter.GetAssemblyReference(h)).Where(a=>adapter.GetString(a.Name).StartsWith("Siemens.Engineering",StringComparison.Ordinal)).ToArray();
    string expected=key=="14sp1"?"14.0.1.0":key=="15.1"?"15.1.0.0":key+".0.0.0";
    Check(native.Length==(key=="21"?3:1),key+" expected native module count");
    Check(native.All(a=>a.Version.ToString()==expected),key+" exact native reference versions");
    Check(native.All(a=>Convert.ToHexString(adapter.GetBlobBytes(a.PublicKeyOrToken)).ToLowerInvariant()==(key=="21"?"29bfe5fdf4ba5d3b":"d29ec89bac048f84")),key+" exact native reference tokens");
    Check(!Directory.GetFiles(folder,"Siemens*.dll").Any(),key+" no copied Siemens DLL");
}
Console.WriteLine($"PASS: {checks} portable diagnostics and PE isolation checks; native NOT RUN.");
return 0;
} catch(Exception ex) { Console.Error.WriteLine("FAIL: "+ex); return 1; }
sealed class ExplosiveToString { public int Calls; public override string ToString(){Calls++;throw new Exception("Must never be called");} }
