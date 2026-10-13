using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcWorker
{
    internal static class WorkerOperations
    {
    internal static bool IsIndependentRuntime(string operation) => operation == "runtime-channels.ExecutePlcSimAdvanced";
        internal static string[] FamilyTools(TiaMcp.Adapters.Contracts.PortedFamilies.Family family)
            => TiaMcp.Adapters.Contracts.PortedFamilies.WorkerTools(family);
        internal static string[] FamilyNames(string family) => family == "F04" ? new[] { "ExecutePlcSimAdvanced" } : family == "F22" ? new[] { "ExecuteOnline" } : family == "F05" ? new[] { "ReadPortalInfo", "ConnectIsolatedPortal", "EnsureOpennessUserGroup", "ReadProjectLifecycleBinding", "ArchiveSaveProjectCopy", "ArchiveSavedProject", "RetrieveProjectArchive", "ExecuteMultiuserSession", "ValidateAutomationContext", "ExecuteProjectScaffoldPrimitive" } : family == "F07" ? new[] { "ReadProjectProtection", "ExecuteProjectUsers", "ExecuteCertificate", "ExecuteSecurityDeep", "ExecuteDeviceUsers", "ExecuteHardwareSecurity" } : family == "F33" ? new[] { "ExecuteVersionControl", "ExecuteTeamcenter" } : family == "F26" ? new[] { "ExecuteUnifiedHmi", "ExecuteUnifiedHmiMetadata" } : (family == "F24" || family == "F25") ? new[] { "ExecuteHmi" } : (family == "F10" || family == "F14" || family == "F15") ? new[] { "ExecutePlcB5" } : family == "F23" ? new[] { "ExecuteLibrary" } : family == "F06" ? new[] { "ExecuteEngineeringServices", "BeginEngineeringTransaction", "ReadEngineeringTransaction", "CommitEngineeringTransaction", "DisposeEngineeringTransaction" } : family == "F35" ? new[] { "ExecuteOpennessReflection" } : (family == "F08" || family == "F09" || family == "F11" || family == "F12" || family == "F13" || family == "F16" || family == "F17") ? family == "F09" ? new[] { "ExecutePlcOrganisation", "SeedReferenceHmi" } : new[] { "ExecutePlcOrganisation" } : family == "F19" ? new[] {
            "ReadHardwareIoAddresses", "DescribeHardwareIoChildren", "SetHardwareIoAddress",
            "ReadHardwareAddressing", "UpdateHardwareAddress", "ReadHardwareIpAddress"
        } : family == "F18" ? new[] { "HardwareCreateDevice", "HardwareCreateCatalogDevice", "HardwareCreateGsdDevice", "HardwareCreateDeviceFallback", "HardwareLegacySearchInstalledGsdDevices", "HardwareLegacySearchHardwareCatalog", "HardwareDescribeDevices", "HardwareDescribeDeviceAt", "HardwareDescribeItemAt", "HardwareReadItemTree", "HardwareReadPlcNames", "HardwareLegacySetDeviceItemAttribute", "HardwareLegacySetCpuCommonSettings", "HardwareLegacyDumpDeviceAttributes", "HardwareReadPlugLocations", "HardwarePlugModule", "HardwareManageHardwareObject", "HardwareDeviceCandidate", "HardwareReadCommunicationConnections", "HardwareManageCommunicationConnection", "HardwareExchangeSystemDiagnosticsSettings", "HardwareReadHardwareFeatures", "HardwareManageDeviceServiceObjects", "HardwareManageHardwareUtilities", "HardwareManageDeviceUserGroup" } : family == "F20" ? new[] { "HardwareReadIoSystems", "HardwareManageIoSystem", "HardwareReadNetworkDomains", "HardwareManageNetworkDomain", "HardwareReadTransferAreas", "HardwareManageTransferArea", "HardwareReadDeviceItemChannels", "HardwareUpdateDeviceItemChannel", "HardwareManagePortInterconnection", "HardwareGetProjectTopology", "HardwareEnsureSubnet", "HardwareAttachDeviceNodeToSubnet", "ReadHardwareOwnerCandidates", "ReadHardwareWhitelistedServices", "HardwareGetDeviceItemNetworkInfo", "HardwareProbeConnectDeviceNodesToSubnet", "HardwareReadCommunicationConnections", "HardwareManageCommunicationConnection" } : family == "F21" ? new[] { "ExportHardwareAml", "ImportHardwareAml" } : Array.Empty<string>();
        internal static bool IsFamilyOperation(string name)
        {
            foreach (var family in TiaMcp.Adapters.Contracts.PortedFamilies.All)
                foreach (var operation in FamilyNames(family.Name))
                    if (name == family.OperationPrefix + "." + operation) return true;
            return false;
        }
        internal static Action<string> MutationIdentity(string name, Action<string> nativeIdentity, Action<string>? sharedFamilyIdentity)
            => sharedFamilyIdentity != null && IsFamilyOperation(name) ? sharedFamilyIdentity : nativeIdentity;
        private static bool IsFamilyRead(string name) => name == "project-security.ReadProjectProtection" || name == "hardware-addressing.ReadHardwareIoAddresses"
            || name == "hardware-addressing.DescribeHardwareIoChildren" || name == "hardware-addressing.ReadHardwareAddressing"
            || name == "hardware-addressing.ReadHardwareIpAddress" || name == "hardware-aml.ExportHardwareAml"
            || new[] { "hardware-devices.HardwareLegacySearchInstalledGsdDevices", "hardware-devices.HardwareLegacySearchHardwareCatalog", "hardware-devices.HardwareDescribeDevices", "hardware-devices.HardwareDescribeDeviceAt", "hardware-devices.HardwareDescribeItemAt", "hardware-devices.HardwareReadItemTree", "hardware-devices.HardwareReadPlcNames", "hardware-devices.HardwareLegacyDumpDeviceAttributes", "hardware-devices.HardwareReadPlugLocations", "hardware-devices.HardwareReadHardwareFeatures", "hardware-network.HardwareReadIoSystems", "hardware-network.HardwareReadNetworkDomains", "hardware-network.HardwareReadTransferAreas", "hardware-network.HardwareReadDeviceItemChannels", "hardware-network.HardwareGetProjectTopology", "hardware-network.ReadHardwareOwnerCandidates", "hardware-network.ReadHardwareWhitelistedServices", "hardware-network.HardwareGetDeviceItemNetworkInfo", "hardware-network.HardwareReadCommunicationConnections" }.Contains(name, StringComparer.Ordinal);
        internal const string DeviceCreationCandidate = "CreateHardwareDeviceCandidate";
        internal const string CompileCandidate = "CompileCandidate";
        internal static bool IsCompilePreview(string name, string? mode) => name == CompileCandidate && (mode == null || mode == "preview");
        internal const string SourceCandidate = "SourceCandidate";
        internal static bool IsSourcePreview(string name, string? mode) => name == SourceCandidate && (mode == null || mode == "preview");
        internal const string SaveCloseCandidate = "SaveCloseCandidate";
        internal static bool IsSaveClosePreview(string name, string? mode) => name == SaveCloseCandidate && (mode == null || mode == "preview");
        internal const string SessionCandidate = "SessionCandidate";
        internal static bool IsSessionPreview(string name, string? mode) => name == SessionCandidate && (mode == null || mode == "preview");
        internal const string PlcExportCandidate = "ExportPlcCandidate";
        internal const string PlcImportCandidate = "ImportPlcCandidate";
        internal static bool IsExportPreview(string name, string? mode) => name == PlcExportCandidate && (mode == null || mode == "preview");
        internal static bool IsImportPreview(string name, string? mode) => name == PlcImportCandidate && (mode == null || mode == "preview");
        internal static bool IsDevicePreview(string name, string? mode) => name == DeviceCreationCandidate && (mode == null || mode == "preview");
        internal static bool RequiresPortal(string name) => name != "hmi-unified.ExecuteUnifiedHmiMetadata" && name != "runtime-channels.ExecutePlcSimAdvanced" && name != "project-lifecycle.ReadPortalInfo" && name != "project-lifecycle.ValidateAutomationContext" && name != "project-lifecycle.ConnectIsolatedPortal" && name != "project-lifecycle.EnsureOpennessUserGroup" && name != "project-lifecycle.ExecuteProjectScaffoldPrimitive" && name != "Attach" && name != "Disconnect" && name != "engineering-services.DisposeEngineeringTransaction"
            && name != "ReadState" && name != "ReadPortalProcessProjects" && name != "ReadPortalConnectReadiness"
            && name != SessionCandidate && name != "hardware-devices.HardwareLegacySearchInstalledGsdDevices";
        internal static bool IsReadOnly(string name) => name == "hmi-unified.ExecuteUnifiedHmiMetadata" || name == "project-lifecycle.ReadPortalInfo" || name == "project-lifecycle.ValidateAutomationContext" || name == "project-lifecycle.ReadProjectLifecycleBinding" || name == "engineering-services.ReadEngineeringTransaction" || name == "plc-analysis.ExportBlockDocument" || IsFamilyRead(name) || name=="SearchHardwareCatalog" || name=="PlanPlcExternalSourceImport" || name=="ReadState" || name=="ReadPortalProcessProjects" || name=="ReadPortalConnectReadiness" || name=="ReadWatchTableNames" || name=="ReadTechnologyObjects" || name=="ReadSoftwareInfo" || name=="ReadSoftwareTree" || name=="ReadExternalSourceNames" || name=="ListTags" || name=="ListUserConstants" || name=="ListSystemConstants" || name=="ReadBlockInfo" || name=="ReadTypeInfo" || name=="ReadBlocks" || name=="ReadTypes" || name=="ReadTagTableNames" || name=="ReadBlockHierarchy" || name=="ReadProjectTree" || name=="ListProjects";

        internal static readonly HashSet<string> Names=new HashSet<string>(StringComparer.Ordinal) {
            "SearchHardwareCatalog",
            "ReadState","ReadPortalProcessProjects","ReadPortalConnectReadiness",
            "ReadWatchTableNames","ReadTechnologyObjects",
            "PlanPlcExternalSourceImport","DeletePlcExternalSource","AddDeviceWithFallback",
            "ImportPlcExternalSource","GenerateBlocksFromExternalSource",
            "ReadSoftwareInfo","ReadSoftwareTree",
            "ReadBlockInfo","ReadTypeInfo","ReadExternalSourceNames",
            "Attach","Disconnect","ListProjects","BindProject","OpenProject","CreateProject","SaveProject","CloseProject","ReadProjectTree",
            "ReadBlocks","ReadBlockHierarchy","ReadTypes","ReadTagTableNames","ListTags","ListUserConstants","ListSystemConstants",
            "ImportFromDocuments","ImportBlocksFromDocuments","ImportBlocksFromDirectory","ImportPlcProgramFromDirectory","ExportAsDocuments","ExportBlocksAsDocuments","ExportPlcWatchTable","ExportTechnologyObject","ExportBlocks","ExportTypes","ExportBlock","ExportType","ExportTagTable","ImportBlocks","ImportTypes","ImportTagTables","CreateTagTable","CreateTag","CreateUserConstant","CompileSoftware"
        };
    }
}
