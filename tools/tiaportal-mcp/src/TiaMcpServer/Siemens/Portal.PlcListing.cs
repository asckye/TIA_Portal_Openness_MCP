using System;
using Siemens.Engineering.SW;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Listings must not rely on a SoftwareContainer proxy cached by an earlier operation.
        // Resolve once and retain the typed software for this call. No fuzzy/single-CPU fallback.
        private PlcSoftware ResolvePlcForListing(string path)
        {
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "PLC listing: no project is open.");
            var read = new PlcListingRead();
            var container = read.Required(path + "/@resolveSoftware", () => ResolveSoftwareContainerUncached(path));
            var software = read.Required(path + "/@Software", () => container?.Software);
            if (software is PlcSoftware plc) return plc;
            throw new PortalException(PortalErrorCode.NotFound,
                $"PLC listing: fresh exact resolution of '{path}' did not return PlcSoftware "
                + $"(actual type: {software?.GetType().FullName ?? "null"}; server {typeof(Portal).Assembly.GetName().Version}). "
                + "BlockGroup has NOT been accessed. No regex or single-PLC fallback was used.");
        }
    }
}
