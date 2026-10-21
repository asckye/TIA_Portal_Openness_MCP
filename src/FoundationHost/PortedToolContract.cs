#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
#endif
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Logic.V4;
#if TIA_FOUNDATION_TEST_HOST
using enginehost::TiaMcpServer.ModelContextProtocol;
using enginehost::TiaMcpServer.Siemens.Services;
using OptionalPorts = enginehost::TiaMcp.Shared;
#else
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using OptionalPorts = TiaMcp.Shared;
#endif

namespace TiaMcp.FoundationHost;

// Declarations, availability and the existing Foundation session lane are shared
// by every port. Only the typed service factory is specific to a family.
internal static class PortedToolContract
{
    // Device creation refuses before a native create, or after a TIA refusal verified by an unchanged device
    // inventory. The worker reports both as a typed precondition; keep it typed for the hardware contract.
    private static readonly HashSet<string> DeviceCreationOperations = new(StringComparer.Ordinal) {
        "hardware-devices.HardwareCreateDevice", "hardware-devices.HardwareCreateDeviceFallback",
        "hardware-devices.HardwareCreateGsdDevice", "hardware-devices.HardwareCreateCatalogDevice" };
    internal static bool IsDeviceRefusal(string operation, WorkerOperationException error)
        => DeviceCreationOperations.Contains(operation) && error.KnownNoMutation && error.Outcome == "rejected-before-operation"
            && error.ExceptionType == nameof(TiaMcp.Adapters.Contracts.AdapterPreconditionException);
    internal static TiaMcp.Adapters.Contracts.AdapterPreconditionException DeviceRefusal(WorkerOperationException error)
    {
        string message = error.Message;
        string suffix = error.Parameter == null ? "" : " (Parameter '" + error.Parameter + "')";
        if (suffix.Length > 0 && message.EndsWith(suffix, StringComparison.Ordinal)) message = message[..^suffix.Length];
        return new TiaMcp.Adapters.Contracts.AdapterPreconditionException(message, error.Parameter, error.Code == -32602, error);
    }
    // V20/V21 B1 declarations belong to the full EngineHost catalog. The
    // bounded plc-foundation profile includes F18/F19/F20/F21 hardware ports.
    internal static IEnumerable<PortedFamilies.Family> Families(string release) =>
        PortedFamilies.All.Where(family => (family.Name == "F04" || family.Name == "F22" || family.Name == "F29" || family.Name == "F30" || family.Name == "F31" || family.Name == "F32" || family.Name == "F34" || family.Name == "F05" || family.Name == "F07" || family.Name == "F33" || family.Name == "F24" || family.Name == "F25" || family.Name == "F26" || family.Name == "F27" || family.Name == "F28" || family.Name == "F10" || family.Name == "F14" || family.Name == "F15" || family.Name == "F23" || family.Name == "F06" || family.Name == "F35" || family.Name == "F08" || family.Name == "F09" || family.Name == "F11" || family.Name == "F12" || family.Name == "F13" || family.Name == "F16" || family.Name == "F17" || family.Name == "F18" || family.Name == "F19" || family.Name == "F20" || family.Name == "F21") && family.Available(release))
            .Select(family => new PortedFamilies.Family(family.Name, family.OperationPrefix, family.Releases,
                TiaMcp.PlcWorker.WorkerOperations.FamilyTools(family), family.Actions));
    internal static McpServerTool Wrap(FoundationTool tool)
    {
        string? candidate = tool.ProtocolTool.Name switch {
            "CompileDevice" or "CompileHmiDiagnostics" => "P6-COMPILE",
            "ImportPlcTagTablesFromDirectory" => "P6-IMPORT",
            "ManagePlcExternalSources" => "P6-SOURCE", _ => null };
        if (candidate != null && BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, tool.Release, candidate) == BehaviorPolicy.SafeV4)
            return new FoundationV4Tool(tool, tool.Release, null);
        // The readiness guard of a bridged call asks whether this target refuses its arguments by capability (P8-04j2).
        // The answer depends only on the release, the tool and the arguments, so any registered instance answers it.
        McpServer.PortedCapabilityRefusals[(tool.Release, tool.ProtocolTool.Name)] = tool.RefusesByCapability;
        return tool.CapabilityAdmission(McpServer.WrapTools(new[] { tool.ValidatedTool() }).Single());
    }

    // The primary module operation comes first; previews and fallback reads are
    // also part of the wiring. Pure builders do not read session state.
    internal static string[] WiredOperations(string tool) => tool switch
    {
        "ListPlcSimAdvancedInstances" or "ManagePlcSimAdvancedInstance" or "GetPlcSimAdvancedTags" or "WritePlcSimAdvancedTags" or "RunPlcSimAdvancedTestScenario" => new[] { "runtime-channels.ExecutePlcSimAdvanced" },
        "CheckDownloadReadiness" or "CompareSoftwareToOnline" or "ConnectOnlinePlc" or "DisconnectOnlinePlc" or "DisconnectOnlinePlcs" or "DownloadPlc" or "DownloadPlcToFolder" or "GeneratePlcLoadableFile" or "GetOnlineState" or "GetPlcBlockFingerprints" or "ListTransferRoutes" or "ManagePlcDataBlockSnapshot" or "ScanAccessibleDevices" or "UploadDeviceParameters" or "UploadStationFromPlc" => new[] { "plc-online.ExecuteOnline", "ReadState" },
        "ListSafetyActivationTests" or "ManageSafetyActivationTest" or "ManageSafetyActivationTestGroup" or "ManageSafetyFunction" or "ManageSafetyFunctionCondition" => new[] { "plc-safety.ExecuteSafetyValidation", "ReadState" },
        "ExportSafetyPrintout" or "GetSafetyBlockSignatures" or "ManagePlcSafety" or "ManageSafetyGlobalSettings" => new[] { "plc-safety.ExecuteSafety", "ReadState" },
        "GenerateSivarc" or "GetSivarcRuleTree" or "ListSivarcBlockDefinitions" or "ListSivarcRules" or "ManageSivarcBlockDefinition" or "ManageSivarcRule" or "ManageSivarcRuleContainer" or "ManageSivarcScreenLayout" or "ManageSivarcTableRule" or "ResolveSivarcExpression" or "UpgradeSivarcDefinitions" => new[] { "sivarc.ExecuteSivarc", "ReadState" },
        "GetDccObject" or "ListDccCharts" or "ManageDcbLibraries" or "ManageDccBlock" or "ManageDccChart" or "ManageDccChartInterface" or "ManageDccChartPartition" or "ManageDccPin" => new[] { "startdrive.ExecuteDcc", "ReadState" },
        "GetDriveParameters" or "GetOnlineDriveParameters" or "ListDriveObjects" or "ManageDriveFunctions" or "ManageDriveHardwareModule" or "ManageDriveSafetyAcceptanceTest" or "ManageDriveSecurity" or "ManageDriveTelegrams" or "ManageOnlineDriveFunctions" or "ManageStartdriveParameter" or "ManageTechnologyExtensions" => new[] { "startdrive.ExecuteStartdrive", "ReadState" },
        "ArchiveSavedProject" => new[] { "project-lifecycle.ArchiveSavedProject", "ReadState" },
        "BuildProjectScaffold" => new[] { "project-lifecycle.ExecuteProjectScaffoldPrimitive", "ReadState" },
        "ConnectIsolatedPortal" => new[] { "project-lifecycle.ConnectIsolatedPortal", "ReadState" },
        "EnsureOpennessUserGroup" => new[] { "project-lifecycle.EnsureOpennessUserGroup", "ReadState" },
        "GetPortalInfo" => new[] { "project-lifecycle.ReadPortalInfo", "ReadState" },
        "ManageMultiuserSession" => new[] { "project-lifecycle.ExecuteMultiuserSession", "ReadState" },
        "RetrieveProjectArchive" => new[] { "project-lifecycle.RetrieveProjectArchive", "ReadState" },
        "ExchangeCfcCharts" or "ManageCfcChartProtection" => new[] { "cfc-charts.ExecuteOptionalProduct", "ReadState" },
        "ListTestSuiteCases" or "ManageTestSuiteCase" or "ExchangeTestSuiteCase" or "RunTestSuiteCase" or "ExportScadaData" or "ImportSinumerikAlarmTexts" or "ManageSinumerikArchive" or "ManageSinumerikSafetyMode" or "InitializeSimotionScripting" => new[] { "optional-products.ExecuteOptionalProduct", "ReadState" },
        "SaveProjectCopy" => new[] { "project-lifecycle.ArchiveSaveProjectCopy", "ReadState" },
        "ValidateAutomationContext" => new[] { "project-lifecycle.ValidateAutomationContext", "ReadState" },
        "GetProjectUserManagement" or "ManageProjectUserManagement" => new[] { "project-security.ExecuteProjectUsers", "ReadState" },
        "ManagePlcProtection" or "GetPlcPutGetAccess" or "SetPlcPutGetAccess" or "ManageWatchForceTableWebAccess" => new[] { "project-security.ExecuteHardwareSecurity", "ReadState" },
                "ManageDeviceUsers" => new[] { "project-security.ExecuteDeviceUsers", "ReadState" },
                "ManagePlcCertificate" => new[] { "project-security.ExecuteCertificate", "ReadState" },
        "ManageSyslogServers" or "ManagePasswordPolicy" or "ManageUmcUsers" => new[] { "project-security.ExecuteSecurityDeep", "ReadState" },
        "GetProjectProtection" => new[] { "project-security.ReadProjectProtection", "ReadState" },
        "ManagePlcGitRepository" => Array.Empty<string>(),
        "ManageTeamcenterConnection" or "ManageTeamcenterDataset" or "ManageTeamcenterWorkflow" => new[] { "version-control.ExecuteTeamcenter", "ReadState" },
        "ConnectProjectToWorkspace" or "CreateVersionControlWorkspace" or "GetVersionControlStatus" or "ListVersionControlWorkspaces" or "SynchronizeVersionControlWorkspace" => new[] { "version-control.ExecuteVersionControl", "ReadState" },
        "ExportPlcBlockDocuments" or "ExportPlcBlocksDocuments" or "GetPlcBlockScopes" or "ImportPlcBlockDocuments" or "ImportPlcBlocksDocuments" or "ManagePlcBlockDocuments" or "ManagePlcDocuments" => new[] { "plc-documents.ExecutePlcB5", "ReadState" },
        "ExchangePlcAlarmTextLists" or "ExchangePlcSupervisions" or "ExportAlarmClasses" or "ExportAlarmInstanceTexts" or "ExportAlarmTextLists" or "ExportPlcProDiagInfo" or "ImportAlarmClasses" or "ImportAlarmTextLists" or "ImportPlcAlarmInstanceTexts" or "ManagePlcAlarmTextList" or "ManagePlcSupervision" => new[] { "plc-alarms.ExecutePlcB5", "ReadState" },
        "ExportOpcUaInterface" or "GenerateOpcUaModelledInterface" or "GetOpcUaAccessControl" or "GetPlcOpcUaConfiguration" or "ImportOpcUaInterface" or "ManageOpcUaAccessControl" or "ManageOpcUaInterface" or "SetOpcUaInterfaceEnabled" => new[] { "plc-opcua.ExecutePlcB5", "ReadState" },
        "ConfigureMotionHardwareConnection" or "ExchangeMotionCamData" or "ExportTechnologyObjectsToDirectory" or "GetMotionAxisConfiguration" or "GetTechnologyObjectTree" or "ImportTechnologyObject" or "ImportTechnologyObjectsFromDirectory" or "ManageMotionAxis" or "ManageTechnologyObject" => new[] { "plc-technology.ExecutePlcOrganisation", "ReadState" },
        "ExportPlcWatchTablesToDirectory" or "GetPlcWatchTableCurrentValuesReadOnly" or "ImportPlcWatchTableOffline" or "ListPlcForceTables" or "ManagePlcTableEntries" or "MonitorPlcWatchTableS7" or "ProbePlcMonitorOnlineCapabilities" or "SetPlcWatchTableModifyValue" or "TraceTagCause" or "TraceTagCauseLive" => new[] { "plc-watch.ExecutePlcOrganisation", "ReadState" },
        "PlanOnlineReadOnlyMonitoring" or "PlanOnlineReadOnlyDataProvider" => Array.Empty<string>(),
        "GetDeviceAddressing" => new[] { "hardware-addressing.ReadHardwareAddressing", "ReadState" },
        "GetDeviceIpAddress" => new[] { "hardware-addressing.ReadHardwareIpAddress", "ReadState" },
        "GetDeviceItemIoAddresses" => new[] { "hardware-addressing.ReadHardwareIoAddresses", "hardware-addressing.DescribeHardwareIoChildren", "ReadState" },
        "SetDeviceAddress" => new[] { "hardware-addressing.UpdateHardwareAddress", "ReadState" },
        "SetDeviceItemIoAddress" => new[] { "hardware-addressing.SetHardwareIoAddress", "hardware-addressing.ReadHardwareIoAddresses", "ReadState" },
        "ExportDeviceAml" => new[] { "hardware-aml.ExportHardwareAml", "ReadState" },
        "ImportDeviceAml" => new[] { "hardware-aml.ImportHardwareAml", "ReadState" },
        "BuildDeviceAmlDocument" => Array.Empty<string>(),
        "CreateDevice" => new[] { "hardware-devices.HardwareCreateDevice", "ReadState" },
        "CreateGsdDevice" => new[] { "hardware-devices.HardwareCreateGsdDevice", "ReadState" },
        "CreateHardwareCatalogDevice" => new[] { "hardware-devices.HardwareCreateCatalogDevice", "ReadState" },
        "ListDevices" => new[] { "hardware-devices.HardwareDescribeDevices", "hardware-devices.HardwareReadPlcNames", "ReadState" },
        "GetDeviceInfo" => new[] { "hardware-devices.HardwareDescribeDeviceAt", "ReadState" },
        "GetDeviceItemInfo" => new[] { "hardware-devices.HardwareDescribeItemAt", "ReadState" },
        "GetDeviceItemTree" => new[] { "hardware-devices.HardwareReadItemTree", "ReadState" },
        "SearchInstalledGsdDevices" => new[] { "hardware-devices.HardwareLegacySearchInstalledGsdDevices", "ReadState" },
        "GetDeviceAttributes" => new[] { "hardware-devices.HardwareLegacyDumpDeviceAttributes", "ReadState" },
        "SetDeviceItemAttribute" => new[] { "hardware-devices.HardwareLegacySetDeviceItemAttribute", "ReadState" },
        "SetPlcCpuSettings" => new[] { "hardware-devices.HardwareLegacySetCpuCommonSettings", "ReadState" },
        "GetDevicePlugLocations" => new[] { "hardware-devices.HardwareReadPlugLocations", "ReadState" },
        "PlugDeviceItem" => new[] { "hardware-devices.HardwarePlugModule", "ReadState" },
        "ManageHardwareObject" => new[] { "hardware-devices.HardwareManageHardwareObject", "ReadState" },
        "ManageDeviceUserGroup" => new[] { "hardware-devices.HardwareManageDeviceUserGroup", "ReadState" },
        "GetHardwareFeatures" => new[] { "hardware-devices.HardwareReadHardwareFeatures", "ReadState" },
        "ManageDeviceServiceObjects" => new[] { "hardware-devices.HardwareManageDeviceServiceObjects", "ReadState" },
        "ManageHardwareUtilities" => new[] { "hardware-devices.HardwareManageHardwareUtilities", "ReadState" },
        "ExchangeSystemDiagnosticsSettings" => new[] { "hardware-devices.HardwareExchangeSystemDiagnosticsSettings", "ReadState" },
        "ListIoSystems" => new[] { "hardware-network.HardwareReadIoSystems", "ReadState" },
        "ManageIoSystem" => new[] { "hardware-network.HardwareManageIoSystem", "ReadState" },
        "ListNetworkDomains" => new[] { "hardware-network.HardwareReadNetworkDomains", "ReadState" },
        "ManageNetworkDomain" => new[] { "hardware-network.HardwareManageNetworkDomain", "ReadState" },
        "ListTransferAreas" => new[] { "hardware-network.HardwareReadTransferAreas", "ReadState" },
        "ManageTransferArea" => new[] { "hardware-network.HardwareManageTransferArea", "ReadState" },
        "ListDeviceItemChannels" => new[] { "hardware-network.HardwareReadDeviceItemChannels", "ReadState" },
        "SetDeviceItemChannel" => new[] { "hardware-network.HardwareUpdateDeviceItemChannel", "ReadState" },
        "ManagePortInterconnection" => new[] { "hardware-network.HardwareManagePortInterconnection", "ReadState" },
        "GetDeviceItemNetworkInfo" => new[] { "hardware-network.HardwareGetDeviceItemNetworkInfo", "ReadState" },
        "ConnectDeviceNodesToProfinetSubnet" => new[] { "hardware-network.HardwareProbeConnectDeviceNodesToSubnet", "ReadState" },
        "PlanHardwareNetworkConfiguration" => Array.Empty<string>(),
        "EnsureSubnet" => new[] { "hardware-network.HardwareEnsureSubnet", "ReadState" },
        "AttachDeviceNodeToSubnet" => new[] { "hardware-network.HardwareAttachDeviceNodeToSubnet", "ReadState" },
        "ProbeHardwareHmiConnectionOwnerCandidates" => new[] { "hardware-network.ReadHardwareOwnerCandidates", "ReadState" },
        "ProbeHardwareHmiConnectionWhitelistedServices" => new[] { "hardware-network.ReadHardwareWhitelistedServices", "ReadState" },
        "GetProjectTopology" => new[] { "hardware-network.HardwareGetProjectTopology", "ReadState" },
        "ListCommunicationConnections" => new[] { "hardware-network.HardwareReadCommunicationConnections", "ReadState" },
        "ManageCommunicationConnection" => new[] { "hardware-network.HardwareManageCommunicationConnection", "ReadState" },
        "GetLibraryOverview" or "GetLibraryType" or "ManageLibraryType" or "CheckLibraryUpdates" or "SynchronizeLibrary" or "CompareLibraryObjects" or "ManageLibraryTypeVersion" or "CreateLibraryMasterCopy" or "ManageLibraryMasterCopy" or "ImportLibraryTypeDocuments" or "ManageGlobalLibrary" or "ManageLibraryFolder" or "ProbeGlobalLibrary" or "ImportMasterCopyFromGlobalLibrary" => new[] { "libraries.ExecuteLibrary", "ReadState" },
        "RunToolTransaction" => new[] { "engineering-services.BeginEngineeringTransaction", "engineering-services.ReadEngineeringTransaction", "engineering-services.CommitEngineeringTransaction", "engineering-services.DisposeEngineeringTransaction", "ReadState" },
        "ManageProjectLanguage" or "ExportProjectTexts" or "ImportProjectTexts" or "CompareLibraries" or "CompareProjects" or "GetProjectSettings" or "GetObjectIdentifier" or "ShowObjectInEditor" or "ManageProjectCompilationSettings" => new[] { "engineering-services.ExecuteEngineeringServices", "ReadState" },
        "GetHmiProgramInfo" or "DescribeHmiSoftware" or "DescribeHmiScreen" or "DescribeHmiTagTable" or "DescribeHmiTag" or "DescribeHmiScreenItem" or "ListHmiScreens" or "ListHmiTagTables" or "ListHmiTags" or "ListHmiConnections" or "ExportHmiScreen" or "ExportHmiTagTable" or "ExportHmiConnection" or "ExportHmiProgram" or "ImportHmiScreen" or "ImportHmiTagTable" or "ImportHmiConnection" or "ImportHmiScreensFromDirectory" or "ImportHmiTagTablesFromDirectory" or "GetHmiScreenSnapshot" or "ListHmiScreenPaths" or "DeleteHmiTag" => new[] { "hmi-generic.ExecuteHmi", "ReadState" },
        "ApplyUnifiedHmiLayout" or "ApplyUnifiedHmiScreenDesign" or "ApplyUnifiedHmiTheme" or "BindUnifiedHmiButtonPressedTag" or "BindUnifiedHmiTagDynamization" or "DeleteEmptyUnifiedHmiScreenGroup" or "DeleteUnifiedHmiButtonEvent" or "DeleteUnifiedHmiDynamization" or "DescribeUnifiedHmiButtonEventScript" or "EnsureUnifiedHmiButtonAction" or "EnsureUnifiedHmiButtonEventHandler" or "EnsureUnifiedHmiConnection" or "EnsureUnifiedHmiDynamization" or "EnsureUnifiedHmiScreen" or "EnsureUnifiedHmiScreenItem" or "EnsureUnifiedHmiTag" or "EnsureUnifiedHmiTagTable" or "GetUnifiedHmiButtonEvent" or "GetUnifiedHmiDynamization" or "GetUnifiedHmiTexts" or "ManageUnifiedHmiGroup" or "ManageUnifiedScreenItem" or "SetUnifiedHmiButtonEventScriptCode" or "SetUnifiedHmiRuntimeState" => new[] { "hmi-unified.ExecuteUnifiedHmi", "ReadState" },
        "ListUnifiedHmiApiTypes" or "DescribeUnifiedScreenItemType" => new[] { "hmi-unified.ExecuteUnifiedHmiMetadata" },
        "GetUnifiedObjectProperties" or "SetUnifiedObjectProperties" or "SetUnifiedMultilingualProperty" or "ValidateUnifiedObject" or "GetUnifiedCrossReferences" or "ExportUnifiedEngineeringList" or "ManageUnifiedLoggingTag" or "SetUnifiedLogDuration" or "ManageUnifiedOpcUaAlarmType" or "GetUnifiedPlantObject" or "ManageUnifiedPlantNode" or "SetUnifiedPlantObject" or "GetUnifiedObjectEvents" or "ManageUnifiedObjectParts" or "ManageUnifiedDynamization" or "ManageUnifiedScreenLayout" or "ManageUnifiedListEntries" or "GetUnifiedAlarmCommon" or "GetUnifiedAuditSettings" or "ImportUnifiedEngineeringList" or "ListUnifiedEngineeringObjects" or "ManageUnifiedEngineeringObject" or "ManageUnifiedEvent" => new[] { "hmi-unified-model.ExecuteUnified", "ReadState" },
        "ExchangeUnifiedTags" or "ExchangeUnifiedScriptModules" or "ImportUnifiedOpcUaAlarms" or "SetUnifiedGlobalScript" or "GetUnifiedGraphicSelection" or "GetUnifiedRuntimeSettings" or "SetUnifiedRuntimeSettings" or "ListUnifiedGlobalScripts" or "GetUnifiedGlobalScript" or "ListUnifiedTagDefinitions" or "GetUnifiedScreenBranch" or "GetUnifiedLibraryType" or "ListUnifiedLibraryFolderEntries" or "GetUnifiedFaceplateInstance" => new[] { "hmi-unified-exchange.ExecuteUnified", "ReadState" },
        "CompareUnifiedGraphicSelections" => Array.Empty<string>(),
        "ReleaseUnifiedReadCursor" => new[] { "hmi-unified-exchange.ReleaseUnifiedReadCursor" },
        "GetClassicHmiScreenTree" or "ManageClassicHmiScreenObject" or "ManageClassicHmiFolder" or "ManageClassicHmiGraphic" or "ListClassicHmiScripts" or "ManageClassicHmiScript" or "ManageClassicHmiCycle" or "ManageClassicHmiTextGraphicList" or "GetClassicHmiGlobalization" or "ListClassicHmiFaceplates" => new[] { "hmi-classic.ExecuteHmi", "ReadState" },
        "DescribeObject" or "DescribeObjectProperty" or "DescribeService" or "GetObjectProperty" or "ListObjectChildren" or "InvokeObject" or "InvokeService" => new[] { "openness-reflection.ExecuteOpennessReflection", "ReadState" },
        "CreatePlcBlockGroup" or "CreatePlcTypeGroup" or "DeleteEmptyPlcBlockGroup" or "DeletePlcBlock" or "DeletePlcTagTable" or "DeletePlcType" or "ManagePlcBlockProtection" or "ManagePlcUserGroup" or "MovePlcBlockToGroup" or "ListPlcSystemGroups" => new[] { "plc-organisation.ExecutePlcOrganisation", "ReadState" },
        "CreatePlcInstanceDb" or "ManagePlcTagDefinition" or "SetPlcProgram" or "GetPlcTagTableConstants" or "ImportPlcBlockVerified" or "BuildAndImportPlcArtifact" or "DescribePlcBlockLogic" or "RepairAndReimportPlcBlock" or "ImportPlcTagTablesFromDirectory" => new[] { "plc-software.ExecutePlcOrganisation", "ReadState" },
        "SeedProjectFromReference" => new[] { "plc-software.ExecutePlcOrganisation", "plc-software.SeedReferenceHmi", "ReadState" },
        "ListPlcSoftwareUnits" or "ManagePlcSoftwareUnit" or "SetPlcUnitObjectAccess" or "GetPlcChecksums" or "GetPlcObjectFingerprints" or "ManagePlcBlockWriteProtection" => new[] { "plc-integrity.ExecutePlcOrganisation", "ReadState" },
        "CompileDevice" or "CompileHmiDiagnostics" => new[] { "plc-compile.ExecutePlcOrganisation", "ReadState" },
        "GetPlcBlockEditCapabilities" => new[] { "plc-analysis.ExportBlockDocument", "ReadState" },
        "GeneratePlcSourceFromBlocks" or "ManagePlcExternalSources" or "GetPlcCrossReferences" => new[] { "plc-sources.ExecutePlcOrganisation", "ReadState" },
        _ when PortedFamilies.Contains(tool) && PortedFamilies.ForTool(tool).Name is "F01" or "F02" or "F03" or "F04" => Array.Empty<string>(),
        _ => throw new NotSupportedException("Unregistered ported tool: " + tool)
    };

    internal static IEnumerable<McpServerTool> Create(IFoundationWorker worker, string release)
    {
        foreach (var method in PortedToolDeclarations.Methods(release))
        {
            string name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name!;
            // The four SIMATIC SD candidate entries already belong to Foundation.
            // Retain their admitted schemas and policies while the raw engine
            // callbacks use the new document port.
            if (PortedFamilies.ForTool(name).Name == "F10" && FoundationTools.Definitions.Any(d =>
                FoundationTools.Available(d, release) && FoundationV4Tool.Name(d.Name) == name)) continue;
            if (PortedFamilies.ForTool(name).Name == "F04" && method.DeclaringType!.Name != "PlcSimAdvancedTools") continue;
            if (Families(release).Any(family => family.Tools.Contains(name, StringComparer.Ordinal))) yield return new FoundationTool(worker, release, method, name);
        }
    }
}

