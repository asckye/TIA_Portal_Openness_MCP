using System;

namespace TiaMcp.Adapters.Contracts
{
    public static class PlcSoftwareCapabilities
    {
        public static string? Unsupported(string release, string tool, string action = "", string family = "", string unitName = "", string unitKind = "unit", string targetKind = "", string copyMode = "", string generateOption = "None")
        {
            int version = release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release);
            if (tool == "ManagePlcBlockProtection" && version < 15) return "PlcBlockProtectionProvider requires V15.1.";
            if (tool == "ManagePlcUserGroup" && family == "watchTables" && version < 15) return "Watch-table user groups require V15.1.";
            if (tool == "ManagePlcUserGroup" && family == "technology" && version < 19) return "Technological instance DB user groups require V19.";
            if (tool == "ManagePlcUserGroup" && family == "externalSources" && action == "rename" && version < 21) return "PLC_EXTERNAL_SOURCE_GROUP_RENAME requires V21.";
            if (tool == "ListPlcSystemGroups" && version < 15) return "SystemTypeGroups requires V15.1.";
            if (tool == "CreatePlcInstanceDb" && version < 15) return "CreateInstanceDB requires V15.1.";
            if (tool == "SetPlcProgram" && version < 18) return "UpdateProgram requires V18.";
            if (tool == "GetPlcCrossReferences" && version < 18) return "CrossReferenceService requires V18.";
            if (tool == "ManagePlcExternalSources" && action == "renameGroup" && version < 21) return "PLC_EXTERNAL_SOURCE_GROUP_RENAME requires V21.";
            if (!string.IsNullOrEmpty(unitName) && (version < 16 || unitKind == "safety" && version < 18)) return "The requested PLC unit scope is unavailable in this release.";
            if (tool == "ManagePlcExternalSources" && version < 17 && targetKind.Length != 0) return "Generating into an explicit target group requires V17 or later.";
            if (tool == "ManagePlcExternalSources" && version < 20 && copyMode.Length != 0) return "External-source master-copy modes require V20 or later.";
            if (tool == "ManagePlcExternalSources" && version < 15 && generateOption != "None") return "GenerateBlockOption requires V15.1 or later.";
            if (tool == "BuildAndImportPlcArtifact" && version < 20)
            {
                var format = family.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
                if (format != "udt" && format != "type" && format != "plcstruct" && format != "tagtable" && format != "plctagtable" && format != "globaldb" && format != "db")
                    return "The V21 FlgNet FC/FB builder format is unavailable on this release; UDT, tag-table and global DB XML use the release-specific import header.";
            }
            return null;
        }
    }
}
