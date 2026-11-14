using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Siemens.Engineering;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        public PlcRuntimeState ReadState()
        {
            Check();
            return new PlcRuntimeState { ReleaseKey=ReleaseKey,IsAttached=lifecycle.ProcessId.HasValue,
                ProcessId=lifecycle.ProcessId,ProjectFile=lifecycle.ProjectFile,OwnsProject=lifecycle.OwnsProject,
                IsLocalSession=lifecycle.IsLocalSession };
        }
        // GetProcesses returns diagnostic snapshots; no Portal(), Project(), Attach(), launch or disposal.
        public PlcRecoveryProcess[] ReadRecoveryProcesses()
        {
            Check();
            var rows = new List<PlcRecoveryProcess>();
            foreach (var process in TiaPortal.GetProcesses())
            {
                string? start = null;
                try
                {
                    using (var observed = Process.GetProcessById(process.Id))
                    {
                        var before = observed.StartTime.ToUniversalTime();
                        if (!observed.HasExited && observed.StartTime.ToUniversalTime() == before)
                            start = before.ToString("O", CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException
                    || error is System.ComponentModel.Win32Exception || error is System.Security.SecurityException)
                { /* swallow(env-probe): unavailable OS identity excludes the same-PID fast path. */ }
                rows.Add(new PlcRecoveryProcess { ProcessId = process.Id, ProcessStartUtc = start,
                    ProjectPath = process.ProjectPath?.FullName, AttachedSessions = process.AttachedSessions.Count });
            }
            return rows.ToArray();
        }

        public PlcProcessQuery ReadPortalProcessProjects()
        {
            Check();
            try
            {
                var rows=new List<PlcProcessSnapshot>();
                var ids=new HashSet<int>();
                foreach(var process in TiaPortal.GetProcesses())
                {
                    if(rows.Count>=1024 || process.Id<=0 || !ids.Add(process.Id))
                        throw new InvalidOperationException("Invalid or excessive process snapshot.");
                    // Exact V14 SP1 / V15.1 / V16–V21 metadata confirms FileInfo (base V14 excluded).
                    string? projectPath=process.ProjectPath?.FullName;
                    var row=new PlcProcessSnapshot { ProcessId=process.Id,ProjectPath=projectPath,
                        SnapshotAcquisitionTime=process.AcquisitionTime.ToString("O",CultureInfo.InvariantCulture) };
                    // Independent OS observation, never AcquisitionTime and never used as an attach token.
                    // Access denial, exit races and missing permissions remain unknown, without raw error text.
                    try
                    {
                        using(var os=Process.GetProcessById(row.ProcessId))
                        {
                            var before=os.StartTime.ToUniversalTime();
                            if(!os.HasExited && os.StartTime.ToUniversalTime()==before)
                            { row.OsStartTimeUtc=before.ToString("O",CultureInfo.InvariantCulture); row.OsIdentityStatus="observed-not-bound"; }
                        }
                    }
                    catch(Exception) /* swallow(env-probe): OS process exit or access failure leaves start time and identity explicitly unknown */ { row.OsStartTimeUtc=null; row.OsIdentityStatus="unknown"; }
                    rows.Add(row);
                }
                return new PlcProcessQuery { ReleaseKey=ReleaseKey,Processes=rows.ToArray() };
            }
            catch(Exception) { throw new InvalidOperationException("Portal process metadata read failed; no attachment attempted."); }
        }
        public PlcConnectReadiness ReadPortalConnectReadiness(int processId)
        {
            Check(); PlcRuntimeQueryPolicy.RequirePid(processId);
            return PlcRuntimeQueryPolicy.Diagnose(ReadPortalProcessProjects(),processId);
        }
    }
}
