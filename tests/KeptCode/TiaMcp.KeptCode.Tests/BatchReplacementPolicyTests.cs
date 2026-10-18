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
        [Theory]
        [InlineData(false,"inconsistent-stale")][InlineData(true,"inconsistent-stale")]
        [InlineData(false,"stale")][InlineData(true,"stale")]
        [InlineData(false,"precheck-io")][InlineData(true,"precheck-io")]
        [InlineData(false,"inventory-io")][InlineData(true,"inventory-io")]
        public void Apply_admission_is_typed_before_recovery_or_native_callbacks(bool program,string scenario)
        {
            using var fixture=new AdmissionFixture(scenario,program);
            var refusal=Assert.Throws<AdapterPreconditionException>(()=>fixture.Execute(fixture.Arguments()));
            Assert.Equal(scenario.Contains("stale"),refusal.IsArgument);Assert.Empty(fixture.Calls);
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void Create_only_first_recheck_failure_is_typed_without_import_or_session_poisoning(bool program)
        {
            using var fixture=new AdmissionFixture("stale",program);
            var args=fixture.Arguments();
            var request=new PlcBatchImportRequest { Release="19",Project="C:/fixture.ap19",ExpectedProject="C:/fixture.ap19",Software="CPU/PLC_1",Directory=(string)args[program ? "sourceDir" : "dir"]!,Program=program };
            int calls=0;
            var preview=PlcBatchImportPolicy.Run(request,Array.Empty<PlcBatchImportObject>(),()=>{},(_,item)=>new[]{item});
            request.DryRun=false;request.Confirm=true;request.Order=new[]{"A.xml"};request.ExpectedHash=preview.PlanHash;
            var refusal=Assert.Throws<AdapterPreconditionException>(()=>PlcBatchImportPolicy.Run(request,Array.Empty<PlcBatchImportObject>(),()=>throw new IOException("Target became unavailable."),(_,item)=>{calls++;return new[]{item};}));
            Assert.False(refusal.IsArgument);Assert.Contains("before any import",refusal.Message);Assert.Equal(0,calls);
        }
        [Theory]
        [InlineData(false)][InlineData(true)]
        public void Only_overwrite_collisions_inspect_properties_and_unknown_consistency_blocks_before_callbacks(bool program)
        {
            string folder = Path.GetFullPath(Path.Combine("bin-build/P6-68/batch-properties", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "A.xml"), "<Document><Engineering version=\"V17\"/><SW.Blocks.FC><AttributeList><Name>A</Name><Number>1</Number><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>");
                var target = new PlcBatchImportObject { Name = "A", Kind = "FC", Number = 1 };
                var unrelated = new PlcBatchImportObject { Name = "Unrelated", Kind = "FC", Number = 99 };
                var request = new PlcBatchImportRequest { Release = "17", Project = "C:/fixture.ap17", Software = "CPU/PLC", Directory = folder, Program = program, Overwrite = true };
                var inspected = new List<string>(); int mutations = 0;
                PlcBatchImportResult Run(IEnumerable<PlcBatchImportObject> inventory) => PlcBatchImportPolicy.Run(request, inventory,
                    () => mutations++, (_, item) => { mutations++; return new[] { item }; },
                    (_, _) => mutations++, (_, item) => { mutations++; return new[] { item }; }, () => { mutations++; return folder; }, () => mutations++,
                    item => { inspected.Add(item.Name); throw new IOException("Native property getter refused"); });
                var result = Run(new[] { target, unrelated });
                Assert.Equal(new[] { "A" }, inspected); Assert.Equal("replace-blocked: unknown-consistency", result.Items[0].Action); Assert.Equal(0, mutations);
                request.DryRun = false; request.Confirm = true; request.ExpectedProject = request.Project; request.ExpectedHash = result.PlanHash; request.Order = new[] { "A.xml" };
                Assert.False(Assert.Throws<AdapterPreconditionException>(()=>Run(new[] { target, unrelated })).IsArgument); Assert.Equal(0, mutations);
                request.DryRun = true; request.Overwrite = false; inspected.Clear();
                Assert.Throws<AdapterPreconditionException>(() => Run(new[] { target, unrelated })); Assert.Empty(inspected);
                Assert.Equal("create", Run(new[] { unrelated }).Items[0].Action); Assert.Empty(inspected);
            }
            finally { Directory.Delete(folder, true); }
        }
    }
}
