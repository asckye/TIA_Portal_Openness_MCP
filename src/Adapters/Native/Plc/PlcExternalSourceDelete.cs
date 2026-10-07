using Documents = TiaMcp.Adapters.Native.Plc.PlcDocumentPrimitives;
using System;
using TiaMcp.Adapters.Contracts;
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
            if(externalDeleteOutcomeUnknown) throw new AdapterPreconditionException("Prior delete outcome unknown; explicit inspection and a new session required. Never replay.","softwarePath",false);
            var request=new PlcExternalSourceDeleteRequest {Release=ReleaseKey,Software=softwarePath,Group=groupPath,Name=externalSourceName,DryRun=dryRun,ExpectedHash=expectedPlanHash,Confirm=confirm,ExpectedProject=expectedProjectFile};
            PlcExternalSourceDeletePolicy.ValidateOptions(request);
            var selected=ReadSelection(softwarePath);
            request.Software=selected.ExactPath;
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            request.Project=Project().Path.FullName;request.ProcessId=lifecycle.ProcessId??throw new AdapterPreconditionException("Explicit attached process required.","softwarePath",false);
            var root=Documents.ExternalSourceGroup(selected.Value)??throw new AdapterPreconditionException("External-source root unavailable.","softwarePath",false);
            request.RootIdentity=externalDeleteIdentities.Get(root);
            Action check=()=> {
                RequireProjectIdentity(request.Project);
                if(lifecycle.ProcessId!=request.ProcessId) throw new AdapterPreconditionException("Process identity changed.","softwarePath",false);
                var current=ReadSelection(softwarePath);
                if(current.ExactPath!=selected.ExactPath || !object.Equals(current.Value,selected.Value) || !object.Equals(current.Context,selected.Context) || !object.Equals(Documents.ExternalSourceGroup(current.Value),root)) throw new AdapterPreconditionException("PLC/root identity changed.","softwarePath",false);
                RequireTargetOffline(current);
            };
            Func<IEnumerable<PlcExternalSourceDeleteItem>> inventory=()=>Documents.Sources(root).Select(source=>new PlcExternalSourceDeleteItem {Name=Documents.Name(source),Identity=externalDeleteIdentities.Get(source),ParentVerified=object.Equals(source.Parent,root)});
            PlcExternalSource? nativeTarget=null;
            var result=PlcExternalSourceDeletePolicy.Run(request,inventory,check,identity=> {
                // The complete fresh snapshot must contain the identical object, name and direct root parent.
                var source=externalDeleteIdentities.Resolve(identity) as PlcExternalSource??throw new AdapterPreconditionException("Source identity unavailable.","softwarePath",false);
                nativeTarget=Documents.Find(Documents.Sources(root),externalSourceName);
                if(Documents.Name(source)!=externalSourceName || nativeTarget==null || Documents.Name(nativeTarget)!=externalSourceName || !object.Equals(source.Parent,root) || !object.Equals(nativeTarget.Parent,root) || !object.Equals(nativeTarget,source)) throw new AdapterPreconditionException("Exact source identity changed; delete not called.","softwarePath",false);
                },identity=> Documents.Delete(nativeTarget!));
            if(result.RequiresSessionReset) externalDeleteOutcomeUnknown=true;
            return result;
        }
    }
}
