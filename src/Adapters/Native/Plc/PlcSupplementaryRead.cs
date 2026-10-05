using Native = TiaMcp.PlcFoundation.WatchTechnologyPrimitives;
using System;
using System.Linq;
using Siemens.Engineering.SW.TechnologicalObjects;
#if PLC_WATCH_READ
using Siemens.Engineering.SW.WatchAndForceTables;
#endif

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcSupplementaryReadResult ReadWatchTableNames(string softwarePath)
        {
            PlcSupplementaryReadPolicy.RequireRelease(ReleaseKey,true);
#if PLC_WATCH_READ
            return PlcSupplementaryReadPolicy.ReadSnapshot(()=> {
                var selected=ReadSelection(softwarePath);
                var root=Native.WatchGroup(selected.Value);
                if(root==null) throw new InvalidOperationException("WatchAndForceTableGroup is unavailable.");
                var items=PlcSupplementaryReadPolicy.WatchPaths<PlcWatchAndForceTableGroup>(root,group=>Native.WatchTables(group).Select(table=>Native.Name(table)),group=>Native.Groups(group),group=>group.Name);
                return new PlcSupplementaryReadResult { SoftwarePath=selected.ExactPath,ReleaseKey=ReleaseKey,Scope=PlcSupplementaryReadPolicy.WatchScope,Items=items };
            });
#else
            throw new NotSupportedException("No watch-table API was compiled for this release.");
#endif
        }
        public PlcSupplementaryReadResult ReadTechnologyObjects(string softwarePath)
        {
            PlcSupplementaryReadPolicy.RequireRelease(ReleaseKey);
            return PlcSupplementaryReadPolicy.ReadSnapshot(()=> {
                var selected=ReadSelection(softwarePath);
                var root=Native.TechnologyGroup(selected.Value);
                if(root==null) throw new InvalidOperationException("TechnologicalObjectGroup is unavailable.");
                var items=PlcSupplementaryReadPolicy.Technologies<TechnologicalInstanceDBGroup>(root,
                    group=>Native.Objects(group).Select(item=>TechnologyMetadata(item)),
#if PLC_TECH_GROUP_READ
                    group=>Native.Groups(group),
#else
                    group=>new TechnologicalInstanceDBGroup[0],
#endif
                    group=>Native.Name(group));
                return new PlcSupplementaryReadResult { SoftwarePath=selected.ExactPath,ReleaseKey=ReleaseKey,Scope=PlcSupplementaryReadPolicy.TechnologyReadScope(ReleaseKey),Items=items };
            });
        }
        private static PlcTechnologyReadRow TechnologyMetadata(TechnologicalInstanceDB item)
        {
            // Exact PE/XML evidence: both properties have public getters in all
            // eight adapters. OfSystemLibVersion also has a setter; never use it.
            return PlcSupplementaryReadPolicy.TechnologyMetadata(Native.Name(item),
                ()=>item.OfSystemLibElement,()=>item.OfSystemLibVersion);
        }
    }
}
