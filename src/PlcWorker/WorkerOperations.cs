using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.PlcWorker
{
    internal static class WorkerOperations
    {
        internal static string[] FamilyNames(string family) => (family == "F08" || family == "F09" || family == "F11" || family == "F17") ? family == "F09" ? new[] { "ExecutePlcOrganisation", "SeedReferenceHmi" } : new[] { "ExecutePlcOrganisation" } : family == "F19" ? new[] {
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
        private static bool IsFamilyRead(string name) => name == "hardware-addressing.ReadHardwareIoAddresses"
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
        internal static bool RequiresPortal(string name) => name != "Attach" && name != "Disconnect"
            && name != "ReadState" && name != "ReadPortalProcessProjects" && name != "ReadPortalConnectReadiness"
            && name != SessionCandidate && name != "hardware-devices.HardwareLegacySearchInstalledGsdDevices";
        internal static bool IsReadOnly(string name) => name == "plc-analysis.ExportBlockDocument" || IsFamilyRead(name) || name=="SearchHardwareCatalog" || name=="PlanPlcExternalSourceImport" || name=="ReadState" || name=="ReadPortalProcessProjects" || name=="ReadPortalConnectReadiness" || name=="ReadWatchTableNames" || name=="ReadTechnologyObjects" || name=="ReadSoftwareInfo" || name=="ReadSoftwareTree" || name=="ReadExternalSourceNames" || name=="ListTags" || name=="ListUserConstants" || name=="ListSystemConstants" || name=="ReadBlockInfo" || name=="ReadTypeInfo" || name=="ReadBlocks" || name=="ReadTypes" || name=="ReadTagTableNames" || name=="ReadBlockHierarchy" || name=="ReadProjectTree" || name=="ListProjects";

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
