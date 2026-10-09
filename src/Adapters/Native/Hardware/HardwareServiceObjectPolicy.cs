using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using TiaMcp.Adapters.Contracts;

namespace TiaMcp.Adapters.Hardware
{
    internal static class HardwareServiceObjectPolicy
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesPolicy.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
            { if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace)."); }
        private static void RequireKeys(Dictionary<string, HardwareScalar> properties, string[] allowed, string parameter)
            { var unknown = properties.Keys.Where(k => !allowed.Contains(k, StringComparer.Ordinal)).ToArray(); if (unknown.Length > 0) throw new ArgumentException(parameter + " accepts only " + string.Join(" / ", allowed) + "; unknown: " + string.Join(", ", unknown)); }
        private static void RequireAbsoluteNewFile(string filePath, string parameter)
            { if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) throw new ArgumentException("Absolute " + parameter + " required."); }
        // ---- device service objects ---------------------------------------------------------------------------------------------
        internal static readonly string[] ServiceObjectFamilies = new[] { "webApplications", "telecontrolDataPoints", "certificateServices" };
        internal static readonly string[] WebApplicationActions = new[] { "read", "setDefault" };
        internal static readonly string[] TelecontrolActions = new[] { "read", "update", "delete", "export", "import" };
        internal static readonly string[] TelecontrolProperties = { "Name", "DataPointType", "DataPointIndex", "MasterFunction" };
        internal static readonly string[] CertificateServiceActions = new[] { "read", "update", "setServiceGroupName", "createService", "deleteService" };
        internal static readonly string[] CertificateConfigurationProperties = { "Usage", "CertificateExpirationEventActivated", "RemainingCertificateLifetime" };
        internal static readonly string[] CertificateConfigurationUsages = { "TIAPortal", "Runtime" };
        internal static readonly string[] CertificateSupportedServiceNames = { "None", "OpcUaClient", "OpcUaServer", "Webserver" };
        internal static readonly string[] WebApplicationTypes = { "None", "UserDefined", "System", "System_Inbuilt" };
        internal const int ServiceGroupNameMaxLength = 64;                        // official: EngineeringTargetInvocationException above 64 characters

        internal static void ValidateServiceObjectRequest(string family, string action, string name, Dictionary<string, HardwareScalar> properties, string filePath, bool confirmChange, bool dryRun)
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
                if (!properties.TryGetValue("ServiceGroupName", out var group) || group.Kind == "null" || !properties.TryGetValue("ServiceType", out var type) || type.Kind == "null") throw new ArgumentException("createService needs propertiesJson {ServiceGroupName, ServiceType} (CertificateSupportedServiceComposition.Create(id, serviceGroupName, serviceType)).");
                if ((properties["ServiceGroupName"].Text).Length > ServiceGroupNameMaxLength) throw new ArgumentException("ServiceGroupName is limited to " + ServiceGroupNameMaxLength + " characters.");
                RequireOneOf(properties["ServiceType"].Text, CertificateSupportedServiceNames, "ServiceType");
            }
            else if (action == "update" || action == "setServiceGroupName")
            {
                if (properties.Count == 0) throw new ArgumentException(action + " needs propertiesJson.");
                if (family == "telecontrolDataPoints") RequireKeys(properties, TelecontrolProperties, "propertiesJson");
                else if (action == "setServiceGroupName")
                {
                    RequireKeys(properties, new[] { "ServiceGroupName" }, "propertiesJson");
                    var value = properties.TryGetValue("ServiceGroupName", out var groupName) ? groupName.Text : "";
                    if (value.Length > ServiceGroupNameMaxLength) throw new ArgumentException("ServiceGroupName is limited to " + ServiceGroupNameMaxLength + " characters (TIA throws EngineeringTargetInvocationException above it).");
                }
                else
                {
                    RequireKeys(properties, CertificateConfigurationProperties, "propertiesJson");
                    if (properties.TryGetValue("Usage", out var usage)) RequireOneOf(usage.Kind == "null" ? "" : usage.Text, CertificateConfigurationUsages, "Usage");
                    if (properties.TryGetValue("RemainingCertificateLifetime", out var lifetime) && (lifetime.Kind != "number" || !long.TryParse(lifetime.Text, out var percent) || percent < 10 || percent > 90)) throw new ArgumentException("RemainingCertificateLifetime is a percentage 10..90 (official range).");
                }
            }
            else if (properties.Count != 0) throw new ArgumentException("propertiesJson applies to update / setServiceGroupName / createService only.");
            if (action == "export" || action == "import") RequireAbsoluteNewFile(filePath, "filePath"); else if (!string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath applies to export / import only.");
            if (action != "read") HardwareServicesPolicy.RequireConfirmation(confirmChange, "confirmChange", dryRun);
        }
    }
}
