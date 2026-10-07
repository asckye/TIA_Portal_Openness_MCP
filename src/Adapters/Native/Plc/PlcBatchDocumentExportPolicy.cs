using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcBatchDocumentExportSource
    {
        internal string Path="";
        internal string Language="";
        internal bool Consistent;
        internal bool Protected;
        internal Func<DirectoryInfo,string,string[]> Export=null!;
    }
    internal static class PlcBatchDocumentExportPolicy
    {
        internal static string PlanHash(PlcBatchDocumentExportResult result)
        {
            var fields=new List<string> {"batch-documents-v1",result.ReleaseKey,result.ProjectFile,result.SoftwarePath,result.GroupPath,result.Recursive.ToString(),result.MaxItems.ToString(System.Globalization.CultureInfo.InvariantCulture),result.OutputDirectory,result.Options};
            foreach(var item in result.Items) fields.AddRange(new[]{item.BlockPath,item.OutputDirectory,item.Language,item.Consistent.ToString(),Uri.UnescapeDataString(item.BlockPath.Split('/').Last())+".s7dcl",Uri.UnescapeDataString(item.BlockPath.Split('/').Last())+".s7res"});
            return PlcDocumentExportPolicy.Hash(fields.ToArray());
        }
        internal static PlcBatchDocumentExportResult Run(string release,string project,string software,string group,bool recursive,string output,int maxItems,bool dryRun,string expectedHash,IEnumerable<PlcBatchDocumentExportSource> inventory,Action requireOffline,Action<DirectoryInfo,DirectoryInfo>? move=null)
        {
            PlcDocumentExportPolicy.RequireRelease(release);
            if(string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(software) || PlcExchangePolicy.ObjectPath(group,true)!=group) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact project/software/canonical group identity required.");
            if(maxItems<1 || maxItems>256) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("maxItems must be 1..256; oversized inventories are refused, never truncated.");
            output=TiaOpenness.Shared.NativeInputPolicy.NormalizeSeparators(output);
            var destination=PlcDocumentExportPolicy.Destination(output);
            var sources=inventory.Take(maxItems+1).OrderBy(x=>x.Path,StringComparer.Ordinal).ToArray();
            if(sources.Length==0 || sources.Length>maxItems) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("A nonempty complete inventory within maxItems is required.");
            if(sources.Select(x=>x.Path).Distinct(StringComparer.Ordinal).Count()!=sources.Length) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Ambiguous block inventory.");
            TiaOpenness.Shared.NativeExportPolicy.RequireConsistent("blocks",sources.Where(x=>!x.Consistent).Select(x=>x.Path),"groupPath");
            var result=new PlcBatchDocumentExportResult {ReleaseKey=release,ProjectFile=project,SoftwarePath=software,GroupPath=group,Recursive=recursive,MaxItems=maxItems,OutputDirectory=output};
            result.Items=sources.Select(source=> {
                if(PlcExchangePolicy.ObjectPath(source.Path)!=source.Path || !(group=="" || source.Path.StartsWith(group+"/",StringComparison.Ordinal)) || (!recursive && source.Path.Substring(group==""?0:group.Length+1).Contains('/'))) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Block is outside the exact requested group scope.");
                if(source.Protected) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Protected blocks are outside document export scope.","export-plan",false);
                if(source.Language!="LAD" && source.Language!="DB") throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Entire inventory must contain LAD/DB blocks only.","export-plan",false);
                var name=Uri.UnescapeDataString(source.Path.Split('/').Last()); PlcDocumentExportPolicy.Name(name);
                var directory=System.IO.Path.Combine(output,"block-"+PlcDocumentExportPolicy.Hash(source.Path));
                var stagedPath=System.IO.Path.Combine(destination.Parent!.FullName,".tia-batch-documents-"+new string('0',32),"block-"+new string('0',64),name+".s7dcl");
                if(directory.Length>240 || System.IO.Path.Combine(directory,name+".s7dcl").Length>259 || stagedPath.Length>259) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Batch document path exceeds the bounded Windows path scope.");
                return new PlcBatchDocumentExportItem {BlockPath=source.Path,OutputDirectory=directory,Language=source.Language,Consistent=source.Consistent,Status=source.Consistent?"planned":"inconsistent",Files=new[]{name+".s7dcl",name+".s7res"}};
            }).ToArray();
            if(result.Items.Select(x=>x.OutputDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=sources.Length) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Document output collision.");
            result.Status=sources.All(x=>x.Consistent)?"planned":"inconsistent";
            result.PlanHash=PlanHash(result);
            if(dryRun) return result;
            if(expectedHash!=result.PlanHash) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Execution requires exact fresh preview plan hash.");
            TiaOpenness.Shared.NativeExportPolicy.RequireApply(result.Status);
            if(move==null && Environment.OSVersion.Platform!=PlatformID.Win32NT) throw new PlatformNotSupportedException("Document tree publication requires the Windows worker.");
            requireOffline(); PlcDocumentExportPolicy.Destination(output);
            var stage=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(System.IO.Path.Combine(destination.Parent!.FullName,".tia-batch-documents-"+Guid.NewGuid().ToString("N"))));
            result.Executed=true; bool exportReturned=false; foreach(var item in result.Items) item.Status="not-attempted";
            try
            {
                stage.Create();
                if((stage.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("Staging directory cannot be a link.");
                for(int i=0;i<sources.Length;i++)
                {
                    var item=result.Items[i]; item.Status="failed";
                    requireOffline();
                    var directory=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(System.IO.Path.Combine(stage.FullName,new DirectoryInfo(item.OutputDirectory).Name))); directory.Create();
                    if((directory.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("Staging directory cannot be a link.");
                    var name=Uri.UnescapeDataString(item.BlockPath.Split('/').Last());
                    var reported=sources[i].Export(directory,name); exportReturned=true;
                    item.Files=PlcDocumentExportPolicy.ValidatePair(directory,item.Files,reported,release=="21");
                    item.Status="staged";
                }
                // Revalidate all retained sets after the final callback, including the whole tree shape.
                stage.Refresh();
                if((stage.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("Staging root became a link.");
                var expected=result.Items.Select(item=>System.IO.Path.Combine(stage.FullName,new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(item.OutputDirectory)).Name)).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
                if(!Directory.EnumerateFileSystemEntries(stage.FullName).Take(maxItems+1).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(expected,StringComparer.Ordinal)) throw new IOException("Unexpected staging tree entries.");
                foreach(var item in result.Items)
                {
                    var directory=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(System.IO.Path.Combine(stage.FullName,new DirectoryInfo(item.OutputDirectory).Name)));
                    if((directory.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("Staged block directory became a link.");
                    PlcDocumentExportPolicy.ValidatePair(directory,item.Files,item.Files.Select(name=>System.IO.Path.Combine(directory.FullName,name)).ToArray(),release=="21");
                }
                PlcDocumentExportPolicy.Destination(output);
                (move??((from,to)=>Directory.Move(from.FullName,to.FullName)))(stage,destination);
                foreach(var item in result.Items) item.Status="exported";
                result.Status="exported";
            }
            catch(TiaMcp.Adapters.Contracts.AdapterPreconditionException) when(!exportReturned) { throw; }
            catch(Exception) /* swallow(native-fallback): export or publication failure returns failed status, session reset and the retained staging path */
            {
                result.Status="failed";result.RequiresSessionReset=true;result.RecoveryDirectory=stage.FullName;
            }
            return result;
        }
    }
}
