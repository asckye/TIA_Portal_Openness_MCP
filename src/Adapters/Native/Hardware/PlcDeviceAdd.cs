using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
#if PLC_HARDWARE_CATALOG
using Hardware = TiaMcp.Adapters.Hardware.HardwarePrimitives;
#endif

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private readonly List<KeyValuePair<object,string>> deviceAddIdentities=new List<KeyValuePair<object,string>>();
        private bool deviceAddOutcomeUnknown;
        private string DeviceAddIdentity(object value)
        {
            foreach(var pair in deviceAddIdentities) if(object.Equals(pair.Key,value)) return pair.Value;
            if(deviceAddIdentities.Count>=16384) throw new InvalidOperationException("Device identity budget exhausted; new explicitly reviewed session required.");
            var identity=Guid.NewGuid().ToString("N");deviceAddIdentities.Add(new KeyValuePair<object,string>(value,identity));return identity;
        }
        public PlcDeviceAddResult AddDeviceWithFallback(string preferredMlfb,string preferredVersion,string deviceName,string family="S7-1500",bool dryRun=true,string expectedPlanHash="",bool confirm=false,string expectedProjectFile="")
        {
            if(deviceAddOutcomeUnknown) throw new InvalidOperationException("Prior device creation outcome unknown; inspect explicitly and establish a new session. Never replay.");
            var request=new PlcDeviceAddRequest {Release=ReleaseKey,PreferredMlfb=preferredMlfb,PreferredVersion=preferredVersion,Name=deviceName,Family=family,DryRun=dryRun,ExpectedHash=expectedPlanHash,Confirm=confirm,ExpectedProject=expectedProjectFile};
            PlcDeviceAddPolicy.ValidateOptions(request);
#if PLC_HARDWARE_CATALOG
            RequireHardwareCatalogBinding();
            PlcLifecyclePolicy.RequireLocalSessionExecution(lifecycle.IsLocalSession,false);
            var project=Project();var portal=Portal();
            request.Project=project.Path.FullName;request.ProcessId=lifecycle.ProcessId??throw new InvalidOperationException("Explicit attached process required.");request.RootIdentity=DeviceAddIdentity(project);
            Action check=()=> {
                RequireProjectIdentity(request.Project);
                if(lifecycle.ProcessId!=request.ProcessId || !object.Equals(Project(),project) || !object.Equals(Portal(),portal)) throw new InvalidOperationException("Project/process/session identity changed.");
            };
            Func<IEnumerable<PlcHardwareCatalogCandidate>> catalog=()=>portal.HardwareCatalog.Find(preferredMlfb).Select(entry=>new PlcHardwareCatalogCandidate {TypeIdentifier=Hardware.TypeIdentifier(entry),ArticleNumber=Hardware.ArticleNumber(entry),Version=Hardware.Version(entry)});
            Func<IEnumerable<PlcDeviceAddItem>> inventory=()=> {
                // Complete root, grouped and ungrouped device inventory; no software/online reads.
                var rows=new List<PlcDeviceAddItem>();var groups=new List<object>();int visited=0;
                Action<DeviceComposition,object> devices=(composition,parent)=> {
                    if(composition==null) throw new InvalidOperationException("Device composition unavailable.");
                    foreach(var device in composition) {
                        if(++visited>4096) throw new InvalidOperationException("Device graph budget exceeded.");
                        if(device==null) throw new InvalidOperationException("Device unavailable.");
                        rows.Add(new PlcDeviceAddItem {Name=device.Name,Identity=DeviceAddIdentity(device),ParentIdentity=DeviceAddIdentity(parent),ParentVerified=object.Equals(device.Parent,parent)});
                    }
                };
                Action<DeviceUserGroup,object,int> visit=null!;
                visit=(group,parent,depth)=> {
                    if(group==null || depth>32 || ++visited>4096 || groups.Any(x=>object.Equals(x,group))) throw new InvalidOperationException("Unsupported, cyclic or oversized device graph.");
                    groups.Add(group);rows.Add(new PlcDeviceAddItem {Name=group.Name,Identity=DeviceAddIdentity(group),ParentIdentity=DeviceAddIdentity(parent),IsGroup=true,ParentVerified=object.Equals(group.Parent,parent)});devices(group.Devices,group);
                    foreach(var child in group.Groups) visit(child,group,depth+1);
                };
                devices(Hardware.Devices(project),project);
                var ungrouped=project.UngroupedDevicesGroup??throw new InvalidOperationException("Ungrouped system group unavailable.");
                rows.Add(new PlcDeviceAddItem {Name="$ungrouped",Identity=DeviceAddIdentity(ungrouped),ParentIdentity=request.RootIdentity,IsGroup=true,ParentVerified=object.Equals(ungrouped.Parent,project)});
                devices(ungrouped.Devices,ungrouped);
                foreach(var group in project.DeviceGroups) visit(group,project,0);
                return rows;
            };
            var result=PlcDeviceAddPolicy.Run(request,catalog,inventory,check,(identifier,name)=> {
                var created=Hardware.CreateWithItem(Hardware.Devices(project),identifier,name,name);
                return created==null ? null! : new PlcDeviceAddItem {Name=created.Name,Identity=DeviceAddIdentity(created),ParentIdentity=request.RootIdentity,ParentVerified=object.Equals(created.Parent,project)};
            });
            if(result.RequiresSessionReset) deviceAddOutcomeUnknown=true;
            return result;
#else
            throw new NotSupportedException("This build has no verified typed hardware catalog selection API.");
#endif
        }
    }
}
