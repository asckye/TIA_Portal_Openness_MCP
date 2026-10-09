#!/usr/bin/env dotnet
// Compile current production members against managed stand-ins; no TIA or Siemens assembly is loaded.
// Usage: dotnet run scripts/checks/Test-FoundationProxyIdentity.cs -- -WorkDir bin-build/Test-FoundationProxyIdentity
#:property PublishAot=false
#:property NuGetAudit=false
#:project ../../build-tools/common/TiaMcp.BuildCommon/TiaMcp.BuildCommon.csproj

using System.Text.RegularExpressions;
using TiaMcp.BuildCommon;

var options = args.ToList();
var root = FindRoot();
var work = Take(options, "-WorkDir", "--work-dir");
if (options.Count != 0) throw new ArgumentException("Unexpected argument: " + options[0]);
var sources = new EngineSources(root);
work = Required(work);
var batch = Repository.ReadSource(Path.Combine(root, "src/Adapters/Native/Plc/PlcBatchImport.cs"));
var start = batch.IndexOf("        private static string BatchReturnedGroup(", StringComparison.Ordinal);
var end = batch.IndexOf("        private IEnumerable<PlcBatchImportObject>", start, StringComparison.Ordinal);
Require(start >= 0 && end > start, "batch member boundaries");
var program = """"
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters;
using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;

namespace Siemens.Engineering {
    public interface IEngineeringObject { object Parent { get; } }
    public class Proxy {
        public string Id;
        public Proxy(string id) { Id=id; }
        public override bool Equals(object? other) => other is Proxy p && p.GetType()==GetType() && p.Id==Id;
        public override int GetHashCode() => Id.GetHashCode();
    }
}
namespace Siemens.Engineering.SW.ExternalSources {
    public sealed class PlcExternalSourceGroup : Siemens.Engineering.Proxy { public PlcExternalSourceGroup(string id):base(id) { } }
    public sealed class PlcExternalSource : Siemens.Engineering.Proxy, Siemens.Engineering.IEngineeringObject {
        public string Name; public PlcExternalSource(string name):base(name) { Name=name; }
        public object Parent => new PlcExternalSourceGroup("root");
    }
}
namespace Siemens.Engineering.SW {
    public sealed class PlcSoftware : Siemens.Engineering.Proxy {
        public string Root;
        public PlcSoftware(string id,string root):base(id) { Root=root; }
    }
}
namespace TiaMcp.Adapters.Native.Plc {
    using Siemens.Engineering.SW;
    using Siemens.Engineering.SW.ExternalSources;
    public static class PlcDocumentPrimitives {
        public static readonly List<string> Names=new(); public static int Deletes;
        public static PlcExternalSourceGroup ExternalSourceGroup(PlcSoftware plc) => new(plc.Root);
        public static IEnumerable<PlcExternalSource> Sources(PlcExternalSourceGroup root) => Names.Select(n=>new PlcExternalSource(n));
        public static string Name(PlcExternalSource source) => source.Name;
        public static PlcExternalSource? Find(IEnumerable<PlcExternalSource> sources,string name) => sources.SingleOrDefault(s=>s.Name==name);
        public static void Delete(PlcExternalSource source) { Deletes++; Names.Remove(source.Name); }
    }
    public static class PlcBlockPrimitives { public static object Parent(Siemens.Engineering.IEngineeringObject item)=>item.Parent; }
}
namespace TiaMcp.Adapters {
    internal static class PlcLifecyclePolicy { internal static void RequireLocalSessionExecution(bool local,bool preview) { if(local) throw new AdapterPreconditionException("Local session refused.",isArgument:false); } }
    public sealed partial class PlcFoundationEngine {
        private sealed class Lifecycle { internal bool IsLocalSession=false; internal int? ProcessId=123; }
        private readonly Lifecycle lifecycle=new();
        public string ReleaseKey => "14sp1";
        public string Change=""; public int Reads, OfflineChecks;
        private readonly int owner=Environment.CurrentManagedThreadId;
        public string ProjectPath="C:\\Projects\\Demo.ap14";
        private sealed class ProjectProxy { public FileInfo Path; public ProjectProxy(string path) { Path=new FileInfo(path); } }
        private ProjectProxy Project() => new(ProjectPath);
        private void RequireProjectIdentity(string expected) { if(Environment.CurrentManagedThreadId!=owner) throw new Exception("wrong thread"); MutationIdentityPolicy.RequireSameProject(expected,ProjectPath); }
        private PlcReadCandidate<Siemens.Engineering.SW.PlcSoftware> ReadSelection(string path) {
            Reads++; string id=Reads>1 && Change=="plc"?"other":"plc";
            string context=Reads>1 && Change=="context"?"other":"context";
            string root=Reads>1 && Change=="root"?"other":"root";
            string exact=Reads>1 && Change=="path"?"devices/Other/PLC_1":"devices/Station/PLC_1";
            return PlcReadPathPolicy.Select(new[]{new PlcReadCandidate<Siemens.Engineering.SW.PlcSoftware> {
                Value=new(id,root),Context=new Siemens.Engineering.Proxy(context),ExactPath=exact,Host="PLC_1",Device="Station" }},path);
        }
        private void RequireTargetOffline(PlcReadCandidate<Siemens.Engineering.SW.PlcSoftware> selected) { OfflineChecks++; if(Change=="online") throw new AdapterPreconditionException("Target online.","softwarePath",false); }
        public static string ReturnedGroup(Siemens.Engineering.IEngineeringObject source,object root)=>BatchReturnedGroup(source,root,"exact/group");
        BATCH_RETURNED_GROUP
    }
}
internal static class Program {
    private static int checks;
    private static void Check(bool value,string label) { if(!value) throw new Exception(label); checks++; }
    private static void Refused(Action call,string label) { try { call(); } catch(ArgumentException) { checks++; return; } throw new Exception("Accepted "+label); }
    private static void Main(string[] args) {
        string file=Path.GetFullPath(Path.Combine(args[0],"Pump.scl")); File.WriteAllText(file,"FUNCTION Pump : Void\nEND_FUNCTION\n",new System.Text.UTF8Encoding(false));
        var engine=new PlcFoundationEngine();
        var plan=engine.PlanPlcExternalSourceImport("PLC_1","",file.Replace('\\','/'),file);
        Check(plan.SoftwarePath=="devices/Station/PLC_1" && plan.FilePath==file,"alias and file normalized before plan binding");
        Check(engine.Reads>=2 && engine.OfflineChecks>=2,"fresh equal PLC/context/root proxies survive every recheck");
        Check(plan.PlanHash==engine.PlanPlcExternalSourceImport("devices/Station/PLC_1","",file,file).PlanHash,"exact and alias bind identical plan");
        foreach(string change in new[]{"plc","context","root","path","online"}) {
            var changed=new PlcFoundationEngine { Change=change };
            Refused(()=>changed.PlanPlcExternalSourceImport("PLC_1","",file,file),change);
            Check(TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives.Deletes==0,"no mutation on identity refusal");
        }
        Refused(()=>new PlcFoundationEngine().PlanPlcExternalSourceImport("WrongPLC","",file,file),"wrong short name");
        var ambiguous=new[]{new PlcReadCandidate<object> {ExactPath="devices/A/PLC_1",Host="PLC_1"},new PlcReadCandidate<object> {ExactPath="devices/B/PLC_1",Host="PLC_1"}};
        Refused(()=>PlcReadPathPolicy.Select(ambiguous,"PLC_1"),"ambiguous short name");
        var missing=engine.DeletePlcExternalSource("PLC_1","","Missing.scl");
        Check(missing.Status=="not-found-not-deleted" && missing.SoftwarePath==plan.SoftwarePath,"missing deletion preview retains exact alias target");
        TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives.Names.Add("Existing.scl");
        var preview=engine.DeletePlcExternalSource("PLC_1","","Existing.scl");
        var deleted=engine.DeletePlcExternalSource("devices/Station/PLC_1","","Existing.scl",false,preview.PlanHash,true,engine.ProjectPath);
        Check(deleted.Deleted && TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives.Deletes==1,"one delete verified across fresh equal source proxies");
        var source=new Siemens.Engineering.SW.ExternalSources.PlcExternalSource("Existing.scl");
        Check(PlcFoundationEngine.ReturnedGroup(source,new Siemens.Engineering.SW.ExternalSources.PlcExternalSourceGroup("root"))=="exact/group","batch returned parent uses value identity");
        Check(PlcFoundationEngine.ReturnedGroup(source,new Siemens.Engineering.SW.ExternalSources.PlcExternalSourceGroup("other")).StartsWith("../"),"batch returned wrong parent still refused");
        Console.WriteLine($"Foundation fresh-proxy regression: {checks} passed; 0 failed; native TIA NOT RUN.");
    }
}
"""".Replace("BATCH_RETURNED_GROUP", batch[start..end]);
return ExtractedChecks.Run(root, work, program, links: ["src/Adapters/Native/Plc/PlcExternalSourceImport.cs", "src/Adapters/Native/Plc/PlcExternalSourceImportPolicy.cs", "src/Adapters/Native/Plc/PlcExternalSourceDelete.cs", "src/Adapters/Native/Plc/PlcExternalSourceDeletePolicy.cs", "src/Adapters/Native/Plc/PlcReadPathPolicy.cs", "src/Adapters/Native/Session/MutationIdentityPolicy.cs", "src/Adapters.Contracts/AdapterPreconditionException.cs", "src/Adapters.Contracts/ExternalSourceImportContract.cs", "src/Adapters.Contracts/PlcExternalSourceImportContracts.cs", "src/Adapters.Contracts/PlcExternalSourceDeleteContracts.cs", "src/Adapters.Contracts/IWorkerOperationReply.cs", "src/Shared/NativePathSelection.cs", "src/Shared/NativeInputPolicy.cs"]);

static string FindRoot() => Repository.FindRoot();
static string? Take(List<string> values, params string[] names)
{
    var index = values.FindIndex(names.Contains);
    if (index < 0) return null;
    if (index + 1 >= values.Count) throw new ArgumentException("Option requires a value: " + values[index]);
    var value = values[index + 1]; values.RemoveRange(index, 2); return value;
}
static string Required(string? value) => value ?? throw new ArgumentException("-WorkDir is required");
static void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
