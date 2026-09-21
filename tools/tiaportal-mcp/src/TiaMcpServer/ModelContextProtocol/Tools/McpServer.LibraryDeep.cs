using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        [McpServerTool(Name="ReadLibraryOverview"), Description("[L2][Library][READ] Official library overview: empty libraryName = project library, otherwise an exact already-open global library. Header (GlobalLibrary Author/Comment per culture/Copyright/Family/Version/Path/CreationTime/LastModified(By)/IsModified/IsWriteProtected/Size, HistoryEntries, UsedProducts), the Types folder tree (LibraryTypeFolder Status, each LibraryType with Guid/Namespace/DoNotUse/SetForUpdate/MinimumTargetDeviceVersion/Status/version summary) and the master copy tree (MasterCopy Author/CreationDate/ContentDescriptions). Bounded by maxDepth/maxItems; no modification.")]
        public static ResponseMessage ReadLibraryOverview(
            string libraryName="",
            [Description("includeTypes: true also lists the types.")] bool includeTypes=true,
            [Description("includeMasterCopies: true also lists the master copies.")] bool includeMasterCopies=true,
            int maxDepth=6,
            int maxItems=500)
            => Portal.ReadLibraryOverview(libraryName,includeTypes,includeMasterCopies,maxDepth,maxItems);
        [McpServerTool(Name="ReadLibraryType"), Description("[L2][Library][READ] One library type by exact typePath (relative to the Types folder) or by guid (type GUID via ILibrary.FindType, or version GUID via FindVersion): type scalars incl. Status/DoNotUse/SetForUpdate/MinimumTargetDeviceVersion, GetSupportedExportFormats, and every version with State/IsDefault/Author/ModifiedDate/OriginalLibrary/Dependencies/Dependents/MasterCopiesContainingInstances (per-field failures captured; TIA may throw on InWork versions). Paginated versions; no modification.")]
        public static ResponseMessage ReadLibraryType(
            string libraryName="",
            string typePath="",
            [Description("guid: GUID of the type ('' = look up by name).")] string guid="",
            int offset=0,
            int limit=100)
            => Portal.ReadLibraryType(libraryName,typePath,guid,offset,limit);
        [McpServerTool(Name="ManageLibraryType"), Description("[L2][Library][WRITE] One exact library type: update (propertiesJson Name/DoNotUse/SetForUpdate; SetForUpdate is refused natively on project-library and write-protected types), delete (all versions, needs confirmDelete), updateLibrary (LibraryType.UpdateLibrary into targetLibraryName with deleteUnusedVersionsMode/structureConflictResolutionMode/forceUpdateMode, target read back by GUID) and updateProject (LibraryType.UpdateProject per scopeSoftwarePathsJson entry: PLC / HMI / Unified HMI software paths). Default dryRun=true; no save/compile/download.")]
        public static ResponseMessage ManageLibraryType(
            string typePath,
            [Description("action: the operation to perform - update | delete | updateLibrary | updateProject.")] string action,
            string libraryName="",
            string propertiesJson="{}",
            string targetLibraryName="",
            [Description("scopeSoftwarePathsJson: JSON array of software paths that limit the update scope ('[]' = whole project).")] string scopeSoftwarePathsJson="[]",
            [Description("deleteUnusedVersionsMode: AutomaticallyDelete | DoNotDelete.")] string deleteUnusedVersionsMode="DoNotDelete",
            [Description("structureConflictResolutionMode: UpdateStructure | RetainStructure | CancelIfStructureConflicts.")] string structureConflictResolutionMode="RetainStructure",
            [Description("forceUpdateMode: SetOnlyHigherUpdatedVersionAsDefault | ForceSetAnyUpdatedVersionAsDefault | NoDefaultVersionChange.")] string forceUpdateMode="SetOnlyHigherUpdatedVersionAsDefault",
            bool confirmDelete=false,
            bool dryRun=true)
            => Portal.ManageLibraryType(typePath,action,libraryName,propertiesJson,targetLibraryName,scopeSoftwarePathsJson,deleteUnusedVersionsMode,structureConflictResolutionMode,forceUpdateMode,confirmDelete,dryRun);
        [McpServerTool(Name="CheckLibraryUpdates"), Description("[L2][Library][READ] Native ILibrary.UpdateCheck(project, updateCheckMode ReportOutOfDateOnly/ReportOutOfDateAndUpToDate) of the project library or an exact open global library against the bound project: the UpdateCheckResult message tree (Description, MessageParts, nested Messages) flattened with bounds. Read-only; nothing is updated.")]
        public static ResponseMessage CheckLibraryUpdates(
            string libraryName="",
            [Description("updateCheckMode: ReportOutOfDateOnly | ReportOutOfDateAndUpToDate.")] string updateCheckMode="ReportOutOfDateOnly",
            int maxItems=2000)
            => Portal.CheckLibraryUpdates(libraryName,updateCheckMode,maxItems);
        [McpServerTool(Name="SynchronizeLibrary"), Description("[L2][Library][WRITE] ILibrary-level operations on a selection (selectionJson: [{\"type\":\"Folder/Type\"},{\"folder\":\"Folder\"}], {\"folder\":\"\"} = whole Types folder): updateLibrary (UpdateLibrary into targetLibraryName with forceUpdateMode/deleteUnusedVersionsMode/structureConflictResolutionMode; types matched by GUID), updateProject (UpdateProject into scopeSoftwarePathsJson scopes; a global library source synchronizes the project library first), harmonizeProject (HarmonizeProject with harmonizeOptionsJson HarmonizeNames/HarmonizePaths; renames and moves instances) and cleanUp (ProjectLibrary.CleanUpLibrary with cleanUpMode PreserveDefaultVersionOfUnusedTypes/DeleteUnusedTypes, UserGlobalLibrary.CleanUpLibrary without mode). Real execution needs confirmChange=true; default dryRun=true; no save/compile/download.")]
        public static ResponseMessage SynchronizeLibrary(
            [Description("action: the operation to perform - updateLibrary | updateProject | harmonizeProject | cleanUp.")] string action,
            [Description("selectionJson: JSON array selecting the types / instances to synchronise ('[]' = all).")] string selectionJson,
            string libraryName="",
            string targetLibraryName="",
            [Description("scopeSoftwarePathsJson: JSON array of software paths that limit the update scope ('[]' = whole project).")] string scopeSoftwarePathsJson="[]",
            [Description("forceUpdateMode: SetOnlyHigherUpdatedVersionAsDefault | ForceSetAnyUpdatedVersionAsDefault | NoDefaultVersionChange.")] string forceUpdateMode="SetOnlyHigherUpdatedVersionAsDefault",
            [Description("deleteUnusedVersionsMode: AutomaticallyDelete | DoNotDelete.")] string deleteUnusedVersionsMode="DoNotDelete",
            [Description("structureConflictResolutionMode: UpdateStructure | RetainStructure | CancelIfStructureConflicts.")] string structureConflictResolutionMode="RetainStructure",
            [Description("harmonizeOptionsJson: JSON object of harmonisation options (see the tool description).")] string harmonizeOptionsJson="[\"HarmonizeNames\",\"HarmonizePaths\"]",
            [Description("cleanUpMode: PreserveDefaultVersionOfUnusedTypes | DeleteUnusedTypes.")] string cleanUpMode="PreserveDefaultVersionOfUnusedTypes",
            bool confirmChange=false,
            bool dryRun=true)
            => Portal.SynchronizeLibrary(action,selectionJson,libraryName,targetLibraryName,scopeSoftwarePathsJson,forceUpdateMode,deleteUnusedVersionsMode,structureConflictResolutionMode,harmonizeOptionsJson,cleanUpMode,confirmChange,dryRun);
        [McpServerTool(Name="CompareLibraryObjects"), Description("[L2][Library][READ] Native detailed comparison of two library objects of the same kind (type: LibraryType.CompareTo; version: LibraryTypeVersion.CompareTo with leftVersion/rightVersion Major.Minor.Build; masterCopy: MasterCopy.CompareTo) across the project library and open global libraries: DetailedCompareResult.Properties rows (Description, DetailCompareStatus, LeftValue, RightValue) with a status summary; identical rows hidden unless includeIdentical. Read-only.")]
        public static ResponseMessage CompareLibraryObjects(
            [Description("kind: type | version | masterCopy.")] string kind,
            [Description("leftPath: 'Folder/Name' of the left object.")] string leftPath,
            [Description("rightPath: 'Folder/Name' of the right object.")] string rightPath,
            [Description("leftLibraryName: global library on the left side of the comparison ('' = the project library).")] string leftLibraryName="",
            [Description("rightLibraryName: global library on the right side of the comparison ('' = the project library).")] string rightLibraryName="",
            [Description("leftVersion: version string of the left type (e.g. 'V1.0.0'; '' = default version).")] string leftVersion="",
            [Description("rightVersion: version string of the right type (e.g. 'V1.0.1'; '' = default version).")] string rightVersion="",
            bool includeIdentical=false,
            int offset=0,
            int limit=100)
            => Portal.CompareLibraryObjects(kind,leftPath,rightPath,leftLibraryName,rightLibraryName,leftVersion,rightVersion,includeIdentical,offset,limit);
    }
}
