using System;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 3 sub-batch 3 (2.7.32) pure logic: syslog (project / PLC) request gating, password-policy targets and ranges,
    // UMC user / group / server request model with credential rules, certificate template properties and subject
    // alternative names, plus the anonymous-user actions added to ProjectSecurityLogic.
    internal static class SecurityDeepTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        private static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

        internal static void Run(Action<bool, string> check)
        {
            // ---- syslog: project scope ----
            SecurityDeepLogic.ValidateSyslogRequest("project", "read", "", O("{}"), "[]", "", 0, false, true);
            SecurityDeepLogic.ValidateSyslogRequest("project", "create", "Syslog_1", O("{\"Address\":\"10.0.0.5\",\"Port\":6514,\"Tls\":true,\"Comment\":\"c\"}"), "[]", "", 0, false, true);
            SecurityDeepLogic.ValidateSyslogRequest("project", "delete", "Syslog_1", O("{}"), "[]", "", 0, true, false);
            SecurityDeepLogic.ValidateSyslogRequest("project", "assignModule", "Syslog_1", O("{}"), "[\"ET 200SP station_1\"]", "", 0, false, true);
            check(true, "secdeep: project syslog read/create/delete/assignModule requests accepted");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "create", "", O("{}"), "[]", "", 0, false, true)), "secdeep: project create without name refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "update", "S", O("{}"), "[]", "", 0, false, true)), "secdeep: project update without properties refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "update", "S", O("{\"Host\":\"x\"}"), "[]", "", 0, false, true)), "secdeep: unknown syslog server property refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "create", "S", O("{\"Port\":70000}"), "[]", "", 0, false, true)), "secdeep: port outside 1..65535 refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "create", "S", O("{\"Port\":\"514\"}"), "[]", "", 0, false, true)), "secdeep: string port refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "delete", "S", O("{}"), "[]", "", 0, false, false)), "secdeep: real delete without confirmDelete refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "assignModule", "S", O("{}"), "[]", "", 0, false, true)), "secdeep: assignModule without device path refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "read", "", O("{}"), "[]", "1.2.3.4", 0, false, true)), "secdeep: serverAddress on project scope refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("project", "createServer", "S", O("{}"), "[]", "", 0, false, true)), "secdeep: PLC action on project scope refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("global", "read", "", O("{}"), "[]", "", 0, false, true)), "secdeep: unknown scope refused");
            // ---- syslog: PLC scope ----
            SecurityDeepLogic.ValidateSyslogRequest("plc", "read", "", O("{}"), "[\"ET 200SP station_1\"]", "", 0, false, true);
            SecurityDeepLogic.ValidateSyslogRequest("plc", "update", "", O("{\"EnableSystemLogging\":true,\"TransportProtocol\":\"UDP\"}"), "[\"D\"]", "", 0, false, true);
            SecurityDeepLogic.ValidateSyslogRequest("plc", "createServer", "", O("{}"), "[\"D\"]", "syslog.example.com", 514, false, true);
            SecurityDeepLogic.ValidateSyslogRequest("plc", "deleteServer", "", O("{}"), "[\"D\"]", "syslog.example.com", 0, true, false);
            check(true, "secdeep: PLC syslog read/update/createServer/deleteServer requests accepted");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("plc", "read", "", O("{}"), "[]", "", 0, false, true)), "secdeep: PLC scope without device path refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("plc", "update", "", O("{\"TransportProtocol\":\"Tcp\"}"), "[\"D\"]", "", 0, false, true)), "secdeep: unknown transport protocol refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("plc", "createServer", "", O("{}"), "[\"D\"]", "host", 0, false, true)), "secdeep: createServer without port refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("plc", "createServer", "", O("{}"), "[\"D\"]", "", 514, false, true)), "secdeep: createServer without address refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateSyslogRequest("plc", "read", "S", O("{}"), "[\"D\"]", "", 0, false, true)), "secdeep: name on PLC scope refused");
            check(SecurityDeepLogic.SyslogTransportProtocols.SequenceEqual(new[] { "None", "TLSServerAndClientAuthentication", "TLSOnlyServerAuthentication", "UDP" }) && SecurityDeepLogic.SyslogPlcAttributes.Length == 3, "secdeep: syslog catalogs (official enum names and the three CPU attributes)");
            check(SecurityDeepLogic.ProjectSyslogCreateRefusal.Contains("preview-only") && SecurityDeepLogic.ProjectSyslogCreateRefusal.Contains("SyslogServerComposition.Create") && SecurityDeepLogic.ProjectSyslogCreateRefusal.Contains("crashed TIA Portal"), "secdeep: project syslog create refusal names the crashing API and the TIA UI alternative (2.7.33)");

            // ---- password policies ----
            SecurityDeepLogic.ValidatePolicyRequest("read", "", O("{}"), false, true);
            SecurityDeepLogic.ValidatePolicyRequest("read", "legacyPlc", O("{}"), false, true);
            SecurityDeepLogic.ValidatePolicyRequest("update", "umac", O("{\"MinimumLength\":8,\"EnablePasswordAging\":false}"), false, true);
            SecurityDeepLogic.ValidatePolicyRequest("update", "plc", O("{\"PasswordPolicyEnabled\":true}"), true, false);
            SecurityDeepLogic.ValidatePolicyRequest("update", "legacyPlc", O("{\"MinimumLength\":6,\"MinimumNumericCharacterLength\":0,\"MinimumSpecialCharacterLength\":8}"), true, false);
            check(true, "secdeep: policy read/update requests accepted");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("update", "", O("{\"MinimumLength\":8}"), true, false)), "secdeep: update without target refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("update", "umac", O("{}"), true, false)), "secdeep: update without properties refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("update", "plc", O("{\"MinimumLength\":8}"), true, false)), "secdeep: MinimumLength is not a plc (S7-1500) policy property");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("update", "legacyPlc", O("{\"MinimumLength\":4}"), true, false)), "secdeep: legacy MinimumLength below 5 refused before TIA");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("update", "legacyPlc", O("{\"MinimumSpecialCharacterLength\":9}"), true, false)), "secdeep: legacy special-character length above 8 refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("update", "umac", O("{\"EnablePasswordAging\":1}"), true, false)), "secdeep: boolean policy property with a number refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("update", "umac", O("{\"PasswordValidity\":-5}"), true, false)), "secdeep: negative Int16 policy value refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("update", "umac", O("{\"MinimumLength\":8}"), false, false)), "secdeep: real policy update without confirmChange refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidatePolicyRequest("read", "hmi", O("{}"), false, true)), "secdeep: unknown policy target refused");
            check(SecurityDeepLogic.UmacPolicyProperties.Length == 8 && SecurityDeepLogic.LegacyPlcPolicyProperties.Length == 5 && SecurityDeepLogic.PlcPolicyProperties.SequenceEqual(new[] { "PasswordPolicyEnabled" }), "secdeep: policy property catalogs (8 / 1 / 5 official members)");

            // ---- UMC users / groups / server ----
            var r = SecurityDeepLogic.ValidateUmcRequest("user", "read", "", "", "", "", "", false, true);
            check(r.Reads && !r.Writes && !r.NeedsCredentials, "secdeep: umc read request");
            r = SecurityDeepLogic.ValidateUmcRequest("group", "createOffline", "Grp", "", "", "", "", true, false);
            check(r.Creates && r.Writes && !r.NeedsCredentials, "secdeep: offline group creation needs no credentials");
            r = SecurityDeepLogic.ValidateUmcRequest("user", "importFromServer", "alice", "", "", "umcadmin", "pw", true, false);
            check(r.Creates && r.NeedsCredentials, "secdeep: import from server carries credentials");
            r = SecurityDeepLogic.ValidateUmcRequest("user", "rename", "alice", "alice2", "", "", "", true, false);
            check(r.NeedsNewName && r.Writes, "secdeep: rename request");
            r = SecurityDeepLogic.ValidateUmcRequest("group", "assignRole", "Grp", "", "Engineering administrator", "", "", true, false);
            check(r.NeedsRole, "secdeep: assignRole request");
            r = SecurityDeepLogic.ValidateUmcRequest("server", "checkConsistency", "", "", "", "umcadmin", "pw", false, true);
            check(!r.Writes && !r.NeedsCredentials, "secdeep: server checkConsistency accepts optional credentials without confirmation");
            r = SecurityDeepLogic.ValidateUmcRequest("server", "synchronize", "", "", "", "", "", true, false);
            check(r.Writes, "secdeep: synchronize is a write");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("user", "importFromServer", "alice", "", "", "", "", true, false)), "secdeep: import without credentials refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("user", "importFromServer", "alice", "", "", "umcadmin", "", true, false)), "secdeep: user name without password refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("user", "activate", "alice", "", "", "umcadmin", "pw", true, false)), "secdeep: credentials on a local action refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("user", "createOffline", "", "", "", "", "", true, false)), "secdeep: createOffline without name refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("user", "rename", "alice", "alice", "", "", "", true, false)), "secdeep: rename to the same name refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("user", "activate", "alice", "x", "", "", "", true, false)), "secdeep: newName outside rename refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("user", "assignRole", "alice", "", "", "", "", true, false)), "secdeep: assignRole without roleName refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("user", "delete", "alice", "", "", "", "", false, false)), "secdeep: real delete without confirmChange refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("server", "read", "x", "", "", "", "", false, true)), "secdeep: name on kind=server refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("server", "createOffline", "", "", "", "", "", true, false)), "secdeep: member action on kind=server refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateUmcRequest("role", "read", "", "", "", "", "", false, true)), "secdeep: unknown kind refused");
            check(SecurityDeepLogic.UmcMemberActions.Length == 9 && SecurityDeepLogic.UmcServerActions.SequenceEqual(new[] { "read", "checkConsistency", "synchronize" }), "secdeep: umc action catalogs");

            // ---- certificate templates ----
            var sans = SecurityDeepLogic.ParseSubjectAlternativeNames("[{\"type\":\"Dns\",\"value\":\"plc.local\"},{\"type\":\"IP\",\"value\":\"192.168.0.10\"}]");
            check(sans.Length == 2 && sans[0].Type == "Dns" && sans[1].Value == "192.168.0.10", "secdeep: subject alternative names parsed");
            check(SecurityDeepLogic.ParseSubjectAlternativeNames("").Length == 0 && SecurityDeepLogic.ParseSubjectAlternativeNames("[]").Length == 0, "secdeep: empty SAN list");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ParseSubjectAlternativeNames("[{\"type\":\"Mac\",\"value\":\"x\"}]")), "secdeep: unknown SAN type refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ParseSubjectAlternativeNames("[{\"type\":\"Dns\",\"value\":\"a\"},{\"type\":\"Dns\",\"value\":\"A\"}]")), "secdeep: duplicate SAN refused (case-insensitive)");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ParseSubjectAlternativeNames("[{\"type\":\"Dns\"}]")), "secdeep: SAN without value refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ParseSubjectAlternativeNames("{\"type\":\"Dns\",\"value\":\"x\"}")), "secdeep: SAN object instead of array refused");
            SecurityDeepLogic.ValidateTemplateProperties(O("{\"Signature\":\"Sha256RSA\",\"SubjectCommonName\":\"plc\",\"Usage\":\"Tls\",\"ValidFrom\":\"2026-01-01\",\"ValidUntil\":\"2036-01-01T00:00:00\"}"));
            check(true, "secdeep: template properties accepted");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateTemplateProperties(O("{\"Signature\":\"Md5RSA\"}"))), "secdeep: unknown signature algorithm refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateTemplateProperties(O("{\"Usage\":\"None\"}"))), "secdeep: usage None refused (officially not supported)");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateTemplateProperties(O("{\"ValidFrom\":\"2030-01-01\",\"ValidUntil\":\"2029-01-01\"}"))), "secdeep: ValidUntil before ValidFrom refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateTemplateProperties(O("{\"ValidUntil\":\"next year\"}"))), "secdeep: non-ISO date refused");
            check(Fails<ArgumentException>(() => SecurityDeepLogic.ValidateTemplateProperties(O("{\"Issuer\":\"x\"}"))), "secdeep: unknown template property refused");
            check(SecurityDeepLogic.CertificateUsages.Length == 5 && SecurityDeepLogic.SignatureAlgorithms.SequenceEqual(new[] { "Sha1RSA", "Sha256RSA" }) && SecurityDeepLogic.SubjectAlternativeNameTypes.SequenceEqual(new[] { "Dns", "Email", "IP", "Uri" }), "secdeep: certificate catalogs");

            // ---- anonymous user actions (ProjectSecurityLogic) ----
            var anonymous = ProjectSecurityLogic.ValidateUserManagement("activateAnonymousUser", "", "", "", "", "", "[]");
            check(anonymous.Target == "anonymousUser" && anonymous.NoName && !anonymous.NeedsPassword, "secdeep: activateAnonymousUser takes no name");
            check(ProjectSecurityLogic.ValidateUserManagement("deactivateAnonymousUser", "", "", "", "", "", "[]").NoName, "secdeep: deactivateAnonymousUser takes no name");
            check(Fails<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("activateAnonymousUser", "Anonymous", "", "", "", "", "[]")), "secdeep: anonymous action with a name refused");
            check(Fails<ArgumentException>(() => ProjectSecurityLogic.ValidateUserManagement("activateAnonymousUser", "", "pw", "", "", "", "[]")), "secdeep: anonymous action with a password refused");
            check(ProjectSecurityLogic.UserActionNames.Count() == 17, "secdeep: 17 project user-management actions (15 + anonymous activate/deactivate)");
        }
    }
}
