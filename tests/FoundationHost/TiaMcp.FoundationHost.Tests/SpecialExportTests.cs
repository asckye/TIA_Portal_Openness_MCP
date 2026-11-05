using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;

internal static class SpecialExportTests
{
    internal static void Run(Action<bool,string> check,Action<string> skip)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-special-export-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var output=Path.Combine(root,"special.xml"); int exports=0,offline=0;
        void Refuse(Action action,string label) {
            try {action();throw new Exception("Accepted: "+label);} catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) {check(true,label);
            } catch(ArgumentException){check(true,label);} catch(NotSupportedException){check(true,label);}
        }
        PlcSpecialExportResult Run(bool dry,string hash="",string kind="watch-table",string release="17",string path="folder/WT",bool consistent=true,
                Func<FileInfo,Action<FileInfo>,string?>? publisher=null)=>
            PlcSpecialExportPolicy
            .Run(kind,release,"/project.ap17","devices/Station/PLC",path,output,consistent,dry,hash,()=>offline++,f=>{
                exports++; File.WriteAllText(f.FullName,"<Document><Name>Original</Name><Number>42</Number></Document>");},publisher);
        JsonObject Wire(PlcSpecialExportResult value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
        try
        {
            var preview=Run(true); SpecialExportContract.Validate(Wire(preview),true);
            check(exports==0 && offline==0 && Directory.GetFileSystemEntries(root).Length==0,
                    "Special export preview performs no native export, offline callback or filesystem writes");
            output=output.Replace('\\','/');
            var slash=Run(true);
            check(slash.PlanHash==preview.PlanHash && slash.OutputFile==Path.GetFullPath(output),
                    "Forward slashes normalize before special export canonical validation");
            output=Path.GetFullPath(output);
            Refuse(()=>Run(false,"wrong"),"Special export requires exact fresh plan hash");
            Refuse(()=>Run(false,preview.PlanHash,path:"different"),"Special export hash binds exact object path");
            Refuse(()=>Run(false,preview.PlanHash,release:"18"),"Special export hash binds exact release");
            foreach(var release in new[]{"14sp1","15.1","21"})
            {
                var unknown=Run(true,kind:"technology-object",release:release);
                check(unknown.Status=="semantics-unverified" && unknown.MethodEvidence=="static-sdk-signature-verified",
                        "Unverified TO semantics stay read-only despite verified signature for "+release);
                SpecialExportContract.Validate(Wire(unknown),true);
                Refuse(()=>Run(false,unknown.PlanHash,kind:"technology-object",release:release),"Unverified export semantics never invoked for "+release);
            }
            foreach(var release in new[]{"15.1","16","17","18","19","20","21"})
            {
                var watch=Run(true,release:release);
                check(watch.Status=="planned" && watch.MethodEvidence=="static-sdk-signature-verified",
                        "Watch signature and per-release semantic eligibility stay separate: "+release);
                SpecialExportContract.Validate(Wire(watch),true);
            }
            foreach(var release in new[]{"16","17","18","19","20"})
            {
                var technology=Run(true,kind:"technology-object",release:release);
                check(technology.Status=="planned","Manual-backed TO release plans without native export: "+release);
                SpecialExportContract.Validate(Wire(technology),true);
            }
            Refuse(()=>Run(true,consistent:false),"Inconsistent watch-table preview refuses before export");
            Refuse(()=>Run(false,preview.PlanHash,consistent:false),"Watch consistency gates native export");
            var oldWatch=Run(true,release:"15.1");
            var oldExecuted=Run(false,oldWatch.PlanHash,release:"15.1",publisher:(_,_)=>null);
            SpecialExportContract.Validate(Wire(oldExecuted),false);
            check(oldExecuted.Status=="exported" && offline==1 && exports==0,
                    "Watch15.1 official export semantics allow confirmed apply through the offline gate");
            offline=0;
            Refuse(()=>Run(true,release:"14sp1"),"Known absent V14 SP1 watch API refused");
            Refuse(()=>Run(true,kind:"technology-object",consistent:false),"Inconsistent technology-object preview refuses before approval");
            Refuse(()=>Run(false,preview.PlanHash,kind:"technology-object",consistent:false),"Inconsistent technology object cannot export");
            check(exports==0 && offline==0,"Rejected special export plans reach no native callbacks");
            var executed=Run(false,preview.PlanHash,publisher:(_,_)=>null);
            check(executed.Status=="exported" && offline==1 && !executed.RequiresSessionReset,"Special export gates exact offline target before publication");
            var failed=
                Run(false,preview.PlanHash,publisher:(_,_)=>{
                    var error=new IOException("private native text"); error.Data["stagedFile"]="retained.xml"; error.Data["secret"]="hidden"; throw error;});
            check(failed.Status=="failed" && failed.RequiresSessionReset && failed.Evidence.Count==1,
                    "Unknown export outcome requires fail-stop and preserves only allowlisted evidence");
            SpecialExportContract.Validate(Wire(failed),false);
            var classification=WorkerFailurePolicy
                .Classify(new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Blocked policy","export-plan",false),true,false);
            check(classification.Outcome==TiaMcp.WorkerChannel.ChannelOutcome.RejectedBeforeNative,"Typed worker export policy refusal never becomes unknown");
            var request=new JsonObject {
                ["dryRun"]=false,["softwarePath"]="devices/Station/PLC",["watchTableName"]="folder/WT",["exportPath"]=output,["expectedProjectFile"]=
                    "/project.ap17",["expectedPlanHash"]=preview.PlanHash
            };
            SpecialExportContract.ValidateRequest("ExportPlcWatchTable",request,Wire(failed));
            request["softwarePath"]="PLC"; request["exportPath"]=output.Replace('\\','/');
            SpecialExportContract.ValidateRequest("ExportPlcWatchTable",request,Wire(failed));
            check(true,"Special transport accepts resolved short PLC identity and normalized slash spelling");
            request["softwarePath"]="devices/Station/PLC";
            request["watchTableName"]="other";
            try {SpecialExportContract.ValidateRequest("ExportPlcWatchTable",request,Wire(failed)); throw new Exception("Conflicting result accepted");
            } catch(InvalidDataException){check(true,"Host refuses mismatched special export identity");}
            var malformed=Wire(failed);malformed["RequiresSessionReset"]=false;
            try {SpecialExportContract.Validate(malformed,false);throw new Exception("Unpoisoned failure accepted");} catch(InvalidDataException){
                check(true,"Host refuses conflicting special export fail-stop flag");}
            File.WriteAllText(output,"existing"); Refuse(()=>Run(true),"Existing output refuses preview without overwrite"); File.Delete(output);
            var originalOutput=output;
            output=Path.Combine(root,"..",Path.GetFileName(root),"special.xml");
            Refuse(()=>Run(true),"Noncanonical output containing dot segments refused");
            output=originalOutput;
            foreach(var unsafeName in new[]{"CON.xml","con .xml","COM1.xml","LPT².xml","ordinary:stream.xml","bad?.xml","bad\\name.xml"})
            {
                output=Path.Combine(root,unsafeName); Refuse(()=>Run(true),"Unsafe Windows export filename refused: "+unsafeName);
            }
            output=originalOutput;
            Directory.CreateDirectory(output); Refuse(()=>Run(true),"Existing directory cannot become XML destination"); Directory.Delete(output);
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)
            {
                var real=Path.Combine(root,"real"); Directory.CreateDirectory(real);
                var link=Path.Combine(root,"linked"); Directory.CreateSymbolicLink(link,real);
                output=Path.Combine(link,"special.xml"); Refuse(()=>Run(true),"Linked export directory ancestry refused without writes");
                output=originalOutput; Directory.Delete(link); Directory.Delete(real);
                File.CreateSymbolicLink(output,Path.Combine(root,"missing.xml"));
                Refuse(()=>Run(true),"Dangling output symlink refused without writes"); File.Delete(output);
            }
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)
            {
                var unsupported=Run(false,preview.PlanHash);
                check(unsupported.RequiresSessionReset && exports==0 && Directory.GetFileSystemEntries(root).Length==0,
                        "Linux publication guard writes nothing and calls no native export");
                skip("Special export Windows native XML preservation/atomic filesystem publication requires Windows.");
            }
            else
            {
                var published=Run(false,preview.PlanHash);
                check(published.Status=="exported" && exports==1 && File.ReadAllText(output)=="<Document><Name>Original</Name><Number>42</Number></Document>",
                        "Windows publishes native XML unchanged, preserving names and numbers");
            }
        }
        finally {Directory.Delete(root,true);}
    }
}

