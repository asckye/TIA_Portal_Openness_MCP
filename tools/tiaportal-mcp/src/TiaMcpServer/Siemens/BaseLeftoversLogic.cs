using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 3 sub-batch 4 (2.7.33): pure logic (no Siemens dependency) for the Base leftovers - portal / session
    // diagnostics, transfer routes and R/H targets, hardware utilities (module information, OPC UA and PSC export),
    // device service objects (web applications, telecontrol data points, dynamic certificate management), object
    // identifiers, show-in-editor, transactions and credential-bearing open / online requests.
    internal static class BaseLeftoversLogic
    {
        internal static string RequireOneOf(string value, string[] allowed, string parameter) => HardwareServicesLogic.RequireOneOf(value, allowed, parameter);
        private static void RequireName(string value, string parameter, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Trim() != value) throw new ArgumentException("Exact nonempty " + parameter + " required (max " + max + " chars, no surrounding whitespace).");
        }
        private static void RequireKeys(JsonObject properties, string[] allowed, string parameter)
        {
            var unknown = properties.Select(p => p.Key).Where(k => !allowed.Contains(k, StringComparer.Ordinal)).ToArray();
            if (unknown.Length > 0) throw new ArgumentException(parameter + " accepts only " + string.Join(" / ", allowed) + "; unknown: " + string.Join(", ", unknown));
        }
        private static void RequireAbsoluteNewFile(string filePath, string parameter)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.Path.IsPathRooted(filePath)) throw new ArgumentException("Absolute " + parameter + " required.");
        }

        // ---- hardware utilities (ProjectBase.HwUtilities) --------------------------------------------------------------------
        internal static readonly string[] HardwareUtilityActions = { "list", "findModuleTypes", "findContainerTypes", "normalizeTypeIdentifier", "exportOpcUa", "exportCardReaderPsc" };
        internal const string ModuleInformationProviderId = "ModuleInformationProvider";
        internal const string OpcUaExportProviderId = "OPCUAExportProvider";        // official page: project.HwUtilities.Find("OPCUAExportProvider")
        internal const string CardReaderPscProviderId = "CardReaderPscProvider";

        internal static void ValidateHardwareUtilityRequest(string action, string typeIdentifier, string devicePathJson, string filePath, string password, bool dryRun)
        {
            RequireOneOf(action, HardwareUtilityActions, "action");
            bool needsType = action == "findModuleTypes" || action == "findContainerTypes" || action == "normalizeTypeIdentifier";
            if (needsType) RequireName(typeIdentifier, "typeIdentifier", 512); else if (!string.IsNullOrEmpty(typeIdentifier)) throw new ArgumentException("typeIdentifier applies to findModuleTypes / findContainerTypes / normalizeTypeIdentifier only.");
            if (action == "exportOpcUa" || action == "exportCardReaderPsc") { ProjectSecurityLogic.ValidateDevicePath(devicePathJson); RequireAbsoluteNewFile(filePath, "filePath"); }
            else if (!string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath applies to the export actions only.");
            if (action == "exportOpcUa" && !filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("exportOpcUa writes an OPC UA XML file; filePath must end with .xml.");
            if (action == "exportCardReaderPsc" && !filePath.EndsWith(".psc", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("exportCardReaderPsc writes a .psc file; filePath must end with .psc.");
            if (!string.IsNullOrEmpty(password) && action != "exportCardReaderPsc") throw new ArgumentException("password applies to exportCardReaderPsc only (encrypted PSC, CPU V40.0+; never echoed).");
        }

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

        // ---- object identifiers / show in editor ------------------------------------------------------------------------------
        internal static readonly string[] ObjectKinds = { "device", "deviceItem", "plcBlock", "plcType", "plcTagTable" };
        internal static void ValidateObjectSelection(string kind, string devicePathJson, string itemPathJson, string softwarePath, string objectPath, string identifier)
        {
            RequireOneOf(kind, ObjectKinds, "kind");
            if (!string.IsNullOrEmpty(identifier)) { if (identifier.Length > 4096) throw new ArgumentException("identifier too long."); return; }
            if (kind == "device" || kind == "deviceItem") { ProjectSecurityLogic.ValidateDevicePath(devicePathJson); if (kind == "deviceItem" && (string.IsNullOrWhiteSpace(itemPathJson) || itemPathJson.Trim() == "[]")) throw new ArgumentException("kind=deviceItem needs a non-empty itemPathJson."); }
            else { RequireName(softwarePath, "softwarePath"); RequireName(objectPath, "objectPath", 1024); }
        }

        // ---- transactions ------------------------------------------------------------------------------------------------------
        internal sealed class ToolCall { public string Name = ""; public string ArgumentsJson = "{}"; }
        internal static ToolCall[] ParseToolCalls(string json)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(json ?? ""); } catch (System.Text.Json.JsonException ex) { throw new ArgumentException("callsJson must be a JSON array of {name, arguments}: " + ex.Message); }
            if (node is not JsonArray array) throw new ArgumentException("callsJson must be a JSON array of {name, arguments}.");
            if (array.Count < 1 || array.Count > 20) throw new ArgumentException("callsJson needs 1..20 tool calls.");
            var calls = new List<ToolCall>();
            foreach (var entry in array)
            {
                if (entry is not JsonObject o || o["name"] is not JsonValue nameValue) throw new ArgumentException("Each call is {\"name\":\"<tool>\",\"arguments\":{...}}.");
                var name = nameValue.ToString();
                if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c))) throw new ArgumentException("Tool names are plain identifiers: " + name);
                if (string.Equals(name, "RunToolsInTransaction", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "CallTool", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Nested transactions / CallTool are refused inside a transaction.");
                var arguments = o["arguments"];
                if (arguments != null && arguments is not JsonObject) throw new ArgumentException("arguments of " + name + " must be a JSON object.");
                calls.Add(new ToolCall { Name = name, ArgumentsJson = arguments?.ToJsonString() ?? "{}" });
            }
            return calls.ToArray();
        }
        internal static void ValidateTransactionRequest(string text, ToolCall[] calls, bool confirmChange, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200) throw new ArgumentException("text (the undo description shown in TIA) must be 1..200 characters; TIA refuses an empty one.");
            if (calls.Length == 0) throw new ArgumentException("At least one tool call is required.");
            HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
        }
        // Inside a real transaction every call must run for real or the transaction is pointless. 2.7.33 real project: only an
        // existing dryRun key was flipped, so calls without one ran as previews (tool default true) while the transaction still
        // reported committed=true; the key is now written unconditionally (CallTool ignores it on tools without a dryRun parameter).
        internal static string ForceRealExecution(string argumentsJson)
        {
            var o = JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
            var key = o.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, "dryRun", StringComparison.OrdinalIgnoreCase)) ?? "dryRun";
            o[key] = false;
            return o.ToJsonString();
        }

        // ---- credentials for OpenProject / GoOnline; R/H targets ---------------------------------------------------------------
        internal static readonly string[] UmacUserTypes = { "Project", "Global" };
        internal static readonly string[] OnlineUserTypes = { "None", "AnonymousUser", "GlobalUser", "ProjectUser", "SingleSignOnUser", "PasswordOnly" };
        internal static readonly string[] RhTargets = { "", "primary", "backup" };
        internal static void ValidateUmacCredentials(string userName, string password, string userType)
        {
            bool any = !string.IsNullOrEmpty(userName) || !string.IsNullOrEmpty(password);
            if (!any) { if (!string.IsNullOrEmpty(userType)) throw new ArgumentException("umacUserType applies together with umacUserName + umacPassword."); return; }
            if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password)) throw new ArgumentException("umacUserName and umacPassword must be given together (protected project credentials; the password is converted to SecureString and never echoed).");
            RequireOneOf(string.IsNullOrEmpty(userType) ? "Project" : userType, UmacUserTypes, "umacUserType");
        }
        internal static void ValidateOnlineCredentials(string userName, string password, string userType)
        {
            if (!string.IsNullOrEmpty(userName) && string.IsNullOrEmpty(password)) throw new ArgumentException("userName needs a password (online authentication for UMAC-protected PLCs).");
            if (!string.IsNullOrEmpty(userType)) { RequireOneOf(userType, OnlineUserTypes, "userType"); if (string.IsNullOrEmpty(userName) && userType != "None" && userType != "AnonymousUser" && userType != "PasswordOnly") throw new ArgumentException("userType " + userType + " needs a userName."); }
        }
        // 2.7.52 (real machine, PLCSIM Advanced MCP_SIM via Softbus): every first online contact with an S7-1500 FW >= 2.9 raises
        // TlsVerificationConfiguration on ConnectionConfiguration.OnlineLegitimation ("The device is not trusted. Please check the
        // certificate." when nobody answers). The official answer is CurrentSelection = Trusted; the decision is the caller's
        // (trustDeviceCertificate) and the selection TIA reported before it is kept for the record.
        internal static readonly string[] TlsSelections = { "NonVerified", "Trusted", "NonTrusted" };
        internal static string? TlsSelectionToApply(bool trustDeviceCertificate, string currentSelection)
        {
            if (!trustDeviceCertificate) return null;
            return string.Equals(currentSelection, "Trusted", StringComparison.Ordinal) ? null : "Trusted";
        }
        internal static string ValidateRhTarget(string rhTarget)
        {
            var value = rhTarget ?? "";
            if (!RhTargets.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("rhTarget must be empty (standard CPU), primary or backup (R/H systems via RHDownloadProvider / RHOnlineProvider).");
            return value;
        }
    }
}
