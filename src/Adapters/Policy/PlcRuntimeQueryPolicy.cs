using System;
using System.Globalization;

namespace TiaMcp.Adapters
{
    internal static class PlcRuntimeQueryPolicy
    {
        internal static void RequirePid(int processId)
        { if(processId<=0) throw new ArgumentException("Select an explicit positive processId."); }
        internal static PlcConnectReadiness Diagnose(PlcProcessQuery query,int processId)
        {
            RequirePid(processId);
            PlcProcessSnapshot? match=null;
            foreach(var item in query.Processes) if(item.ProcessId==processId)
            { if(match!=null) throw new InvalidOperationException("Duplicate process identity in diagnostic snapshot."); match=item; }
            return new PlcConnectReadiness { ReleaseKey=query.ReleaseKey,ProcessId=processId,ProcessFound=match!=null,Process=match,
                Readiness=match==null ? "not-found" : "unknown",
                Reason=match==null ? "Selected process absent from this release snapshot." : "Process metadata does not prove attach permission, installed options, or runtime readiness. No attachment was attempted." };
        }
    }
}
