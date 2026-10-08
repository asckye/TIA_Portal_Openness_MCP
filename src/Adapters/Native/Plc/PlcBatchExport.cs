using PlcNative = TiaMcp.Adapters.Native.Plc.PlcBlockPrimitives;
using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        private static T BatchGroup<T>(IEnumerable<Located<T>> groups,string path,string parameter="groupPath")
        {
            var inventory=groups.Take(1025).ToArray();
            if(inventory.Length>1024) throw new AdapterPreconditionException("Group inventory exceeds the bounded batch scope.",parameter);
            return PlcExchangePolicy.Exact(inventory,x=>x.Path,path,parameter).Value;
        }
        public PlcBatchExportResult ExportBlocks(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true)
        {
            var selected=ReadSelection(softwarePath);
            TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,selected.ExactPath,true);
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var path=PlcExchangePolicy.ObjectPath(groupPath,true,"groupPath");
            if(path!=groupPath) throw new AdapterPreconditionException("Use the exact canonical group path from the software inventory.","groupPath");
            var group=BatchGroup(BlockGroups(PlcNative.BlockGroup(selected.Value)),path,"groupPath");
            var groups=recursive ? BlockGroups(group,path) : new[]{new Located<PlcBlockGroup>(path,group)};
            var sources=BatchGroupsBounded(groups).SelectMany(g=>PlcNative.Blocks(g.Value).Select(b=>new PlcBatchExportSource {
                Path=Child(g.Path,PlcNative.Name(b)),Consistent=PlcNative.IsConsistent(b),Capability=PlcBlockXmlPolicy.Export(ReleaseKey,PlcNative.Language(b).ToString()),
                Export=f=>{ RequireTargetOffline(selected); if(!PlcNative.IsConsistent(b)) throw new InvalidOperationException("Block is no longer consistent."); PlcNative.Export(b,f,ExportOptions.None); }
            }));
            return PlcBatchExportPolicy.Run("blocks",Project().Path.FullName,selected.ExactPath,path,recursive,exportPath,maxItems,dryRun,expectedInventoryHash,sources,()=>RequireTargetOffline(selected));
        }
        public PlcBatchExportResult ExportTypes(string softwarePath,string groupPath,string exportPath,bool recursive=false,int maxItems=128,string expectedInventoryHash="",bool dryRun=true)
        {
            var selected=ReadSelection(softwarePath);
            TiaOpenness.Shared.NativeExportPolicy.RequireSoftwarePath(softwarePath,selected.ExactPath,true);
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var path=PlcExchangePolicy.ObjectPath(groupPath,true,"groupPath");
            if(path!=groupPath) throw new AdapterPreconditionException("Use the exact canonical group path from the software inventory.","groupPath");
            var group=BatchGroup(TypeGroups(PlcNative.TypeGroup(selected.Value)),path,"groupPath");
            var groups=recursive ? TypeGroups(group,path) : new[]{new Located<PlcTypeGroup>(path,group)};
            var sources=BatchGroupsBounded(groups).SelectMany(g=>PlcNative.Types(g.Value).Select(t=>new PlcBatchExportSource {
                Path=Child(g.Path,PlcNative.Name(t)),Consistent=t.IsConsistent,
                Export=f=>{ RequireTargetOffline(selected); if(!t.IsConsistent) throw new InvalidOperationException("Type is no longer consistent."); t.Export(f,ExportOptions.None); }
            }));
            return PlcBatchExportPolicy.Run("types",Project().Path.FullName,selected.ExactPath,path,recursive,exportPath,maxItems,dryRun,expectedInventoryHash,sources,()=>RequireTargetOffline(selected));
        }
        private static IEnumerable<Located<T>> BatchGroupsBounded<T>(IEnumerable<Located<T>> groups)
        {
            int count=0;
            foreach(var group in groups) { if(++count>1024) throw new AdapterPreconditionException("Group inventory exceeds the bounded batch scope.","groupPath"); yield return group; }
        }
    }
}
