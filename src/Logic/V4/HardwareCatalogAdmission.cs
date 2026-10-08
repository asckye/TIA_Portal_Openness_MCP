using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;

namespace TiaMcp.Logic.V4
{
    public static class HardwareCatalogAdmission
    {
        public static void RequireBound(bool available, bool foundation)
        {
            if (!available) throw new AdapterPreconditionException("Hardware catalog tools require " +
                (foundation ? "ConnectPortal followed by AttachOpenProject" : "ListPortalProcessProjects followed by ConnectProject") + " first.", "session", false);
        }

        public static void ExactRow(IEnumerable<DeviceCatalogEntry> rows, string preferredMlfb, string preferredVersion)
        {
            bool identifier = preferredMlfb.StartsWith("OrderNumber:", StringComparison.Ordinal);
            if (rows.Count(row => identifier ? row.TypeIdentifier == preferredMlfb && preferredVersion == ""
                : row.ArticleNumber == preferredMlfb && row.Version == preferredVersion) != 1)
                throw new AdapterPreconditionException("preferredMlfb/preferredVersion must select exactly one catalog row. Run SearchHardwareCatalog and copy articleNumber and version from the same row, preserving spaces.", "preferredMlfb/preferredVersion");
        }

        public static void AvailableName(IEnumerable<string> names, string deviceName)
        {
            if (names.Any(name => string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase)))
                throw new AdapterPreconditionException("Device name already exists; no create attempted. Choose a new deviceName and preview again.", "deviceName", false);
        }
    }
}
