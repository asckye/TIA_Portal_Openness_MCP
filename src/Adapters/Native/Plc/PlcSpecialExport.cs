using NativeCalls = TiaMcp.Adapters.WatchTechnologyPrimitives;
using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;
#if PLC_WATCH_READ
using Siemens.Engineering.SW.WatchAndForceTables;
#endif

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcSpecialExportResult ExportPlcWatchTable(string softwarePath,string watchTableName,string exportPath,string expectedPlanHash="",bool dryRun=true)
        {
            PlcSupplementaryReadPolicy.RequireRelease(ReleaseKey,true);
#if PLC_WATCH_READ
            var selected=ReadSelection(softwarePath);
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var root=NativeCalls.WatchGroup(selected.Value);
            if(root==null) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("WatchAndForceTableGroup unavailable.","export-plan",false);
            // Use the existing bounded read traversal to collect genuine typed objects.
            var objects=new Dictionary<string,PlcWatchTable>(StringComparer.Ordinal);
            var groups=new Dictionary<PlcWatchAndForceTableGroup,string> { [root]="" };
            PlcSupplementaryReadPolicy.WatchPaths<PlcWatchAndForceTableGroup>(root,g=>NativeCalls.WatchTables(g).Select(t=> {
                var folder=groups[g]; var path=folder+Uri.EscapeDataString(PlcSupplementaryReadPolicy.Name(NativeCalls.Name(t)));
                objects.Add(path,t); return NativeCalls.Name(t);
            }),g=>NativeCalls.Groups(g).Select(child=> { groups.Add(child,groups[g]+Uri.EscapeDataString(PlcSupplementaryReadPolicy.Name(NativeCalls.Name(child)))+"/"); return child; }),g=>g.Name);
            if(!objects.TryGetValue(watchTableName,out var table)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact canonical watch-table path not found.");
            return PlcSpecialExportPolicy.Run("watch-table",ReleaseKey,Project().Path.FullName,selected.ExactPath,watchTableName,exportPath,NativeCalls.IsConsistent(table),dryRun,expectedPlanHash,()=>RequireTargetOffline(selected),file=> {
#if PLC_SPECIAL_EXPORT || PLC_WATCH_EXPORT
                RequireTargetOffline(selected);
                if(!NativeCalls.IsConsistent(table)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Watch table is no longer consistent.","export-plan",false);
                NativeCalls.Export(table,file,ExportOptions.None);
#else
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Export method evidence is unknown for this release.","export-plan",false);
#endif
            });
#else
            throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("V14 SP1 watch-table API is absent.","export-plan",false);
#endif
        }
        public PlcSpecialExportResult ExportTechnologyObject(string softwarePath,string toName,string exportPath,string expectedPlanHash="",bool dryRun=true)
        {
            PlcSupplementaryReadPolicy.RequireRelease(ReleaseKey);
            var selected=ReadSelection(softwarePath);
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var root=NativeCalls.TechnologyGroup(selected.Value);
            if(root==null) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("TechnologicalObjectGroup unavailable.","export-plan",false);
            var objects=new Dictionary<string,TechnologicalInstanceDB>(StringComparer.Ordinal);
            var groups=new Dictionary<TechnologicalInstanceDBGroup,string> { [root]="" };
            PlcSupplementaryReadPolicy.Technologies(root,g=>NativeCalls.Objects(g).Select(t=> {
                objects.Add(groups[g]+Uri.EscapeDataString(PlcSupplementaryReadPolicy.Name(NativeCalls.Name(t))),t); return TechnologyMetadata(t);
            }),g=> {
#if PLC_TECH_GROUP_READ
                return NativeCalls.Groups(g).Select(child=> { groups.Add(child,groups[g]+Uri.EscapeDataString(PlcSupplementaryReadPolicy.Name(NativeCalls.Name(child)))+"/"); return child; });
#else
                return new TechnologicalInstanceDBGroup[0];
#endif
            },g=>NativeCalls.Name(g));
            if(!objects.TryGetValue(toName,out var item)) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact canonical technology-object path not found within this release's documented read scope.");
            if(item.IsKnowHowProtected) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Know-how protected technology objects are outside this export scope.","export-plan",false);
            bool consistent=item.IsConsistent;
            return PlcSpecialExportPolicy.Run("technology-object",ReleaseKey,Project().Path.FullName,selected.ExactPath,toName,exportPath,consistent,dryRun,expectedPlanHash,()=>RequireTargetOffline(selected),file=> {
#if PLC_SPECIAL_EXPORT
                RequireTargetOffline(selected);
                if(item.IsKnowHowProtected) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Technology object protection changed after preview.","export-plan",false);
                if(!item.IsConsistent) throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Technology object is no longer consistent.","export-plan",false);
                item.Export(file,ExportOptions.None);
#else
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Export method evidence is unknown for this release.","export-plan",false);
#endif
            });
        }
    }
}
