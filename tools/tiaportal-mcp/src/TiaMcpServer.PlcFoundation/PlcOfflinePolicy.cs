using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    internal static class PlcOfflinePolicy
    {
        internal static void RequireReviewedExecution(string operation)
        {
            throw new NotSupportedException("Execution remains blocked for "+operation+": hardware classification/R-H coverage review is incomplete; preview does not establish readiness.");
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
