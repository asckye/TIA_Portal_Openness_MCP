using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    // Phase 3 sub-batch 3 (2.7.32): pure logic (no Siemens dependency) for the project-global security and UMC family -
    // syslog servers (Security.SyslogServerProvider / HW.Features.SysLogConfigurationManager), password policies
    // (Umac.PasswordPolicyConfigurator, Security.PlcPasswordPolicyService / LegacyPlcPasswordPolicyService), UMC users and
    // groups (Umac.UmcUser / UmcUserGroup / UmcServerConfigurator) and certificate templates (Security.CertificateTemplate /
    // SubjectAlternativeName). Everything here is parameter gating and catalogs; the Portal partial does the native calls.
    internal static class SecurityDeepLogic
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

        // ---- syslog -------------------------------------------------------------------------------------------------------
        internal static readonly string[] SyslogScopes = { "project", "plc" };
        internal static readonly string[] SyslogProjectActions = { "read", "create", "update", "delete", "assignModule", "unassignModule" };
        internal static readonly string[] SyslogPlcActions = { "read", "update", "createServer", "deleteServer" };
        internal static readonly string[] SyslogServerProperties = { "Name", "Address", "Port", "Tls", "Comment" };      // Security.SyslogServer
        internal static readonly string[] SyslogManagerProperties = { "EnableSystemLogging", "TransportProtocol" };      // HW.Features.SysLogConfigurationManager
        internal static readonly string[] SyslogTransportProtocols = { "None", "TLSServerAndClientAuthentication", "TLSOnlyServerAuthentication", "UDP" };
        internal static readonly string[] SyslogPlcAttributes = { "SysLogAutoAcceptClient", "SysLogClientCertificateId", "SysLogTrustedCertificateIds" }; // dynamic attributes on the CPU item (official page)
        // 2.7.32 real project (2026-09-18): SyslogServerComposition.Create(name) threw a message-less NonRecoverableException and the
        // TIA Portal V21 process exited, twice in a row. Real creation stays disabled until a precondition is known; preview works.
        internal const string ProjectSyslogCreateRefusal = "scope=project action=create is preview-only: Siemens.Engineering.Security.SyslogServerComposition.Create(name) crashed TIA Portal V21 twice on the reference project (2026-09-18, message-less NonRecoverableException, process gone). Create the server in the TIA UI (project security settings) and manage it here with read / update / assignModule / unassignModule / delete.";

        internal static void ValidateSyslogRequest(string scope, string action, string name, JsonObject properties, string devicePathJson, string serverAddress, int serverPort, bool confirmDelete, bool dryRun)
        {
            RequireOneOf(scope, SyslogScopes, "scope");
            RequireOneOf(action, scope == "project" ? SyslogProjectActions : SyslogPlcActions, "action");
            if (scope == "project")
            {
                if (action != "read") RequireName(name, "name");
                else if (!string.IsNullOrEmpty(name)) RequireName(name, "name");
                if (action == "create" || action == "update") RequireKeys(properties, SyslogServerProperties, "propertiesJson");
                else if (properties.Count != 0) throw new ArgumentException("propertiesJson applies to create/update only.");
                if (action == "update" && properties.Count == 0) throw new ArgumentException("update needs at least one property in propertiesJson (" + string.Join(" / ", SyslogServerProperties) + ").");
                if (properties.TryGetPropertyValue("Port", out var port) && (port is not JsonValue portValue || !portValue.TryGetValue<long>(out var p) || p < 1 || p > 65535)) throw new ArgumentException("Port must be an integer 1..65535.");
                if (action == "assignModule" || action == "unassignModule") ProjectSecurityLogic.ValidateDevicePath(devicePathJson);
                if (action == "delete") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                if (!string.IsNullOrEmpty(serverAddress) || serverPort != 0) throw new ArgumentException("serverAddress/serverPort belong to scope=plc; use propertiesJson Address/Port for project syslog servers.");
            }
            else
            {
                ProjectSecurityLogic.ValidateDevicePath(devicePathJson);
                if (action == "update")
                {
                    if (properties.Count == 0) throw new ArgumentException("update needs EnableSystemLogging and/or TransportProtocol in propertiesJson.");
                    RequireKeys(properties, SyslogManagerProperties, "propertiesJson");
                    if (properties.TryGetPropertyValue("TransportProtocol", out var protocol)) RequireOneOf(protocol?.ToString() ?? "", SyslogTransportProtocols, "TransportProtocol");
                }
                else if (properties.Count != 0) throw new ArgumentException("propertiesJson applies to action=update only.");
                if (action == "createServer" || action == "deleteServer") RequireName(serverAddress, "serverAddress", 255);
                if (action == "createServer" && (serverPort < 1 || serverPort > 65535)) throw new ArgumentException("serverPort must be 1..65535 for createServer.");
                if (action == "deleteServer") HardwareServicesLogic.RequireConfirmation(confirmDelete, "confirmDelete", dryRun);
                if (!string.IsNullOrEmpty(name)) throw new ArgumentException("name belongs to scope=project; PLC syslog servers are addressed by serverAddress.");
            }
        }

        // ---- password policies --------------------------------------------------------------------------------------------
        internal static readonly string[] PolicyActions = { "read", "update" };
        internal static readonly string[] PolicyTargets = { "umac", "plc", "legacyPlc" };
        internal static readonly string[] UmacPolicyProperties = { "IncludesLowerCaseAndUpperCaseCharacters", "MinimumLength", "MinimumNumericCharacterLength", "MinimumSpecialCharacterLength", "EnablePasswordAging", "MinimumUserPasswordsBlockedForReuse", "PasswordValidity", "PasswordValidityPrewarningTime" };
        internal static readonly string[] PlcPolicyProperties = { "PasswordPolicyEnabled" };
        internal static readonly string[] LegacyPlcPolicyProperties = { "PasswordPolicyEnabled", "MinimumLength", "MinimumNumericCharacterLength", "MinimumSpecialCharacterLength", "IncludesLowerCaseAndUpperCaseCharacters" };
        // Official ranges for the legacy PLC policy (S7-300/400, ET 200S, WinAC); TIA throws PasswordPolicySettingsException outside them.
        internal static readonly Dictionary<string, (int Min, int Max)> LegacyRanges = new(StringComparer.Ordinal) { ["MinimumLength"] = (5, 8), ["MinimumNumericCharacterLength"] = (0, 8), ["MinimumSpecialCharacterLength"] = (0, 8) };

        internal static string[] PolicyProperties(string target) => target switch { "umac" => UmacPolicyProperties, "plc" => PlcPolicyProperties, _ => LegacyPlcPolicyProperties };

        internal static void ValidatePolicyRequest(string action, string target, JsonObject properties, bool confirmChange, bool dryRun)
        {
            RequireOneOf(action, PolicyActions, "action");
            if (action == "read")
            {
                if (!string.IsNullOrEmpty(target)) RequireOneOf(target, PolicyTargets, "target");
                if (properties.Count != 0) throw new ArgumentException("propertiesJson applies to action=update only.");
                return;
            }
            RequireOneOf(target, PolicyTargets, "target");
            if (properties.Count == 0) throw new ArgumentException("update needs at least one property in propertiesJson (" + string.Join(" / ", PolicyProperties(target)) + ").");
            RequireKeys(properties, PolicyProperties(target), "propertiesJson");
            foreach (var pair in properties)
            {
                bool boolean = pair.Key == "PasswordPolicyEnabled" || pair.Key == "IncludesLowerCaseAndUpperCaseCharacters" || pair.Key == "EnablePasswordAging";
                if (boolean) { if (pair.Value is not JsonValue v || !v.TryGetValue<bool>(out _)) throw new ArgumentException(pair.Key + " must be a JSON boolean."); continue; }
                if (pair.Value is not JsonValue n || !n.TryGetValue<long>(out var number) || number < 0 || number > short.MaxValue) throw new ArgumentException(pair.Key + " must be an integer 0..32767 (the API type is Int16).");
                if (target == "legacyPlc" && LegacyRanges.TryGetValue(pair.Key, out var range) && (number < range.Min || number > range.Max)) throw new ArgumentException(pair.Key + " must be " + range.Min + ".." + range.Max + " for the legacy PLC policy (official range; TIA throws PasswordPolicySettingsException otherwise).");
            }
            HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
        }

        // ---- UMC users / groups / server ----------------------------------------------------------------------------------
        internal static readonly string[] UmcKinds = { "user", "group", "server" };
        internal static readonly string[] UmcMemberActions = { "read", "createOffline", "importFromServer", "rename", "activate", "deactivate", "delete", "assignRole", "unassignRole" };
        internal static readonly string[] UmcServerActions = { "read", "checkConsistency", "synchronize" };
        internal static readonly string[] UmcCredentialActions = { "importFromServer", "checkConsistency", "synchronize" }; // may trigger UmcServer.Authentication

        internal sealed class UmcRequest
        {
            public string Kind = "", Action = "";
            public bool Reads, Creates, Deletes, NeedsRole, NeedsNewName, NeedsCredentials, Writes;
        }

        internal static UmcRequest ValidateUmcRequest(string kind, string action, string name, string newName, string roleName, string serverUserName, string serverPassword, bool confirmChange, bool dryRun)
        {
            RequireOneOf(kind, UmcKinds, "kind");
            var request = new UmcRequest { Kind = kind, Action = action };
            if (kind == "server")
            {
                RequireOneOf(action, UmcServerActions, "action");
                request.Reads = action == "read"; request.Writes = action == "synchronize";
                if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(newName) || !string.IsNullOrEmpty(roleName)) throw new ArgumentException("name/newName/roleName do not apply to kind=server.");
            }
            else
            {
                RequireOneOf(action, UmcMemberActions, "action");
                request.Reads = action == "read"; request.Creates = action == "createOffline" || action == "importFromServer"; request.Deletes = action == "delete";
                request.NeedsRole = action == "assignRole" || action == "unassignRole"; request.NeedsNewName = action == "rename"; request.Writes = !request.Reads;
                if (request.Reads) { if (!string.IsNullOrEmpty(name)) RequireName(name, "name"); }
                else RequireName(name, "name");
                if (request.NeedsRole) RequireName(roleName, "roleName"); else if (!string.IsNullOrEmpty(roleName)) throw new ArgumentException("roleName applies to assignRole/unassignRole only.");
                if (request.NeedsNewName) { RequireName(newName, "newName"); if (newName == name) throw new ArgumentException("newName equals the current name."); }
                else if (!string.IsNullOrEmpty(newName)) throw new ArgumentException("newName applies to action=rename only.");
            }
            request.NeedsCredentials = action == "importFromServer";
            bool credentialsGiven = !string.IsNullOrEmpty(serverUserName) || !string.IsNullOrEmpty(serverPassword);
            if (credentialsGiven && !UmcCredentialActions.Contains(action)) throw new ArgumentException("serverUserName/serverPassword apply to importFromServer, checkConsistency and synchronize only (UMC server authentication).");
            if (credentialsGiven && (string.IsNullOrEmpty(serverUserName) || string.IsNullOrEmpty(serverPassword))) throw new ArgumentException("serverUserName and serverPassword must be given together; the password is converted to SecureString and never echoed.");
            if (request.NeedsCredentials && !credentialsGiven) throw new ArgumentException("importFromServer needs serverUserName + serverPassword: UmcServer.GetUserByName/GetUserGroupByName raise the Authentication event and need a UMC account with the UMC View right.");
            if (request.Writes) HardwareServicesLogic.RequireConfirmation(confirmChange, "confirmChange", dryRun);
            return request;
        }

        // ---- certificate templates ----------------------------------------------------------------------------------------
        internal static readonly string[] CertificateUsages = { "Tls", "WebServer", "OpcUaServer", "OpcUaClient", "OpcUaClientServer" }; // None is "not supported" officially
        internal static readonly string[] SignatureAlgorithms = { "Sha1RSA", "Sha256RSA" };
        internal static readonly string[] SubjectAlternativeNameTypes = { "Dns", "Email", "IP", "Uri" };
        internal static readonly string[] TemplateProperties = { "Signature", "SubjectCommonName", "Usage", "ValidFrom", "ValidUntil" };

        internal sealed class SubjectAlternativeName { public string Type = ""; public string Value = ""; }

        internal static SubjectAlternativeName[] ParseSubjectAlternativeNames(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SubjectAlternativeName>();
            JsonNode? node;
            try { node = JsonNode.Parse(json); } catch (System.Text.Json.JsonException ex) { throw new ArgumentException("subjectAlternativeNamesJson must be a JSON array of {type, value}: " + ex.Message); }
            if (node is not JsonArray array) throw new ArgumentException("subjectAlternativeNamesJson must be a JSON array of {type, value}.");
            if (array.Count > 64) throw new ArgumentException("At most 64 subject alternative names.");
            var result = new List<SubjectAlternativeName>();
            foreach (var entry in array)
            {
                if (entry is not JsonObject o || o.Count != 2 || o["type"] is not JsonValue t || o["value"] is not JsonValue v) throw new ArgumentException("Each subject alternative name is {\"type\":\"Dns|Email|IP|Uri\",\"value\":\"...\"}.");
                var type = RequireOneOf(t.ToString(), SubjectAlternativeNameTypes, "type"); var value = v.ToString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 255) throw new ArgumentException("Subject alternative name value must be 1..255 chars.");
                if (result.Any(r => r.Type == type && string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Duplicate subject alternative name: " + type + " " + value);
                result.Add(new SubjectAlternativeName { Type = type, Value = value });
            }
            return result.ToArray();
        }

        internal static void ValidateTemplateProperties(JsonObject properties)
        {
            RequireKeys(properties, TemplateProperties, "propertiesJson");
            if (properties.TryGetPropertyValue("Signature", out var signature)) RequireOneOf(signature?.ToString() ?? "", SignatureAlgorithms, "Signature");
            if (properties.TryGetPropertyValue("Usage", out var usage)) RequireOneOf(usage?.ToString() ?? "", CertificateUsages, "Usage");
            if (properties.TryGetPropertyValue("SubjectCommonName", out var cn) && string.IsNullOrWhiteSpace(cn?.ToString())) throw new ArgumentException("SubjectCommonName must be nonempty when given.");
            DateTime? from = ParseDate(properties, "ValidFrom"), until = ParseDate(properties, "ValidUntil");
            if (from != null && until != null && until <= from) throw new ArgumentException("ValidUntil must be later than ValidFrom.");
        }
        private static DateTime? ParseDate(JsonObject properties, string key)
        {
            if (!properties.TryGetPropertyValue(key, out var node) || node == null) return null;
            if (!DateTime.TryParse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var value)) throw new ArgumentException(key + " must be an ISO 8601 date/time.");
            return value;
        }
    }
}
