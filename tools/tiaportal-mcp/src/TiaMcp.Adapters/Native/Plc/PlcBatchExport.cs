using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private static T BatchGroup<T>(IEnumerable<Located<T>> groups,string path)
        {
            var inventory=groups.Take(1025).ToArray();
            if(inventory.Length>1024) throw new ArgumentException("Group inventory exceeds the bounded batch scope.");
            return PlcExchangePolicy.Exact(inventory,x=>x.Path,path).Value;
        }
        public PlcBatchExportResult ExportBlocks(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true)
        {
            var selected=ReadSelection(softwarePath);
            if(!string.Equals(softwarePath,selected.ExactPath,StringComparison.Ordinal)) throw new ArgumentException("Batch export requires the exact software path, not an alias.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var path=PlcExchangePolicy.ObjectPath(groupPath,true);
            if(path!=groupPath) throw new ArgumentException("Use the exact canonical group path from the software inventory.");
            var group=BatchGroup(BlockGroups(selected.Value.BlockGroup),path);
            var groups=recursive ? BlockGroups(group,path) : new[]{new Located<PlcBlockGroup>(path,group)};
            var sources=BatchGroupsBounded(groups).SelectMany(g=>g.Value.Blocks.Select(b=>new PlcBatchExportSource {
                Path=Child(g.Path,b.Name),Consistent=b.IsConsistent,Capability=PlcBlockXmlPolicy.Export(ReleaseKey,b.ProgrammingLanguage.ToString()),
                Export=f=>{ RequireTargetOffline(selected); if(!b.IsConsistent) throw new InvalidOperationException("Block is no longer consistent."); b.Export(f,ExportOptions.None); }
            }));
            return PlcBatchExportPolicy.Run("blocks",Project().Path.FullName,selected.ExactPath,path,recursive,exportPath,maxItems,dryRun,expectedInventoryHash,sources,()=>RequireTargetOffline(selected));
        }
        public PlcBatchExportResult ExportTypes(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true)
        {
            var selected=ReadSelection(softwarePath);
            if(!string.Equals(softwarePath,selected.ExactPath,StringComparison.Ordinal)) throw new ArgumentException("Batch export requires the exact software path, not an alias.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var path=PlcExchangePolicy.ObjectPath(groupPath,true);
            if(path!=groupPath) throw new ArgumentException("Use the exact canonical group path from the software inventory.");
            var group=BatchGroup(TypeGroups(selected.Value.TypeGroup),path);
            var groups=recursive ? TypeGroups(group,path) : new[]{new Located<PlcTypeGroup>(path,group)};
            var sources=BatchGroupsBounded(groups).SelectMany(g=>g.Value.Types.Select(t=>new PlcBatchExportSource {
                Path=Child(g.Path,t.Name),Consistent=t.IsConsistent,
                Export=f=>{ RequireTargetOffline(selected); if(!t.IsConsistent) throw new InvalidOperationException("Type is no longer consistent."); t.Export(f,ExportOptions.None); }
            }));
            return PlcBatchExportPolicy.Run("types",Project().Path.FullName,selected.ExactPath,path,recursive,exportPath,maxItems,dryRun,expectedInventoryHash,sources,()=>RequireTargetOffline(selected));
        }
        private static IEnumerable<Located<T>> BatchGroupsBounded<T>(IEnumerable<Located<T>> groups)
        {
            int count=0;
            foreach(var group in groups) { if(++count>1024) throw new ArgumentException("Group inventory exceeds the bounded batch scope."); yield return group; }
        }
    }
}
