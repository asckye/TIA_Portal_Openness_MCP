using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcp.Versioning;
using TiaMcp.Adapters.Contracts;

namespace TiaMcpServer.Siemens
{
    // Admission is deliberately different from native acceptance. Existing V20/V21
    // implementations retain their own object, license, firmware and write guards.
    // This table only removes known unavailable routes; it certifies no native call.
    internal static class ToolVersionPolicy
    {
        // V17 Siemens.Engineering.xml: SafetyAdministration.LoginToSafetyOfflineProgram(SecureString).
        private static readonly string[] SafetyPasswordReleases = ReleaseRange.Since("17");

        internal static readonly IReadOnlyDictionary<string, string> V21Only =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ListCommunicationConnections"] = "HW.CommunicationConnections",
                ["ManageCommunicationConnection"] = "HW.CommunicationConnections",
                ["ListSafetyActivationTests"] = "SafetyValidationAssistant",
                ["ManageSafetyActivationTest"] = "SafetyValidationAssistant",
                ["ManageSafetyActivationTestGroup"] = "SafetyValidationAssistant",
                ["ManageSafetyFunction"] = "SafetyValidationAssistant",
                ["ManageSafetyFunctionCondition"] = "SafetyValidationAssistant",
                ["ManagePlcBlockWriteProtection"] = "PlcBlockWriteProtectionProvider",
                ["ManageDriveSafetyAcceptanceTest"] = "SafetyAcceptanceTestProvider",
                ["ManageSivarcScreenLayout"] = "SiVArc LayoutData",
                ["BindUnifiedHmiButtonPressedTag"] = "HmiButton.PressedStateTags",
                ["UploadDeviceParameters"] = "ParameterUploadProvider"
            };

        private static readonly IReadOnlyDictionary<string, string[]> ToolReleases = V21Only.Keys
            .ToDictionary(name => name, name => PortedFamilies.ToolReleases[name], StringComparer.OrdinalIgnoreCase);

        internal static string ToolProblem(string versionKey, string name)
        {
            TiaVersionDescriptor version;
            try { version = TiaVersionCatalog.Get(versionKey); }
            catch (ArgumentException) /* swallow(parse-fallback): an unknown release key returns the existing policy rejection */ { return "Unknown TIA release identity: " + versionKey + "."; }
            if (string.IsNullOrWhiteSpace(name)) return "A registered tool name is required.";
            if (V21Only.TryGetValue(name, out var api) && !Contains(ToolReleases[name], version.Key))
                return name + " requires the V21 " + api + " API; unavailable on " + version.DisplayName + ".";
            if (PortedFamilies.Contains(name) && !PortedFamilies.Available(version.Key, name))
                return name + " has no supported API action on " + version.DisplayName + ".";
            return "";
        }

