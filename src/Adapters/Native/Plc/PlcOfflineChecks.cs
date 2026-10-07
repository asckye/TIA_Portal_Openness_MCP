using System;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;

namespace TiaMcp.PlcFoundation
{
    public sealed partial class PlcFoundationEngine
    {
        private static bool ReadRedundantStates(Device device,List<string?> states)
        {
#if PLC_RH
            var provider=((IEngineeringServiceProvider)device).GetService<RHOnlineProvider>();
            if(provider!=null) { states.Add(provider.PrimaryState.ToString()); states.Add(provider.BackupState.ToString()); return true; }
#endif
            return false;
        }
        private void RequireTargetOffline(PlcReadCandidate<PlcSoftware> selected)
        {
            PlcOfflinePolicy.RequireDocumentedRelease(ReleaseKey);
            if(!(selected.DeviceContext is Device device) || !(selected.Context is DeviceItem item))
                throw new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Exact device and device-item identity are required for offline checking.","offline-state",false);
            // Positive standard-provider evidence, never absence of R/H as proof.
            // R/H exchange remains outside this bounded standard-target contract.
            bool redundant=false;
#if PLC_RH
            redundant=((IEngineeringServiceProvider)device).GetService<RHOnlineProvider>()!=null;
#endif
            PlcOfflinePolicy.RequireStandardTarget(
                device.DeviceItems, item, selected.Value,
                current=>current.DeviceItems,
                current=>((IEngineeringServiceProvider)current).GetService<SoftwareContainer>()?.Software as PlcSoftware,
                current=>
                {
                    var provider=((IEngineeringServiceProvider)current).GetService<OnlineProvider>();
                    return new PlcOfflineObservation(provider!=null,provider?.State.ToString());
                }, redundant);
        }
        private string[] CheckProjectOfflineProviders()
        {
            PlcOfflinePolicy.RequireDocumentedRelease(ReleaseKey);
            var project=Project();
            var unobserved=new List<string>();
            PlcOfflinePolicy.RequireProjectDevicesOffline(project.Devices,project.UngroupedDevicesGroup.Devices,
                project.DeviceGroups,group=>group.Devices,group=>group.Groups,
                device=> { if(!CheckDeviceOfflineProviders(device)) unobserved.Add(device.Name); });
            return unobserved.ToArray();
        }
        private static bool CheckDeviceOfflineProviders(Device device)
        {
            var states=new List<string?>(); bool covered=true,unobservedSoftware=false;
            bool redundant=ReadRedundantStates(device,states);
            foreach(var item in device.DeviceItems) ReadItemStates(item,redundant,states,ref covered,ref unobservedSoftware,0);
            return PlcOfflinePolicy.CheckCompileStates(states,covered,"device "+device.Name) && !unobservedSoftware;
        }
        private static void ReadItemStates(DeviceItem item,bool redundant,List<string?> states,ref bool covered,ref bool unobservedSoftware,int depth)
        {
            Depth(depth);
            var provider=((IEngineeringServiceProvider)item).GetService<OnlineProvider>();
            if(provider!=null) states.Add(provider.State.ToString());
            var software=((IEngineeringServiceProvider)item).GetService<SoftwareContainer>()?.Software;
            if(software is PlcSoftware && !redundant && provider==null) covered=false;
            if(software!=null && !(software is PlcSoftware) && provider==null) unobservedSoftware=true;
            foreach(var child in item.DeviceItems) ReadItemStates(child,redundant,states,ref covered,ref unobservedSoftware,depth+1);
        }
        private T WithTargetOffline<T>(PlcReadCandidate<PlcSoftware> selected,Func<T> action)
        { RequireTargetOffline(selected); return action(); }
    }
}
