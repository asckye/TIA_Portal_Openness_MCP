using System;
using System.Globalization;

namespace TiaMcp.PlcFoundation
{
    // Cached attachment is deliberately not proof that the same OS process still exists.
    public sealed class PlcRuntimeState
    {
        public string ReleaseKey { get; set; } = "";
        public bool IsAttached { get; set; }
        public int? ProcessId { get; set; }
        public string? ProjectFile { get; set; }
        public bool OwnsProject { get; set; }
        public bool IsLocalSession { get; set; }
        public string IdentityStatus { get; set; } = "unverified-cached-pid";
        public string RuntimeConnectionStatus { get; set; } = "not-probed";
    }
    public sealed class PlcProcessSnapshot
    {
        public int ProcessId { get; set; }
        public string SnapshotAcquisitionTime { get; set; } = "";
        public string? ProjectPath { get; set; }
        public string? OsStartTimeUtc { get; set; }
        public string OsIdentityStatus { get; set; } = "unknown";
    }
    public sealed class PlcProcessQuery
    {
        public string ReleaseKey { get; set; } = "";
        public PlcProcessSnapshot[] Processes { get; set; } = new PlcProcessSnapshot[0];
        public string IdentityStrategy { get; set; } = "os-pid-and-start-time-observation-only";
    }
    public sealed class PlcConnectReadiness
    {
        public string ReleaseKey { get; set; } = "";
        public int ProcessId { get; set; }
        public bool ProcessFound { get; set; }
        public string Readiness { get; set; } = "unknown";
        public string PermissionStatus { get; set; } = "not-probed";
        public string Reason { get; set; } = "";
        public PlcProcessSnapshot? Process { get; set; }
    }
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