internal sealed partial class FoundationTool
{
    private static readonly ConditionalWeakTable<IFoundationWorker, DeviceCreationSession> DeviceCandidates = new();
    private readonly MethodInfo? portedMethod;
    private readonly string? portedRelease;
    private readonly ToolDescriptor? portedDescriptor;
    internal bool Ported => portedMethod != null;
    internal string Release => portedRelease!;

    internal FoundationTool(IFoundationWorker worker, string release, MethodInfo method, string name)
    {
        this.worker = worker; portedMethod = method; portedRelease = release;
        var declaration = DeclaredToolMetadata.Create(name, method, _ => throw new InvalidOperationException("Metadata only."), out var descriptor);
        DeclaredToolMetadata.V4(declaration.ProtocolTool, foundation: true);
        portedDescriptor = descriptor; tool = declaration.ProtocolTool;
        definition = new Definition(name, PortedFamilies.ForTool(name).OperationPrefix, descriptor.RawDescription,
            descriptor.Parameters.Select(p => new Argument(p.Name, p.FriendlyType, p.Required)).ToArray(), PortedFamilies.ForTool(name).Name);
    }

    internal McpServerTool ValidatedTool() => new InfrastructureInputTool(this, portedDescriptor!);
    internal McpServerTool CapabilityAdmission(McpServerTool inner) => new CapabilityTool(this, inner);
    // The same admission as CapabilityTool: only valid arguments reach the capability check.
    internal bool RefusesByCapability(JsonElement arguments)
        => McpServer.ToolInvoker.ValidateArguments(portedDescriptor!, arguments, tool.InputSchema) == null
            && RefusePortedCapability(arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)) != null;

    private sealed class CapabilityTool(FoundationTool owner, McpServerTool inner) : McpServerTool
    {
        public override Tool ProtocolTool => inner.ProtocolTool;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
            // Invalid input still goes through the existing wrapper so its
            // behaviour disclosure and raw rejection payload remain unchanged.
            var input = JsonSerializer.SerializeToElement(arguments);
            var validation = McpServer.ToolInvoker.ValidateArguments(owner.portedDescriptor!, input, ProtocolTool.InputSchema);
            if (validation != null && PortedFamilies.ForTool(owner.tool.Name).Name is "F27" or "F28"
                && UnifiedInputCodec.HasDuplicateKeys(input))
            {
                var rejected = McpServer.V4TargetReject(owner.tool.Name, validation, current: true);
                McpServer.RecordAdmissionProjection(request, rejected);
                return new ValueTask<CallToolResult>(rejected);
            }
            if (validation == null && owner.RefusePortedCapability(arguments) is { } refusal)
            {
                McpServer.RecordAdmissionProjection(request, refusal);
                return new ValueTask<CallToolResult>(refusal);
            }
            return inner.InvokeAsync(request, cancellationToken);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> UnifiedParameters = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["softwarePath"] = nameof(UnifiedRequest.SoftwarePath),
        ["objectPath"] = nameof(UnifiedRequest.ObjectPathJson),
        ["offset"] = nameof(UnifiedRequest.Offset),
        ["limit"] = nameof(UnifiedRequest.Limit),
        ["properties"] = nameof(UnifiedRequest.PropertiesJson),
        ["dryRun"] = nameof(UnifiedRequest.DryRun),
        ["property"] = nameof(UnifiedRequest.Property),
        ["culture"] = nameof(UnifiedRequest.Culture),
        ["rawText"] = nameof(UnifiedRequest.RawText),
        ["filter"] = nameof(UnifiedRequest.Filter),
        ["category"] = nameof(UnifiedRequest.Category),
        ["name"] = nameof(UnifiedRequest.Name),
        ["destinationDirectory"] = nameof(UnifiedRequest.DestinationDirectory),
        ["tagPath"] = nameof(UnifiedRequest.TagPathJson),
        ["action"] = nameof(UnifiedRequest.Action),
        ["durationPath"] = nameof(UnifiedRequest.DurationPathJson),
        ["kind"] = nameof(UnifiedRequest.Kind),
        ["days"] = nameof(UnifiedRequest.Days),
        ["hours"] = nameof(UnifiedRequest.Hours),
        ["minutes"] = nameof(UnifiedRequest.Minutes),
        ["seconds"] = nameof(UnifiedRequest.Seconds),
        ["hundredNanoseconds"] = nameof(UnifiedRequest.HundredNanoseconds),
        ["nodeId"] = nameof(UnifiedRequest.NodeId),
        ["connection"] = nameof(UnifiedRequest.Connection),
        ["plantPath"] = nameof(UnifiedRequest.PlantPath),
        ["plantObjectType"] = nameof(UnifiedRequest.PlantObjectType),
        ["collectionProperty"] = nameof(UnifiedRequest.CollectionProperty),
        ["partName"] = nameof(UnifiedRequest.PartName),
        ["partIndex"] = nameof(UnifiedRequest.PartIndex),
        ["partKind"] = nameof(UnifiedRequest.PartKind),
        ["confirmDelete"] = nameof(UnifiedRequest.ConfirmDelete),
        ["propertyName"] = nameof(UnifiedRequest.PropertyName),
        ["dynamizationKind"] = nameof(UnifiedRequest.DynamizationKind),
        ["mappingEntries"] = nameof(UnifiedRequest.MappingEntriesJson),
        ["listName"] = nameof(UnifiedRequest.ListName),
        ["entryKey"] = nameof(UnifiedRequest.EntryKey),
        ["entry"] = nameof(UnifiedRequest.EntryJson),
        ["filePath"] = nameof(UnifiedRequest.FilePath),
        ["expectedNames"] = nameof(UnifiedRequest.ExpectedNamesJson),
        ["eventType"] = nameof(UnifiedRequest.EventType),
        ["scriptProperties"] = nameof(UnifiedRequest.ScriptPropertiesJson),
        ["directory"] = nameof(UnifiedRequest.Directory),
        ["tagTable"] = nameof(UnifiedRequest.TagTable),
        ["fileName"] = nameof(UnifiedRequest.FileName),
        ["expectedTagNames"] = nameof(UnifiedRequest.ExpectedTagNamesJson),
        ["moduleName"] = nameof(UnifiedRequest.ModuleName),
        ["connectionName"] = nameof(UnifiedRequest.ConnectionName),
        ["xmlPath"] = nameof(UnifiedRequest.XmlPath),
        ["displayName"] = nameof(UnifiedRequest.DisplayName),
        ["expectedProject"] = nameof(UnifiedRequest.ExpectedProject),
        ["scriptCode"] = nameof(UnifiedRequest.ScriptCode),
        ["screenPath"] = nameof(UnifiedRequest.ScreenPath),
        ["itemNames"] = nameof(UnifiedRequest.ItemNamesJson),
        ["cursor"] = nameof(UnifiedRequest.Cursor),
        ["budgetMs"] = nameof(UnifiedRequest.BudgetMs),
        ["beforePages"] = nameof(UnifiedRequest.BeforePagesJson),
        ["afterPages"] = nameof(UnifiedRequest.AfterPagesJson),
        ["fields"] = nameof(UnifiedRequest.FieldsJson),
        ["changes"] = nameof(UnifiedRequest.ChangesJson),
        ["itemName"] = nameof(UnifiedRequest.ItemName),
        ["branch"] = nameof(UnifiedRequest.BranchJson),
        ["typePath"] = nameof(UnifiedRequest.TypePath),
        ["version"] = nameof(UnifiedRequest.Version),
        ["folderPath"] = nameof(UnifiedRequest.FolderPath),
        ["previewToken"] = nameof(UnifiedRequest.ExpectedToken)
    };

    internal static UnifiedRequest BindUnifiedRequest(string operation, System.Reflection.MethodInfo method,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var request = new UnifiedRequest { Operation = operation };
        bool paged = operation is "GetUnifiedGraphicSelection" or "ListUnifiedGlobalScripts" or "GetUnifiedGlobalScript"
            or "ListUnifiedTagDefinitions" or "GetUnifiedScreenBranch" or "GetUnifiedLibraryType"
            or "ListUnifiedLibraryFolderEntries" or "GetUnifiedFaceplateInstance";
        foreach (var parameter in method.GetParameters())
        {
            var name = parameter.Name!;
            var propertyName = name == "limit" && paged ? nameof(UnifiedRequest.PageSize)
                : UnifiedParameters.TryGetValue(name, out var mapped) ? mapped
                : throw new InvalidOperationException("Unified parameter has no request mapping: " + operation + "." + name);
            var property = typeof(UnifiedRequest).GetProperty(propertyName)!;
            if (arguments.TryGetValue(name, out var value))
                property.SetValue(request, property.PropertyType == typeof(string) && value.ValueKind != JsonValueKind.String
                    ? value.GetRawText() : JsonSerializer.Deserialize(value.GetRawText(), property.PropertyType));
            else if (parameter.DefaultValue != null && property.PropertyType.IsInstanceOfType(parameter.DefaultValue))
                property.SetValue(request, parameter.DefaultValue);
        }
        return request;
    }

    internal CallToolResult? RefusePortedCapability(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        using var release = HardwareContract.UseRelease(portedRelease!);
        var family = PortedFamilies.ForTool(tool.Name);
        if (!PortedFamilies.Available(portedRelease!, tool.Name) || arguments.TryGetValue("action", out var action)
            && !family.SupportsAction(portedRelease!, action.GetString()!))
            return HardwareContract.Run(tool.Name, () => throw new NotSupportedException("Tool is unavailable in this release."), false, true);
        string actionName = arguments.TryGetValue("action", out var requestedAction) ? requestedAction.GetString() ?? "" : "";
        string scope = arguments.TryGetValue("family", out var requestedFamily) ? requestedFamily.GetString() ?? "" : arguments.TryGetValue("kind", out var requestedKind) ? requestedKind.GetString() ?? "" : "";
        if (HardwareCapabilities.Unsupported(portedRelease!, tool.Name, actionName, scope,
            arguments.TryGetValue("positionNumber", out var position) ? position.GetInt32() : -1,
            arguments.TryGetValue("extendedPositionNumber", out var extendedPosition) ? extendedPosition.GetInt32() : -1,
            arguments.TryGetValue("password", out var password) && !string.IsNullOrEmpty(password.GetString())) != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        if (PlcSoftwareCapabilities.Unsupported(portedRelease!, tool.Name, actionName, scope,
            arguments.TryGetValue("unitName", out var unit) ? unit.GetString() ?? "" : "",
            arguments.TryGetValue("unitKind", out var unitKind) ? unitKind.GetString() ?? "unit" : "unit",
            arguments.TryGetValue("targetKind", out var targetKind) ? targetKind.GetString() ?? "" : "",
            arguments.TryGetValue("copyMode", out var copyMode) ? copyMode.GetString() ?? "" : "",
            arguments.TryGetValue("generateOption", out var generateOption) ? generateOption.GetString() ?? "None" : "None") != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        var b4 = new PlcSoftwareRequest { Operation = tool.Name, Action = actionName };
        string Text(string name) => arguments.TryGetValue(name, out var value) ? value.GetString() ?? "" : "";
        if (family.Name == "F26")
        {
            if (UnifiedHmiCapabilities.Unsupported(portedRelease!, tool.Name, actionName, Text("itemType"), Text("dynamizationType")) != null)
                return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
            foreach (var name in new[] { "design", "layout" })
                if (arguments.TryGetValue(name, out var design) && design.ValueKind == JsonValueKind.Object
                    && design.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var type)
                            && type.ValueKind == JsonValueKind.String
                            && UnifiedHmiCapabilities.Unsupported(portedRelease!, tool.Name, actionName, type.GetString()!) != null)
                            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        }
        if (family.Name == "F22" && OnlineCapabilities.Unsupported(portedRelease!, new OnlineRequest {
            Operation = tool.Name, Action = actionName, RhTarget = Text("rhTarget"), Password = Text("password"),
            UserName = Text("userName"), UserType = Text("userType"), IpAddress = Text("ipAddress"),
            TargetIpAddress = Text("targetIpAddress"), ObjectKind = Text("objectKind") }) != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        if (family.Name is "F27" or "F28")
        {
            var unified = BindUnifiedRequest(tool.Name, portedMethod!, arguments);
            UnifiedPortService.DecodeInputs(unified);
            if (UnifiedCapabilities.Unsupported(portedRelease!, unified) != null)
                return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
            return null;
        }
        if (family.Name == "F07" && SecurityCapabilities.Unsupported(portedRelease!, tool.Name, actionName,
            tool.Name == "ManageSyslogServers" ? (Text("scope").Length == 0 ? "project" : Text("scope")) : scope,
            Text("category"), Text("filePath")) != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        b4.Aspect = Text(tool.Name == "ConfigureMotionHardwareConnection" ? "interfaceKind" : "aspect");
        b4.Path = Text("objectPath");
        b4.GroupPath = Text(tool.Name.StartsWith("ImportTechnologyObject", StringComparison.Ordinal) ? "folderPath" : "groupPath");
        if (tool.Name == "ManagePlcSoftwareUnit" && arguments.TryGetValue("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("NamespacePreset", out _))
            b4.Properties["NamespacePreset"] = new HardwareScalar();
        if ((family.Name is "F10" or "F14" or "F15") && PlcB5Capabilities.Unsupported(portedRelease!, new PlcB5Request {
            Operation = tool.Name, Action = actionName, UnitName = Text("unitName"), UnitKind = Text("unitKind"), InterfaceType = Text("interfaceType"), ObjectKind = Text("objectKind").Length == 0 ? "document" : Text("objectKind"), CopyMode = Text("copyMode") }) != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        if (HmiCapabilities.Unsupported(portedRelease!, tool.Name) != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        if (PlcB4Capabilities.Unsupported(portedRelease!, b4) != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        if (OptionalProductCapabilities.Unsupported(portedRelease!, tool.Name,
            arguments.TryGetValue("category", out var optionalCategory) ? optionalCategory.GetString() ?? "" : "",
            arguments.TryGetValue("kind", out var optionalKind) ? optionalKind.GetString() ?? "" : "", actionName,
            arguments.TryGetValue("executionMode", out var executionMode) ? executionMode.GetString() ?? "" : "",
            arguments.TryGetValue("password", out var optionalPassword) ? optionalPassword.GetString() ?? "" : "") != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        if (EngineeringServicesCapabilities.Unsupported(portedRelease!, tool.Name, scope,
            arguments.TryGetValue("customIdentityKey", out var customIdentity) ? customIdentity.GetString() ?? "" : "",
            arguments.TryGetValue("properties", out var engineeringProperties) && engineeringProperties.ValueKind == JsonValueKind.Object ? engineeringProperties.EnumerateObject().Select(p => p.Name) : null) != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        if (LibraryCapabilities.Unsupported(portedRelease!, tool.Name, actionName,
            arguments.TryGetValue("sourceKind", out var sourceKind) ? sourceKind.GetString() ?? "" : "",
            arguments.TryGetValue("properties", out var libraryProperties) && libraryProperties.ValueKind == JsonValueKind.Object ? libraryProperties.EnumerateObject().Select(p => p.Name) : null,
            arguments.TryGetValue("forceUpdateMode", out var forceMode) ? forceMode.GetString() ?? "" : "SetOnlyHigherUpdatedVersionAsDefault",
            arguments.TryGetValue("deleteUnusedVersionsMode", out var deleteMode) ? deleteMode.GetString() ?? "" : "DoNotDelete",
            arguments.TryGetValue("structureConflictResolutionMode", out var structureMode) ? structureMode.GetString() ?? "" : "RetainStructure") != null)
            return HardwareToolContract.Unsupported(tool.Name, portedRelease!, actionName);
        return null;
    }

    private async ValueTask<CallToolResult> InvokePortedAsync(RequestContext<CallToolRequestParams> request, CancellationToken token)
    {
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        using var release = HardwareContract.UseRelease(portedRelease!);
        if (RefusePortedCapability(arguments) is { } refusal) return refusal;
        // ReadState is managed lifecycle data, never an Openness lookup. Keep it in
        // the same lane as the eventual request so approval cannot race a rebind.
        using var lane = await AcquireLane(token);
        ActivateLane(lane);
        var state = PortedToolContract.WiredOperations(tool.Name).Contains("ReadState", StringComparer.Ordinal)
            ? await worker.Call("ReadState", new JsonObject(), token) : null;
        Func<bool> hasProject = () => !string.IsNullOrEmpty((string?)state?["ProjectFile"]);
        Func<string> identity = () => (string?)state?["ProjectFile"] ?? "";
        bool missingPortal = state != null && state["ProcessId"] == null && (bool?)state["IsAttached"] != true && !hasProject();
        bool portalDispatchRefused = false;
        JsonNode? Call(string operation, JsonObject input)
        {
            if (missingPortal && TiaMcp.PlcWorker.WorkerOperations.RequiresPortal(operation))
            {
                portalDispatchRefused = true;
                throw new InvalidOperationException(TiaOpenness.Shared.SessionBehavior.PortalRequired);
            }
            try { return worker.Call(operation, input, token).GetAwaiter().GetResult(); }
            catch (WorkerOperationException error) when (PortedToolContract.IsDeviceRefusal(operation, error))
            { throw PortedToolContract.DeviceRefusal(error); }
        }
        Func<string, JsonObject, JsonNode?> call = Call;
        var service = new HardwareAddressingService(call, hasProject, identity);
        object target = portedMethod!.DeclaringType!.Name switch
        {
            "SafetyValidationTools" => new SafetyValidationTools(new SafetyValidationService(call, identity)),
            "SivarcTools" => new SivarcTools(new SivarcService(call, identity)),
            "OptionalEngineeringTools" => new OptionalEngineeringTools(new OptionalEngineeringService(call, identity)),
            "StartdriveTools" => new StartdriveTools(new StartdriveService(call, identity)),
            "DccTools" => new DccTools(new DccService(call, identity)),
            "SafetyManagementTools" => new SafetyManagementTools(new SafetyManagementService(call, identity)),
            "DocumentsTools" => new DocumentsTools(new DocumentsService(call, identity)),
            "AlarmsTools" => new AlarmsTools(new AlarmsService(call, identity)),
            "OpcUaTools" => new OpcUaTools(new OpcUaService(call, identity)),
            "EngineeringAuditTools" => new EngineeringAuditTools(new EngineeringAuditService(call, identity)),
            "SoftwareUnitDeepTools" => new SoftwareUnitDeepTools(new SoftwareUnitDeepService(call, identity)),
            "SpecializedExchangeTools" => new SpecializedExchangeTools(new SpecializedExchangeService(call, identity)),
            "PlcProDiagTools" => new PlcProDiagTools(new PlcB5PortService(call, identity)),
            "PlcTechnologyTools" => new PlcTechnologyTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "PlcMotionTools" => new PlcMotionTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "PlcWatchTools" => new PlcWatchTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "PlcCausalTraceTools" => new PlcCausalTraceTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "PlcIntegrityTools" => new PlcIntegrityTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "LibraryTools" => new LibraryTools(new LibraryPortService(call, identity)),
            "ProjectUserManagementTools" => new ProjectUserManagementTools(new ProjectUserManagementPortService(call, hasProject, identity)),
            "CertificateManagementTools" => new CertificateManagementTools(new CertificateManagementPortService(call, hasProject, identity)),
            "HardwareServicesTools" => new HardwareServicesTools(new HardwareSecurityPortService(call, hasProject, identity)),
            "HardwareSecurityTools" => new HardwareSecurityTools(new DeviceUserPortService(call, hasProject, identity)),
            "SecurityDeepTools" => new SecurityDeepTools(new SecurityDeepPortService(call, hasProject, identity)),
            "ProjectSecurityTools" => new ProjectSecurityTools(new ProjectLifecyclePortService(call, hasProject, identity)),
            "ProjectLifecycleSessionTools" => new ProjectLifecycleSessionTools(new ProjectLifecyclePortService(call, hasProject, identity)),
            "ProjectLifecycleProjectTools" => new ProjectLifecycleProjectTools(new ProjectLifecyclePortService(call, hasProject, identity)),
            "ProjectLifecycleInspectionTools" => new ProjectLifecycleInspectionTools(new ProjectLifecyclePortService(call, hasProject, identity)),
            "ProjectLifecycleDeviceTools" => new ProjectLifecycleDeviceTools(new ProjectLifecyclePortService(call, hasProject, identity)),
            "ProjectLifecycleExchangeTools" => new ProjectLifecycleExchangeTools(new ProjectLifecyclePortService(call, hasProject, identity)),
            "ProjectProtectionTools" => new ProjectProtectionTools(new ProjectProtectionPortService(call, hasProject)),
            "PlcSimAdvancedTools" => new PlcSimAdvancedTools(new PlcSimAdvancedPortService(call)),
            "OnlineDownloadTools" => new OnlineDownloadTools(new OnlineDownloadService(call, identity)),
            "NativeExchangeTools" => new NativeExchangeTools(new OnlineDownloadService(call, identity)),
            "OnlineDataTools" => new OnlineDataTools(new OnlineDownloadService(call, identity)),
            "VersionControlTools" => new VersionControlTools(new VersionControlPortService(call, identity)),
            "TeamcenterTools" => new TeamcenterTools(new TeamcenterPortService(call, identity)),
            "GitWorkflowTools" => new GitWorkflowTools(),
            "CfcTools" => new CfcTools(new OptionalPorts.CfcPortService(call, identity)),
            "TestSuiteTools" => new TestSuiteTools(new OptionalPorts.TestSuitePortService(call, identity)),
            "V20OptionsTools" => new V20OptionsTools(new OptionalPorts.OptionalProductsPortService(call, identity)),
            "EngineeringServicesTools" => new EngineeringServicesTools(new EngineeringServicesPortService(call, identity)),
            "HmiDescribeTools" => new HmiDescribeTools(new HmiDescribeService(call, identity)),
            "UnifiedObjectServicesTools" => new UnifiedObjectServicesTools(new UnifiedObjectServicesService(call, identity)),
            "UnifiedUiModelTools" => new UnifiedUiModelTools(new UnifiedUiModelService(call, identity)),
            "UnifiedEngineeringTools" => new UnifiedEngineeringTools(new UnifiedEngineeringService(call, identity)),
            "UnifiedEventsTools" => new UnifiedEventsTools(new UnifiedEventsService(call, identity)),
            "UnifiedExchangeTools" => new UnifiedExchangeTools(new UnifiedExchangeService(call, identity)),
            "GlobalScriptEditTools" => new GlobalScriptEditTools(new GlobalScriptEditService(call, identity)),
            "GraphicSelectionTools" => new GraphicSelectionTools(new GraphicSelectionService(call, identity)),
            "RuntimeSettingsTools" => new RuntimeSettingsTools(new RuntimeSettingsService(call, identity)),
            "MigrationReadTools" => new MigrationReadTools(new MigrationReadService(call, identity)),
            "HmiExchangeTools" => new HmiExchangeTools(new HmiExchangeService(call, identity)),
            "HmiInspectionPortTools" => new HmiInspectionPortTools(new HmiInspectionPortService(call, identity)),
            "UnifiedHmiTools" => new UnifiedHmiTools(new UnifiedHmiService(call, identity)),
            "UnifiedHmiGroupsTools" => new UnifiedHmiGroupsTools(new UnifiedHmiGroupsService(call, identity)),
            "UnifiedScreenItemsTools" => new UnifiedScreenItemsTools(new UnifiedScreenItemsService(call, identity)),
            "HmiInspectionTools" => new HmiInspectionTools(new HmiInspectionService(call, identity)),
            "HmiTagDeletionTools" => new HmiTagDeletionTools(new HmiTagDeletionService(call, identity)),
            "ClassicHmiFoldersTools" => new ClassicHmiFoldersTools(new ClassicHmiFoldersService(call, identity)),
            "MotionProDiagClassicHmiTools" => new MotionProDiagClassicHmiTools(new MotionProDiagClassicHmiService(call, identity)),
            "ReflectionTools" => new ReflectionTools(new ReflectionPortService(call, identity)),
            "PlcCompilePortTools" => new PlcCompilePortTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "PlcBuildTools" => new PlcBuildTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "PlcReadModifyTools" => new PlcReadModifyTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "PlcOrganisationTools" => new PlcOrganisationTools(new PlcOrganisationPortService(call, hasProject, identity)),
            "HardwareDevicesTools" => new HardwareDevicesTools(new HardwareDevicesService(call, hasProject, identity,
                DeviceCandidates.GetValue(worker, _ => new DeviceCreationSession()))),
            "ModulesTools" => new ModulesTools(new HardwareModulesService(call, hasProject, identity)),
            "HardwareManagementTools" => new HardwareManagementTools(new HardwareManagementService(call, hasProject, identity)),
            "HardwareNetworkTools" => new HardwareNetworkTools(new HardwareNetworkPortService(call, hasProject, identity)),
            "HardwareServicesPortTools" => new HardwareServicesPortTools(new HardwareServicesPortService(call, hasProject, identity)),
            "HardwareAmlTools" => new HardwareAmlTools(new HardwareAmlService(call, hasProject, identity)),
            _ => new AddressesTools(service)
        };
        var values = portedMethod!.GetParameters().Select(p => arguments.TryGetValue(p.Name!, out var value)
            ? JsonSerializer.Deserialize(value.GetRawText(), p.ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false }) : p.DefaultValue).ToArray();
        try
        {
            using var nativeCalls = InvocationJournal.BeginNativeCallScope();
            var invocation = portedMethod.Invoke(target, values)!;
            var result = invocation is Task<CallToolResult> pending ? pending.GetAwaiter().GetResult() : (CallToolResult)invocation;
            // Preserve typed refusals and offline previews. A missing session can
            // make legacy write wrappers report unknown even though no operation
            // started; use the retired engine's no-native-call projection for all ports.
            if (missingPortal && !nativeCalls.NativeCallIssued
                && PortedToolContract.WiredOperations(tool.Name).Any(TiaMcp.PlcWorker.WorkerOperations.RequiresPortal)
                && (portalDispatchRefused || (string?)result.StructuredContent?["meta"]?["outcome"] == "unknown"))
                return McpServer.V4Result(tool.Name, result.StructuredContent?["data"] is JsonObject data ? (JsonObject)data.DeepClone() : null,
                    new Error(TiaOpenness.Shared.SessionBehavior.PortalRequired, new PreconditionFailedDetails("ConnectPortal", null)),
                    Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, current: true);
            return result;
        }
        catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
    }
}
