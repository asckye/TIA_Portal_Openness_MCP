using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    internal static class DeviceServiceObjectRules
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => ArgumentRules.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
            => ArgumentRules.RequireText(value, parameter, max);
        private static void RequireKeys(JsonObject properties, string[] allowed, string parameter)
            => ArgumentRules.RequireKeys(properties, allowed, parameter);
        private static void RequireAbsoluteNewFile(string filePath, string parameter)
            => ArgumentRules.RequireAbsolutePath(filePath, "Absolute " + parameter + " required.");
        // ---- device service objects ---------------------------------------------------------------------------------------------
        internal static readonly string[] ServiceObjectFamilies = { "webApplications", "telecontrolDataPoints", "certificateServices" };
        internal static readonly string[] WebApplicationActions = { "read", "setDefault" };
        internal static readonly string[] TelecontrolActions = { "read", "update", "delete", "export", "import" };
        internal static readonly string[] TelecontrolProperties = { "Name", "DataPointType", "DataPointIndex", "MasterFunction" };
        internal static readonly string[] CertificateServiceActions = { "read", "update", "setServiceGroupName", "createService", "deleteService" };
        internal static readonly string[] CertificateConfigurationProperties = { "Usage", "CertificateExpirationEventActivated", "RemainingCertificateLifetime" };
        internal static readonly string[] CertificateConfigurationUsages = { "TIAPortal", "Runtime" };
        internal static readonly string[] CertificateSupportedServiceNames = { "None", "OpcUaClient", "OpcUaServer", "Webserver" };
        internal static readonly string[] WebApplicationTypes = { "None", "UserDefined", "System", "System_Inbuilt" };
        internal const int ServiceGroupNameMaxLength = 64;                        // official: EngineeringTargetInvocationException above 64 characters

        internal static void ValidateServiceObjectRequest(string family, string action, string name, JsonObject properties, string filePath, bool confirmChange, bool dryRun)
        {
            RequireOneOf(family, ServiceObjectFamilies, "family");
            string[] actions = family == "webApplications" ? WebApplicationActions : family == "telecontrolDataPoints" ? TelecontrolActions : CertificateServiceActions;
            RequireOneOf(action, actions, "action");
            bool needsName = action == "setDefault" || action == "update" && family == "telecontrolDataPoints" || action == "delete" || action == "setServiceGroupName" || action == "deleteService" || action == "createService";
            if (needsName) RequireName(name, "name", 256);
            else if (!string.IsNullOrEmpty(name) && action != "read") throw new ArgumentException("name applies to setDefault / update / delete / setServiceGroupName / createService / deleteService.");
            if (action == "createService")
            {
                if (!uint.TryParse(name, out _)) throw new ArgumentException("createService: name is the new service Id (UInt32).");
                RequireKeys(properties, new[] { "ServiceGroupName", "ServiceType" }, "propertiesJson");
                if (properties["ServiceGroupName"] == null || properties["ServiceType"] == null) throw new ArgumentException("createService needs propertiesJson {ServiceGroupName, ServiceType} (CertificateSupportedServiceComposition.Create(id, serviceGroupName, serviceType)).");
                if ((properties["ServiceGroupName"]!.ToString()).Length > ServiceGroupNameMaxLength) throw new ArgumentException("ServiceGroupName is limited to " + ServiceGroupNameMaxLength + " characters.");
                RequireOneOf(properties["ServiceType"]!.ToString(), CertificateSupportedServiceNames, "ServiceType");
            }
            else if (action == "update" || action == "setServiceGroupName")
            {
                if (properties.Count == 0) throw new ArgumentException(action + " needs propertiesJson.");
                if (family == "telecontrolDataPoints") RequireKeys(properties, TelecontrolProperties, "propertiesJson");
                else if (action == "setServiceGroupName")
                {
                    RequireKeys(properties, new[] { "ServiceGroupName" }, "propertiesJson");
                    var value = properties["ServiceGroupName"]?.ToString() ?? "";
                    if (value.Length > ServiceGroupNameMaxLength) throw new ArgumentException("ServiceGroupName is limited to " + ServiceGroupNameMaxLength + " characters (TIA throws EngineeringTargetInvocationException above it).");
                }
                else
                {
                    RequireKeys(properties, CertificateConfigurationProperties, "propertiesJson");
                    if (properties.TryGetPropertyValue("Usage", out var usage)) RequireOneOf(usage?.ToString() ?? "", CertificateConfigurationUsages, "Usage");
                    if (properties.TryGetPropertyValue("RemainingCertificateLifetime", out var lifetime) && (lifetime is not JsonValue v || !v.TryGetValue<long>(out var percent) || percent < 10 || percent > 90)) throw new ArgumentException("RemainingCertificateLifetime is a percentage 10..90 (official range).");
                }
            }
            else if (properties.Count != 0) throw new ArgumentException("propertiesJson applies to update / setServiceGroupName / createService only.");
            if (action == "export" || action == "import") RequireAbsoluteNewFile(filePath, "filePath"); else if (!string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath applies to export / import only.");
            if (action != "read") HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
        }
    }
}
