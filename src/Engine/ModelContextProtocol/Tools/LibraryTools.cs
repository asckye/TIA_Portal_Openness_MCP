using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    // Contract boundary for library, SiVArc and version control tools.
    // The services retain native execution; only their observed evidence is mapped here.
    [McpServerToolType]
    internal sealed class LibraryTools
    {
        private readonly LibraryService _library;

        public LibraryTools(LibraryService library) => _library = library;

        [McpServerTool(Name="GetLibraryOverview"), Description("[L2][Library][READ] Official library overview: empty libraryName = project library, otherwise an exact already-open global library. Header (GlobalLibrary Author/Comment per culture/Copyright/Family/Version/Path/CreationTime/LastModified(By)/IsModified/IsWriteProtected/Size, HistoryEntries, UsedProducts), the Types folder tree (LibraryTypeFolder Status, each LibraryType with Guid/Namespace/DoNotUse/SetForUpdate/MinimumTargetDeviceVersion/Status/version summary) and the master copy tree (MasterCopy Author/CreationDate/ContentDescriptions). Bounded by maxDepth/maxItems; no modification. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult GetLibraryOverviewV4(
            string libraryName="",
            [Description("includeTypes: true also lists the types.")] bool includeTypes=true,
            [Description("includeMasterCopies: true also lists the master copies.")] bool includeMasterCopies=true,
            int maxDepth=6,
            int maxItems=500)
            => LibraryToolContract.Run("GetLibraryOverview", false, true, () =>
            {
                LibraryToolContract.Check(() => { LibraryDeepLogic.ValidateBounds(maxDepth, maxItems); });
                return ReadLibraryOverview(libraryName, includeTypes, includeMasterCopies, maxDepth, maxItems);
            });

        internal ResponseMessage ReadLibraryOverview(
            string libraryName="",
            bool includeTypes=true,
            bool includeMasterCopies=true,
            int maxDepth=6,
            int maxItems=500)
        => _library.ReadLibraryOverview(libraryName,includeTypes,includeMasterCopies,maxDepth,maxItems);
        [McpServerTool(Name="GetLibraryType"), Description("[L2][Library][READ] One library type by exact typePath (relative to the Types folder) or by guid (type GUID via ILibrary.FindType, or version GUID via FindVersion): type scalars incl. Status/DoNotUse/SetForUpdate/MinimumTargetDeviceVersion, GetSupportedExportFormats, and every version with State/IsDefault/Author/ModifiedDate/OriginalLibrary/Dependencies/Dependents/MasterCopiesContainingInstances (per-field failures captured; TIA may throw on InWork versions). Paginated versions; no modification. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult GetLibraryTypeV4(
            string libraryName="",
            string typePath="",
            [Description("guid: GUID of the type ('' = look up by name).")] string guid="",
            int offset=0,
            int limit=100)
            => LibraryToolContract.Run("GetLibraryType", false, true, () =>
            {
                LibraryToolContract.Check(() => { LibraryDeepLogic.ParseGuid(guid, "guid"); HardwareServicesLogic.ValidatePagination(offset, limit); });
                return ReadLibraryType(libraryName, typePath, guid, offset, limit);
            });

        internal ResponseMessage ReadLibraryType(
            string libraryName="",
            string typePath="",
            string guid="",
            int offset=0,
            int limit=100)
        => _library.ReadLibraryType(libraryName,typePath,guid,offset,limit);
        [McpServerTool(Name="ManageLibraryType"), Description("[L2][Library][WRITE] One exact library type: update (properties Name/DoNotUse/SetForUpdate), delete (all versions, needs confirmDelete), updateLibrary (LibraryType.UpdateLibrary into targetLibraryName with deleteUnusedVersionsMode/structureConflictResolutionMode/forceUpdateMode, target read back by GUID) and updateProject (LibraryType.UpdateProject per scopeSoftwarePaths entry: PLC / HMI / Unified HMI software paths). V21 Unified ScriptModuleType.Name writes are blocked due to reported native TIA crashes, including previews. Project-library SetForUpdate and property writes to protected global libraries are refused before setters. Default dryRun=true; preview does not prove native setter safety. No save/compile/download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageLibraryTypeV4(
            string typePath,
            [Description("action: the operation to perform - update | delete | updateLibrary | updateProject.")] string action,
            string libraryName="",
            AttributeMap<Scalar> properties = null!,
            string targetLibraryName="",
            [Description("scopeSoftwarePaths: JSON array of software paths that limit the update scope (required for updateProject / harmonizeProject; an unscoped update is refused).")] string[] scopeSoftwarePaths = null!,
            [Description("deleteUnusedVersionsMode: AutomaticallyDelete | DoNotDelete.")] string deleteUnusedVersionsMode="DoNotDelete",
            [Description("structureConflictResolutionMode: UpdateStructure | RetainStructure | CancelIfStructureConflicts.")] string structureConflictResolutionMode="RetainStructure",
            [Description("forceUpdateMode: SetOnlyHigherUpdatedVersionAsDefault | ForceSetAnyUpdatedVersionAsDefault | NoDefaultVersionChange.")] string forceUpdateMode="SetOnlyHigherUpdatedVersionAsDefault",
            bool confirmDelete=false,
            bool dryRun=true)
            => LibraryToolContract.Run("ManageLibraryType", !dryRun, true, () =>
            {
                string propertiesJson = V4Json.Serialize(properties ?? new AttributeMap<Scalar>(new Dictionary<string, Scalar>()));
                string scopeSoftwarePathsJson = V4Json.Serialize(scopeSoftwarePaths ?? Array.Empty<string>());
                LibraryToolContract.Check(() => { LibraryDeepLogic.ValidateTypeRequest(action, HardwareNetworkLogic.ParseObject(propertiesJson, "properties"), targetLibraryName, libraryName, LibraryDeepLogic.ParseScopes(scopeSoftwarePathsJson));
                LibraryDeepLogic.RequireOneOf(deleteUnusedVersionsMode, LibraryDeepLogic.DeleteUnusedVersionsModes, "deleteUnusedVersionsMode");
                LibraryDeepLogic.RequireOneOf(structureConflictResolutionMode, LibraryDeepLogic.StructureConflictResolutionModes, "structureConflictResolutionMode");
                LibraryDeepLogic.RequireOneOf(forceUpdateMode, LibraryDeepLogic.ForceUpdateModes, "forceUpdateMode");
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun); });
                return ManageLibraryType(typePath, action, libraryName, propertiesJson, targetLibraryName, scopeSoftwarePathsJson, deleteUnusedVersionsMode, structureConflictResolutionMode, forceUpdateMode, confirmDelete, dryRun);
            });

        internal ResponseMessage ManageLibraryType(
            string typePath,
            string action,
            string libraryName="",
            string propertiesJson="{}",
            string targetLibraryName="",
            string scopeSoftwarePathsJson="[]",
            string deleteUnusedVersionsMode="DoNotDelete",
            string structureConflictResolutionMode="RetainStructure",
            string forceUpdateMode="SetOnlyHigherUpdatedVersionAsDefault",
            bool confirmDelete=false,
            bool dryRun=true)
        => _library.ManageLibraryType(typePath,action,libraryName,propertiesJson,targetLibraryName,scopeSoftwarePathsJson,deleteUnusedVersionsMode,structureConflictResolutionMode,forceUpdateMode,confirmDelete,dryRun);
        [McpServerTool(Name="CheckLibraryUpdates"), Description("[L2][Library][READ] Native ILibrary.UpdateCheck(project, updateCheckMode ReportOutOfDateOnly/ReportOutOfDateAndUpToDate) of the project library or an exact open global library against the bound project: the UpdateCheckResult message tree (Description, MessageParts, nested Messages) flattened with bounds. Read-only; nothing is updated. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult CheckLibraryUpdatesV4(
            string libraryName="",
            [Description("updateCheckMode: ReportOutOfDateOnly | ReportOutOfDateAndUpToDate.")] string updateCheckMode="ReportOutOfDateOnly",
            int maxItems=2000)
            => LibraryToolContract.Run("CheckLibraryUpdates", false, true, () =>
            {
                LibraryToolContract.Check(() => { LibraryDeepLogic.RequireOneOf(updateCheckMode, LibraryDeepLogic.UpdateCheckModes, "updateCheckMode"); LibraryDeepLogic.ValidateBounds(1, maxItems); });
                return CheckLibraryUpdates(libraryName, updateCheckMode, maxItems);
            });

        internal ResponseMessage CheckLibraryUpdates(
            string libraryName="",
            string updateCheckMode="ReportOutOfDateOnly",
            int maxItems=2000)
        => _library.CheckLibraryUpdates(libraryName,updateCheckMode,maxItems);
        [McpServerTool(Name="SynchronizeLibrary"), Description("[L2][Library][WRITE] ILibrary-level operations on a selection (selection: [{\"type\":\"Folder/Type\"},{\"folder\":\"Folder\"}], {\"folder\":\"\"} = whole Types folder): updateLibrary (UpdateLibrary into targetLibraryName with forceUpdateMode/deleteUnusedVersionsMode/structureConflictResolutionMode; types matched by GUID), updateProject (UpdateProject into scopeSoftwarePaths scopes; a global library source synchronizes the project library first), harmonizeProject (HarmonizeProject with harmonizeOptions HarmonizeNames/HarmonizePaths; renames and moves instances) and cleanUp (ProjectLibrary.CleanUpLibrary with cleanUpMode PreserveDefaultVersionOfUnusedTypes/DeleteUnusedTypes, UserGlobalLibrary.CleanUpLibrary without mode). Real execution needs confirmChange=true; default dryRun=true; no save/compile/download. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult SynchronizeLibraryV4(
            [Description("action: the operation to perform - updateLibrary | updateProject | harmonizeProject | cleanUp.")] string action,
            [Description("selection: JSON array selecting the types / instances to synchronise (at least one type or folder; {folder: ''} selects the whole Types folder).")] LibrarySelection[] selection,
            string libraryName="",
            string targetLibraryName="",
            [Description("scopeSoftwarePaths: JSON array of software paths that limit the update scope (required for updateProject / harmonizeProject; an unscoped update is refused).")] string[] scopeSoftwarePaths = null!,
            [Description("forceUpdateMode: SetOnlyHigherUpdatedVersionAsDefault | ForceSetAnyUpdatedVersionAsDefault | NoDefaultVersionChange.")] string forceUpdateMode="SetOnlyHigherUpdatedVersionAsDefault",
            [Description("deleteUnusedVersionsMode: AutomaticallyDelete | DoNotDelete.")] string deleteUnusedVersionsMode="DoNotDelete",
            [Description("structureConflictResolutionMode: UpdateStructure | RetainStructure | CancelIfStructureConflicts.")] string structureConflictResolutionMode="RetainStructure",
            [Description("harmonizeOptions: Array of harmonisation option names (see the tool description).")] string[] harmonizeOptions = null!,
            [Description("cleanUpMode: PreserveDefaultVersionOfUnusedTypes | DeleteUnusedTypes.")] string cleanUpMode="PreserveDefaultVersionOfUnusedTypes",
            bool confirmChange=false,
            bool dryRun=true)
            => LibraryToolContract.Run("SynchronizeLibrary", !dryRun, true, () =>
            {
                string selectionJson = V4Json.Serialize(selection);
                string scopeSoftwarePathsJson = V4Json.Serialize(scopeSoftwarePaths ?? Array.Empty<string>());
                string harmonizeOptionsJson = V4Json.Serialize(harmonizeOptions ?? new[] { "HarmonizeNames", "HarmonizePaths" });
                LibraryToolContract.Check(() => { LibraryDeepLogic.ValidateSyncRequest(action, libraryName, targetLibraryName, LibraryDeepLogic.ParseScopes(scopeSoftwarePathsJson), LibraryDeepLogic.ParseSelection(selectionJson), forceUpdateMode, deleteUnusedVersionsMode, structureConflictResolutionMode, cleanUpMode);
                if (action == "harmonizeProject") LibraryDeepLogic.JoinHarmonizeOptions(HardwareNetworkLogic.ParseNames(harmonizeOptionsJson, "harmonizeOptions", 2));
                HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun); });
                return SynchronizeLibrary(action, selectionJson, libraryName, targetLibraryName, scopeSoftwarePathsJson, forceUpdateMode, deleteUnusedVersionsMode, structureConflictResolutionMode, harmonizeOptionsJson, cleanUpMode, confirmChange, dryRun);
            });

        internal ResponseMessage SynchronizeLibrary(
            string action,
            string selectionJson,
            string libraryName="",
            string targetLibraryName="",
            string scopeSoftwarePathsJson="[]",
            string forceUpdateMode="SetOnlyHigherUpdatedVersionAsDefault",
            string deleteUnusedVersionsMode="DoNotDelete",
            string structureConflictResolutionMode="RetainStructure",
            string harmonizeOptionsJson="[\"HarmonizeNames\",\"HarmonizePaths\"]",
            string cleanUpMode="PreserveDefaultVersionOfUnusedTypes",
            bool confirmChange=false,
            bool dryRun=true)
        => _library.SynchronizeLibrary(action,selectionJson,libraryName,targetLibraryName,scopeSoftwarePathsJson,forceUpdateMode,deleteUnusedVersionsMode,structureConflictResolutionMode,harmonizeOptionsJson,cleanUpMode,confirmChange,dryRun);
        [McpServerTool(Name="CompareLibraryObjects"), Description("[L2][Library][READ] Native detailed comparison of two library objects of the same kind (type: LibraryType.CompareTo; version: LibraryTypeVersion.CompareTo with leftVersion/rightVersion Major.Minor.Build; masterCopy: MasterCopy.CompareTo) across the project library and open global libraries: DetailedCompareResult.Properties rows (Description, DetailCompareStatus, LeftValue, RightValue) with a status summary; identical rows hidden unless includeIdentical. Read-only. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult CompareLibraryObjectsV4(
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
            => LibraryToolContract.Run("CompareLibraryObjects", false, true, () =>
            {
                LibraryToolContract.Check(() => { LibraryDeepLogic.ValidateCompareRequest(kind, leftPath, rightPath, leftVersion, rightVersion); HardwareServicesLogic.ValidatePagination(offset, limit); });
                return CompareLibraryObjects(kind, leftPath, rightPath, leftLibraryName, rightLibraryName, leftVersion, rightVersion, includeIdentical, offset, limit);
            });

        internal ResponseMessage CompareLibraryObjects(
            string kind,
            string leftPath,
            string rightPath,
            string leftLibraryName="",
            string rightLibraryName="",
            string leftVersion="",
            string rightVersion="",
            bool includeIdentical=false,
            int offset=0,
            int limit=100)
        => _library.CompareLibraryObjects(kind,leftPath,rightPath,leftLibraryName,rightLibraryName,leftVersion,rightVersion,includeIdentical,offset,limit);
        [McpServerTool(Name="ManageLibraryTypeVersion"), Description("[L2][Library][WRITE] Exact library type/version read/edit/release/setDefault/deleteVersion/updateInstances/discard/findInstances. Empty libraryName selects project library; otherwise unique already-open global library. typePath relative to TypeFolder. Release requires newVersion and official dependenciesMode. findInstances is read-only and requires exact targetSoftwarePath; discard removes the selected editable version. updateInstances requires exact targetSoftwarePath; native type update chooses its applicable versions, not necessarily version argument. dryRun=true default. No export, save, close or compile. Semantic validity/dependency impact determined by native TIA. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageLibraryTypeVersionV4(
            string typePath,
            string version,
            [Description("action: the operation to perform - read | edit | release | setDefault | deleteVersion | updateInstances | discard | findInstances.")] string action,
            string libraryName="",
            [Description("newVersion: version string of the new type version, e.g. 'V1.0.1'.")] string newVersion="",
            [Description("dependenciesMode: how dependent types are handled by the action ('' = default).")] string dependenciesMode="",
            [Description("author: author text of the version.")] string author="",
            [Description("comment: comment text.")] string comment="",
            string targetSoftwarePath="",
            bool dryRun=true)
            => LibraryToolContract.Run("ManageLibraryTypeVersion", !dryRun && !(action == "read" || action == "findInstances"), true, () =>
            {
                return ManageLibraryTypeVersion(typePath, version, action, libraryName, newVersion, dependenciesMode, author, comment, targetSoftwarePath, dryRun);
            });

        internal ResponseMessage ManageLibraryTypeVersion(
            string typePath,
            string version,
            string action,
            string libraryName="",
            string newVersion="",
            string dependenciesMode="",
            string author="",
            string comment="",
            string targetSoftwarePath="",
            bool dryRun=true)
        => _library.ManageLibraryTypeVersion(typePath,version,action,libraryName,newVersion,dependenciesMode,author,comment,targetSoftwarePath,dryRun);
        [McpServerTool(Name="CreateLibraryMasterCopy"), Description("[L2][Library][WRITE] Create a native master copy from an exact block/type/device/screen in an existing library folder. sourcePath is relative object path; for device use JSON array of exact group/station names. Empty libraryName=project library; otherwise already-open global library. dryRun=true default. Native IMasterCopySource required; no automatic save or close. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult CreateLibraryMasterCopyV4(
            [Description("sourceKind: block | type | device | screen.")] string sourceKind,
            [Description("sourcePath: 'Group/Name' of the source object.")] string sourcePath,
            string softwarePath="",
            string folderPath="",
            string libraryName="",
            bool dryRun=true)
            => LibraryToolContract.Run("CreateLibraryMasterCopy", !dryRun, true, () =>
            {
                return CreateLibraryMasterCopy(sourceKind, sourcePath, softwarePath, folderPath, libraryName, dryRun);
            });

        internal ResponseMessage CreateLibraryMasterCopy(
            string sourceKind,
            string sourcePath,
            string softwarePath="",
            string folderPath="",
            string libraryName="",
            bool dryRun=true)
        => _library.CreateLibraryMasterCopy(sourceKind,sourcePath,softwarePath,folderPath,libraryName,dryRun);
        [McpServerTool(Name="ManageLibraryMasterCopy"), Description("[L2][Library][WRITE] Exact master-copy read/copy/compare/delete. copy destinationPath is a folder; compare uses exact master-copy path. No overwrites or automatic save. Default preview for mutations. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageLibraryMasterCopyV4(
            [Description("sourcePath: 'Group/Name' of the source object.")] string sourcePath,
            [Description("action: the operation to perform - read | copy | compare | delete.")] string action,
            string libraryName="",
            [Description("destinationLibraryName: global library that receives the copy ('' = the project library).")] string destinationLibraryName="",
            [Description("destinationPath: 'Folder/Name' where the copy is created.")] string destinationPath="",
            bool dryRun=true)
            => LibraryToolContract.Run("ManageLibraryMasterCopy", !dryRun && !(action == "read" || action == "compare"), true, () =>
            {
                return ManageLibraryMasterCopy(sourcePath, action, libraryName, destinationLibraryName, destinationPath, dryRun);
            });

        internal ResponseMessage ManageLibraryMasterCopy(
            string sourcePath,
            string action,
            string libraryName="",
            string destinationLibraryName="",
            string destinationPath="",
            bool dryRun=true)
        => _library.ManageLibraryMasterCopy(sourcePath,action,libraryName,destinationLibraryName,destinationPath,dryRun);
        [McpServerTool(Name="ImportLibraryTypeDocuments"), Description("[L2][Library][WRITE] Native library document import (SimaticML / WinCC ML / S7DCL / SCL / STL / UDT / NVT): without typePath, LibraryTypeComposition.CreateFromDocuments creates a new type with an InWork default version in the exact library folder; with typePath, LibraryTypeVersionComposition.CreateFromDocuments adds a version to that type (createOptions None fails natively if an in-work version exists, Override replaces it). STEP 7 documents need targetSoftwarePath + targetGroupKind (blocks/types) + targetGroupPath as target environment. importOptions None/SkipInactiveCultures/ActivateInactiveCultures. Returns TransferResultState, messages and the created type/version. Project library only (global libraries throw natively); default preview; no automatic save. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ImportLibraryTypeDocumentsV4(
            string filePath,
            string folderPath="",
            string libraryName="",
            [Description("importOptions: import option - None | SkipInactiveCultures | ActivateInactiveCultures.")] string importOptions="None",
            string typePath="",
            [Description("createOptions: None | Override.")] string createOptions="None",
            string targetSoftwarePath="",
            [Description("targetGroupKind: blocks or types - which group receives the imported documents.")] string targetGroupKind="",
            string targetGroupPath="",
            bool dryRun=true)
            => LibraryToolContract.Run("ImportLibraryTypeDocuments", !dryRun, true, () =>
            {
                return ImportLibraryTypeDocuments(filePath, folderPath, libraryName, importOptions, typePath, createOptions, targetSoftwarePath, targetGroupKind, targetGroupPath, dryRun);
            });

        internal ResponseMessage ImportLibraryTypeDocuments(
            string filePath,
            string folderPath="",
            string libraryName="",
            string importOptions="None",
            string typePath="",
            string createOptions="None",
            string targetSoftwarePath="",
            string targetGroupKind="",
            string targetGroupPath="",
            bool dryRun=true)
        => _library.ImportLibraryTypeDocuments(filePath,folderPath,libraryName,importOptions,typePath,createOptions,targetSoftwarePath,targetGroupKind,targetGroupPath,dryRun);
        [McpServerTool(Name="ManageGlobalLibrary"), Description("[L2][Library][WRITE] Global library lifecycle: list (open libraries), infos (GlobalLibraryComposition.GetGlobalLibraryInfos: every library this Portal knows with path, type and IsOpen), create/open/openInfo (Open(GlobalLibraryInfo) by exact name)/retrieve/save/saveAs/close, and archive (UserGlobalLibrary.Archive(destinationDirectory, archiveName, archiveMode None/Compressed/DiscardRestorableData/DiscardRestorableDataAndCompressed); the library must be saved first). Exact expected name, new destination; preview by default. Close is explicit, never auto-save. Upgrade opening requires explicit ReadWrite. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageGlobalLibraryV4(
            [Description("action: the operation to perform - list | infos | create | open | openInfo | retrieve | save | saveAs | close | archive.")] string action,
            string libraryName="",
            string filePath="",
            string destinationDirectory="",
            [Description("openMode: how to open the library - ReadOnly or ReadWrite.")] string openMode="ReadOnly",
            [Description("upgrade: true opens with an upgrade to the current TIA version when the file is older.")] bool upgrade=false,
            [Description("archiveName: file name of the library archive to write.")] string archiveName="",
            [Description("archiveMode: None | Compressed | DiscardRestorableData | DiscardRestorableDataAndCompressed.")] string archiveMode="Compressed",
            bool dryRun=true)
            => LibraryToolContract.Run("ManageGlobalLibrary", !dryRun && !(action == "list" || action == "infos"), true, () =>
            {
                return ManageGlobalLibrary(action, libraryName, filePath, destinationDirectory, openMode, upgrade, archiveName, archiveMode, dryRun);
            });

        internal ResponseMessage ManageGlobalLibrary(
            string action,
            string libraryName="",
            string filePath="",
            string destinationDirectory="",
            string openMode="ReadOnly",
            bool upgrade=false,
            string archiveName="",
            string archiveMode="Compressed",
            bool dryRun=true)
        => _library.ManageGlobalLibrary(action,libraryName,filePath,destinationDirectory,openMode,upgrade,archiveName,archiveMode,dryRun);
        [McpServerTool(Name="ManageLibraryFolder"), Description("[L2][Library][WRITE] Read/create/rename/delete exact types or masterCopies folder. Empty-folder deletion only, no recursive deletion or save. Default preview. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ManageLibraryFolderV4(
            [Description("folderKind: types | masterCopies.")] string folderKind,
            string folderPath,
            [Description("action: the operation to perform - read | create | rename | delete.")] string action,
            string libraryName="",
            string newName="",
            bool dryRun=true)
            => LibraryToolContract.Run("ManageLibraryFolder", !dryRun && !(action == "read"), true, () =>
            {
                return ManageLibraryFolder(folderKind, folderPath, action, libraryName, newName, dryRun);
            });

        internal ResponseMessage ManageLibraryFolder(
            string folderKind,
            string folderPath,
            string action,
            string libraryName="",
            string newName="",
            bool dryRun=true)
        => _library.ManageLibraryFolder(folderKind,folderPath,action,libraryName,newName,dryRun);

        [McpServerTool(Name = "ProbeGlobalLibrary"), Description("[L2][HMI-Library]Open a TIA global library (.al21) read-only/best-effort and list accessible master copies/types/folders through public/reflection APIs. It does not import library content. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ProbeGlobalLibraryV4(
            [Description("libraryPath: full path to .al21 file or its containing folder")] string libraryPath,
            [Description("maxItems: maximum items per list")] int maxItems = 500)
            => LibraryToolContract.Run("ProbeGlobalLibrary", false, true, () =>
            {
                return ProbeGlobalLibrary(libraryPath, maxItems);
            });

        internal ResponseGlobalLibraryProbe ProbeGlobalLibrary(
            string libraryPath,
            int maxItems = 500)
        {
            try
            {
                var result = _library.ProbeGlobalLibrary(libraryPath, maxItems);
                result.Meta = ResponseMeta.Basic(DateTime.Now, result.Ok == true);
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing global library: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportMasterCopyFromGlobalLibrary"), Description("[L2][HMI-Library] Import one MasterCopy from a TIA global library into a real Unified HMI screen and return ScreenItems readback evidence. This modifies the project, must be tried in a temporary project first, and reports failure unless the imported item is visible after readback. Current native policy; V4 native acceptance is pending.")]
        public CallToolResult ImportMasterCopyFromGlobalLibraryV4(
            [Description("libraryPath: full path to .al21 file or its containing folder")] string libraryPath,
            [Description("masterCopyName: exact or suffix path/name from ProbeGlobalLibrary MasterCopies readback")] string masterCopyName,
            [Description("hmiSoftwarePath: real Unified HMI software path resolved from GetProjectTree, e.g. HMI_RT_1")] string hmiSoftwarePath,
            [Description("screenName: existing target Unified screen name; create it first with EnsureUnifiedHmiScreen if needed")] string screenName,
            [Description("importedItemName: optional expected item name after import; empty means use masterCopyName leaf")] string importedItemName = "",
            [Description("left: optional Left coordinate applied after import when supported")] int left = 0,
            [Description("top: optional Top coordinate applied after import when supported")] int top = 0)
            => LibraryToolContract.Run("ImportMasterCopyFromGlobalLibrary", true, true, () =>
            {
                return ImportMasterCopyFromGlobalLibrary(libraryPath, masterCopyName, hmiSoftwarePath, screenName, importedItemName, left, top);
            });

        internal ResponseGlobalLibraryImport ImportMasterCopyFromGlobalLibrary(
            string libraryPath,
            string masterCopyName,
            string hmiSoftwarePath,
            string screenName,
            string importedItemName = "",
            int left = 0,
            int top = 0)
        {
            try
            {
                var result = _library.ImportMasterCopyFromGlobalLibrary(libraryPath, masterCopyName, hmiSoftwarePath, screenName, importedItemName, left, top);
                result.Meta = ResponseMeta.Basic(DateTime.Now, result.Ok == true);
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing global-library master copy: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
















    }
}
