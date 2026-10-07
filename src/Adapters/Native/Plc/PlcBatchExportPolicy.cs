using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcBatchExportSource
    {
        internal string Path="";
        internal bool Consistent;
        internal PlcBlockXmlCapability Capability=new PlcBlockXmlCapability();
        internal Action<FileInfo> Export=null!;
    }
    internal static class PlcBatchExportPolicy
    {
        internal const int MaximumItems=256;
        internal static string Hash(string value)
        { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant(); }
        internal static PlcBatchExportResult Run(string kind,string project,string software,string group,bool recursive,string directory,int maxItems,bool dryRun,string expectedInventoryHash,IEnumerable<PlcBatchExportSource> inventory,Action requireOffline,Func<FileInfo,Action<FileInfo>,string?>? publish=null)
        {
            if(kind!="blocks" && kind!="types") throw new AdapterPreconditionException("Unknown batch kind.","kind");
            if(maxItems<1 || maxItems>MaximumItems) throw new AdapterPreconditionException("maxItems must be between 1 and 256; oversized inventories are refused, never truncated.","maxItems");
            if(!Path.IsPathRooted(directory)) throw new AdapterPreconditionException("An absolute existing export directory is required.","exportPath");
            var root=new DirectoryInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(directory));
            if(!root.Exists) throw new AdapterPreconditionException("Export directory must already exist.","exportPath");
            for(var ancestor=root; ancestor!=null; ancestor=ancestor.Parent)
                if((ancestor.Attributes & FileAttributes.ReparsePoint)!=0) throw new AdapterPreconditionException("Export directory ancestry cannot contain reparse points.","exportPath");
            var sources=inventory.Take(maxItems+1).OrderBy(x=>x.Path,StringComparer.Ordinal).ToArray();
            if(sources.Length>maxItems) throw new AdapterPreconditionException("Complete inventory exceeds maxItems; narrow the exact group scope.","groupPath");
            if(sources.Select(x=>x.Path).Distinct(StringComparer.Ordinal).Count()!=sources.Length) throw new AdapterPreconditionException("Ambiguous inventory paths.","groupPath");
            TiaOpenness.Shared.NativeExportPolicy.RequireConsistent(kind,sources.Where(x=>!x.Consistent).Select(x=>x.Path),"groupPath");
            var result=new PlcBatchExportResult { Executed=!dryRun,ProjectFile=project,SoftwarePath=software,GroupPath=group,Recursive=recursive };
            result.Items=sources.Select(source=>new PlcBatchExportItem {
                ObjectPath=PlcExchangePolicy.ObjectPath(source.Path),
                // Hash the full canonical path; never place a PLC name in a filesystem path.
                // Prefix avoids reserved Windows devices; fixed ASCII avoids traversal/case collisions.
                OutputFile=PlcFoundationPolicy.XmlOutput(Path.Combine(root.FullName,kind+"-"+Hash(source.Path)+".xml")).FullName,
                Status=source.Consistent ? "planned" : "inconsistent",XmlContent=source.Capability.Content,Warnings=source.Capability.Warnings
            }).ToArray();
            if(result.Items.Select(x=>x.OutputFile).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=sources.Length) throw new AdapterPreconditionException("Export destination collision.","exportPath");
            result.InventoryHash=Hash(kind+"\n"+project+"\n"+software+"\n"+group+"\n"+recursive+"\n"+string.Join("\n",result.Items.Select(x=>x.ObjectPath+"\t"+x.Status+"\t"+x.XmlContent+"\t"+x.OutputFile)));
            if(dryRun) return result;
            if(!string.Equals(expectedInventoryHash,result.InventoryHash,StringComparison.Ordinal)) throw new AdapterPreconditionException("Execution requires the exact inventory hash from a fresh preview.","expectedInventoryHash");
            publish=publish ?? PlcExportPublication.Publish;
            bool stopped=false;
            for(int i=0;i<sources.Length;i++)
            {
                var item=result.Items[i];
                if(item.Status=="inconsistent") continue;
                if(stopped) { item.Status="not-attempted"; continue; }
                try
                {
                    requireOffline();
                    var recovery=publish(new FileInfo(TiaOpenness.Shared.NativeInputPolicy.FullPath(item.OutputFile)),sources[i].Export);
                    item.Status="exported";
                    if(recovery!=null) item.Evidence["recoveryDirectory"]=recovery;
                }
                catch(AdapterPreconditionException) when(i==0) { throw; }
                catch(Exception error)
                {
                    item.Status="failed"; stopped=true; result.RequiresSessionReset=true;
                    // Preserve only allowlisted publication evidence, never native messages/XML.
                    foreach(var name in new[]{"outputFile","stagedFile","recoveryDirectory","exportPhase","stagedSha256"})
                        if(error.Data[name] is string value) item.Evidence[name]=value;
                }
            }
            return result;
        }
    }
}
