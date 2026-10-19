extern alias foundationhost;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Candidates;
using TiaMcp.WorkerChannel;
using Xunit;
using Foundation = foundationhost::TiaMcp.FoundationHost;
using Operations = foundationhost::TiaMcp.PlcWorker.WorkerOperations;

public sealed class PlcWorkerRobustnessTests
{
    private static readonly IReadOnlyDictionary<string, string> PlcCategories = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["plc-online.ExecuteOnline"] = "transfer",
        ["runtime-channels.ExecutePlcSimAdvanced"] = "transfer",
        ["plc-safety.ExecuteSafetyValidation"] = "importExport",
        ["sivarc.ExecuteSivarc"] = "compile",
        ["startdrive.ExecuteStartdrive"] = "default",
        ["startdrive.ExecuteDcc"] = "importExport",
        ["plc-safety.ExecuteSafety"] = "importExport",
        ["AddDeviceWithFallback"] = "default",
        ["Attach"] = "default",
        ["BindProject"] = "default",
        ["CloseProject"] = "default",
        ["CompileCandidate"] = "compile",
        ["CompileSoftware"] = "compile",
        ["CreateHardwareDeviceCandidate"] = "default",
        ["CreateProject"] = "default",
        ["CreateTag"] = "default",
        ["CreateTagTable"] = "default",
        ["CreateUserConstant"] = "default",
        ["DeletePlcExternalSource"] = "default",
        ["Disconnect"] = "default",
        ["ExportAsDocuments"] = "importExport",
        ["ExportBlock"] = "importExport",
        ["ExportBlocks"] = "importExport",
        ["ExportBlocksAsDocuments"] = "importExport",
        ["ExportPlcCandidate"] = "importExport",
        ["ExportPlcWatchTable"] = "importExport",
        ["ExportTagTable"] = "importExport",
        ["ExportTechnologyObject"] = "importExport",
        ["ExportType"] = "importExport",
        ["ExportTypes"] = "importExport",
        ["GenerateBlocksFromExternalSource"] = "compile",
        ["ImportBlocks"] = "importExport",
        ["ImportBlocksFromDirectory"] = "importExport",
        ["ImportBlocksFromDocuments"] = "importExport",
        ["ImportFromDocuments"] = "importExport",
        ["ImportPlcCandidate"] = "importExport",
        ["ImportPlcExternalSource"] = "importExport",
        ["ImportPlcProgramFromDirectory"] = "importExport",
        ["ImportTagTables"] = "importExport",
        ["ImportTypes"] = "importExport",
        ["ListProjects"] = "default",
        ["ListSystemConstants"] = "default",
        ["ListTags"] = "default",
        ["ListUserConstants"] = "default",
        ["OpenProject"] = "default",
        ["PlanPlcExternalSourceImport"] = "default",
        ["ReadBlockHierarchy"] = "default",
        ["ReadBlockInfo"] = "default",
        ["ReadBlocks"] = "default",
        ["ReadExternalSourceNames"] = "default",
        ["ReadPortalConnectReadiness"] = "default",
        ["ReadPortalProcessProjects"] = "default",
        ["ReadProjectTree"] = "default",
        ["ReadSoftwareInfo"] = "default",
        ["ReadSoftwareTree"] = "default",
        ["ReadState"] = "default",
        ["ReadTagTableNames"] = "default",
        ["ReadTechnologyObjects"] = "default",
        ["ReadTypeInfo"] = "default",
        ["ReadTypes"] = "default",
        ["ReadWatchTableNames"] = "default",
        ["SaveCloseCandidate"] = "default",
        ["SaveProject"] = "default",
        ["SearchHardwareCatalog"] = "default",
        ["SessionCandidate"] = "default",
        ["SourceCandidate"] = "compile",
        ["engineering-services.BeginEngineeringTransaction"] = "default",
        ["engineering-services.CommitEngineeringTransaction"] = "default",
        ["engineering-services.DisposeEngineeringTransaction"] = "default",
        ["engineering-services.ExecuteEngineeringServices"] = "default",
        ["engineering-services.ReadEngineeringTransaction"] = "default",
        ["hardware-addressing.DescribeHardwareIoChildren"] = "default",
        ["hardware-addressing.ReadHardwareAddressing"] = "default",
        ["hardware-addressing.ReadHardwareIoAddresses"] = "default",
        ["hardware-addressing.ReadHardwareIpAddress"] = "default",
        ["hardware-addressing.SetHardwareIoAddress"] = "default",
        ["hardware-addressing.UpdateHardwareAddress"] = "default",
        ["hardware-aml.ExportHardwareAml"] = "importExport",
        ["hardware-aml.ImportHardwareAml"] = "importExport",
        ["hardware-devices.HardwareCreateCatalogDevice"] = "default",
        ["hardware-devices.HardwareCreateDevice"] = "default",
        ["hardware-devices.HardwareCreateDeviceFallback"] = "default",
        ["hardware-devices.HardwareCreateGsdDevice"] = "default",
        ["hardware-devices.HardwareDescribeDeviceAt"] = "default",
        ["hardware-devices.HardwareDescribeDevices"] = "default",
        ["hardware-devices.HardwareDescribeItemAt"] = "default",
        ["hardware-devices.HardwareDeviceCandidate"] = "default",
        ["hardware-devices.HardwareExchangeSystemDiagnosticsSettings"] = "default",
        ["hardware-devices.HardwareLegacyDumpDeviceAttributes"] = "default",
        ["hardware-devices.HardwareLegacySearchHardwareCatalog"] = "default",
        ["hardware-devices.HardwareLegacySearchInstalledGsdDevices"] = "default",
        ["hardware-devices.HardwareLegacySetCpuCommonSettings"] = "default",
        ["hardware-devices.HardwareLegacySetDeviceItemAttribute"] = "default",
        ["hardware-devices.HardwareManageCommunicationConnection"] = "default",
        ["hardware-devices.HardwareManageDeviceServiceObjects"] = "default",
        ["hardware-devices.HardwareManageDeviceUserGroup"] = "default",
        ["hardware-devices.HardwareManageHardwareObject"] = "default",
        ["hardware-devices.HardwareManageHardwareUtilities"] = "default",
        ["hardware-devices.HardwarePlugModule"] = "default",
        ["hardware-devices.HardwareReadCommunicationConnections"] = "default",
        ["hardware-devices.HardwareReadHardwareFeatures"] = "default",
        ["hardware-devices.HardwareReadItemTree"] = "default",
        ["hardware-devices.HardwareReadPlcNames"] = "default",
        ["hardware-devices.HardwareReadPlugLocations"] = "default",
        ["hardware-network.HardwareAttachDeviceNodeToSubnet"] = "default",
        ["hardware-network.HardwareEnsureSubnet"] = "default",
        ["hardware-network.HardwareGetDeviceItemNetworkInfo"] = "default",
        ["hardware-network.HardwareGetProjectTopology"] = "default",
        ["hardware-network.HardwareManageCommunicationConnection"] = "default",
        ["hardware-network.HardwareManageIoSystem"] = "default",
        ["hardware-network.HardwareManageNetworkDomain"] = "default",
        ["hardware-network.HardwareManagePortInterconnection"] = "default",
        ["hardware-network.HardwareManageTransferArea"] = "default",
        ["hardware-network.HardwareProbeConnectDeviceNodesToSubnet"] = "default",
        ["hardware-network.HardwareReadCommunicationConnections"] = "default",
        ["hardware-network.HardwareReadDeviceItemChannels"] = "default",
        ["hardware-network.HardwareReadIoSystems"] = "default",
        ["hardware-network.HardwareReadNetworkDomains"] = "default",
        ["hardware-network.HardwareReadTransferAreas"] = "default",
        ["hardware-network.HardwareUpdateDeviceItemChannel"] = "default",
        ["hardware-network.ReadHardwareOwnerCandidates"] = "default",
        ["hardware-network.ReadHardwareWhitelistedServices"] = "default",
        ["cfc-charts.ExecuteOptionalProduct"] = "default",
        ["optional-products.ExecuteOptionalProduct"] = "default",
        ["hmi-classic.ExecuteHmi"] = "default",
        ["hmi-generic.ExecuteHmi"] = "default",
        ["hmi-unified.ExecuteUnifiedHmi"] = "default",
        ["hmi-unified.ExecuteUnifiedHmiMetadata"] = "default",
        ["hmi-unified-model.ExecuteUnified"] = "importExport",
        ["hmi-unified-exchange.ExecuteUnified"] = "importExport",
        ["hmi-unified-exchange.ReleaseUnifiedReadCursor"] = "default",
        ["libraries.ExecuteLibrary"] = "default",
        ["openness-reflection.ExecuteOpennessReflection"] = "default",
        ["plc-alarms.ExecutePlcB5"] = "default",
        ["plc-analysis.ExportBlockDocument"] = "default",
        ["plc-compile.ExecutePlcOrganisation"] = "compile",
        ["plc-documents.ExecutePlcB5"] = "default",
        ["plc-integrity.ExecutePlcOrganisation"] = "default",
        ["plc-opcua.ExecutePlcB5"] = "default",
        ["plc-organisation.ExecutePlcOrganisation"] = "default",
        ["plc-software.ExecutePlcOrganisation"] = "default",
        ["plc-software.SeedReferenceHmi"] = "importExport",
        ["plc-sources.ExecutePlcOrganisation"] = "default",
        ["plc-technology.ExecutePlcOrganisation"] = "default",
        ["plc-watch.ExecutePlcOrganisation"] = "default",
        ["project-lifecycle.ReadPortalInfo"] = "default",
        ["project-lifecycle.ConnectIsolatedPortal"] = "default",
        ["project-lifecycle.EnsureOpennessUserGroup"] = "default",
        ["project-lifecycle.ReadProjectLifecycleBinding"] = "default",
        ["project-lifecycle.ArchiveSaveProjectCopy"] = "archive",
        ["project-lifecycle.ArchiveSavedProject"] = "archive",
        ["project-lifecycle.RetrieveProjectArchive"] = "archive",
        ["project-lifecycle.ExecuteMultiuserSession"] = "default",
        ["project-lifecycle.ValidateAutomationContext"] = "default",
        ["project-lifecycle.ExecuteProjectScaffoldPrimitive"] = "default",
        ["project-security.ReadProjectProtection"] = "default",
        ["project-security.ExecuteProjectUsers"] = "default",
        ["project-security.ExecuteCertificate"] = "default",
        ["project-security.ExecuteSecurityDeep"] = "default",
        ["project-security.ExecuteDeviceUsers"] = "default",
        ["project-security.ExecuteHardwareSecurity"] = "default",
        ["version-control.ExecuteVersionControl"] = "default",
        ["version-control.ExecuteTeamcenter"] = "default",
    };
    private static readonly IReadOnlyDictionary<string, string> PortedCategories = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ExchangeCfcCharts"] = "default",
        ["ExchangeTestSuiteCase"] = "default",
        ["ExportScadaData"] = "importExport",
        ["ImportSinumerikAlarmTexts"] = "importExport",
        ["InitializeSimotionScripting"] = "default",
        ["ListTestSuiteCases"] = "default",
        ["ManageCfcChartProtection"] = "default",
        ["ManageSinumerikArchive"] = "archive",
        ["ManageSinumerikSafetyMode"] = "default",
        ["ManageTestSuiteCase"] = "default",
        ["RunTestSuiteCase"] = "default",
        ["ApplyUnifiedHmiLayout"] = "default",
        ["ApplyUnifiedHmiScreenDesign"] = "default",
        ["ApplyUnifiedHmiTheme"] = "default",
        ["BindUnifiedHmiButtonPressedTag"] = "default",
        ["BindUnifiedHmiTagDynamization"] = "default",
        ["DeleteEmptyUnifiedHmiScreenGroup"] = "default",
        ["DeleteUnifiedHmiButtonEvent"] = "default",
        ["DeleteUnifiedHmiDynamization"] = "default",
        ["DescribeUnifiedHmiButtonEventScript"] = "default",
        ["DescribeUnifiedScreenItemType"] = "default",
        ["EnsureUnifiedHmiButtonAction"] = "default",
        ["EnsureUnifiedHmiButtonEventHandler"] = "default",
        ["EnsureUnifiedHmiConnection"] = "default",
        ["EnsureUnifiedHmiDynamization"] = "default",
        ["EnsureUnifiedHmiScreen"] = "default",
        ["EnsureUnifiedHmiScreenItem"] = "default",
        ["EnsureUnifiedHmiTag"] = "default",
        ["EnsureUnifiedHmiTagTable"] = "default",
        ["GetUnifiedHmiButtonEvent"] = "default",
        ["GetUnifiedHmiDynamization"] = "default",
        ["GetUnifiedHmiTexts"] = "default",
        ["ListUnifiedHmiApiTypes"] = "default",
        ["ManageUnifiedHmiGroup"] = "default",
        ["ManageUnifiedScreenItem"] = "default",
        ["SetUnifiedHmiButtonEventScriptCode"] = "default",
        ["SetUnifiedHmiRuntimeState"] = "default",
        ["ListPlcSimAdvancedInstances"] = "default",
        ["GetPlcSimAdvancedTags"] = "default",
        ["ManagePlcSimAdvancedInstance"] = "transfer",
        ["WritePlcSimAdvancedTags"] = "transfer",
        ["RunPlcSimAdvancedTestScenario"] = "transfer",
        ["CheckDownloadReadiness"] = "transfer",
        ["CompareSoftwareToOnline"] = "default",
        ["ConnectOnlinePlc"] = "default",
        ["DisconnectOnlinePlc"] = "default",
        ["DisconnectOnlinePlcs"] = "default",
        ["DownloadPlc"] = "transfer",
        ["DownloadPlcToFolder"] = "transfer",
        ["GeneratePlcLoadableFile"] = "transfer",
        ["GetOnlineState"] = "default",
        ["GetPlcBlockFingerprints"] = "default",
        ["ListTransferRoutes"] = "default",
        ["ManagePlcDataBlockSnapshot"] = "default",
        ["ScanAccessibleDevices"] = "default",
        ["UploadDeviceParameters"] = "transfer",
        ["UploadStationFromPlc"] = "transfer",
        ["GenerateSivarc"] = "default",
        ["GetSivarcRuleTree"] = "default",
        ["ListSivarcBlockDefinitions"] = "default",
        ["ListSivarcRules"] = "default",
        ["ManageSivarcBlockDefinition"] = "default",
        ["ManageSivarcRule"] = "default",
        ["ManageSivarcRuleContainer"] = "default",
        ["ManageSivarcScreenLayout"] = "default",
        ["ManageSivarcTableRule"] = "default",
        ["ResolveSivarcExpression"] = "default",
        ["UpgradeSivarcDefinitions"] = "default",
        ["ExportSafetyPrintout"] = "importExport",
        ["GetSafetyBlockSignatures"] = "default",
        ["ListSafetyActivationTests"] = "default",
        ["ManagePlcSafety"] = "default",
        ["ManageSafetyActivationTest"] = "default",
        ["ManageSafetyActivationTestGroup"] = "default",
        ["ManageSafetyFunction"] = "default",
        ["ManageSafetyFunctionCondition"] = "default",
        ["ManageSafetyGlobalSettings"] = "default",
        ["GetDccObject"] = "default",
        ["GetDriveParameters"] = "default",
        ["GetOnlineDriveParameters"] = "default",
        ["ListDccCharts"] = "default",
        ["ListDriveObjects"] = "default",
        ["ManageDcbLibraries"] = "default",
        ["ManageDccBlock"] = "default",
        ["ManageDccChart"] = "default",
        ["ManageDccChartInterface"] = "default",
        ["ManageDccChartPartition"] = "default",
        ["ManageDccPin"] = "default",
        ["ManageDriveFunctions"] = "default",
        ["ManageDriveHardwareModule"] = "default",
        ["ManageDriveSafetyAcceptanceTest"] = "default",
        ["ManageDriveSecurity"] = "default",
        ["ManageDriveTelegrams"] = "default",
        ["ManageOnlineDriveFunctions"] = "default",
        ["ManageStartdriveParameter"] = "default",
        ["ManageTechnologyExtensions"] = "default",
        ["CompareUnifiedGraphicSelections"] = "default",
        ["ExchangeUnifiedScriptModules"] = "default",
        ["ExchangeUnifiedTags"] = "default",
        ["ExportUnifiedEngineeringList"] = "importExport",
        ["GetUnifiedAlarmCommon"] = "default",
        ["GetUnifiedAuditSettings"] = "default",
        ["GetUnifiedCrossReferences"] = "default",
        ["GetUnifiedFaceplateInstance"] = "default",
        ["GetUnifiedGlobalScript"] = "default",
        ["GetUnifiedGraphicSelection"] = "default",
        ["GetUnifiedLibraryType"] = "default",
        ["GetUnifiedObjectEvents"] = "default",
        ["GetUnifiedObjectProperties"] = "default",
        ["GetUnifiedPlantObject"] = "default",
        ["GetUnifiedRuntimeSettings"] = "default",
        ["GetUnifiedScreenBranch"] = "default",
        ["ImportUnifiedEngineeringList"] = "importExport",
        ["ImportUnifiedOpcUaAlarms"] = "importExport",
        ["ListUnifiedEngineeringObjects"] = "default",
        ["ListUnifiedGlobalScripts"] = "default",
        ["ListUnifiedLibraryFolderEntries"] = "default",
        ["ListUnifiedTagDefinitions"] = "default",
        ["ManageUnifiedDynamization"] = "default",
        ["ManageUnifiedEngineeringObject"] = "default",
        ["ManageUnifiedEvent"] = "default",
        ["ManageUnifiedListEntries"] = "default",
        ["ManageUnifiedLoggingTag"] = "default",
        ["ManageUnifiedObjectParts"] = "default",
        ["ManageUnifiedOpcUaAlarmType"] = "default",
        ["ManageUnifiedPlantNode"] = "default",
        ["ManageUnifiedScreenLayout"] = "default",
        ["ReleaseUnifiedReadCursor"] = "default",
        ["SetUnifiedGlobalScript"] = "default",
        ["SetUnifiedLogDuration"] = "default",
        ["SetUnifiedMultilingualProperty"] = "default",
        ["SetUnifiedObjectProperties"] = "default",
        ["SetUnifiedPlantObject"] = "default",
        ["SetUnifiedRuntimeSettings"] = "default",
        ["ValidateUnifiedObject"] = "default",
        ["ArchiveSavedProject"] = "archive",
        ["BuildProjectScaffold"] = "default",
        ["ConnectIsolatedPortal"] = "default",
        ["ConnectProjectToWorkspace"] = "default",
        ["CreateVersionControlWorkspace"] = "default",
        ["EnsureOpennessUserGroup"] = "default",
        ["GetPlcPutGetAccess"] = "default",
        ["GetPortalInfo"] = "default",
        ["GetProjectProtection"] = "default",
        ["GetProjectUserManagement"] = "default",
        ["GetVersionControlStatus"] = "default",
        ["ListVersionControlWorkspaces"] = "default",
        ["ManageDeviceUsers"] = "default",
        ["ManageMultiuserSession"] = "default",
        ["ManagePasswordPolicy"] = "default",
        ["ManagePlcCertificate"] = "default",
        ["ManagePlcGitRepository"] = "default",
        ["ManagePlcProtection"] = "default",
        ["ManageProjectUserManagement"] = "default",
        ["ManageSyslogServers"] = "default",
        ["ManageTeamcenterConnection"] = "default",
        ["ManageTeamcenterDataset"] = "default",
        ["ManageTeamcenterWorkflow"] = "default",
        ["ManageUmcUsers"] = "default",
        ["ManageWatchForceTableWebAccess"] = "default",
        ["RetrieveProjectArchive"] = "archive",
        ["SaveProjectCopy"] = "archive",
        ["SetPlcPutGetAccess"] = "default",
        ["SynchronizeVersionControlWorkspace"] = "default",
        ["ValidateAutomationContext"] = "default",
        ["AttachDeviceNodeToSubnet"] = "default",
        ["BuildAndImportPlcArtifact"] = "importExport",
        ["BuildDeviceAmlDocument"] = "default",
        ["CheckLibraryUpdates"] = "default",
        ["CompareLibraries"] = "default",
        ["CompareLibraryObjects"] = "default",
        ["CompareProjects"] = "default",
        ["CompileDevice"] = "compile",
        ["CompileHmiDiagnostics"] = "compile",
        ["ConfigureMotionHardwareConnection"] = "default",
        ["ConnectDeviceNodesToProfinetSubnet"] = "default",
        ["CreateDevice"] = "default",
        ["CreateGsdDevice"] = "default",
        ["CreateHardwareCatalogDevice"] = "default",
        ["CreateLibraryMasterCopy"] = "default",
        ["CreatePlcBlockGroup"] = "default",
        ["CreatePlcInstanceDb"] = "default",
        ["CreatePlcTypeGroup"] = "default",
        ["DeleteEmptyPlcBlockGroup"] = "default",
        ["DeleteHmiTag"] = "default",
        ["DeletePlcBlock"] = "default",
        ["DeletePlcTagTable"] = "default",
        ["DeletePlcType"] = "default",
        ["DescribeHmiScreen"] = "default",
        ["DescribeHmiScreenItem"] = "default",
        ["DescribeHmiSoftware"] = "default",
        ["DescribeHmiTag"] = "default",
        ["DescribeHmiTagTable"] = "default",
        ["DescribeObject"] = "default",
        ["DescribeObjectProperty"] = "default",
        ["DescribePlcBlockLogic"] = "default",
        ["DescribeService"] = "default",
        ["EnsureSubnet"] = "default",
        ["ExchangeMotionCamData"] = "default",
        ["ExchangePlcAlarmTextLists"] = "default",
        ["ExchangePlcSupervisions"] = "default",
        ["ExchangeSystemDiagnosticsSettings"] = "default",
        ["ExportAlarmClasses"] = "importExport",
        ["ExportAlarmInstanceTexts"] = "importExport",
        ["ExportAlarmTextLists"] = "importExport",
        ["ExportDeviceAml"] = "importExport",
        ["ExportHmiConnection"] = "importExport",
        ["ExportHmiProgram"] = "importExport",
        ["ExportHmiScreen"] = "importExport",
        ["ExportHmiTagTable"] = "importExport",
        ["ExportOpcUaInterface"] = "importExport",
        ["ExportPlcBlockDocuments"] = "importExport",
        ["ExportPlcBlocksDocuments"] = "importExport",
        ["ExportPlcProDiagInfo"] = "importExport",
        ["ExportPlcWatchTablesToDirectory"] = "importExport",
        ["ExportProjectTexts"] = "importExport",
        ["ExportTechnologyObjectsToDirectory"] = "importExport",
        ["GenerateOpcUaModelledInterface"] = "compile",
        ["GeneratePlcSourceFromBlocks"] = "importExport",
        ["GetClassicHmiGlobalization"] = "default",
        ["GetClassicHmiScreenTree"] = "default",
        ["GetDeviceAddressing"] = "default",
        ["GetDeviceAttributes"] = "default",
        ["GetDeviceInfo"] = "default",
        ["GetDeviceIpAddress"] = "default",
        ["GetDeviceItemInfo"] = "default",
        ["GetDeviceItemIoAddresses"] = "default",
        ["GetDeviceItemNetworkInfo"] = "default",
        ["GetDeviceItemTree"] = "default",
        ["GetDevicePlugLocations"] = "default",
        ["GetHardwareFeatures"] = "default",
        ["GetHmiProgramInfo"] = "default",
        ["GetHmiScreenSnapshot"] = "default",
        ["GetLibraryOverview"] = "default",
        ["GetLibraryType"] = "default",
        ["GetMotionAxisConfiguration"] = "default",
        ["GetObjectIdentifier"] = "default",
        ["GetObjectProperty"] = "default",
        ["GetOpcUaAccessControl"] = "default",
        ["GetPlcBlockEditCapabilities"] = "default",
        ["GetPlcBlockScopes"] = "default",
        ["GetPlcChecksums"] = "default",
        ["GetPlcCrossReferences"] = "default",
        ["GetPlcObjectFingerprints"] = "default",
        ["GetPlcOpcUaConfiguration"] = "default",
        ["GetPlcTagTableConstants"] = "default",
        ["GetPlcWatchTableCurrentValuesReadOnly"] = "default",
        ["GetProjectSettings"] = "default",
        ["GetProjectTopology"] = "default",
        ["GetTechnologyObjectTree"] = "default",
        ["ImportAlarmClasses"] = "importExport",
        ["ImportAlarmTextLists"] = "importExport",
        ["ImportDeviceAml"] = "importExport",
        ["ImportHmiConnection"] = "importExport",
        ["ImportHmiScreen"] = "importExport",
        ["ImportHmiScreensFromDirectory"] = "importExport",
        ["ImportHmiTagTable"] = "importExport",
        ["ImportHmiTagTablesFromDirectory"] = "importExport",
        ["ImportLibraryTypeDocuments"] = "importExport",
        ["ImportMasterCopyFromGlobalLibrary"] = "importExport",
        ["ImportOpcUaInterface"] = "importExport",
        ["ImportPlcAlarmInstanceTexts"] = "importExport",
        ["ImportPlcBlockDocuments"] = "importExport",
        ["ImportPlcBlockVerified"] = "importExport",
        ["ImportPlcBlocksDocuments"] = "importExport",
        ["ImportPlcTagTablesFromDirectory"] = "importExport",
        ["ImportPlcWatchTableOffline"] = "importExport",
        ["ImportProjectTexts"] = "importExport",
        ["ImportTechnologyObject"] = "importExport",
        ["ImportTechnologyObjectsFromDirectory"] = "importExport",
        ["InvokeObject"] = "default",
        ["InvokeService"] = "default",
        ["ListClassicHmiFaceplates"] = "default",
        ["ListClassicHmiScripts"] = "default",
        ["ListCommunicationConnections"] = "default",
        ["ListDeviceItemChannels"] = "default",
        ["ListDevices"] = "default",
        ["ListHmiConnections"] = "default",
        ["ListHmiScreenPaths"] = "default",
        ["ListHmiScreens"] = "default",
        ["ListHmiTagTables"] = "default",
        ["ListHmiTags"] = "default",
        ["ListIoSystems"] = "default",
        ["ListNetworkDomains"] = "default",
        ["ListObjectChildren"] = "default",
        ["ListPlcForceTables"] = "default",
        ["ListPlcSoftwareUnits"] = "default",
        ["ListPlcSystemGroups"] = "default",
        ["ListTransferAreas"] = "default",
        ["ManageClassicHmiCycle"] = "default",
        ["ManageClassicHmiFolder"] = "default",
        ["ManageClassicHmiGraphic"] = "default",
        ["ManageClassicHmiScreenObject"] = "default",
        ["ManageClassicHmiScript"] = "default",
        ["ManageClassicHmiTextGraphicList"] = "default",
        ["ManageCommunicationConnection"] = "default",
        ["ManageDeviceServiceObjects"] = "default",
        ["ManageDeviceUserGroup"] = "default",
        ["ManageGlobalLibrary"] = "default",
        ["ManageHardwareObject"] = "default",
        ["ManageHardwareUtilities"] = "default",
        ["ManageIoSystem"] = "default",
        ["ManageLibraryFolder"] = "default",
        ["ManageLibraryMasterCopy"] = "default",
        ["ManageLibraryType"] = "default",
        ["ManageLibraryTypeVersion"] = "default",
        ["ManageMotionAxis"] = "default",
        ["ManageNetworkDomain"] = "default",
        ["ManageOpcUaAccessControl"] = "default",
        ["ManageOpcUaInterface"] = "default",
        ["ManagePlcAlarmTextList"] = "default",
        ["ManagePlcBlockDocuments"] = "default",
        ["ManagePlcBlockProtection"] = "default",
        ["ManagePlcBlockWriteProtection"] = "default",
        ["ManagePlcDocuments"] = "default",
        ["ManagePlcExternalSources"] = "default",
        ["ManagePlcSoftwareUnit"] = "default",
        ["ManagePlcSupervision"] = "default",
        ["ManagePlcTableEntries"] = "default",
        ["ManagePlcTagDefinition"] = "default",
        ["ManagePlcUserGroup"] = "default",
        ["ManagePortInterconnection"] = "default",
        ["ManageProjectCompilationSettings"] = "default",
        ["ManageProjectLanguage"] = "default",
        ["ManageTechnologyObject"] = "default",
        ["ManageTransferArea"] = "default",
        ["MonitorPlcWatchTableS7"] = "default",
        ["MovePlcBlockToGroup"] = "default",
        ["PlanHardwareNetworkConfiguration"] = "default",
        ["PlanOnlineReadOnlyDataProvider"] = "default",
        ["PlanOnlineReadOnlyMonitoring"] = "default",
        ["PlugDeviceItem"] = "default",
        ["ProbeGlobalLibrary"] = "default",
        ["ProbeHardwareHmiConnectionOwnerCandidates"] = "default",
        ["ProbeHardwareHmiConnectionWhitelistedServices"] = "default",
        ["ProbePlcMonitorOnlineCapabilities"] = "default",
        ["RepairAndReimportPlcBlock"] = "importExport",
        ["RunToolTransaction"] = "default",
        ["SearchInstalledGsdDevices"] = "default",
        ["SeedProjectFromReference"] = "importExport",
        ["SetDeviceAddress"] = "default",
        ["SetDeviceItemAttribute"] = "default",
        ["SetDeviceItemChannel"] = "default",
        ["SetDeviceItemIoAddress"] = "default",
        ["SetOpcUaInterfaceEnabled"] = "default",
        ["SetPlcCpuSettings"] = "default",
        ["SetPlcProgram"] = "default",
        ["SetPlcUnitObjectAccess"] = "default",
        ["SetPlcWatchTableModifyValue"] = "default",
        ["ShowObjectInEditor"] = "default",
        ["SynchronizeLibrary"] = "libraryUpdate",
        ["TraceTagCause"] = "default",
        ["TraceTagCauseLive"] = "default",
    };

    [Fact]
    public void Every_Plc_dispatcher_registration_has_a_pinned_timeout_category()
    {
        var candidates = typeof(Operations).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!);
        string dispatcher = File.ReadAllText(Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "src", "PlcWorker", "FoundationWorkerDispatcher.cs"));
        var direct = Regex.Matches(dispatcher, "new WorkerOperationModule\\(\"([^\"]*)\", typeof\\(PlcFoundationEngine\\), new\\[\\] \\{([^}]+)\\}")
            .SelectMany(module => Regex.Matches(module.Groups[2].Value, "\"([^\"]+)\"")
                .Select(name => module.Groups[1].Value + "." + name.Groups[1].Value)).ToArray();
        Assert.NotEmpty(direct);
        var names = Operations.Names.Concat(candidates).Concat(direct)
            .Concat(PortedFamilies.All.SelectMany(family => Operations.FamilyNames(family.Name).Select(name => family.OperationPrefix + "." + name)))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name).ToArray();
        Assert.Equal(names, PlcCategories.Keys.OrderBy(name => name));
        Assert.Equal(names, WorkerTimeoutPolicy.PlcFamilies.Keys.OrderBy(name => name));
        foreach (var pair in PlcCategories) Assert.Equal(pair.Value, WorkerTimeoutPolicy.PlcFamilies[pair.Key]);
        var ported = PortedFamilies.All.Where(family => Operations.FamilyNames(family.Name).Length != 0)
            .SelectMany(Operations.FamilyTools).Distinct(StringComparer.Ordinal).OrderBy(name => name);
        Assert.Equal(ported, PortedCategories.Keys.OrderBy(name => name));
    }

    public static IEnumerable<object[]> PlcTimeoutCategories()
    {
        foreach (var pair in PlcCategories) yield return new object[] { pair.Key, pair.Value };
        foreach (var family in PortedFamilies.All.Where(family => Operations.FamilyNames(family.Name).Length != 0))
            foreach (string tool in Operations.FamilyTools(family))
                yield return new object[] { family.OperationPrefix + "." + Operations.FamilyNames(family.Name)[0], PortedCategories[tool], tool };
    }

    [Theory, MemberData(nameof(PlcTimeoutCategories))]
    public void Plc_timeout_categories_and_previews(string operation, string expected, string? nested = null)
    {
        var args = new JsonObject { ["mode"] = "apply", ["dryRun"] = false };
        if (nested != null) args["request"] = new JsonObject { ["Operation"] = nested, ["DryRun"] = false };
        if (operation == "runtime-channels.ExecutePlcSimAdvanced" && nested != null)
            args["request"] = new JsonObject { ["operation"] = nested == "ListPlcSimAdvancedInstances" ? "ReadPlcSimAdvancedInstances"
                : nested == "GetPlcSimAdvancedTags" ? "ReadPlcSimAdvancedTags" : nested, ["arguments"] = new JsonObject { ["dryRun"] = false } };
        if (operation == "SourceCandidate") args["candidate"] = new JsonObject { ["Check"] = new JsonObject {
            ["Request"] = new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["Action"] = "generate" }) } } };
        Assert.Equal(expected, WorkerTimeoutPolicy.PlcFamily(operation, args));
        Assert.Equal(WorkerTimeoutPolicy.Defaults[expected], new WorkerTimeoutPolicy().ForPlc(operation, args));
        args["mode"] = "preview";
        Assert.Equal("default", WorkerTimeoutPolicy.PlcFamily(operation, args));
        args["mode"] = "apply"; args["dryRun"] = true;
        Assert.Equal("default", WorkerTimeoutPolicy.PlcFamily(operation, args));
    }

    [Theory]
    [InlineData("libraries.ExecuteLibrary", "ManageLibraryType", "updateProject", "libraryUpdate")]
    [InlineData("libraries.ExecuteLibrary", "ManageLibraryType", "updateLibrary", "libraryUpdate")]
    [InlineData("libraries.ExecuteLibrary", "ManageLibraryType", "read", "default")]
    [InlineData("libraries.ExecuteLibrary", "ManageLibraryTypeVersion", "updateInstances", "libraryUpdate")]
    [InlineData("libraries.ExecuteLibrary", "ManageGlobalLibrary", "archive", "archive")]
    [InlineData("libraries.ExecuteLibrary", "ManageGlobalLibrary", "retrieve", "archive")]
    [InlineData("hmi-classic.ExecuteHmi", "ManageClassicHmiScript", "import", "importExport")]
    [InlineData("hmi-classic.ExecuteHmi", "ManageClassicHmiGraphic", "export", "importExport")]
    [InlineData("plc-sources.ExecutePlcOrganisation", "ManagePlcExternalSources", "generateBlocks", "compile")]
    [InlineData("plc-documents.ExecutePlcB5", "ManagePlcDocuments", "export", "importExport")]
    public void Plc_delegated_actions_use_the_operation_budget(string operation, string nested, string action, string expected)
    {
        var args = new JsonObject { ["request"] = new JsonObject { ["Operation"] = nested, ["Action"] = action, ["DryRun"] = false } };
        Assert.Equal(expected, WorkerTimeoutPolicy.PlcFamily(operation, args));
        args["request"]!["DryRun"] = true;
        Assert.Equal("default", WorkerTimeoutPolicy.PlcFamily(operation, args));
    }

    [Theory]
    [InlineData("compile", "compile")]
    [InlineData("sclGenerate", "compile")]
    [InlineData("lad", "importExport")]
    [InlineData("sclImport", "importExport")]
    [InlineData("hmiInfo", "default")]
    [InlineData("save", "default")]
    public void Scaffold_primitives_use_the_existing_operation_categories(string kind, string expected)
    {
        var args = new JsonObject { ["request"] = new JsonObject { ["Kind"] = kind } };
        Assert.Equal(expected, WorkerTimeoutPolicy.PlcFamily("project-lifecycle.ExecuteProjectScaffoldPrimitive", args));
        args["dryRun"] = true;
        Assert.Equal("default", WorkerTimeoutPolicy.PlcFamily("project-lifecycle.ExecuteProjectScaffoldPrimitive", args));
    }

    private sealed class PlcTimeoutScope : IDisposable
    {
        internal readonly Foundation.WorkerClient Worker;
        private readonly string fixtureDirectory = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "plc-timeout-" + Guid.NewGuid().ToString("N")[..8]);
        internal readonly string Log = Path.Combine(AppContext.BaseDirectory, "plc-timeout-" + Guid.NewGuid().ToString("N") + ".jsonl");
        private readonly string? previousLog = Environment.GetEnvironmentVariable("TIA_FIXTURE_LOG");
        private readonly string? previousFault = Environment.GetEnvironmentVariable("TIA_FIXTURE_FAULT");
        private readonly string? previousOverrides = Environment.GetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS");
        internal PlcTimeoutScope(string release, string config, string? environment = null, int defaultSeconds = 120)
        {
            Environment.SetEnvironmentVariable("TIA_FIXTURE_LOG", Log);
            Environment.SetEnvironmentVariable("TIA_FIXTURE_FAULT", "delay");
            Environment.SetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS", environment);
            string exe = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture("TiaMcp.WorkerChannel.TransportFixture", "TiaMcp.WorkerChannel.TransportFixture.exe");
            Assert.True(File.Exists(exe), exe);
            Directory.CreateDirectory(fixtureDirectory);
            foreach (string source in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(fixtureDirectory, Path.GetRelativePath(Path.GetDirectoryName(exe)!, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            exe = Path.Combine(fixtureDirectory, Path.GetFileName(exe));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
            Worker = new Foundation.WorkerClient(release, exe, Path.GetDirectoryName(exe)!, true, defaultSeconds, config) { Bundled = false };
        }
        internal async Task WarmBeforeScaledBudgets()
        {
            // Startup is not the behaviour this scope measures. Warm the real
            // channel with the production startup budget, then restore the exact
            // configured default before any measured compile/import dispatch.
            var policy = (WorkerTimeoutPolicy)typeof(Foundation.WorkerClient)
                .GetField("timeouts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Worker)!;
            var budgets = (IDictionary<string, TimeSpan>)policy.Budgets;
            var original = budgets["default"];
            Assert.Equal(TimeSpan.FromSeconds(1), original);
            try
            {
                budgets["default"] = WorkerTimeoutPolicy.Defaults["default"];
                await Worker.Call("ReadState", new(), CancellationToken.None);
            }
            finally { budgets["default"] = original; }
            Assert.Equal(original, policy.For("status"));
        }
        // The fixture appends while the test reads; File.ReadAllText refuses a file another process has open for writing.
        internal string ReadLog()
        {
            using var stream = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        internal async Task WaitForDispatch(string operation)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!ReadLog().Contains("\"operation\":\"" + operation + "\"", StringComparison.Ordinal))
                await Task.Delay(10, deadline.Token);
        }
        public void Dispose()
        {
            Process? owned = null;
            if ((int?)Worker.OperatorSnapshot()["pid"] is { } pid)
            {
                try { owned = Process.GetProcessById(pid); }
                catch (ArgumentException) { }
            }
            using (owned)
            {
                if (owned != null && !owned.HasExited && !string.Equals(owned.MainModule?.FileName,
                    Path.Combine(fixtureDirectory, "TiaMcp.WorkerChannel.TransportFixture.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Refusing to dispose a worker outside the private fixture directory.");
                Worker.Dispose();
                // Busy shutdown requests termination asynchronously. Keep our
                // own process handle until its DLL mappings have been released.
                owned?.WaitForExit();
            }
            Environment.SetEnvironmentVariable("TIA_FIXTURE_LOG", previousLog);
            Environment.SetEnvironmentVariable("TIA_FIXTURE_FAULT", previousFault);
            Environment.SetEnvironmentVariable("TIA_MCP_WORKER_TIMEOUTS", previousOverrides);
            if (File.Exists(Log)) File.Delete(Log);
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    private static JsonObject PlcCompletedArguments(string operation, int delay)
    {
        string project = Path.Combine(AppContext.BaseDirectory, "fixture.ap19");
        var reply = new JsonObject { ["Executed"] = true, ["ProjectFile"] = project, ["fixtureDelayMilliseconds"] = delay };
        if (operation == "CompileSoftware")
        {
            foreach (string key in new[] { "Messages", "Errors", "Warnings", "Info" }) reply[key] = new JsonArray();
            reply["State"] = "Success"; reply["ErrorCount"] = 0; reply["WarningCount"] = 0;
        }
        return new JsonObject { ["dryRun"] = false, ["expectedProjectFile"] = project,
            ["fixtureDelayMilliseconds"] = delay, ["fixtureReply"] = reply };
    }

    [Theory]
    [InlineData("14sp1")][InlineData("15.1")][InlineData("16")][InlineData("17")][InlineData("18")][InlineData("19")]
    public async Task Plc_long_compile_and_import_complete_with_scaled_production_budgets(string release)
    {
        // Scale seconds by 1/120: 1300 ms models a 156 s call, beyond the old 120 s limit.
        using var scope = new PlcTimeoutScope(release, "{\"default\":1,\"compile\":15,\"importExport\":15}");
        await scope.WarmBeforeScaledBudgets();
        foreach (string operation in new[] { "CompileSoftware", "ImportBlocks" })
        {
            var result = await scope.Worker.Call(operation, PlcCompletedArguments(operation, 1300), CancellationToken.None);
            Assert.Equal(1300, (int?)result!["fixtureDelayMilliseconds"]);
            Assert.False(scope.Worker.Poisoned);
        }
    }

    [Theory, InlineData("CompileSoftware"), InlineData("ImportBlocks")]
    public async Task Plc_scaled_long_call_still_fails_with_only_the_default_budget(string operation)
    {
        using var scope = new PlcTimeoutScope("19", "{\"default\":1,\"compile\":1,\"importExport\":1}");
        await scope.WarmBeforeScaledBudgets();
        var error = await Assert.ThrowsAsync<ChannelFaultException>(() =>
            scope.Worker.Call(operation, PlcCompletedArguments(operation, 1300), CancellationToken.None));
        Assert.Equal(true, error.Data["foundationRequestSent"]);
        Assert.True(scope.Worker.Poisoned);
    }

    [Fact]
    public async Task Plc_compile_preview_uses_default_instead_of_the_compile_override()
    {
        using var scope = new PlcTimeoutScope("19", "{\"default\":2,\"compile\":0.2}");
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        var observation = new CompileObservation { Binding = new() { ProcessId = Environment.ProcessId,
            ProcessStartUtc = DateTimeOffset.UtcNow, ProjectFile = Path.Combine(AppContext.BaseDirectory, "fixture.ap19"), Ownership = "borrowed" },
            ObjectValidity = "valid", Dirty = false, TargetId = "software-1", SoftwareId = "software-1", TargetKind = "PlcSoftware",
            TargetName = "PLC_1", Compilable = true, OfflineState = "offline", SafetyPermission = "not-applicable" };
        var reply = await scope.Worker.Call("CompileCandidate", new() { ["mode"] = "preview", ["fixtureDelayMilliseconds"] = 400,
            ["candidate"] = JsonSerializer.SerializeToNode(new CompileCall { Action = "observe" }),
            ["fixtureReply"] = JsonSerializer.SerializeToNode(new CompileReply { Observation = observation }) }, CancellationToken.None);
        Assert.Equal("software-1", (string?)reply!["Observation"]?["TargetId"]);
        Assert.False(scope.Worker.Poisoned);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Plc_over_budget_call_is_unknown_and_locks_the_session(bool environmentOverride)
    {
        string shortBudget = "{\"default\":2,\"compile\":0.2}";
        using var scope = new PlcTimeoutScope("19", environmentOverride ? "{\"compile\":15}" : shortBudget, environmentOverride ? shortBudget : null);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<ChannelFaultException>(() => scope.Worker.Call("CompileSoftware", new() { ["dryRun"] = false, ["fixtureDelayMilliseconds"] = 1300 }, CancellationToken.None));
        Assert.Equal(true, error.Data["foundationRequestSent"]);
        Assert.Equal(true, error.Data["foundationSessionPoisoned"]);
        Assert.True(scope.Worker.Poisoned);
        var result = Foundation.FoundationV4Result.Failure("19", "CompilePlc", "timeout", true, true, null, error);
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.True((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Worker.Call("ReadState", new(), CancellationToken.None));
    }

    [Fact]
    public async Task Plc_default_seconds_and_first_attach_budget_apply_once_per_generation()
    {
        using var scope = new PlcTimeoutScope("19", "{\"firstAttach\":3}", defaultSeconds: 1);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        await scope.Worker.Call("SessionCandidate", new() { ["mode"] = "preview",
            ["candidate"] = JsonSerializer.SerializeToNode(new SessionCandidateCall { Action = "observe", Check = new() { Request = new() { Action = "attach" } } }),
            ["fixtureReply"] = JsonSerializer.SerializeToNode(new SessionCandidateReply { Observation = new() }) }, CancellationToken.None);
        var args = new JsonObject { ["processId"] = Environment.ProcessId, ["fixtureDelayMilliseconds"] = 1300 };
        await scope.Worker.Call("Attach", args, CancellationToken.None);
        Assert.False(scope.Worker.Poisoned);
        await Assert.ThrowsAsync<ChannelFaultException>(() => scope.Worker.Call("Attach", args, CancellationToken.None));
        var restart = await scope.Worker.RestartHostWorker(true, CancellationToken.None);
        Assert.True((bool?)restart["restarted"]);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        await scope.Worker.Call("Attach", args, CancellationToken.None);
        Assert.False(scope.Worker.Poisoned);
    }

    [Fact]
    public async Task Plc_cancellation_before_dispatch_is_CANCELLED_and_after_dispatch_keeps_the_result()
    {
        using var scope = new PlcTimeoutScope("19", "{\"default\":2,\"compile\":3}");
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
        using var before = new CancellationTokenSource(); before.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.Worker.Call("CompileSoftware", new(), before.Token));
        var refused = Foundation.FoundationV4Result.Failure("19", "CompilePlc", "cancelled", false, true, null, error);
        Assert.Equal("CANCELLED", (string?)refused.StructuredContent?["error"]?["code"]);
        Assert.DoesNotContain("CompileSoftware", scope.ReadLog());
        using var after = new CancellationTokenSource();
        var call = scope.Worker.Call("CompileSoftware", PlcCompletedArguments("CompileSoftware", 400), after.Token);
        await scope.WaitForDispatch("CompileSoftware"); after.Cancel();
        Assert.Equal(400, (int?)(await call)!["fixtureDelayMilliseconds"]);
        Assert.False(scope.Worker.Poisoned);
        await scope.Worker.Call("ReadState", new(), CancellationToken.None);
    }
}
