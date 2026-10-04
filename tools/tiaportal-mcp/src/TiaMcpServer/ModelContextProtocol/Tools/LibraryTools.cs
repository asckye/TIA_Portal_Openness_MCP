using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
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
    [McpServerToolType]
    internal sealed class LibraryTools
    {
        private readonly LibraryService _library;

        public LibraryTools(LibraryService library) => _library = library;

        [McpServerTool(Name="ReadLibraryOverview"), Description("[L2][Library][READ] Official library overview: empty libraryName = project library, otherwise an exact already-open global library. Header (GlobalLibrary Author/Comment per culture/Copyright/Family/Version/Path/CreationTime/LastModified(By)/IsModified/IsWriteProtected/Size, HistoryEntries, UsedProducts), the Types folder tree (LibraryTypeFolder Status, each LibraryType with Guid/Namespace/DoNotUse/SetForUpdate/MinimumTargetDeviceVersion/Status/version summary) and the master copy tree (MasterCopy Author/CreationDate/ContentDescriptions). Bounded by maxDepth/maxItems; no modification.")]
        public ResponseMessage ReadLibraryOverview(
            string libraryName="",
            [Description("includeTypes: true also lists the types.")] bool includeTypes=true,
            [Description("includeMasterCopies: true also lists the master copies.")] bool includeMasterCopies=true,
            int maxDepth=6,
            int maxItems=500)
            => _library.ReadLibraryOverview(libraryName,includeTypes,includeMasterCopies,maxDepth,maxItems);
        [McpServerTool(Name="ReadLibraryType"), Description("[L2][Library][READ] One library type by exact typePath (relative to the Types folder) or by guid (type GUID via ILibrary.FindType, or version GUID via FindVersion): type scalars incl. Status/DoNotUse/SetForUpdate/MinimumTargetDeviceVersion, GetSupportedExportFormats, and every version with State/IsDefault/Author/ModifiedDate/OriginalLibrary/Dependencies/Dependents/MasterCopiesContainingInstances (per-field failures captured; TIA may throw on InWork versions). Paginated versions; no modification.")]
        public ResponseMessage ReadLibraryType(
            string libraryName="",
            string typePath="",
            [Description("guid: GUID of the type ('' = look up by name).")] string guid="",
            int offset=0,
            int limit=100)
            => _library.ReadLibraryType(libraryName,typePath,guid,offset,limit);
        [McpServerTool(Name="ManageLibraryType"), Description("[L2][Library][WRITE] One exact library type: update (propertiesJson Name/DoNotUse/SetForUpdate), delete (all versions, needs confirmDelete), updateLibrary (LibraryType.UpdateLibrary into targetLibraryName with deleteUnusedVersionsMode/structureConflictResolutionMode/forceUpdateMode, target read back by GUID) and updateProject (LibraryType.UpdateProject per scopeSoftwarePathsJson entry: PLC / HMI / Unified HMI software paths). V21 Unified ScriptModuleType.Name writes are blocked due to reported native TIA crashes, including previews. Project-library SetForUpdate and property writes to protected global libraries are refused before setters. Default dryRun=true; preview does not prove native setter safety. No save/compile/download.")]
        public ResponseMessage ManageLibraryType(
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
            => _library.ManageLibraryType(typePath,action,libraryName,propertiesJson,targetLibraryName,scopeSoftwarePathsJson,deleteUnusedVersionsMode,structureConflictResolutionMode,forceUpdateMode,confirmDelete,dryRun);
        [McpServerTool(Name="CheckLibraryUpdates"), Description("[L2][Library][READ] Native ILibrary.UpdateCheck(project, updateCheckMode ReportOutOfDateOnly/ReportOutOfDateAndUpToDate) of the project library or an exact open global library against the bound project: the UpdateCheckResult message tree (Description, MessageParts, nested Messages) flattened with bounds. Read-only; nothing is updated.")]
        public ResponseMessage CheckLibraryUpdates(
            string libraryName="",
            [Description("updateCheckMode: ReportOutOfDateOnly | ReportOutOfDateAndUpToDate.")] string updateCheckMode="ReportOutOfDateOnly",
            int maxItems=2000)
            => _library.CheckLibraryUpdates(libraryName,updateCheckMode,maxItems);
        [McpServerTool(Name="SynchronizeLibrary"), Description("[L2][Library][WRITE] ILibrary-level operations on a selection (selectionJson: [{\"type\":\"Folder/Type\"},{\"folder\":\"Folder\"}], {\"folder\":\"\"} = whole Types folder): updateLibrary (UpdateLibrary into targetLibraryName with forceUpdateMode/deleteUnusedVersionsMode/structureConflictResolutionMode; types matched by GUID), updateProject (UpdateProject into scopeSoftwarePathsJson scopes; a global library source synchronizes the project library first), harmonizeProject (HarmonizeProject with harmonizeOptionsJson HarmonizeNames/HarmonizePaths; renames and moves instances) and cleanUp (ProjectLibrary.CleanUpLibrary with cleanUpMode PreserveDefaultVersionOfUnusedTypes/DeleteUnusedTypes, UserGlobalLibrary.CleanUpLibrary without mode). Real execution needs confirmChange=true; default dryRun=true; no save/compile/download.")]
        public ResponseMessage SynchronizeLibrary(
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
            => _library.SynchronizeLibrary(action,selectionJson,libraryName,targetLibraryName,scopeSoftwarePathsJson,forceUpdateMode,deleteUnusedVersionsMode,structureConflictResolutionMode,harmonizeOptionsJson,cleanUpMode,confirmChange,dryRun);
        [McpServerTool(Name="CompareLibraryObjects"), Description("[L2][Library][READ] Native detailed comparison of two library objects of the same kind (type: LibraryType.CompareTo; version: LibraryTypeVersion.CompareTo with leftVersion/rightVersion Major.Minor.Build; masterCopy: MasterCopy.CompareTo) across the project library and open global libraries: DetailedCompareResult.Properties rows (Description, DetailCompareStatus, LeftValue, RightValue) with a status summary; identical rows hidden unless includeIdentical. Read-only.")]
        public ResponseMessage CompareLibraryObjects(
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
            => _library.CompareLibraryObjects(kind,leftPath,rightPath,leftLibraryName,rightLibraryName,leftVersion,rightVersion,includeIdentical,offset,limit);
        [McpServerTool(Name="ManageLibraryTypeVersion"), Description("[L2][Library][WRITE] Exact library type/version read/edit/release/setDefault/deleteVersion/updateInstances/discard/findInstances. Empty libraryName selects project library; otherwise unique already-open global library. typePath relative to TypeFolder. Release requires newVersion and official dependenciesMode. findInstances is read-only and requires exact targetSoftwarePath; discard removes the selected editable version. updateInstances requires exact targetSoftwarePath; native type update chooses its applicable versions, not necessarily version argument. dryRun=true default. No export, save, close or compile. Semantic validity/dependency impact determined by native TIA.")]
        public ResponseMessage ManageLibraryTypeVersion(
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
            => _library.ManageLibraryTypeVersion(typePath,version,action,libraryName,newVersion,dependenciesMode,author,comment,targetSoftwarePath,dryRun);
        [McpServerTool(Name="CreateLibraryMasterCopy"), Description("[L2][Library][WRITE] Create a native master copy from an exact block/type/device/screen in an existing library folder. sourcePath is relative object path; for device use JSON array of exact group/station names. Empty libraryName=project library; otherwise already-open global library. dryRun=true default. Native IMasterCopySource required; no automatic save or close.")]
        public ResponseMessage CreateLibraryMasterCopy(
            [Description("sourceKind: block | type | device | screen.")] string sourceKind,
            [Description("sourcePath: 'Group/Name' of the source object.")] string sourcePath,
            string softwarePath="",
            string folderPath="",
            string libraryName="",
            bool dryRun=true)
            => _library.CreateLibraryMasterCopy(sourceKind,sourcePath,softwarePath,folderPath,libraryName,dryRun);
        [McpServerTool(Name="ManageLibraryMasterCopy"), Description("[L2][Library][WRITE] Exact master-copy read/copy/compare/delete. copy destinationPath is a folder; compare uses exact master-copy path. No overwrites or automatic save. Default preview for mutations.")]
        public ResponseMessage ManageLibraryMasterCopy(
            [Description("sourcePath: 'Group/Name' of the source object.")] string sourcePath,
            [Description("action: the operation to perform - read | copy | compare | delete.")] string action,
            string libraryName="",
            [Description("destinationLibraryName: global library that receives the copy ('' = the project library).")] string destinationLibraryName="",
            [Description("destinationPath: 'Folder/Name' where the copy is created.")] string destinationPath="",
            bool dryRun=true)
            => _library.ManageLibraryMasterCopy(sourcePath,action,libraryName,destinationLibraryName,destinationPath,dryRun);
        [McpServerTool(Name="ImportLibraryTypeDocuments"), Description("[L2][Library][WRITE] Native library document import (SimaticML / WinCC ML / S7DCL / SCL / STL / UDT / NVT): without typePath, LibraryTypeComposition.CreateFromDocuments creates a new type with an InWork default version in the exact library folder; with typePath, LibraryTypeVersionComposition.CreateFromDocuments adds a version to that type (createOptions None fails natively if an in-work version exists, Override replaces it). STEP 7 documents need targetSoftwarePath + targetGroupKind (blocks/types) + targetGroupPath as target environment. importOptions None/SkipInactiveCultures/ActivateInactiveCultures. Returns TransferResultState, messages and the created type/version. Project library only (global libraries throw natively); default preview; no automatic save.")]
        public ResponseMessage ImportLibraryTypeDocuments(
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
            => _library.ImportLibraryTypeDocuments(filePath,folderPath,libraryName,importOptions,typePath,createOptions,targetSoftwarePath,targetGroupKind,targetGroupPath,dryRun);
        [McpServerTool(Name="ManageGlobalLibrary"), Description("[L2][Library][WRITE] Global library lifecycle: list (open libraries), infos (GlobalLibraryComposition.GetGlobalLibraryInfos: every library this Portal knows with path, type and IsOpen), create/open/openInfo (Open(GlobalLibraryInfo) by exact name)/retrieve/save/saveAs/close, and archive (UserGlobalLibrary.Archive(destinationDirectory, archiveName, archiveMode None/Compressed/DiscardRestorableData/DiscardRestorableDataAndCompressed); the library must be saved first). Exact expected name, new destination; preview by default. Close is explicit, never auto-save. Upgrade opening requires explicit ReadWrite.")]
        public ResponseMessage ManageGlobalLibrary(
            [Description("action: the operation to perform - list | infos | create | open | openInfo | retrieve | save | saveAs | close | archive.")] string action,
            string libraryName="",
            string filePath="",
            string destinationDirectory="",
            [Description("openMode: how to open the library - ReadOnly or ReadWrite.")] string openMode="ReadOnly",
            [Description("upgrade: true opens with an upgrade to the current TIA version when the file is older.")] bool upgrade=false,
            [Description("archiveName: file name of the library archive to write.")] string archiveName="",
            [Description("archiveMode: None | Compressed | DiscardRestorableData | DiscardRestorableDataAndCompressed.")] string archiveMode="Compressed",
            bool dryRun=true)
            => _library.ManageGlobalLibrary(action,libraryName,filePath,destinationDirectory,openMode,upgrade,archiveName,archiveMode,dryRun);
        [McpServerTool(Name="ManageLibraryFolder"), Description("[L2][Library][WRITE] Read/create/rename/delete exact types or masterCopies folder. Empty-folder deletion only, no recursive deletion or save. Default preview.")]
        public ResponseMessage ManageLibraryFolder(
            [Description("folderKind: types | masterCopies.")] string folderKind,
            string folderPath,
            [Description("action: the operation to perform - read | create | rename | delete.")] string action,
            string libraryName="",
            string newName="",
            bool dryRun=true)
            => _library.ManageLibraryFolder(folderKind,folderPath,action,libraryName,newName,dryRun);

        [McpServerTool(Name = "ProbeGlobalLibrary"), Description("[L2][HMI-Library]Open a TIA global library (.al21) read-only/best-effort and list accessible master copies/types/folders through public/reflection APIs. It does not import library content.")]
        public ResponseGlobalLibraryProbe ProbeGlobalLibrary(
            [Description("libraryPath: full path to .al21 file or its containing folder")] string libraryPath,
            [Description("maxItems: maximum items per list")] int maxItems = 500)
        {
            try
            {
                var result = _library.ProbeGlobalLibrary(libraryPath, maxItems);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing global library: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportMasterCopyFromGlobalLibrary"), Description("[L2][HMI-Library] Import one MasterCopy from a TIA global library into a real Unified HMI screen and return ScreenItems readback evidence. This modifies the project, must be tried in a temporary project first, and reports failure unless the imported item is visible after readback.")]
        public ResponseGlobalLibraryImport ImportMasterCopyFromGlobalLibrary(
            [Description("libraryPath: full path to .al21 file or its containing folder")] string libraryPath,
            [Description("masterCopyName: exact or suffix path/name from ProbeGlobalLibrary MasterCopies readback")] string masterCopyName,
            [Description("hmiSoftwarePath: real Unified HMI software path resolved from GetProjectTree, e.g. HMI_RT_1")] string hmiSoftwarePath,
            [Description("screenName: existing target Unified screen name; create it first with EnsureUnifiedHmiScreen if needed")] string screenName,
            [Description("importedItemName: optional expected item name after import; empty means use masterCopyName leaf")] string importedItemName = "",
            [Description("left: optional Left coordinate applied after import when supported")] int left = 0,
            [Description("top: optional Top coordinate applied after import when supported")] int top = 0)
        {
            try
            {
                var result = _library.ImportMasterCopyFromGlobalLibrary(libraryPath, masterCopyName, hmiSoftwarePath, screenName, importedItemName, left, top);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing global-library master copy: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeGlobalLibraryPackage"), Description("[L2][HMI-Library]Analyze a TIA global library folder offline by file-system structure. It does not connect to TIA Portal, open the library, import content, or modify files.")]
        public ResponseJsonReport AnalyzeGlobalLibraryPackage(
            [Description("libraryPath: global library folder path or .al* file path")] string libraryPath)
        {
            try
            {
                var data = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
                data["timestamp"] = DateTime.Now.ToString("O");
                data["safetyPolicy"] = new JsonObject
                {
                    ["mode"] = "Offline file-system analysis only.",
                    ["tia"] = "TIA Portal is not connected or opened by this analysis.",
                    ["write"] = "No global library content is imported, modified, or written."
                };

                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Global library package offline analysis completed" : "Global library package offline analysis completed with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing global library package: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanGlobalLibraryTemplateReuse"), Description("[L2][HMI-Library] Plan the commercial fallback when direct MasterCopy import is not publicly verifiable: learn reference/global-library template evidence and rebuild screens with native Unified HMI MCP theme/layout/action tools. Offline planning only; it does not import library content or modify projects.")]
        public ResponseJsonReport PlanGlobalLibraryTemplateReuse(
            [Description("libraryPath: reference global library folder path or .al* file path.")] string libraryPath,
            [Description("templateIntentJson: optional JSON {\"screenType\":\"overview\",\"targetRuntime\":\"Unified\",\"preferredComponents\":[...]}.")] string templateIntentJson = "{}")
        {
            try
            {
                var analysis = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
                var intent = ToolJsonArguments.ParseJsonObjectOrEmpty(templateIntentJson, "templateIntentJson");
                var exists = analysis["exists"]?.GetValue<bool>() == true;
                var hasCoreFiles = analysis["ok"]?.GetValue<bool>() == true;
                var stringHints = analysis["stringHints"] as JsonObject;
                var patternCounts = stringHints?["patternCounts"] as JsonObject;
                var screenHintCount = patternCounts?["Screen"]?.GetValue<int>() ?? 0;
                var templateHintCount = patternCounts?["Template"]?.GetValue<int>() ?? 0;
                var masterCopyHintCount = patternCounts?["MasterCopy"]?.GetValue<int>() ?? 0;

                var data = new JsonObject
                {
                    ["libraryPath"] = libraryPath,
                    ["intent"] = intent,
                    ["offlineAnalysisOk"] = exists,
                    ["hasCoreGlobalLibraryFiles"] = hasCoreFiles,
                    ["strategy"] = "template-learn-and-native-rebuild",
                    ["directMasterCopyImportRequired"] = false,
                    ["directMasterCopyImportStatus"] = "optional-unverified-path",
                    ["commercialFallbackReady"] = exists,
                    ["safety"] = new JsonObject
                    {
                        ["offlineOnly"] = true,
                        ["importsLibraryContent"] = false,
                        ["modifiesProject"] = false,
                        ["requiresReadbackBeforeClaimingDirectImport"] = true
                    },
                    ["templateEvidence"] = new JsonObject
                    {
                        ["screenHintCount"] = screenHintCount,
                        ["templateHintCount"] = templateHintCount,
                        ["masterCopyHintCount"] = masterCopyHintCount
                    },
                    ["recommendedMcpTools"] = new JsonArray(
                        "AnalyzeGlobalLibraryPackage",
                        "ProbeGlobalLibrary",
                        "BuildUnifiedHmiThemeDesignJson",
                        "BuildUnifiedHmiLayoutDesignJson",
                        "BuildUnifiedHmiTemplateApplyDesignJson",
                        "ApplyUnifiedHmiScreenDesignJson",
                        "EnsureUnifiedHmiButtonAction"),
                    ["validationGates"] = new JsonArray(
                        "Template plan has offline package evidence.",
                        "Generated Unified design JSON passes layout QA.",
                        "Applied HMI screen items are read back by DescribeHmiScreenItem.",
                        "Button actions pass SyntaxCheck with zero errors.",
                        "HMI tags bind only to declared PLC symbols/DB members."),
                    ["reconstructionPlan"] = new JsonArray(
                        "Analyze global library/package structure and string hints without importing content.",
                        "Use ProbeGlobalLibrary only as read-only evidence when TIA is available; do not claim direct MasterCopy import unless readback succeeds.",
                        "Map reusable UI intent to Unified HMI native tools: theme, layout, template apply design, and button action recipes.",
                        "Apply generated design with ApplyUnifiedHmiScreenDesignJson and verify with item readback plus action SyntaxCheck.",
                        "Bind controls only to declared PLC symbols or DB members discovered from project exports/readback."),
                    ["analysis"] = analysis
                };

                return new ResponseJsonReport
                {
                    Ok = exists,
                    Message = exists
                        ? "Global library template reuse plan built. Direct MasterCopy import remains optional until real readback is verified."
                        : "Global library template reuse plan blocked because the library path was not found.",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = exists }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning global library template reuse: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeHmiTemplateReference"), Description("[L2][HMI-Library]Analyze local Unified HMI JSON templates against reference-project/runtime/global-library hints offline. It does not connect to TIA Portal or modify projects.")]
        public ResponseJsonReport AnalyzeHmiTemplateReference(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("referenceProjectPath: reference TIA project folder containing HMI runtime export/currentConfiguration")] string referenceProjectPath,
            [Description("referenceGlobalLibraryPath: reference global library folder or .al* file")] string referenceGlobalLibraryPath)
        {
            try
            {
                var data = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, referenceProjectPath, referenceGlobalLibraryPath);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI template/reference offline analysis completed" : "HMI template/reference offline analysis completed with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing HMI template/reference assets: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeUnifiedHmiTemplateLayout"), Description("[L2][HMI-Library]Offline-only QA for Unified HMI JSON templates. Checks theme metadata, screen bounds, duplicate item names, size issues, layout overlap warnings, density, and execution JSON shape. It does not connect to TIA Portal or modify projects.")]
        public ResponseJsonReport AnalyzeUnifiedHmiTemplateLayout(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory)
        {
            try
            {
                // 显式传入检查委托，确保 execution JSON shape 检查会验证模板并报告错误。
                var data = HmiTemplateLayoutAnalyzer.AnalyzeDirectory(templateDirectory, HmiTemplateLayoutAnalyzer.ExecutionJsonBuilds);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template layout offline QA completed" : "Unified HMI template layout offline QA found blocking issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing Unified HMI template layout: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }
    }
}
