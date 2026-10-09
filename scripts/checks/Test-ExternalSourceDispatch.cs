#!/usr/bin/env dotnet
// Compile current production members against managed stand-ins; no TIA or Siemens assembly is loaded.
// Usage: dotnet run scripts/checks/Test-ExternalSourceDispatch.cs -- -WorkDir bin-build/Test-ExternalSourceDispatch
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
var methods = string.Join("\n", new[] { "ImportPlcExternalSource", "GenerateBlocksFromExternalSource" }.Select(n => sources.Member(n, signature: "public void"))) + "\n" + sources.Member("ExternalSourceNameMatches");
var primitives = Repository.ReadSource(Path.Combine(root, "src/Adapters/Native/Plc/PlcDocumentPrimitives.cs"));
var primitiveMethods = new List<string>();
foreach (var declaration in new[] { "Software(SoftwareContainer container)", "Sources(PlcExternalSourceGroup group)", "CreateFromFile(PlcExternalSourceComposition sources, string name, string path)", "Generate(PlcExternalSource source)" })
{
    var matches = primitives.Split('\n').Where(line => line.Contains(declaration) && line.Contains("public static")).ToArray();
    Require(matches.Length == 1, "Missing or ambiguous dispatch primitive: " + declaration); primitiveMethods.Add(matches[0]);
}
var program = """"
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
using Siemens.Engineering.SW.ExternalSources;

namespace TiaMcp.Adapters.Native.Plc {
    public static class PlcDocumentPrimitives {
"""" + string.Join("\n", primitiveMethods) + """"
    }
}
namespace Siemens.Engineering.HW { public abstract class Software { } }
namespace Siemens.Engineering.HW.Features {
    public sealed class SoftwareContainer { public Software Software { get; } = new PlcSoftware(); }
}
namespace Siemens.Engineering.SW.ExternalSources {
    public abstract class PlcExternalSourceGroup { public PlcExternalSourceComposition ExternalSources { get; } = new(); }
    public sealed class PlcExternalSourceSystemGroup : PlcExternalSourceGroup { }
    public sealed class PlcExternalSourceUserGroup : PlcExternalSourceGroup { }
    public sealed class PlcExternalSourceComposition : List<PlcExternalSource> {
        public int Calls; public bool Fail; public bool ReturnNull; public string? Name, Path;
        public PlcExternalSource? CreateFromFile(string name, string path) {
            Calls++; Name=name; Path=path;
            if(Fail) throw new IOException("native fake failed after dispatch");
            if(ReturnNull) return null;
            var source = new PlcExternalSource { Name=name }; Add(source); return source;
        }
    }
    public sealed class PlcExternalSource {
        public string Name { get; set; } = ""; public int Calls; public bool Fail;
        public void GenerateBlocksFromSource() { Calls++; if(Fail) throw new IOException("generation failed"); }
    }
}
namespace Siemens.Engineering.SW {
    public sealed class PlcSoftware : Software { public PlcExternalSourceSystemGroup ExternalSourceGroup { get; } = new(); public PlcExternalSourceUserGroup UserGroup { get; } = new(); }
}
public enum PortalErrorCode { InvalidState, NotFound, InvalidParams, OpennessError }
public sealed class PortalException : Exception { public PortalException(PortalErrorCode code,string text,Exception? inner=null):base(text,inner){} }
internal static class PlcExchangeContract {
    internal static int Issued, Confirmed;
    internal static JsonObject Observation = new();
    internal static void StartWrite(string stage) { Issued++; }
    internal static void ConfirmWrite() { Confirmed++; }
    internal static void Observe(string key, JsonNode? value) { Observation[key] = value; }
}
public sealed class Portal {
    public SoftwareContainer Container { get; } = new();
    private Portal _session => this;
    private bool IsProjectNull()=>false;
    private SoftwareContainer GetSoftwareContainer(string path)=>Container;
    private object TryGetExternalSourceGroupByPath(PlcSoftware plc,string path)=>path=="sub" ? plc.UserGroup : plc.ExternalSourceGroup;
    private static IEnumerable<object> TryGetExternalSourcesCollection(PlcSoftware plc)=>plc.ExternalSourceGroup.ExternalSources;
"""" + methods + """"
}
internal static class Program {
    private static int checks;
    private static void Check(bool value,string label) { if(!value) throw new Exception(label); checks++; }
    private static void Fails(Action action) { try { action(); } catch(PortalException) { checks++;return; } throw new Exception("Expected reported failure"); }
    private static int Main() {
        try { RunChecks(); return 0; }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void RunChecks() {
        var path=Path.Combine(AppContext.BaseDirectory,"source.scl"); File.WriteAllText(path,"FUNCTION Test : Void\nBEGIN\nEND_FUNCTION");
        var p=new Portal(); var plc=(PlcSoftware)p.Container.Software;
        p.ImportPlcExternalSource("PLC", "", path);
        Check(plc.ExternalSourceGroup.ExternalSources.Calls==1,"system group imports once");
        Check(plc.ExternalSourceGroup.ExternalSources.Name=="source.scl","source name argument");
        Check(plc.ExternalSourceGroup.ExternalSources.Path==path,"absolute file argument");
        p.ImportPlcExternalSource("PLC", "sub", path);
        Check(plc.UserGroup.ExternalSources.Calls==1,"user group imports once");
        p.GenerateBlocksFromExternalSource("PLC","source.scl");
        Check(plc.ExternalSourceGroup.ExternalSources[0].Calls==1,"generate normal path");
        plc.UserGroup.ExternalSources.Fail=true;
        Fails(()=>p.ImportPlcExternalSource("PLC","sub",path));
        Check(plc.UserGroup.ExternalSources.Calls==2,"failed import not replayed");
        plc.UserGroup.ExternalSources.Fail=false; plc.UserGroup.ExternalSources.ReturnNull=true;
        Fails(()=>p.ImportPlcExternalSource("PLC","sub",path));
        Check(plc.UserGroup.ExternalSources.Calls==3,"null outcome not replayed");
        plc.ExternalSourceGroup.ExternalSources[0].Fail=true;
        Fails(()=>p.GenerateBlocksFromExternalSource("PLC","source.scl"));
        Check(plc.ExternalSourceGroup.ExternalSources[0].Calls==2,"failed generation not replayed");
        Check(PlcExchangeContract.Issued==6,"every native dispatch recorded once");
        Check(PlcExchangeContract.Confirmed==3,"failed/null outcomes not confirmed");
        Check(PlcExchangeContract.Observation["nativeResult"]==null,"observation does not fabricate native result");
        Check((string?)PlcExchangeContract.Observation["observation"]?["source"]=="native-call-returned","observation source retained");
        Console.WriteLine($"External-source actual method bodies: {checks} passed; managed fakes only.");
    }
}
"""";
return ExtractedChecks.Run(root, work, program, links: ["src/Shared/NativeInputPolicy.cs", "src/Shared/NativePathSelection.cs", "src/Adapters.Contracts/AdapterPreconditionException.cs"]);

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
