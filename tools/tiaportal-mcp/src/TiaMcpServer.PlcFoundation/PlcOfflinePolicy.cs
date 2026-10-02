using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    internal sealed class PlcOfflineObservation
    {
        internal readonly bool HasStandardProvider;
        internal readonly string? State;
        internal PlcOfflineObservation(bool hasStandardProvider,string? state)
        { HasStandardProvider=hasStandardProvider; State=state; }
    }

    internal static class PlcOfflinePolicy
    {
        internal static void RequireReviewedExecution(string operation)
        {
            throw new NotSupportedException("Execution remains blocked for "+operation+": all-device inventory coverage review is incomplete; preview does not establish readiness.");
        }
        // Shared traversal/policy exercised with fake graphs; native service adapters
        // remain separately subject to real SDK builds and native acceptance.
        internal static void RequireStandardTarget<TItem,TSoftware>(
            IEnumerable<TItem> roots,TItem selectedItem,TSoftware selectedSoftware,
            Func<TItem,IEnumerable<TItem>> children,Func<TItem,TSoftware?> software,
            Func<TItem,PlcOfflineObservation> observe,bool redundant)
            where TItem:class where TSoftware:class
        {
            if(redundant)
                throw new NotSupportedException("R/H XML exchange is outside the standard-target offline contract.");
            if(selectedItem==null || selectedSoftware==null)
                throw new NotSupportedException("Exact selected PLC and owner are required.");
            var owners=new List<TItem>();
            var visited=new HashSet<TItem>();
            CollectOwners(roots,selectedSoftware,children,software,owners,visited,0);
            if(owners.Count!=1 || !object.Equals(owners[0],selectedItem))
                throw new NotSupportedException("Selected PLC must have one exact matching owner in its device.");
            var observation=observe(owners[0]);
            if(observation==null || !observation.HasStandardProvider)
                throw new NotSupportedException("Owning CPU has no positive standard OnlineProvider evidence.");
            RequireStates(new[]{observation.State},true,"selected standard-provider PLC");
        }
        private static void CollectOwners<TItem,TSoftware>(IEnumerable<TItem> items,TSoftware selected,
            Func<TItem,IEnumerable<TItem>> children,Func<TItem,TSoftware?> software,
            List<TItem> owners,HashSet<TItem> visited,int depth)
            where TItem:class where TSoftware:class
        {
            if(items==null || depth>128)
                throw new NotSupportedException("Selected-device ownership graph is incomplete or too deep.");
            foreach(var item in items)
            {
                if(item==null || !visited.Add(item))
                    throw new NotSupportedException("Selected-device ownership graph is cyclic or ambiguous.");
                if(object.Equals(software(item),selected)) owners.Add(item);
                CollectOwners(children(item),selected,children,software,owners,visited,depth+1);
            }
        }
        internal static void RequireDocumentedRelease(string release)
        {
            if(!new[]{"14sp1","15.1","16","17","18","19","20","21"}.Contains(release))
                throw new NotSupportedException("Offline-state workflow evidence is incomplete for release "+release+"; execution remains blocked.");
        }
        internal static void RequireStates(IEnumerable<string?> states,bool coverageComplete,string scope)
        {
            var values=states.ToArray();
            if(!coverageComplete || values.Length==0)
                throw new NotSupportedException("Offline-state coverage is unknown for "+scope+"; missing services are not evidence of Offline.");
            if(values.Any(s=>!string.Equals(s,"Offline",StringComparison.Ordinal)))
                throw new InvalidOperationException("Every observed connection must be exactly Offline for "+scope+"; no connection state is changed automatically.");
        }
    }
}