public sealed class PlannedExportDirectoryTests
{
    [Xunit.Fact]
    public void Planned_exports_require_a_bound_root_and_reject_paths_outside_it()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "bounded-export-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "Group", "Table.xml");
        void Run(string validatedRoot) => TiaMcp.Adapters.PlcSpecialExportPolicy.Run("watch-table", "20", "C:/P.ap20", "PLC",
            "Group/Table", output, true, true, "", () => { }, _ => { }, allowPlannedDirectories: true, plannedExportRoot: validatedRoot);
        Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => Run(""));
        Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => Run(Path.Combine(root, "Other")));
        Xunit.Assert.False(Directory.Exists(root));
    }

    [Xunit.Fact]
    public void Directory_creation_failure_is_a_precondition_before_publication()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "mkdir-failure-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(root, "Group", "Table.xml"); int publications = 0;
        TiaMcp.Adapters.PlcSpecialExportResult Run(bool dry, string hash = "") => TiaMcp.Adapters.PlcSpecialExportPolicy.Run(
            "watch-table", "20", "C:/P.ap20", "PLC", "Group/Table", output, true, dry, hash, () => { }, _ => { },
            (_, _) => { publications++; return null; }, allowPlannedDirectories: true, plannedExportRoot: root,
            createDirectory: _ => throw new IOException("Synthetic mkdir failure"));
        var preview = Run(true);
        var error = Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => Run(false, preview.PlanHash));
        Xunit.Assert.IsType<IOException>(error.InnerException);
        Xunit.Assert.False(error.IsArgument); Xunit.Assert.Equal(0, publications); Xunit.Assert.False(Directory.Exists(root));
        var refusal = FoundationV4Result.Failure("20", "ExportPlcWatchTable", "mkdir-test", true, true, null, error);
        Xunit.Assert.Equal("rejected-before-operation", (string?)refusal.StructuredContent?["meta"]?["outcome"]);
        Xunit.Assert.False((bool?)refusal.StructuredContent?["meta"]?["requiresSessionReset"]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("watch-table")][Xunit.InlineData("technology-object")]
    public void Grouped_export_previews_missing_parents_and_creates_them_only_on_apply(string kind)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "planned-export-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "Group", "Table.xml"); int writes = 0;
        TiaMcp.Adapters.PlcSpecialExportResult Run(bool dry, string hash = "", bool consistent = true) =>
            TiaMcp.Adapters.PlcSpecialExportPolicy.Run(kind, "20", "C:/Project.ap20", "PLC", "Group/Table", output,
                consistent, dry, hash, () => { }, _ => { }, (file, _) => {
                    Xunit.Assert.True(Directory.Exists(file.DirectoryName)); writes++; return null; }, allowPlannedDirectories: true, plannedExportRoot: root);
        try
        {
            var preview = Run(true); Xunit.Assert.False(Directory.Exists(root));
            Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => Run(false, "stale"));
            Xunit.Assert.False(Directory.Exists(root));
            Xunit.Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => Run(false, preview.PlanHash, false));
            Xunit.Assert.False(Directory.Exists(root));
            Xunit.Assert.Equal("exported", Run(false, preview.PlanHash).Status); Xunit.Assert.Equal(1, writes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
