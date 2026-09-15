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
                return SoftwareContainerLookup.FindUnique(
                    _project.Devices.Cast<object>().Concat(_project.DeviceGroups.Cast<object>()),
                    SoftwareLookupChildren,
                    node => node is Device d ? d.Name : node is DeviceItem item ? item.Name : null,
                    node => node is DeviceItem item ? item.GetService<SoftwareContainer>() : null,
                    sc => sc.Software?.Name, name);
            }
            catch (PortalException) { throw; }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"Hardware/software lookup failed for '{name}' ({ex.GetType().Name}): {ex.Message}. "
                    + "This does not mean the software or its blocks are absent.", null, ex);
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
