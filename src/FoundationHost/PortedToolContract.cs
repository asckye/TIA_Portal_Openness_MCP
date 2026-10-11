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
#else
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
#endif

namespace TiaMcp.FoundationHost;

// Declarations, availability and the existing Foundation session lane are shared
// by every port. Only the typed service factory is specific to a family.
internal static class PortedToolContract
{
    // V20/V21 B1 declarations belong to the full EngineHost catalog. The
    // bounded plc-foundation profile includes F18/F19/F20/F21 hardware ports.
    internal static IEnumerable<PortedFamilies.Family> Families(string release) =>
        PortedFamilies.All.Where(family => (family.Name == "F05" || family.Name == "F07" || family.Name == "F33" || family.Name == "F24" || family.Name == "F25" || family.Name == "F10" || family.Name == "F14" || family.Name == "F15" || family.Name == "F23" || family.Name == "F06" || family.Name == "F35" || family.Name == "F08" || family.Name == "F09" || family.Name == "F11" || family.Name == "F12" || family.Name == "F13" || family.Name == "F16" || family.Name == "F17" || family.Name == "F18" || family.Name == "F19" || family.Name == "F20" || family.Name == "F21") && family.Available(release));
    internal static McpServerTool Wrap(FoundationTool tool)
    {
        string? candidate = tool.ProtocolTool.Name switch {
            "CompileDevice" or "CompileHmiDiagnostics" => "P6-COMPILE",
            "ImportPlcTagTablesFromDirectory" => "P6-IMPORT",
            "ManagePlcExternalSources" => "P6-SOURCE", _ => null };
        if (candidate != null && BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, tool.Release, candidate) == BehaviorPolicy.SafeV4)
            return new FoundationV4Tool(tool, tool.Release, null);
        return tool.CapabilityAdmission(McpServer.WrapTools(new[] { tool.ValidatedTool() }).Single());
    }

    // The primary module operation comes first; previews and fallback reads are
    // also part of the wiring. Pure builders do not read session state.
    internal static string[] WiredOperations(string tool) => tool switch
    {
        "ArchiveSavedProject" => new[] { "project-lifecycle.ArchiveSavedProject", "ReadState" },
        "BuildProjectScaffold" => new[] { "project-lifecycle.ExecuteProjectScaffoldPrimitive", "ReadState" },
        "ConnectIsolatedPortal" => new[] { "project-lifecycle.ConnectIsolatedPortal", "ReadState" },
        "EnsureOpennessUserGroup" => new[] { "project-lifecycle.EnsureOpennessUserGroup", "ReadState" },
        "GetPortalInfo" => new[] { "project-lifecycle.ReadPortalInfo", "ReadState" },
        "ManageMultiuserSession" => new[] { "project-lifecycle.ExecuteMultiuserSession", "ReadState" },
        "RetrieveProjectArchive" => new[] { "project-lifecycle.RetrieveProjectArchive", "ReadState" },
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
        "GetClassicHmiScreenTree" or "ManageClassicHmiScreenObject" or "ManageClassicHmiFolder" or "ManageClassicHmiGraphic" or "ListClassicHmiScripts" or "ManageClassicHmiScript" or "ManageClassicHmiCycle" or "ManageClassicHmiTextGraphicList" or "GetClassicHmiGlobalization" or "ListClassicHmiFaceplates" => new[] { "hmi-classic.ExecuteHmi", "ReadState" },
        "DescribeObject" or "DescribeObjectProperty" or "DescribeService" or "GetObjectProperty" or "ListObjectChildren" or "InvokeObject" or "InvokeService" => new[] { "openness-reflection.ExecuteOpennessReflection", "ReadState" },
        "CreatePlcBlockGroup" or "CreatePlcTypeGroup" or "DeleteEmptyPlcBlockGroup" or "DeletePlcBlock" or "DeletePlcTagTable" or "DeletePlcType" or "ManagePlcBlockProtection" or "ManagePlcUserGroup" or "MovePlcBlockToGroup" or "ListPlcSystemGroups" => new[] { "plc-organisation.ExecutePlcOrganisation", "ReadState" },
        "CreatePlcInstanceDb" or "ManagePlcTagDefinition" or "SetPlcProgram" or "GetPlcTagTableConstants" or "ImportPlcBlockVerified" or "BuildAndImportPlcArtifact" or "DescribePlcBlockLogic" or "RepairAndReimportPlcBlock" or "ImportPlcTagTablesFromDirectory" => new[] { "plc-software.ExecutePlcOrganisation", "ReadState" },
        "SeedProjectFromReference" => new[] { "plc-software.ExecutePlcOrganisation", "plc-software.SeedReferenceHmi", "ReadState" },
        "ListPlcSoftwareUnits" or "ManagePlcSoftwareUnit" or "SetPlcUnitObjectAccess" or "GetPlcChecksums" or "GetPlcObjectFingerprints" or "ManagePlcBlockWriteProtection" => new[] { "plc-integrity.ExecutePlcOrganisation", "ReadState" },
        "CompileDevice" or "CompileHmiDiagnostics" => new[] { "plc-compile.ExecutePlcOrganisation", "ReadState" },
        "GetPlcBlockEditCapabilities" => new[] { "plc-analysis.ExportBlockDocument", "ReadState" },
        "GeneratePlcSourceFromBlocks" or "ManagePlcExternalSources" or "GetPlcCrossReferences" => new[] { "plc-sources.ExecutePlcOrganisation", "ReadState" },
        _ when PortedFamilies.Contains(tool) && PortedFamilies.ForTool(tool).Name is "F01" or "F02" or "F03" => Array.Empty<string>(),
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
        portedDescriptor = descriptor; tool = declaration.ProtocolTool;
        definition = new Definition(name, PortedFamilies.ForTool(name).OperationPrefix, descriptor.RawDescription,
            descriptor.Parameters.Select(p => new Argument(p.Name, p.FriendlyType, p.Required)).ToArray(), PortedFamilies.ForTool(name).Name);
    }

    internal McpServerTool ValidatedTool() => new InfrastructureInputTool(this, portedDescriptor!);
    internal McpServerTool CapabilityAdmission(McpServerTool inner) => new CapabilityTool(this, inner);

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
            if (McpServer.ToolInvoker.ValidateArguments(owner.portedDescriptor!, input, ProtocolTool.InputSchema) == null
                && owner.RefusePortedCapability(arguments) is { } refusal)
            {
                McpServer.RecordAdmissionProjection(request, refusal);
                return new ValueTask<CallToolResult>(refusal);
            }
            return inner.InvokeAsync(request, cancellationToken);
        }
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
        var state = (tool.Name == "ManagePlcGitRepository" || tool.Name == "BuildDeviceAmlDocument" || tool.Name == "PlanHardwareNetworkConfiguration" || tool.Name == "PlanOnlineReadOnlyMonitoring" || tool.Name == "PlanOnlineReadOnlyDataProvider") ? null : await worker.Call("ReadState", new JsonObject(), token);
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
            return worker.Call(operation, input, token).GetAwaiter().GetResult();
        }
        Func<string, JsonObject, JsonNode?> call = Call;
        var service = new HardwareAddressingService(call, hasProject, identity);
        object target = portedMethod!.DeclaringType!.Name switch
        {
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
            "VersionControlTools" => new VersionControlTools(new VersionControlPortService(call, identity)),
            "TeamcenterTools" => new TeamcenterTools(new TeamcenterPortService(call, identity)),
            "GitWorkflowTools" => new GitWorkflowTools(),
            "EngineeringServicesTools" => new EngineeringServicesTools(new EngineeringServicesPortService(call, identity)),
            "HmiDescribeTools" => new HmiDescribeTools(new HmiDescribeService(call, identity)),
            "HmiExchangeTools" => new HmiExchangeTools(new HmiExchangeService(call, identity)),
            "HmiInspectionPortTools" => new HmiInspectionPortTools(new HmiInspectionPortService(call, identity)),
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
