using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;
#if PLC_WATCH_READ
using Siemens.Engineering.SW.WatchAndForceTables;
#endif

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcSpecialExportResult ExportPlcWatchTable(string softwarePath,string watchTableName,string exportPath,string expectedPlanHash="",bool dryRun=true)
        {
            PlcSupplementaryReadPolicy.RequireRelease(ReleaseKey,true);
#if PLC_WATCH_READ
            var selected=ReadSelection(softwarePath);
            if(softwarePath!=selected.ExactPath) throw new ArgumentException("Exact software path required; aliases are not export identities.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var root=selected.Value.WatchAndForceTableGroup;
            if(root==null) throw new InvalidOperationException("WatchAndForceTableGroup unavailable.");
            // Use the existing bounded read traversal to collect genuine typed objects.
            var objects=new Dictionary<string,PlcWatchTable>(StringComparer.Ordinal);
            var groups=new Dictionary<PlcWatchAndForceTableGroup,string> { [root]="" };
            PlcSupplementaryReadPolicy.WatchPaths<PlcWatchAndForceTableGroup>(root,g=>g.WatchTables.Select(t=> {
                var folder=groups[g]; var path=folder+Uri.EscapeDataString(PlcSupplementaryReadPolicy.Name(t.Name));
                objects.Add(path,t); return t.Name;
            }),g=>g.Groups.Select(child=> { groups.Add(child,groups[g]+Uri.EscapeDataString(PlcSupplementaryReadPolicy.Name(child.Name))+"/"); return child; }),g=>g.Name);
            if(!objects.TryGetValue(watchTableName,out var table)) throw new ArgumentException("Exact canonical watch-table path not found.");
            return PlcSpecialExportPolicy.Run("watch-table",ReleaseKey,Project().Path.FullName,selected.ExactPath,watchTableName,exportPath,table.IsConsistent,dryRun,expectedPlanHash,()=>RequireTargetOffline(selected),file=> {
#if PLC_SPECIAL_EXPORT || PLC_WATCH_EXPORT
                RequireTargetOffline(selected);
                if(!table.IsConsistent) throw new InvalidOperationException("Watch table is no longer consistent.");
                table.Export(file,ExportOptions.None);
#else
                throw new NotSupportedException("Export method evidence is unknown for this release.");
#endif
            });
#else
            throw new NotSupportedException("V14 SP1 watch-table API is absent.");
#endif
        }
        public PlcSpecialExportResult ExportTechnologyObject(string softwarePath,string toName,string exportPath,string expectedPlanHash="",bool dryRun=true)
        {
            PlcSupplementaryReadPolicy.RequireRelease(ReleaseKey);
            var selected=ReadSelection(softwarePath);
            if(softwarePath!=selected.ExactPath) throw new ArgumentException("Exact software path required; aliases are not export identities.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var root=selected.Value.TechnologicalObjectGroup;
            if(root==null) throw new InvalidOperationException("TechnologicalObjectGroup unavailable.");
            var objects=new Dictionary<string,TechnologicalInstanceDB>(StringComparer.Ordinal);
            var groups=new Dictionary<TechnologicalInstanceDBGroup,string> { [root]="" };
            PlcSupplementaryReadPolicy.Technologies(root,g=>g.TechnologicalObjects.Select(t=> {
                objects.Add(groups[g]+Uri.EscapeDataString(PlcSupplementaryReadPolicy.Name(t.Name)),t); return TechnologyMetadata(t);
            }),g=> {
#if PLC_TECH_GROUP_READ
                return g.Groups.Select(child=> { groups.Add(child,groups[g]+Uri.EscapeDataString(PlcSupplementaryReadPolicy.Name(child.Name))+"/"); return child; });
#else
                return new TechnologicalInstanceDBGroup[0];
#endif
            },g=>g.Name);
            if(!objects.TryGetValue(toName,out var item)) throw new ArgumentException("Exact canonical technology-object path not found within this release's documented read scope.");
            if(item.IsKnowHowProtected) throw new NotSupportedException("Know-how protected technology objects are outside this export scope.");
            bool consistent=item.IsConsistent;
            return PlcSpecialExportPolicy.Run("technology-object",ReleaseKey,Project().Path.FullName,selected.ExactPath,toName,exportPath,consistent,dryRun,expectedPlanHash,()=>RequireTargetOffline(selected),file=> {
#if PLC_SPECIAL_EXPORT
                RequireTargetOffline(selected);
                if(item.IsKnowHowProtected) throw new NotSupportedException("Technology object protection changed after preview.");
                if(!item.IsConsistent) throw new InvalidOperationException("Technology object is no longer consistent.");
                item.Export(file,ExportOptions.None);
#else
                throw new NotSupportedException("Export method evidence is unknown for this release.");
#endif
            });
        }
    }
}
