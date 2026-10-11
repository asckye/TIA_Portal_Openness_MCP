using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Adapters.Contracts
{
    // Reviewed release availability, shared by the host, worker and roster generators.
    // Availability never implies native acceptance; ported families remain current / NOT RUN.
    public static class PortedFamilies
    {
        public sealed class Family
        {
            public string Name { get; }
            public string OperationPrefix { get; }
            public string[] Releases { get; }
            public string[] Tools { get; }
            public IReadOnlyDictionary<string, string[]> Actions { get; }
            public Family(string name, string operationPrefix, string[] releases, string[] tools,
                IReadOnlyDictionary<string, string[]>? actions = null)
            { Name = name; OperationPrefix = operationPrefix; Releases = releases; Tools = tools;
                Actions = actions ?? new Dictionary<string, string[]>(StringComparer.Ordinal); }
            public bool Available(string release) => Releases.Contains(release, StringComparer.Ordinal);
            public bool SupportsAction(string release, string action) => Available(release)
                && (Actions.Count == 0 || Actions.TryGetValue(action, out var releases) && releases.Contains(release, StringComparer.Ordinal));
        }

        public static readonly IReadOnlyList<Family> All = new[] {
            new Family("F01", "host-meta",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ApplyToolBatch", "BuildReleaseDiagnosticReport", "BuildReleaseHandoffArtifacts", "BuildReleaseManifest", "BuildReleaseRunbook", "CallTool", "CheckProductUpdate", "ClearExportHandles", "DeleteExportHandle", "FindTools", "GenerateAcceptanceReport", "GenerateErrorReport", "GetEnvironmentDiagnostics", "GetExportContent", "GetNativeInvocationLog", "GetOpennessCompatibility", "GetOpennessGuidance", "GetOpennessWorkerStatus", "GetV21EcosystemCatalog", "ListExportHandles", "ListToolCategories", "PreviewToolBatch", "PreviewToolCall", "RestartOpennessWorker", "RunOfflineReleaseValidationSuite", "RunOnlineMonitoringSafetySelfTest", "RunReadOnlyToolBatch", "SaveExportContent" }),
            new Family("F02", "plc-offline",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "AnalyzePlcReferences", "AnalyzePlcSclSource", "AuditEngineeringExports", "BuildPlcAliasAlarmLad", "ComparePlcBlockDocuments", "DecodePlcSimaticMl", "ExtractPlcBlockMetrics", "GeneratePlcDocumentation", "InspectSimaticSdCompatibility", "InstantiatePlcTemplates", "PatchPlcBlockDocument", "RenderPlcBlockDocument", "RenderPlcVisualDiff", "ScanPlcSourceAnnotations", "ValidatePlcDocumentSchemas", "WritePlcSclSourceFile" }),
            new Family("F03", "hmi-offline",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "AnalyzeGlobalLibraryPackage", "AnalyzeHmiTemplateReference", "AnalyzeUnifiedHmiTemplateLayout", "BuildClassicHmiMinimalPackage", "BuildClassicHmiScreen", "BuildClassicHmiTagTable", "BuildUnifiedHmiButtonActionScript", "BuildUnifiedHmiLayoutDesign", "BuildUnifiedHmiTemplateApplyDesign", "BuildUnifiedHmiTemplateApplyDesignManifest", "BuildUnifiedHmiThemeDesign", "ManageUnifiedCwcPackage", "PlanGlobalLibraryTemplateReuse", "RunClassicHmiOfflineValidationSuite", "RunClassicHmiTemporaryImportPreflight", "RunHmiActionScriptRecipeSafetySelfTest", "RunHmiTemplatePlcSyncPrecheckSuite", "ValidateClassicHmiMinimalPackageFiles", "ValidateClassicHmiMinimalPackagePlcSync", "WriteClassicHmiMinimalPackageFiles" }),
            new Family("F06", "engineering-services",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ManageProjectLanguage", "ExportProjectTexts", "ImportProjectTexts", "CompareLibraries", "CompareProjects", "GetProjectSettings", "GetObjectIdentifier", "ShowObjectInEditor", "ManageProjectCompilationSettings", "RunToolTransaction" }),
            new Family("F23", "libraries",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "GetLibraryOverview", "GetLibraryType", "ManageLibraryType", "CheckLibraryUpdates", "SynchronizeLibrary", "CompareLibraryObjects", "ManageLibraryTypeVersion", "CreateLibraryMasterCopy", "ManageLibraryMasterCopy", "ImportLibraryTypeDocuments", "ManageGlobalLibrary", "ManageLibraryFolder", "ProbeGlobalLibrary", "ImportMasterCopyFromGlobalLibrary" }),
            new Family("F24", "hmi-generic",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "GetHmiProgramInfo", "DescribeHmiSoftware", "DescribeHmiScreen", "DescribeHmiTagTable", "DescribeHmiTag", "DescribeHmiScreenItem", "ListHmiScreens", "ListHmiTagTables", "ListHmiTags", "ListHmiConnections", "ExportHmiScreen", "ExportHmiTagTable", "ExportHmiConnection", "ExportHmiProgram", "ImportHmiScreen", "ImportHmiTagTable", "ImportHmiConnection", "ImportHmiScreensFromDirectory", "ImportHmiTagTablesFromDirectory", "GetHmiScreenSnapshot", "ListHmiScreenPaths", "DeleteHmiTag" }),
            new Family("F25", "hmi-classic",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "GetClassicHmiScreenTree", "ManageClassicHmiScreenObject", "ManageClassicHmiFolder", "ManageClassicHmiGraphic", "ListClassicHmiScripts", "ManageClassicHmiScript", "ManageClassicHmiCycle", "ManageClassicHmiTextGraphicList", "GetClassicHmiGlobalization", "ListClassicHmiFaceplates" }),
            new Family("F35", "openness-reflection",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "DescribeObject", "DescribeObjectProperty", "DescribeService", "GetObjectProperty", "ListObjectChildren", "InvokeObject", "InvokeService" }),
            new Family("F08", "plc-organisation",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "CreatePlcBlockGroup", "CreatePlcTypeGroup", "DeleteEmptyPlcBlockGroup", "DeletePlcBlock", "DeletePlcTagTable", "DeletePlcType", "ManagePlcBlockProtection", "ManagePlcUserGroup", "MovePlcBlockToGroup", "ListPlcSystemGroups" }),
            new Family("F09", "plc-software",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "CreatePlcInstanceDb", "ManagePlcTagDefinition", "SetPlcProgram", "GetPlcTagTableConstants", "GetPlcBlockEditCapabilities", "ImportPlcBlockVerified", "BuildAndImportPlcArtifact", "DescribePlcBlockLogic", "RepairAndReimportPlcBlock", "ImportPlcTagTablesFromDirectory", "SeedProjectFromReference" }),
            new Family("F11", "plc-sources",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "GeneratePlcSourceFromBlocks", "ManagePlcExternalSources", "GetPlcCrossReferences" }),
            new Family("F12", "plc-watch",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ExportPlcWatchTablesToDirectory", "GetPlcWatchTableCurrentValuesReadOnly", "ImportPlcWatchTableOffline", "ListPlcForceTables", "ManagePlcTableEntries", "MonitorPlcWatchTableS7", "PlanOnlineReadOnlyDataProvider", "PlanOnlineReadOnlyMonitoring", "ProbePlcMonitorOnlineCapabilities", "SetPlcWatchTableModifyValue", "TraceTagCause", "TraceTagCauseLive" }),
            new Family("F13", "plc-technology",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ConfigureMotionHardwareConnection", "ExchangeMotionCamData", "ExportTechnologyObjectsToDirectory", "GetMotionAxisConfiguration", "GetTechnologyObjectTree", "ImportTechnologyObject", "ImportTechnologyObjectsFromDirectory", "ManageMotionAxis", "ManageTechnologyObject" }),
            new Family("F10", "plc-documents",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ExportPlcBlockDocuments", "ExportPlcBlocksDocuments", "GetPlcBlockScopes", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments", "ManagePlcBlockDocuments", "ManagePlcDocuments" }),
            new Family("F14", "plc-alarms",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ExchangePlcAlarmTextLists", "ExchangePlcSupervisions", "ExportAlarmClasses", "ExportAlarmInstanceTexts", "ExportAlarmTextLists", "ExportPlcProDiagInfo", "ImportAlarmClasses", "ImportAlarmTextLists", "ImportPlcAlarmInstanceTexts", "ManagePlcAlarmTextList", "ManagePlcSupervision" }),
            new Family("F15", "plc-opcua",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ExportOpcUaInterface", "GenerateOpcUaModelledInterface", "GetOpcUaAccessControl", "GetPlcOpcUaConfiguration", "ImportOpcUaInterface", "ManageOpcUaAccessControl", "ManageOpcUaInterface", "SetOpcUaInterfaceEnabled" }),
            new Family("F16", "plc-integrity",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ListPlcSoftwareUnits", "ManagePlcSoftwareUnit", "SetPlcUnitObjectAccess", "GetPlcChecksums", "GetPlcObjectFingerprints", "ManagePlcBlockWriteProtection" }),
            new Family("F17", "plc-compile",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "CompileDevice", "CompileHmiDiagnostics" }),
            new Family("F18", "hardware-devices",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "CreateDevice", "CreateGsdDevice", "CreateHardwareCatalogDevice", "ListDevices", "GetDeviceInfo", "GetDeviceItemInfo", "GetDeviceItemTree", "SearchInstalledGsdDevices", "GetDeviceAttributes", "SetDeviceItemAttribute", "SetPlcCpuSettings", "GetDevicePlugLocations", "PlugDeviceItem", "ManageHardwareObject", "ManageDeviceUserGroup", "GetHardwareFeatures", "ManageDeviceServiceObjects", "ManageHardwareUtilities", "ExchangeSystemDiagnosticsSettings" }),
            new Family("F20", "hardware-network",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "ListIoSystems", "ManageIoSystem", "ListNetworkDomains", "ManageNetworkDomain", "ListTransferAreas", "ManageTransferArea", "ListDeviceItemChannels", "SetDeviceItemChannel", "ManagePortInterconnection", "GetDeviceItemNetworkInfo", "ConnectDeviceNodesToProfinetSubnet", "PlanHardwareNetworkConfiguration", "EnsureSubnet", "AttachDeviceNodeToSubnet", "ProbeHardwareHmiConnectionOwnerCandidates", "ProbeHardwareHmiConnectionWhitelistedServices", "GetProjectTopology", "ListCommunicationConnections", "ManageCommunicationConnection" }),
            new Family("F21", "hardware-aml",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "BuildDeviceAmlDocument", "ExportDeviceAml", "ImportDeviceAml" }),
            new Family("F19", "hardware-addressing",
                new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
                new[] { "GetDeviceAddressing", "GetDeviceIpAddress", "GetDeviceItemIoAddresses", "SetDeviceAddress", "SetDeviceItemIoAddress" })
        };

        public static readonly IReadOnlyDictionary<string, string[]> ToolReleases = new Dictionary<string, string[]>(StringComparer.Ordinal) {
            ["GetHmiProgramInfo"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["DescribeHmiSoftware"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["DescribeHmiScreen"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["DescribeHmiTagTable"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["DescribeHmiTag"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["DescribeHmiScreenItem"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListHmiScreens"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListHmiTagTables"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListHmiTags"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListHmiConnections"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ExportHmiScreen"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ExportHmiTagTable"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ExportHmiConnection"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ExportHmiProgram"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportHmiScreen"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportHmiTagTable"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportHmiConnection"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportHmiScreensFromDirectory"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportHmiTagTablesFromDirectory"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["GetHmiScreenSnapshot"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListHmiScreenPaths"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["DeleteHmiTag"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["GetClassicHmiScreenTree"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageClassicHmiScreenObject"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageClassicHmiFolder"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageClassicHmiGraphic"] = new[] { "21" },
            ["ListClassicHmiScripts"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageClassicHmiScript"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageClassicHmiCycle"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageClassicHmiTextGraphicList"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["GetClassicHmiGlobalization"] = new[] { "20", "21" },
            ["ListClassicHmiFaceplates"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ExportPlcBlockDocuments"] = new[] { "20", "21" },
            ["ExportPlcBlocksDocuments"] = new[] { "20", "21" },
            ["GetPlcBlockScopes"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportPlcBlockDocuments"] = new[] { "20", "21" },
            ["ImportPlcBlocksDocuments"] = new[] { "20", "21" },
            ["ManagePlcBlockDocuments"] = new[] { "20", "21" },
            ["ManagePlcDocuments"] = new[] { "20", "21" },
            ["ExchangePlcAlarmTextLists"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ExchangePlcSupervisions"] = new[] { "17", "18", "19", "20", "21" },
            ["ExportAlarmClasses"] = new[] { "17", "18", "19", "20", "21" },
            ["ExportAlarmInstanceTexts"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ExportAlarmTextLists"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ExportPlcProDiagInfo"] = new[] { "17", "18", "19", "20", "21" },
            ["ImportAlarmClasses"] = new[] { "17", "18", "19", "20", "21" },
            ["ImportAlarmTextLists"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ImportPlcAlarmInstanceTexts"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ManagePlcAlarmTextList"] = new[] { "18", "19", "20", "21" },
            ["ManagePlcSupervision"] = new[] { "17", "18", "19", "20", "21" },
            ["ExportOpcUaInterface"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["GenerateOpcUaModelledInterface"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["GetOpcUaAccessControl"] = new[] { "20", "21" },
            ["GetPlcOpcUaConfiguration"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportOpcUaInterface"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageOpcUaAccessControl"] = new[] { "20", "21" },
            ["ManageOpcUaInterface"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["SetOpcUaInterfaceEnabled"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ConfigureMotionHardwareConnection"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ExchangeMotionCamData"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ExportTechnologyObjectsToDirectory"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["GetMotionAxisConfiguration"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["GetTechnologyObjectTree"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportTechnologyObject"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ImportTechnologyObjectsFromDirectory"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ManageMotionAxis"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageTechnologyObject"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ExportPlcWatchTablesToDirectory"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["GetPlcWatchTableCurrentValuesReadOnly"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ImportPlcWatchTableOffline"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListPlcForceTables"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManagePlcTableEntries"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["MonitorPlcWatchTableS7"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["PlanOnlineReadOnlyDataProvider"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["PlanOnlineReadOnlyMonitoring"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ProbePlcMonitorOnlineCapabilities"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["SetPlcWatchTableModifyValue"] = new[] { "17", "18", "19", "20", "21" },
            ["TraceTagCause"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["TraceTagCauseLive"] = new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListPlcSoftwareUnits"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ManagePlcSoftwareUnit"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["SetPlcUnitObjectAccess"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["GetPlcChecksums"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["GetPlcObjectFingerprints"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManagePlcBlockWriteProtection"] = new[] { "21" },
            ["CompareLibraryObjects"] = new[] { "18", "19", "20", "21" },
            ["ImportLibraryTypeDocuments"] = new[] { "20", "21" },
            ["ImportMasterCopyFromGlobalLibrary"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["GetObjectIdentifier"] = new[] { "20", "21" },
            ["CompareLibraries"] = new[] { "18", "19", "20", "21" },
            ["ManageProjectCompilationSettings"] = new[] { "16", "17", "18", "19", "20", "21" },
            // Format evidence is deliberately separate from managed-code availability.
            ["BuildPlcAliasAlarmLad"] = new[] { "20", "21" },
            ["DecodePlcSimaticMl"] = new[] { "20", "21" },
            ["ValidatePlcDocumentSchemas"] = new[] { "20", "21" },
            ["InspectSimaticSdCompatibility"] = new[] { "20", "21" },
            ["BuildClassicHmiMinimalPackage"] = new[] { "20", "21" },
            ["BuildClassicHmiScreen"] = new[] { "20", "21" },
            ["BuildClassicHmiTagTable"] = new[] { "20", "21" },
            ["RunClassicHmiOfflineValidationSuite"] = new[] { "20", "21" },
            ["RunClassicHmiTemporaryImportPreflight"] = new[] { "20", "21" },
            ["RunHmiTemplatePlcSyncPrecheckSuite"] = new[] { "20", "21" },
            ["ValidateClassicHmiMinimalPackageFiles"] = new[] { "20", "21" },
            ["ValidateClassicHmiMinimalPackagePlcSync"] = new[] { "20", "21" },
            ["WriteClassicHmiMinimalPackageFiles"] = new[] { "20", "21" },
            ["AnalyzeUnifiedHmiTemplateLayout"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiButtonActionScript"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiLayoutDesign"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiTemplateApplyDesign"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiTemplateApplyDesignManifest"] = new[] { "19", "20", "21" },
            ["BuildUnifiedHmiThemeDesign"] = new[] { "19", "20", "21" },
            ["ManageUnifiedCwcPackage"] = new[] { "19", "20", "21" },
            ["RunHmiActionScriptRecipeSafetySelfTest"] = new[] { "19", "20", "21" },
            // Register a port only where at least one implemented action can run.
            // Keep partial tools listed; HardwareCapabilities / PlcSoftwareCapabilities
            // still refuse their unavailable actions before any worker/native call.
            ["ManagePlcBlockProtection"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListPlcSystemGroups"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["CreatePlcInstanceDb"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["SetPlcProgram"] = new[] { "18", "19", "20", "21" },
            ["GetPlcCrossReferences"] = new[] { "18", "19", "20", "21" },
            ["CreateHardwareCatalogDevice"] = new[] { "18", "19", "20", "21" },
            ["ManageDeviceServiceObjects"] = new[] { "18", "19", "20", "21" },
            ["ExchangeSystemDiagnosticsSettings"] = new[] { "17", "18", "19", "20", "21" },
            ["ListNetworkDomains"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ManageNetworkDomain"] = new[] { "16", "17", "18", "19", "20", "21" },
            ["ListTransferAreas"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ManageTransferArea"] = new[] { "15.1", "16", "17", "18", "19", "20", "21" },
            ["ListCommunicationConnections"] = new[] { "21" },
            ["ManageCommunicationConnection"] = new[] { "21" }
        };

        public static Family ForTool(string tool) => All.Single(f => f.Tools.Contains(tool, StringComparer.Ordinal));
        public static bool Contains(string tool) => All.Any(f => f.Tools.Contains(tool, StringComparer.Ordinal));
        // These ports retain the existing Foundation candidate declaration.
        // The relocated raw engine callbacks are not their product contract.
        public static readonly string[] FoundationCandidates = {
            "ExportPlcBlockDocuments", "ExportPlcBlocksDocuments", "ImportPlcBlockDocuments", "ImportPlcBlocksDocuments"
        };
        public static bool UsesFoundationCandidate(string tool) => FoundationCandidates.Contains(tool, StringComparer.Ordinal);
        public static bool Available(string release, string tool) => ForTool(tool).Available(release)
            && (!ToolReleases.TryGetValue(tool, out var releases) || releases.Contains(release, StringComparer.Ordinal));
    }
}
