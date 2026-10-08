using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
using System;
using TiaMcp.Adapters.Contracts;
using System.Linq;

namespace TiaMcp.Adapters
{
    public sealed partial class PlcFoundationEngine
    {
        // This is deliberately a distinct planning operation, not the legacy import operation.
        public PlcExternalSourceImportPlan PlanPlcExternalSourceImport(string softwarePath,string groupPath,string filePath,string allowedFilePath,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="")
        {
            var request=new PlcExternalSourceImportRequest {Release=ReleaseKey,Software=softwarePath,Group=groupPath,File=filePath,AllowedFile=allowedFilePath,DryRun=dryRun,ExpectedHash=expectedPlanHash,Confirm=confirm,ExpectedProject=expectedProjectFile};
            PlcExternalSourceImportPolicy.ValidateOptions(request); // apply refusal precedes native reads and filesystem access
            var selected=ReadSelection(softwarePath);
            request.Software=selected.ExactPath;
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            request.Project=Project().Path.FullName;
            request.ProcessId=lifecycle.ProcessId ?? throw new AdapterPreconditionException("Explicit attached process required.","softwarePath",false);
            var root=Documents.ExternalSourceGroup(selected.Value) ?? throw new AdapterPreconditionException("External-source root unavailable.","softwarePath",false);
            Action check=()=> {
                RequireProjectIdentity(request.Project);
                if(lifecycle.ProcessId!=request.ProcessId) throw new AdapterPreconditionException("Process identity changed.","softwarePath",false);
                var current=ReadSelection(softwarePath);
                if(current.ExactPath!=selected.ExactPath || !object.Equals(current.Value,selected.Value) || !object.Equals(current.Context,selected.Context) || !object.Equals(Documents.ExternalSourceGroup(current.Value),root)) throw new AdapterPreconditionException("PLC/root identity changed.","softwarePath",false);
                // Conservative wrapper restriction, not claimed as a CreateFromFile API prerequisite.
                RequireTargetOffline(current);
            };
            check();
            return PlcExternalSourceImportPolicy.Plan(request,PlcExternalSourceImportPolicy.OpenLocked,()=>Documents.Sources(root).Select(source=>Documents.Name(source)),check);
        }
    }
}
