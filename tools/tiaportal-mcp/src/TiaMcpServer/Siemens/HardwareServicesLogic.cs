using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Pure validation/catalog helpers for the HardwareServices family. No Siemens.Engineering dependency.
    internal static class HardwareServicesLogic
    {
        internal const string ConnectionNamespace = "Siemens.Engineering.HW.CommunicationConnections";
        internal const string FeatureNamespace = "Siemens.Engineering.HW.Features";
        // Concrete connection kinds of ConnectionComposition.Create<T> (V21 Siemens.Engineering.Base.xml).
        internal static readonly string[] ConnectionKinds = { "S7Connection", "IsoConnection", "IsoOnTcpConnection", "TcpConnection", "UdpConnection", "FdlConnection", "PtpConnection", "HmiConnection" };
        // Every T: id under Siemens.Engineering.HW.Features in the V21 XML docs (Base + Step7). Runtime decides presence.
        internal static readonly string[] FeatureCatalog = {
            "AddressController", "AddressControllerAssociation", "CertificateManagementConfiguration", "CommunicationManagement", "DefaultWebPagesFeature",
            "DeviceFeature", "DeviceItemFeature", "DisplayProtection", "FrontPanelDisplay", "GsdDevice", "GsdDeviceItem", "GsdExportProvider", "HardwareFeature",
            "HwIdentifierController", "HwIdentifierControllerAssociation", "IGsdObject", "ImConnection", "ModuleDescriptionUpdater", "MrpDomainOwner", "MrpInstancesOwner",
            "NetworkInterface", "NetworkInterfaceAssociation", "NetworkPort", "NetworkPortAssociation", "OpcUaUserManagement", "PcInterfaceAssignment",
            "PlcAccessControlConfigurationProvider", "PlcAccessLevelProvider", "PlcAccountLockingAtRuntimeFeature", "PlcMasterSecretConfigurator", "ResourceAssignment",
            "SimpleWebserverUserManagement", "SoftwareContainer", "SubnetFeature", "SubnetOwner", "SyncDomainOwner", "SysLogConfigurationManager", "SysLogServerConfiguration",
            "SysLogServerConfigurationComposition", "SystemWebPagesFeature", "TelecontrolManagement", "WatchAndForceTableAccessManager", "WebDBGenerateOptions",
            "WebserverUserDefinedPages", "WebserverUserManagement" };
        // Values of these features are never dumped; only presence is reported.
        internal static readonly string[] FeatureValueDenylist = { "PlcMasterSecretConfigurator", "WebserverUserManagement", "SimpleWebserverUserManagement", "OpcUaUserManagement" };
        internal static readonly string[] OpcUaPermissionNames = { "Browse", "Read", "Write", "Call", "ReceiveEvents", "ReadRolePermissions" };
        internal static readonly string[] OpcUaActions = { "createRole", "addStandardRole", "deleteRole", "setProjectRole", "setPermission", "setRestriction" };
        internal static readonly string[] CaxImportOptions = { "MoveToParkingLot", "OverwriteTiaDevice", "RetainTiaDevice" };
        internal static readonly string[] TableAccessValues = { "None", "Read", "Write" };

        internal static string RequireOneOf(string value, string[] allowed, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value) || !allowed.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException(parameter + " must be one of: " + string.Join("/", allowed) + " (case-sensitive).");
            return value;
        }
        internal static string ConnectionTypeName(string kind) => ConnectionNamespace + "." + RequireOneOf(kind, ConnectionKinds, "connectionType");
        internal static string FeatureTypeName(string feature) => FeatureNamespace + "." + feature;
        internal static bool FeatureValuesAllowed(string feature) => !FeatureValueDenylist.Contains(feature, StringComparer.Ordinal);
        internal static string RequireExactName(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim()) throw new ArgumentException(parameter + " must be an exact nonempty name without surrounding whitespace.");
            return value;
        }
        internal static void ValidatePagination(int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset>=0, limit 1..500 required.");
        }
        internal static JsonObject PageMeta(int total, int offset, int limit, int returned)
            => new JsonObject { ["expectedCount"] = total, ["actualCount"] = returned, ["offset"] = offset, ["limit"] = limit,
                ["nextOffset"] = offset + returned < total ? offset + returned : (int?)null, ["truncated"] = offset + returned < total };
        internal static FileInfo RequireExistingInputFile(string path, string parameter)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException(parameter + " must be an absolute file path.");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) throw new FileNotFoundException(parameter + " does not exist or is empty.", path);
            return file;
        }
        internal static void RequireConfirmation(bool confirmed, string parameter, bool dryRun)
        {
            if (!dryRun && !confirmed) throw new ArgumentException(parameter + "=true is required for real execution; preview does not need it.");
        }
    }
}