        internal static string CallProblem(string versionKey, string name, Func<string, string?> argument)
        {
            var problem = ToolProblem(versionKey, name);
            if (problem.Length != 0) return problem;
            if (!Contains(SafetyPasswordReleases, versionKey) && name is "CompilePlcSoftware" or "CompilePlcDiagnostics"
                && !string.IsNullOrEmpty(argument("password")))
                return "Safety compilation with a password requires V17 or later.";
            if (name == "ManageSafetyGlobalSettings" && versionKey == "16" && argument("action") is null or "" or "read")
                return "GlobalSettings getters require V17 or later. V16 exposes setters for SafetyModificationsPossible and UsernameForFChangeHistory.";
            if (name.Equals("ImportLibraryTypeDocuments", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(argument("libraryName"))) return name + " supports only the project library (leave libraryName empty).";
                var option = argument("importOptions") ?? "None";
                if (!Contains(LibraryDeepLogic.ImportOptions, option)) return name + ": unknown importOptions '" + option + "'.";
            }
            // Full allowlists for these mixed-action tools prevent a future/new action
            // from accidentally inheriting availability. Matching the bridge's enum
            // normalization is safe: invalid direct spellings still fail native binding.
            string? action = argument("action");
            string[]? allowed = null;
            string[] gatedActions = Array.Empty<string>();
            OpennessApi? requiredApi = null;
            string defaultAction = "read";
            if (name.Equals("ManageDcbLibraries", StringComparison.OrdinalIgnoreCase))
                allowed = new[] { "read", "import" };
            if (name.Equals("ManageDriveHardwareModule", StringComparison.OrdinalIgnoreCase))
            {
                allowed = new[] { "read", "changeType", "setPositionNumber" };
                requiredApi = OpennessApi.DriveHardwareChanges;
                gatedActions = new[] { "changeType", "setPositionNumber" };
            }
            if (name.Equals("ManagePlcProtection", StringComparison.OrdinalIgnoreCase))
            {
                allowed = PlcProtectionLogic.Actions;
                requiredApi = OpennessApi.ProtectAllConfiguration;
                gatedActions = new[] { "protectAllConfiguration", "unprotectAllConfiguration" };
            }
            if (name.Equals("ManagePlcExternalSources", StringComparison.OrdinalIgnoreCase))
            {
                allowed = ExternalSourceRules.ExternalSourceActions;
                requiredApi = OpennessApi.ExternalSourceGroupRename;
                gatedActions = new[] { "renameGroup" };
                defaultAction = "";
            }
            if (name.Equals("ManagePlcSafety", StringComparison.OrdinalIgnoreCase))
            {
                allowed = SafetyLogic.Actions;
                requiredApi = OpennessApi.SafetyBaseId;
                gatedActions = new[] { "generateBaseId" };
            }
            if (name.Equals("ManagePlcDocuments", StringComparison.OrdinalIgnoreCase))
            {
                allowed = SoftwareUnitDeepLogic.DocumentActions; defaultAction = "";
                var kind = argument("objectKind") ?? "document";
                if (!Contains(SoftwareUnitDeepLogic.DocumentKinds, kind)) return name + ": unknown objectKind '" + kind + "'.";
                requiredApi = OpennessApi.PlcDocumentCreation;
                gatedActions = kind.Equals("document", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "createFromMasterCopy", "createFromLibraryType" } : Array.Empty<string>();
            }
            if (name.Equals("ManageSivarcBlockDefinition", StringComparison.OrdinalIgnoreCase))
            {
                allowed = SivarcLogic.DefinitionActions;
                var kind = argument("kind") ?? "";
                if (!Contains(SivarcLogic.DefinitionKinds, kind)) return name + ": unknown kind '" + kind + "'.";
                if (!OpennessApi.SivarcTagMembers.Includes(versionKey) && !Contains(new[] { "tagDefinition", "textDefinition" }, kind))
                    return name + ": kind '" + kind + "' requires the V21 TagMemberSetting / TagMember API.";
            }
            if (name.Equals("ManageDeviceServiceObjects", StringComparison.OrdinalIgnoreCase))
            {
                var family = argument("family") ?? "";
                if (!Contains(DeviceServiceObjectActions.ServiceObjectFamilies, family)) return name + ": unknown family '" + family + "'.";
                if (family.Equals("webApplications", StringComparison.OrdinalIgnoreCase))
                {
                    allowed = DeviceServiceObjectActions.WebApplicationActions;
                    requiredApi = OpennessApi.DefaultWebPages;
                    gatedActions = allowed;
                }
                else if (family.Equals("telecontrolDataPoints", StringComparison.OrdinalIgnoreCase))
                {
                    allowed = DeviceServiceObjectActions.TelecontrolActions;
                    requiredApi = OpennessApi.TelecontrolPoints;
                    gatedActions = new[] { "read", "update", "delete" };
                }
                else allowed = DeviceServiceObjectActions.CertificateServiceActions;
            }
            if (allowed != null)
            {
                action = action ?? defaultAction;
                if (!Contains(allowed, action)) return name + ": unknown action '" + action + "'. Allowed: " + string.Join(", ", allowed) + ".";
                if (requiredApi != null && !requiredApi.Includes(versionKey) && Contains(gatedActions, action))
                    return name + "." + action + " is unavailable on " + TiaVersionCatalog.Get(versionKey).DisplayName + "; requires V21.";
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
