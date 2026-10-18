using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters;
using Xunit;

namespace TiaMcp.Engine.Tests
{
    public sealed class BatchReplacementPolicyTests
    {

        // Both host fixtures execute this production policy; every backup/import callback is observed.
        public sealed class AdmissionFixture : IDisposable
        {
            private readonly string folder = Path.GetFullPath(Path.Combine("bin-build/P6-68/apply-admission", Guid.NewGuid().ToString("N")));
            private readonly string scenario;
            private readonly bool program;
            public readonly List<string> Calls = new List<string>();
            private readonly PlcBatchImportObject target = new PlcBatchImportObject { Name = "A", Kind = "FC", Number = 1 };
            public AdmissionFixture(string scenario, bool program)
            {
                this.scenario=scenario;this.program=program; Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"A.xml"),"<Document><Engineering version=\"V19\"/><SW.Blocks.FC><AttributeList><Name>A</Name><Number>1</Number><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>");
                target.BackupBlocker=scenario.Contains("inconsistent") ? "inconsistent" : scenario.Contains("protected") ? "know-how-protected" : scenario.Contains("unknown-consistency") ? "unknown-consistency" : "";
            }
            private PlcBatchImportRequest Request() => new PlcBatchImportRequest { Release="19",Project="C:/fixture.ap19",Software="CPU/PLC_1",Directory=folder,Program=program,Overwrite=true };
            public JsonObject Arguments()
            {
                var preview=PlcBatchImportPolicy.Run(Request(),new[]{target},()=>throw new Exception("Preview recheck"),(_,_)=>throw new Exception("Preview import"));
                var args=new JsonObject { ["softwarePath"]="CPU/PLC_1", [program ? "sourceDir" : "dir"]=folder, [program ? "blockGroupPath" : "groupPath"]="", ["overwrite"]=true,
                    ["dryRun"]=false,["confirm"]=true,["expectedProjectFile"]="C:/fixture.ap19",["expectedPlanHash"]=scenario.Contains("stale") ? new string('0',64) : preview.PlanHash,["importOrder"]=new JsonArray("A.xml") };
                if(scenario=="confirm") args.Remove("confirm");
                if(scenario=="hash") args.Remove("expectedPlanHash");
                if(scenario=="malformed-hash") args["expectedPlanHash"]=new string('z',64);
                if(scenario=="project") args["expectedProjectFile"]="C:/other.ap19";
                if(scenario=="order") args["importOrder"]=new JsonArray("Missing.xml");
                if(scenario=="compile") args["compileAfter"]=true;
                if(scenario=="continue") args["stopOnImportFailure"]=false;
                if(scenario=="technology") args["technologyFolderPath"]="TO";
                return args;
            }
            public PlcBatchImportResult Execute(JsonObject args)
            {
                var r=Request();r.DryRun=(bool?)args["dryRun"] ?? true;r.Confirm=(bool?)args["confirm"] ?? false;
                r.ExpectedHash=(string?)args["expectedPlanHash"] ?? "";r.ExpectedProject=(string?)args["expectedProjectFile"] ?? "";
                r.Order=args["importOrder"]?.Deserialize<string[]>() ?? Array.Empty<string>();
                r.CompileAfter=(bool?)args["compileAfter"] ?? false;r.StopOnImportFailure=(bool?)args["stopOnImportFailure"] ?? true;r.TechnologyGroup=(string?)args["technologyFolderPath"] ?? "";
                IEnumerable<PlcBatchImportObject> Inventory() { if(scenario=="inventory-io") throw new IOException("Inventory cannot be observed.");yield return target; }
                return PlcBatchImportPolicy.Run(r,Inventory(),()=>{ if(scenario=="recheck-io") throw new IOException("Target recheck unavailable."); },
                    (_,item)=>{ Calls.Add("import");return new[]{item}; },
                    (_,_)=>{ Calls.Add("backup");throw new IOException("Recovery export refused for inconsistent object."); },
                    (_,item)=>{ Calls.Add("restore");return new[]{item}; },
                    ()=>{ Calls.Add("directory");return folder; },
                    ()=>{ if(scenario=="precheck-io") throw new IOException("Recovery location unavailable."); });
            }
            public void Dispose() { Directory.Delete(folder,true); }
        }
    }
}
