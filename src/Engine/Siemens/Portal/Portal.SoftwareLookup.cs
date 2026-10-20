using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private SoftwareContainer? ResolveBareSoftwareContainer(string name)
            => ResolveSoftwareLookup(name, false);

        private SoftwareContainer? ResolveSoftwareLookup(string name, bool plcOnly)
        {
            if (_project == null) return null;
            try
            {
                // The first level is the root devices, the user groups and the UngroupedDevicesGroup devices,
                // as in ProjectDeviceWalk.
                var ungrouped = _project.UngroupedDevicesGroup;
                var roots = _project.Devices.Cast<object>().Concat(_project.DeviceGroups.Cast<object>())
                    .Concat(ungrouped == null ? Enumerable.Empty<object>() : ungrouped.Devices.Cast<object>());
                string? Alias(object node) => node is Device d ? d.Name : node is DeviceItem item ? item.Name
                    : plcOnly && node is DeviceUserGroup group ? SoftwareLookupGroupName(group) : null;
                SoftwareContainer? Container(object node) => node is DeviceItem item ? item.GetService<SoftwareContainer>() : null;
                string? Name(SoftwareContainer sc) => sc.Software?.Name;
                var direct = plcOnly
                    ? SoftwareContainerLookup.FindPlc(roots, SoftwareLookupChildren, Alias,
                        node => { var sc = Container(node); return SoftwareLookupValue(sc) is PlcSoftware ? sc : null; },
                        Name, node => node is DeviceUserGroup, name, paths => _plcLookupPathsSuffix = paths)
                    : SoftwareContainerLookup.FindUnique(roots, SoftwareLookupChildren, Alias, Container, Name, name);
                if (plcOnly) return direct;
                if (direct != null) return direct;
                // The same typed hardware traversal used by GetAllPlcSoftware, but retain
                // containers, reject duplicates, and never swallow an incomplete scan.
                return ExactSoftwareMatch.Select(EnumerateSoftwareContainersForExactLookup(), name,
                    sc => sc.Software?.Name);
            }
            catch (PortalException) { throw; }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"Hardware/software lookup failed for '{name}' ({ex.GetType().Name}): {ex.Message}. "
                    + "This does not mean the software or its blocks are absent.", null, ex);
            }
        }

        public string[] GetPlcSoftwareNamesForDesktop()
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open.");
            return EnumerateSoftwareContainersForExactLookup()
                .Select(c => c.Software).OfType<PlcSoftware>().Select(p => p.Name).ToArray();
        }

        private IEnumerable<SoftwareContainer> EnumerateSoftwareContainersForExactLookup()
        {
            if (_project == null) yield break;
            int count = 0;
            IEnumerable<SoftwareContainer> Items(IEnumerable<DeviceItem> items)
            {
                var pending = new Stack<DeviceItem>(items);
                while (pending.Count > 0)
                {
                    if (++count > 100000) throw new PortalException(PortalErrorCode.OpennessError,
                        "Exact software enumeration limit reached; no target selected.");
                    var item = pending.Pop();
                    var sc = item.GetService<SoftwareContainer>();
                    if (SoftwareLookupValue(sc) != null) yield return sc!;
                    foreach (var child in item.DeviceItems) pending.Push(child);
                }
            }
            // Root devices, nested user groups and UngroupedDevicesGroup through the shared walk (group depth
            // limit 64); the element budget also counts the devices.
            foreach (var device in EnumerateAllDevices())
            {
                if (++count > 100000) throw new PortalException(PortalErrorCode.OpennessError,
                    "Exact software enumeration limit reached; no target selected.");
                foreach (var sc in Items(device.DeviceItems)) yield return sc;
            }
        }

        private static Software? SoftwareLookupValue(SoftwareContainer? container)
            => container?.Software;

        private static IEnumerable<object> SoftwareLookupChildren(object node)
        {
            if (node is DeviceUserGroup group)
                return group.Devices.Cast<object>().Concat(group.Groups.Cast<object>());
            if (node is Device device) return device.DeviceItems.Cast<object>();
            if (node is DeviceItem item) return item.DeviceItems.Cast<object>();
            return Enumerable.Empty<object>();
        }
    }
}
