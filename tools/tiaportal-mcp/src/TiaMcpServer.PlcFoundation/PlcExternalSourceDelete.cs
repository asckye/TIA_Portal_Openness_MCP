using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.SW.ExternalSources;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        // Siemens engineering-object Equals compares logical identity across proxy instances.
        private readonly PlcExternalSourceDeleteIdentities externalDeleteIdentities=new PlcExternalSourceDeleteIdentities();
        private bool externalDeleteOutcomeUnknown;
        public PlcExternalSourceDeleteResult DeletePlcExternalSource(string softwarePath,string groupPath,string externalSourceName,bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="")
        {
            if(externalDeleteOutcomeUnknown) throw new InvalidOperationException("Prior delete outcome unknown; explicit inspection and a new session required. Never replay.");
            var request=new PlcExternalSourceDeleteRequest {Release=ReleaseKey,Software=softwarePath,Group=groupPath,Name=externalSourceName,DryRun=dryRun,ExpectedHash=expectedPlanHash,Confirm=confirm,ExpectedProject=expectedProjectFile};
            PlcExternalSourceDeletePolicy.ValidateOptions(request);
            var selected=ReadSelection(softwarePath);
            if(selected.ExactPath!=softwarePath) throw new ArgumentException("Exact softwarePath required; aliases refused.");
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            request.Project=Project().Path.FullName;request.ProcessId=lifecycle.ProcessId??throw new InvalidOperationException("Explicit attached process required.");
            var root=selected.Value.ExternalSourceGroup??throw new InvalidOperationException("External-source root unavailable.");
            request.RootIdentity=externalDeleteIdentities.Get(root);
            Action check=()=> {
                RequireProjectIdentity(request.Project);
                if(lifecycle.ProcessId!=request.ProcessId) throw new InvalidOperationException("Process identity changed.");
                var current=ReadSelection(softwarePath);
                if(current.ExactPath!=selected.ExactPath || !object.Equals(current.Value,selected.Value) || !object.Equals(current.Context,selected.Context) || !object.Equals(current.Value.ExternalSourceGroup,root)) throw new InvalidOperationException("PLC/root identity changed.");
                RequireTargetOffline(current);
            };
            Func<IEnumerable<PlcExternalSourceDeleteItem>> inventory=()=>root.ExternalSources.Select(source=>new PlcExternalSourceDeleteItem {Name=source.Name,Identity=externalDeleteIdentities.Get(source),ParentVerified=object.Equals(source.Parent,root)});
            PlcExternalSource? nativeTarget=null;
            var result=PlcExternalSourceDeletePolicy.Run(request,inventory,check,identity=> {
                // The complete fresh snapshot must contain the identical object, name and direct root parent.
                var source=externalDeleteIdentities.Resolve(identity) as PlcExternalSource??throw new InvalidOperationException("Source identity unavailable.");
                nativeTarget=root.ExternalSources.Find(externalSourceName);
                if(source.Name!=externalSourceName || nativeTarget==null || nativeTarget.Name!=externalSourceName || !object.Equals(source.Parent,root) || !object.Equals(nativeTarget.Parent,root) || !object.Equals(nativeTarget,source)) throw new InvalidOperationException("Exact source identity changed; delete not called.");
                },identity=> nativeTarget!.Delete());
            if(result.RequiresSessionReset) externalDeleteOutcomeUnknown=true;
            return result;
        }
    }
}
