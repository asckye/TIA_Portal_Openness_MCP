using System;
using System.Diagnostics;

namespace TiaMcp.Adapters
{
    internal sealed class BindingSnapshotProcessSource : IBindingProcessObservationSource
    {
        public BindingProcessObservation? Read(int processId)
        {
            try
            {
                // Releases only this short-lived OS query handle, never the TIA portal/project.
                using(var process=Process.GetProcessById(processId))
                {
                    var before=process.StartTime.ToUniversalTime();
                    if(process.HasExited || before!=process.StartTime.ToUniversalTime() || process.HasExited) return null;
                    return new BindingProcessObservation(processId,before.Ticks);
                }
            }
            catch(Exception) /* swallow(env-probe): an exited or inaccessible OS process yields no start-time identity for binding verification */ { return null; }
        }
    }
}
