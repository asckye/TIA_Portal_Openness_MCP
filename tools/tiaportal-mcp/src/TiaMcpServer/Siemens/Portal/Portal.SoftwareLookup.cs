using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        private SoftwareContainer? ResolveBareSoftwareContainer(string name)
        {
            if (_project == null) return null;
            try
            {
                var direct = SoftwareContainerLookup.FindUnique(
                    _project.Devices.Cast<object>().Concat(_project.DeviceGroups.Cast<object>()),
                    SoftwareLookupChildren,
                    node => node is Device d ? d.Name : node is DeviceItem item ? item.Name : null,
                    node => node is DeviceItem item ? item.GetService<SoftwareContainer>() : null,
                    sc => sc.Software?.Name, name);
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
                    if (sc?.Software != null) yield return sc;
                    foreach (var child in item.DeviceItems) pending.Push(child);
                }
            }
            IEnumerable<SoftwareContainer> Devices(DeviceComposition devices)
            {
                foreach (var device in devices)
                    foreach (var sc in Items(device.DeviceItems)) yield return sc;
            }
            foreach (var sc in Devices(_project.Devices)) yield return sc;
            var groups = new Stack<DeviceUserGroup>(_project.DeviceGroups);
            while (groups.Count > 0)
            {
                if (++count > 100000) throw new PortalException(PortalErrorCode.OpennessError,
                    "Exact software enumeration limit reached; no target selected.");
                var group = groups.Pop();
                foreach (var sc in Devices(group.Devices)) yield return sc;
                foreach (var child in group.Groups) groups.Push(child);
            }
        }

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
