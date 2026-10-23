using System;

namespace TiaMcp.Adapters.Contracts
{
    public static class PlcSoftwareCapabilities
    {
        public static string? Unsupported(string release, string tool, string action = "", string family = "", string unitName = "", string unitKind = "unit", string targetKind = "", string copyMode = "", string generateOption = "None")
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release, System.Globalization.CultureInfo.InvariantCulture);
            if (PortedFamilies.Contains(tool) && !PortedFamilies.Available(release, tool)) return "The requested tool is unavailable in this release: " + tool;
            if ((tool == "ListPlcSoftwareUnits" || tool == "ManagePlcSoftwareUnit") && unitKind == "safety" && version < 18) return "PlcSafetyUnit requires V18.";
            if (tool == "ManagePlcSoftwareUnit" && copyMode.Length != 0 && version < 20) return "Software-unit master-copy modes require V20.";
            if (tool == "ManagePlcBlockProtection" && version < 15) return "PlcBlockProtectionProvider requires V15.1.";
            if (tool == "ManagePlcUserGroup" && family == "watchTables" && version < 15) return "Watch-table user groups require V15.1.";
            if (tool == "ManagePlcUserGroup" && family == "technology" && version < 19) return "Technological instance DB user groups require V19.";
            if (tool == "ManagePlcUserGroup" && family == "externalSources" && action == "rename" && version < 21) return "PLC_EXTERNAL_SOURCE_GROUP_RENAME requires V21.";
            // API audit 2026-10-19 F20 (SDK DLL metadata): the user-group Name has a public setter from V21 for block, type and
            // watch-table groups and from V20 for tag-table groups; the rename is refused instead of a SetAttribute fallback.
            if (tool == "ManagePlcUserGroup" && action == "rename" && (family == "blocks" || family == "types" || family == "watchTables") && version < 21)
                return "Renaming " + family + " user groups requires V21: the group Name has no public setter on this release.";
            if (tool == "ManagePlcUserGroup" && action == "rename" && family == "tags" && version < 20)
                return "Renaming tags user groups requires V20: the group Name has no public setter on this release.";
            if (tool == "CreatePlcInstanceDb" && version < 15) return "CreateInstanceDB requires V15.1.";
            if (tool == "SetPlcProgram" && version < 18) return "UpdateProgram requires V18.";
            if (tool == "GetPlcCrossReferences" && version < 18) return "CrossReferenceService requires V18.";
            if (tool == "ManagePlcExternalSources" && action == "renameGroup" && version < 21) return "PLC_EXTERNAL_SOURCE_GROUP_RENAME requires V21.";
            if (!string.IsNullOrEmpty(unitName) && (version < 16 || unitKind == "safety" && version < 18)) return "The requested PLC unit scope is unavailable in this release.";
            if (tool == "ManagePlcExternalSources" && version < 17 && targetKind.Length != 0) return "Generating into an explicit target group requires V17 or later.";
            if (tool == "ManagePlcExternalSources" && version < 20 && copyMode.Length != 0) return "External-source master-copy modes require V20 or later.";
            if (tool == "ManagePlcExternalSources" && version < 15 && generateOption != "None") return "GenerateBlockOption requires V15.1 or later.";
            // 2026-10-20 decision: the whole-block FC/FB composers write V21 documents and open only on V21 (V20 withdrawn).
            if (tool == "BuildAndImportPlcArtifact" && version < 21)
            {
                var format = family.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
                if (format != "udt" && format != "type" && format != "plcstruct" && format != "tagtable" && format != "plctagtable" && format != "globaldb" && format != "db")
                    return "The V21 FlgNet FC/FB builder format is available on V21 only; UDT, tag-table and global DB XML use the release-specific import header.";
            }
            return null;
        }
    }
}
