using System;
using System.Linq;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        // This is deliberately a distinct planning operation, not the legacy import operation.
        public PlcExternalSourceImportPlan PlanPlcExternalSourceImport(string softwarePath,string groupPath,string filePath,string allowedFilePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="")
        {
            var request=new PlcExternalSourceImportRequest {Release=ReleaseKey,Software=softwarePath,Group=groupPath,File=filePath,AllowedFile=allowedFilePath,DryRun=dryRun,ExpectedHash=expectedPlanHash,Confirm=confirm,ExpectedProject=expectedProjectFile};
            PlcExternalSourceImportPolicy.ValidateOptions(request); // apply refusal precedes native reads and filesystem access
            var selected=ReadSelection(softwarePath);
            if(selected.ExactPath!=softwarePath) throw new ArgumentException("Exact softwarePath required; aliases refused.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            request.Project=Project().Path.FullName;
            request.ProcessId=lifecycle.ProcessId ?? throw new InvalidOperationException("Explicit attached process required.");
            var root=selected.Value.ExternalSourceGroup ?? throw new InvalidOperationException("External-source root unavailable.");
            Action check=()=> {
                RequireProjectIdentity(request.Project);
                if(lifecycle.ProcessId!=request.ProcessId) throw new InvalidOperationException("Process identity changed.");
                var current=ReadSelection(softwarePath);
                if(current.ExactPath!=selected.ExactPath || !ReferenceEquals(current.Value,selected.Value) || !ReferenceEquals(current.Context,selected.Context) || !ReferenceEquals(current.Value.ExternalSourceGroup,root)) throw new InvalidOperationException("PLC/root identity changed.");
                // Conservative wrapper restriction, not claimed as a CreateFromFile API prerequisite.
                RequireTargetOffline(current);
            };
            check();
            return PlcExternalSourceImportPolicy.Plan(request,PlcExternalSourceImportPolicy.OpenLocked,()=>root.ExternalSources.Select(source=>source.Name),check);
        }
    }
}
