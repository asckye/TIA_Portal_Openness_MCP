using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.LegacyHost;
using TiaMcp.PlcFoundation;

internal static class SpecialExportTests
{
    internal static void Run(Action<bool,string> check,Action<string> skip)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-special-export-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var output=Path.Combine(root,"special.xml"); int exports=0,offline=0;
        void Refuse(Action action,string label) { try {action();throw new Exception("Accepted: "+label);} catch(ArgumentException){check(true,label);} catch(NotSupportedException){check(true,label);} }
        PlcSpecialExportResult Run(bool dry,string hash="",string kind="watch-table",string release="17",string path="folder/WT",bool consistent=true,Func<FileInfo,Action<FileInfo>,string?>? publisher=null)=>PlcSpecialExportPolicy.Run(kind,release,"/project.ap17","devices/PLC",path,output,consistent,dry,hash,()=>offline++,f=>{exports++; File.WriteAllText(f.FullName,"<Document><Name>Original</Name><Number>42</Number></Document>");},publisher);
        JsonObject Wire(PlcSpecialExportResult value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
        try
        {
            var preview=Run(true); SpecialExportContract.Validate(Wire(preview),true);
            check(exports==0 && offline==0 && Directory.GetFileSystemEntries(root).Length==0,"Special export preview performs no native export, offline callback or filesystem writes");
            Refuse(()=>Run(false,"wrong"),"Special export requires exact fresh plan hash");
            Refuse(()=>Run(false,preview.PlanHash,path:"different"),"Special export hash binds exact object path");
            Refuse(()=>Run(false,preview.PlanHash,release:"18"),"Special export hash binds exact release");
            foreach(var release in new[]{"14sp1","15.1","21"})
            {
                var unknown=Run(true,kind:"technology-object",release:release);
                check(unknown.Status=="semantics-unverified" && unknown.MethodEvidence=="static-sdk-signature-verified","Unverified TO semantics stay read-only despite verified signature for "+release);
                SpecialExportContract.Validate(Wire(unknown),true);
                Refuse(()=>Run(false,unknown.PlanHash,kind:"technology-object",release:release),"Unverified export semantics never invoked for "+release);
            }
            foreach(var release in new[]{"15.1","16","17","18","19","20","21"})
            {
                var watch=Run(true,release:release);
                check(watch.Status==(release=="15.1"?"semantics-unverified":"planned") && watch.MethodEvidence=="static-sdk-signature-verified","Watch signature and per-release semantic eligibility stay separate: "+release);
                SpecialExportContract.Validate(Wire(watch),true);
            }
            foreach(var release in new[]{"16","17","18","19","20"})
            {
                var technology=Run(true,kind:"technology-object",release:release);
                check(technology.Status=="planned","Manual-backed TO release plans without native export: "+release);
                SpecialExportContract.Validate(Wire(technology),true);
            }
            var badWatch=Run(true,consistent:false);
            check(badWatch.Status=="inconsistent","Inconsistent watch-table preview explicitly rejected for apply");
            Refuse(()=>Run(false,badWatch.PlanHash,consistent:false),"Watch consistency gates native export");
            var oldWatch=Run(true,release:"15.1");
            Refuse(()=>Run(false,oldWatch.PlanHash,release:"15.1"),"Watch15.1 signature presence does not bypass missing manual semantics");
            Refuse(()=>Run(true,release:"14sp1"),"Known absent V14 SP1 watch API refused");
            var inconsistent=Run(true,kind:"technology-object",consistent:false);
            Refuse(()=>Run(false,inconsistent.PlanHash,kind:"technology-object",consistent:false),"Inconsistent technology object cannot export");
            check(exports==0 && offline==0,"Rejected special export plans reach no native callbacks");
            var executed=Run(false,preview.PlanHash,publisher:(_,_)=>null);
            check(executed.Status=="exported" && offline==1 && !executed.RequiresSessionReset,"Special export gates exact offline target before publication");
            var failed=Run(false,preview.PlanHash,publisher:(_,_)=>{var error=new IOException("private native text"); error.Data["stagedFile"]="retained.xml"; error.Data["secret"]="hidden"; throw error;});
            check(failed.Status=="failed" && failed.RequiresSessionReset && failed.Evidence.Count==1,"Unknown export outcome requires fail-stop and preserves only allowlisted evidence");
            SpecialExportContract.Validate(Wire(failed),false);
            var request=new JsonObject { ["dryRun"]=false,["softwarePath"]="devices/PLC",["watchTableName"]="folder/WT",["exportPath"]=output,["expectedProjectFile"]="/project.ap17",["expectedPlanHash"]=preview.PlanHash };
            SpecialExportContract.ValidateRequest("ExportPlcWatchTable",request,Wire(failed));
            request["watchTableName"]="other";
            try {SpecialExportContract.ValidateRequest("ExportPlcWatchTable",request,Wire(failed)); throw new Exception("Conflicting result accepted");} catch(InvalidDataException){check(true,"Host refuses mismatched special export identity");}
            var malformed=Wire(failed);malformed["RequiresSessionReset"]=false;
            try {SpecialExportContract.Validate(malformed,false);throw new Exception("Unpoisoned failure accepted");} catch(InvalidDataException){check(true,"Host refuses conflicting special export fail-stop flag");}
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
                check(unsupported.RequiresSessionReset && exports==0 && Directory.GetFileSystemEntries(root).Length==0,"Linux publication guard writes nothing and calls no native export");
                skip("Special export Windows native XML preservation/atomic filesystem publication requires Windows.");
            }
            else
            {
                var published=Run(false,preview.PlanHash);
                check(published.Status=="exported" && exports==1 && File.ReadAllText(output)=="<Document><Name>Original</Name><Number>42</Number></Document>","Windows publishes native XML unchanged, preserving names and numbers");
            }
        }
        finally {Directory.Delete(root,true);}
    }
}
