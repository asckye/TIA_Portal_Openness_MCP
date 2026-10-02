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
                throw new NotSupportedException("Exact device and device-item identity are required for offline checking.");
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
        private void RequireProjectOffline()
        {
            PlcOfflinePolicy.RequireReviewedExecution("project-wide compile");
            PlcOfflinePolicy.RequireDocumentedRelease(ReleaseKey);
            var project=Project(); int count=0;
            foreach(var device in project.Devices) { RequireDeviceOffline(device); count++; }
            foreach(var device in project.UngroupedDevicesGroup.Devices) { RequireDeviceOffline(device); count++; }
            foreach(var group in project.DeviceGroups) count+=RequireGroupOffline(group,0);
            if(count==0) throw new NotSupportedException("An empty device inventory cannot establish all-device offline coverage.");
        }
        private static int RequireGroupOffline(DeviceUserGroup group,int depth)
        {
            Depth(depth); int count=0;
            foreach(var device in group.Devices) { RequireDeviceOffline(device); count++; }
            foreach(var child in group.Groups) count+=RequireGroupOffline(child,depth+1);
            return count;
        }
        private static void RequireDeviceOffline(Device device)
        {
            var states=new List<string?>(); bool covered=true;
            bool redundant=ReadRedundantStates(device,states);
            foreach(var item in device.DeviceItems) ReadItemStates(item,redundant,states,ref covered,0);
            // Devices with no recognized provider remain unknown (including HMI or
            // unsupported hardware), even if other PLCs in the project are offline.
            PlcOfflinePolicy.RequireStates(states,covered,"device "+device.Name);
        }
        private static void ReadItemStates(DeviceItem item,bool redundant,List<string?> states,ref bool covered,int depth)
        {
            Depth(depth);
            var provider=((IEngineeringServiceProvider)item).GetService<OnlineProvider>();
            if(provider!=null) states.Add(provider.State.ToString());
            var software=((IEngineeringServiceProvider)item).GetService<SoftwareContainer>()?.Software;
            if(software!=null && (!(software is PlcSoftware) || (!redundant && provider==null))) covered=false;
            foreach(var child in item.DeviceItems) ReadItemStates(child,redundant,states,ref covered,depth+1);
        }
        private T WithTargetOffline<T>(PlcReadCandidate<PlcSoftware> selected,Func<T> action)
        { RequireTargetOffline(selected); return action(); }
    }
}
