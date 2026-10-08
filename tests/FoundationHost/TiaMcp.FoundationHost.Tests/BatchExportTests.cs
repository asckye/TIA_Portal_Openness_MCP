using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Adapters;

internal static class BatchExportTests
{
    internal static void Run(Action<bool,string> check,Action<string> skip)
    {
        var root=Path.Combine(Path.GetTempPath(),"tia-batch-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        int native=0,offline=0,published=0;
        PlcBatchExportSource Source(string path,bool consistent=true)=>new() {Path=path,Consistent=consistent,Export=file=>{native++;File.WriteAllText(file.FullName,"<Document><Number>42</Number><Name>Original</Name></Document>");}};
        var sources=new[]{Source("z/CON"),Source("a/A"),Source("a/a"),Source("q/%2F..%5CCON")};
        PlcBatchExportResult Run(bool dry,string hash="",IEnumerable<PlcBatchExportSource>? inventory=null,Func<FileInfo,Action<FileInfo>,string?>? publish=null,int max=128,Action? gate=null)=>PlcBatchExportPolicy.Run("blocks","/project.ap17","devices/PLC","",true,root,max,dry,hash,inventory??sources,gate??(()=>offline++),publish);
        void Refused(Action action,string message) {try {action(); throw new Exception("Accepted: "+message);} catch(ArgumentException) {check(true,message);} }
        try
        {
            foreach(string kind in new[]{"blocks","types"})
            foreach(bool dry in new[]{true,false})
            {
                try {PlcBatchExportPolicy.Run(kind,"/project.ap17","devices/PLC","",true,root,128,dry,"",new[]{Source("g/Uncompiled",false)},()=>offline++);throw new Exception("Inconsistent batch accepted");}
                catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
                {check(!error.IsArgument && error.Message.Contains("g/Uncompiled") && native==0 && offline==0,"Inconsistent batch names its objects before preview/apply and issues no export");}
            }
            var preview=Run(true);
            check(native==0 && offline==0 && Directory.GetFileSystemEntries(root).Length==0,"Batch preview invokes no native or offline callback and writes no files");
            check(preview.Items.Length==4 && preview.Items.All(x=>x.Status=="planned") && preview.InventoryComplete,"Complete deterministic preview contains admitted consistent items");
            check(preview.Items.Select(x=>x.ObjectPath).SequenceEqual(preview.Items.Select(x=>x.ObjectPath).OrderBy(x=>x,StringComparer.Ordinal)),"Batch inventory uses ordinal ordering");
            check(preview.Items.Select(x=>x.OutputFile).Distinct(StringComparer.OrdinalIgnoreCase).Count()==4 && preview.Items.All(x=>Path.GetFileName(x.OutputFile).StartsWith("blocks-") && Path.GetDirectoryName(x.OutputFile)==root),"Names including reserved devices, separators and case collisions never become output path segments");
            check(Run(true,inventory:sources.Reverse()).InventoryHash==preview.InventoryHash,"Batch preview hash does not depend on enumeration order");
            Refused(()=>Run(true,max:3),"Oversized inventory fails instead of truncating");
            Refused(()=>Run(true,inventory:new[]{Source("dup"),Source("dup")}),"Duplicate exact object paths rejected");
            Refused(()=>Run(false,"wrong"),"Execution requires matching preview inventory hash");
            Refused(()=>Run(false,preview.InventoryHash,inventory:sources.Take(3)),"Changed inventory invalidates preview consent");
            check(native==0 && offline==0,"Invalid execution plans never reach native boundary");
            var executed=Run(false,preview.InventoryHash,publish:(file,export)=>{published++; return null;});
            check(executed.Items.Count(x=>x.Status=="exported")==4 && executed.Items.All(x=>x.Status=="exported") && offline==4 && published==4,"Per-item offline gate for the complete admitted inventory");
            published=0;
            var partial=Run(false,preview.InventoryHash,publish:(file,export)=>{
                if(++published==2) {var error=new IOException("secret native text");error.Data["stagedFile"]="retained-stage.xml";error.Data["untrusted"]="hidden";throw error;}return null;
            });
            check(partial.RequiresSessionReset && published==2 && partial.Items.Count(x=>x.Status=="failed")==1 && partial.Items.Count(x=>x.Status=="not-attempted")==2,"First failure retains prior success and stops all later exports without retry");
            check(partial.Items.Single(x=>x.Status=="failed").Evidence.Count==1,"Batch failure contains only allowlisted recovery evidence");
            var blocked=Run(false,preview.InventoryHash,publish:(_,_)=>throw new Exception("must not run"),gate:()=>throw new NotSupportedException("unknown provider"));
            check(blocked.RequiresSessionReset && blocked.Items[0].Status=="failed","Unknown offline target fails closed before publication");
            published=0;
            try {Run(false,preview.InventoryHash,publish:(_,_)=>{published++;return null;},gate:()=>PlcOfflinePolicy.RequireStates(new[]{"Online"},true,"selected PLC"));throw new Exception("Policy refusal accepted");}
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            {check(!error.IsArgument && published==0,"Known offline refusal before first export stays a precondition with no native attempt");}
            int checks=0;
            var changed=Run(false,preview.InventoryHash,publish:(_,_)=>{published++;return null;},gate:()=>{if(++checks==2)PlcOfflinePolicy.RequireStates(new[]{"Online"},true,"selected PLC");});
            check(changed.RequiresSessionReset && published==1 && changed.Items[0].Status=="exported" && changed.Items[1].Status=="failed","Policy refusal after a prior export retains partial execution and requires reset");
            JsonObject Wire(PlcBatchExportResult value)=>JsonSerializer.SerializeToNode(value)!.AsObject();
            BatchExportContract.Validate(Wire(preview),true); BatchExportContract.Validate(Wire(partial),false);
            check(true,"Host validates preview and partial execution contract");
            var request=new JsonObject { ["dryRun"]=false,["expectedProjectFile"]="/project.ap17",["softwarePath"]="devices/PLC",["groupPath"]="",["recursive"]=true,["maxItems"]=128,["expectedInventoryHash"]=preview.InventoryHash };
            WorkerProtocol.ValidateExchangeResult("ExportBlocks",request,Wire(partial));
            check(true,"Worker protocol validates exact scope and project on partial result");
            request["groupPath"]="different";
            try {WorkerProtocol.ValidateExchangeResult("ExportBlocks",request,Wire(partial));throw new Exception("Wrong batch scope accepted");} catch(IOException){check(true,"Wrong batch response scope poisons protocol instead of success");}

            var malformed=Wire(partial); malformed["RequiresSessionReset"]=false;
            try {BatchExportContract.Validate(malformed,false);throw new Exception("Malformed partial accepted");} catch(InvalidDataException){check(true,"Host rejects reset/outcome conflict");}
            var state=new WorkerOutcomeState();state.Failed(true,new IOException("batch unknown"));check(state.Poisoned,"Unknown batch outcome poisons subsequent transport use");
            File.WriteAllText(preview.Items[0].OutputFile,"existing");
            Refused(()=>Run(true),"Existing destination blocks entire plan before execution");
            check(File.ReadAllText(preview.Items[0].OutputFile)=="existing","Existing export is unchanged");File.Delete(preview.Items[0].OutputFile);
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)
            {
                native=0;var unsupported=Run(false,preview.InventoryHash);
                check(unsupported.RequiresSessionReset && native==0 && Directory.GetFileSystemEntries(root).Length==0,"Linux publication fails closed with no native invocation or files");
                skip("Batch Windows atomic publication filesystem success/recovery cases require Windows.");
            }
            else
            {
                native=0;var actual=Run(false,preview.InventoryHash);
                check(!actual.RequiresSessionReset && native==4 && actual.Items.Where(x=>x.Status=="exported").All(x=>File.ReadAllText(x.OutputFile).Contains("<Number>42</Number><Name>Original</Name>")),"Windows batch publishes unmodified native XML numbers and symbols");
                foreach(var item in actual.Items.Where(x=>x.Status=="exported"))File.Delete(item.OutputFile);
                int attempts=0;var failure=Run(false,preview.InventoryHash,inventory:sources.Select(s=>new PlcBatchExportSource {Path=s.Path,Consistent=s.Consistent,Export=f=>{File.WriteAllText(f.FullName,"<Document/>");if(++attempts==2)throw new IOException("native unknown");}}));
                check(failure.RequiresSessionReset && attempts==2 && File.Exists(failure.Items.Single(x=>x.Status=="failed").Evidence["stagedFile"]),"Windows batch retains native failure evidence and stops without retry");
            }
        }
        finally {Directory.Delete(root,true);}
    }
}
