using System;
using Siemens.Engineering.SW;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Use the same resolver as GetSoftwareInfo and GetPlcTagTables, including its
        // project-wide PLC enumeration fallback. Resolution never reads block/type groups.
        private PlcSoftware ResolvePlcForListing(string path)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "PLC listing: no project is open.");
            var software = new PlcListingRead().Required(path + "/@GetPlcSoftware",
                () => GetPlcSoftware(path));
            if (software != null) return software;
            throw new PortalException(PortalErrorCode.NotFound,
                $"PLC listing: shared GetPlcSoftware resolver could not resolve '{path}' "
                + $"(server {typeof(Portal).Assembly.GetName().Version}). "
                + "BlockGroup/TypeGroup have NOT been accessed." + AvailablePlcPathsSuffix());
        }
    }
}
