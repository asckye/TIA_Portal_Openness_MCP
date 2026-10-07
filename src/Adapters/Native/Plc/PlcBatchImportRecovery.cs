using System;
using System.IO;
using System.Linq;
using Siemens.Engineering.SW;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.Adapters.Native.Plc;

namespace TiaMcp.PlcFoundation
{
    internal static class PlcBatchImportRecovery
    {
        internal static void Precheck()
        {
            var locations=TiaOpenness.Shared.DataLocations.Current;
            string path=locations.RecoveryAttemptedPath;
            try
            {
                if(locations.Root==null) throw new IOException("No writable data root is available.");
                TiaOpenness.Shared.NativeExportPolicy.CheckWritableDirectory(path);
            }
            catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
            { var refusal=new AdapterPreconditionException("Recovery is unavailable; attempted path: "+path,"overwrite",false,error);refusal.Data["attemptedPath"]=path;throw refusal; }
        }
        internal static string Directory()
        {
            Precheck();
            var root=TiaOpenness.Shared.DataLocations.Current.Root;
            var path=Path.Combine(root!,"recovery","plc-import-"+Guid.NewGuid().ToString("N"));
            try
            {
                for(var ancestor=new DirectoryInfo(path);ancestor!=null;ancestor=ancestor.Parent)
                    if(ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("Recovery ancestry contains a reparse point.");
                System.IO.Directory.CreateDirectory(path);return path;
            }
            catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
            { var refusal=new AdapterPreconditionException("Recovery is unavailable; attempted path: "+path,"overwrite",false,error);refusal.Data["attemptedPath"]=path;throw refusal; }
        }
    }

    // Full engines use the same bounded planning/recovery policy as Foundation.
    // Every callback runs synchronously on the caller's existing Openness lane.
    public static class PlcBatchImportRunner
    {
        public static string SingleImportRecoveryDirectory()=>PlcBatchImportRecovery.Directory();
        public static PlcBatchImportResult Run(PlcBatchImportRequest request,PlcSoftware software,Action recheck,string release="",string project="",int processId=0)
        {
            bool prepared=false;
            try
            {
                if(release!="") {request.Release=release;request.Project=project;request.ProcessId=processId;}
                PlcBatchImportPolicy.ValidateOptions(request);
                foreach(var group in new[]{request.BlockGroup,request.TypeGroup,request.TagGroup}) PlcExchangePolicy.ObjectPath(group,true);
                var adapter=new PlcImportAdapter(request.Release,()=>throw new NotSupportedException(),()=>software,recheck,true);
                var inventory=adapter.ReadBatchInventory().Where(x=>!x.Kind.StartsWith("group-",StringComparison.Ordinal)).ToArray();
                PlcBatchImportObject Map(PlcImportObject item)=>new PlcBatchImportObject {Name=item.Name,Kind=item.Kind,GroupPath=item.GroupPath,Number=item.Number};
                PlcImportInput Input(FileInfo file,PlcBatchImportObject item)=>new PlcImportInput {Path=file.FullName,Target=new PlcImportObject {Name=item.Name,Kind=item.Kind,GroupPath=item.GroupPath,Number=item.Number}};
                foreach(var target in new[]{new PlcImportObject {Kind="FC",GroupPath=request.BlockGroup},new PlcImportObject {Kind="UDT",GroupPath=request.TypeGroup},new PlcImportObject {Kind="TagTable",GroupPath=request.TagGroup}})
                    if((request.Program || target.Kind=="FC") && adapter.TargetGroupIdentity(target)=="") throw new AdapterPreconditionException("Exact destination group is unavailable.","groupPath");
                prepared=true;
                return PlcBatchImportPolicy.Run(request,inventory.Select(Map),recheck,
                    (file,item)=>new[]{Map(adapter.Import(Input(file,item),request.Overwrite))},
                    (item,file)=>adapter.ExportRecovery(inventory.Single(x=>x.Name==item.Name && x.Kind==item.Kind && x.GroupPath==item.GroupPath),file),
                    (file,item)=>new[]{Map(adapter.Import(Input(file,item),true))},PlcBatchImportRecovery.Directory,PlcBatchImportRecovery.Precheck,
                    item=>adapter.RecoveryBlocker(inventory.Single(x=>x.Name==item.Name && x.Kind==item.Kind && x.GroupPath==item.GroupPath)));
            }
            catch(AdapterPreconditionException) { throw; }
            catch(Exception error) when(!prepared)
            { throw new AdapterPreconditionException("Batch target admission failed before any import: "+error.Message,request.InputParameter,false,error); }
        }
    }
}
