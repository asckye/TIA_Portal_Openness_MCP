using System;
using System.Collections.Generic;
using TiaMcp.Versioning;

namespace TiaMcpServer.Siemens
{
    // Admission is deliberately different from native acceptance. Existing V20/V21
    // implementations retain their own object, license, firmware and write guards.
    // This table only removes known unavailable routes; it certifies no native call.
    internal static class ToolVersionPolicy
    {
        internal static readonly IReadOnlyDictionary<string, string> V21Only =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ReadCommunicationConnections"] = "HW.CommunicationConnections",
                ["ManageCommunicationConnection"] = "HW.CommunicationConnections",
                ["ReadSafetyActivationTests"] = "SafetyValidationAssistant",
                ["ManageSafetyActivationTest"] = "SafetyValidationAssistant",
                ["ManageSafetyActivationTestGroup"] = "SafetyValidationAssistant",
                ["ManageSafetyFunction"] = "SafetyValidationAssistant",
                ["ManageSafetyFunctionCondition"] = "SafetyValidationAssistant",
                ["ManagePlcBlockWriteProtection"] = "PlcBlockWriteProtectionProvider",
                ["ManageDriveSafetyAcceptanceTest"] = "SafetyAcceptanceTestProvider",
                ["ManageSivarcScreenLayout"] = "SiVArc LayoutData",
                ["ManageClassicHmiGraphic"] = "GraphicsProvider"
            };

        internal static string ToolProblem(string versionKey, string name)
        {
            TiaVersionDescriptor version;
            try { version = TiaVersionCatalog.Get(versionKey); }
            catch (ArgumentException) { return "Unknown TIA release identity: " + versionKey + "."; }
            if (!version.IsFullEngine) return version.DisplayName + " uses the separate PLC foundation catalog; full-engine tools are unavailable.";
            if (string.IsNullOrWhiteSpace(name)) return "A registered tool name is required.";
            if (version.Key == "20" && V21Only.TryGetValue(name, out var api))
                return name + " requires the V21 " + api + " API; unavailable on V20.";
            return "";
        }

        internal static string CallProblem(string versionKey, string name, Func<string, string?> argument)
        {
            var problem = ToolProblem(versionKey, name);
            if (problem.Length != 0) return problem;
            if (name.Equals("ImportLibraryTypeDocuments", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(argument("libraryName"))) return name + " supports only the project library (leave libraryName empty).";
                var option = argument("importOptions") ?? "None";
                if (!Contains(LibraryDeepLogic.ImportOptions, option)) return name + ": unknown importOptions '" + option + "'.";
                if (versionKey == "20" && !option.Equals("None", StringComparison.OrdinalIgnoreCase))
                    return name + ": V20 supports LibraryImportOptions.None only.";
            }
            // Full allowlists for these mixed-action tools prevent a future/new action
            // from accidentally inheriting availability. Matching the bridge's enum
            // normalization is safe: invalid direct spellings still fail native binding.
            string? action = argument("action");
            string[]? allowed = null;
            string[]? v21Actions = null;
            string defaultAction = "read";
            if (name.Equals("ManageDcbLibraries", StringComparison.OrdinalIgnoreCase))
            { allowed = new[] { "read", "import" }; v21Actions = new[] { "import" }; }
            if (name.Equals("ManageDriveHardwareModule", StringComparison.OrdinalIgnoreCase))
            { allowed = new[] { "read", "changeType", "setPositionNumber" }; v21Actions = new[] { "changeType", "setPositionNumber" }; }
            if (name.Equals("ManagePlcProtection", StringComparison.OrdinalIgnoreCase))
            { allowed = PlcProtectionLogic.Actions; v21Actions = new[] { "protectAllConfiguration", "unprotectAllConfiguration" }; }
            if (name.Equals("ManagePlcExternalSources", StringComparison.OrdinalIgnoreCase))
            { allowed = Step7LeftoversLogic.ExternalSourceActions; v21Actions = new[] { "renameGroup" }; defaultAction = ""; }
            if (name.Equals("ManagePlcSafety", StringComparison.OrdinalIgnoreCase))
            { allowed = SafetyLogic.Actions; v21Actions = new[] { "generateBaseId" }; }
            if (name.Equals("ManagePlcDocuments", StringComparison.OrdinalIgnoreCase))
            {
                allowed = SoftwareUnitDeepLogic.DocumentActions; defaultAction = "";
                var kind = argument("objectKind") ?? "document";
                if (!Contains(SoftwareUnitDeepLogic.DocumentKinds, kind)) return name + ": unknown objectKind '" + kind + "'.";
                v21Actions = kind.Equals("document", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "createFromMasterCopy", "createFromLibraryType" } : Array.Empty<string>();
            }
            if (name.Equals("ManageSivarcBlockDefinition", StringComparison.OrdinalIgnoreCase))
            {
                allowed = SivarcLogic.DefinitionActions; v21Actions = Array.Empty<string>();
                var kind = argument("kind") ?? "";
                if (!Contains(SivarcLogic.DefinitionKinds, kind)) return name + ": unknown kind '" + kind + "'.";
                if (versionKey == "20" && !Contains(new[] { "tagDefinition", "textDefinition" }, kind))
                    return name + ": kind '" + kind + "' requires the V21 TagMemberSetting / TagMember API.";
            }
            if (name.Equals("ManageDeviceServiceObjects", StringComparison.OrdinalIgnoreCase))
            {
                var family = argument("family") ?? "";
                if (!Contains(BaseLeftoversLogic.ServiceObjectFamilies, family)) return name + ": unknown family '" + family + "'.";
                if (family.Equals("webApplications", StringComparison.OrdinalIgnoreCase))
                { allowed = BaseLeftoversLogic.WebApplicationActions; v21Actions = allowed; }
                else if (family.Equals("telecontrolDataPoints", StringComparison.OrdinalIgnoreCase))
                { allowed = BaseLeftoversLogic.TelecontrolActions; v21Actions = new[] { "read", "update", "delete" }; }
                else { allowed = BaseLeftoversLogic.CertificateServiceActions; v21Actions = Array.Empty<string>(); }
            }
            if (allowed != null)
            {
                action = action ?? defaultAction;
                if (!Contains(allowed, action)) return name + ": unknown action '" + action + "'. Allowed: " + string.Join(", ", allowed) + ".";
                if (versionKey == "20" && Contains(v21Actions!, action)) return name + "." + action + " is unavailable on V20; requires V21.";
            }
            return "";
        }

        private static bool Contains(IEnumerable<string> values, string value)
        {
            foreach (var candidate in values)
                if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
