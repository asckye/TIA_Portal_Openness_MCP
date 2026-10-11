using System;

namespace TiaMcp.Adapters.Contracts
{
    public static class LibraryCapabilities
    {
        public static string? Unsupported(string release, string operation, string action = "", string sourceKind = "",
            System.Collections.Generic.IEnumerable<string>? properties = null,
            string forceUpdateMode = "SetOnlyHigherUpdatedVersionAsDefault", string deleteUnusedVersionsMode = "DoNotDelete", string structureConflictResolutionMode = "RetainStructure")
        {
            if (operation == "ManageLibraryTypeVersion" && (action == "edit" || action == "release" || action == "discard") && !EngineeringServicesCapabilities.AtLeast(release, "20"))
                return "Library version editing/release/discard requires V20 or later.";
            if (operation == "ManageLibraryTypeVersion" && action == "setDefault" && !EngineeringServicesCapabilities.AtLeast(release, "17"))
                return "Library version SetAsDefault requires V17 or later.";
            if (operation == "SynchronizeLibrary" && (action == "harmonizeProject" || action == "cleanUp") && !EngineeringServicesCapabilities.AtLeast(release, "17"))
                return "Library harmonization/cleanup requires V17 or later.";
            if ((operation == "ManageLibraryType" || operation == "SynchronizeLibrary") && (action == "updateLibrary" || action == "updateProject")
                && !EngineeringServicesCapabilities.AtLeast(release, "17") && (forceUpdateMode != "SetOnlyHigherUpdatedVersionAsDefault" || deleteUnusedVersionsMode != "DoNotDelete" || structureConflictResolutionMode != "RetainStructure"))
                return "Explicit library update modes require V17 or later; earlier APIs expose the simple overload only.";
            if (operation == "ManageGlobalLibrary" && (action == "archive" || action == "retrieve" || action == "saveAs") && !EngineeringServicesCapabilities.AtLeast(release, "15.1"))
                return "Library archive/retrieve/SaveAs requires V15.1 or later.";
            if (operation == "ManageLibraryType" && properties != null)
                foreach (var property in properties)
                {
                    if (property == "DoNotUse" && !EngineeringServicesCapabilities.AtLeast(release, "20")) return "LibraryType.DoNotUse requires V20 or later.";
                    if (property == "SetForUpdate" && !EngineeringServicesCapabilities.AtLeast(release, "18")) return "LibraryType.SetForUpdate requires V18 or later.";
                }
            if ((operation == "CompareLibraryObjects" || operation == "ManageLibraryMasterCopy" && action == "compare")
                && !EngineeringServicesCapabilities.AtLeast(release, "18"))
                return "Library comparison requires V18 or later.";
            if (operation == "ImportLibraryTypeDocuments" && !EngineeringServicesCapabilities.AtLeast(release, "20"))
                return "Library document import requires V20 or later.";
            if (operation == "ImportMasterCopyFromGlobalLibrary" && !EngineeringServicesCapabilities.AtLeast(release, "19"))
                return "Unified HMI master-copy import requires V19 or later.";
            if (operation == "CreateLibraryMasterCopy" && sourceKind == "screen"
                && !EngineeringServicesCapabilities.AtLeast(release, "16"))
                return "Unified HMI library operations require V16 or later.";
            return null;
        }
    }
}
