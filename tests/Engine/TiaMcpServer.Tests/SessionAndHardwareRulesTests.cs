using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Tests
{
    // Phase 3 sub-batch 4 (2.7.33) pure logic: hardware-utility requests, device service object requests (web applications /
    // telecontrol data points / certificate services), object selection, transaction call lists, UMAC / online credentials
    // and R/H targets.
    internal static class SessionAndHardwareRulesTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        private static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string xml = Path.Combine(temp, "mcp-export.xml"), psc = Path.Combine(temp, "mcp-card.psc"), txt = Path.Combine(temp, "points.txt");

            // ---- hardware utilities ----
            HardwareUtilityRules.ValidateHardwareUtilityRequest("list", "", "[]", "", "", true);
            HardwareUtilityRules.ValidateHardwareUtilityRequest("normalizeTypeIdentifier", "OrderNumber:6ES7 510-1SK03-0AB0/V4.1", "[]", "", "", true);
            HardwareUtilityRules.ValidateHardwareUtilityRequest("exportOpcUa", "", "[\"D\"]", xml, "", true);
            HardwareUtilityRules.ValidateHardwareUtilityRequest("exportCardReaderPsc", "", "[\"D\"]", psc, "secret", false);
            check(true, "baseleft: hardware utility requests accepted");
            check(Fails<ArgumentException>(() => HardwareUtilityRules.ValidateHardwareUtilityRequest("findModuleTypes", "", "[]", "", "", true)), "baseleft: findModuleTypes without typeIdentifier refused");
            check(Fails<ArgumentException>(() => HardwareUtilityRules.ValidateHardwareUtilityRequest("list", "x", "[]", "", "", true)), "baseleft: typeIdentifier on list refused");
            check(Fails<ArgumentException>(() => HardwareUtilityRules.ValidateHardwareUtilityRequest("exportOpcUa", "", "[\"D\"]", "relative.xml", "", true)), "baseleft: relative export path refused");
            check(Fails<ArgumentException>(() => HardwareUtilityRules.ValidateHardwareUtilityRequest("exportOpcUa", "", "[\"D\"]", psc, "", true)), "baseleft: OPC UA export must be .xml");
            check(Fails<ArgumentException>(() => HardwareUtilityRules.ValidateHardwareUtilityRequest("exportCardReaderPsc", "", "[\"D\"]", xml, "", true)), "baseleft: PSC export must be .psc");
            check(Fails<ArgumentException>(() => HardwareUtilityRules.ValidateHardwareUtilityRequest("exportOpcUa", "", "[\"D\"]", xml, "pw", true)), "baseleft: password outside PSC export refused");
            check(Fails<ArgumentException>(() => HardwareUtilityRules.ValidateHardwareUtilityRequest("exportOpcUa", "", "[]", xml, "", true)), "baseleft: export without device path refused");
            check(HardwareUtilityRules.OpcUaExportProviderId == "OPCUAExportProvider" && HardwareUtilityRules.HardwareUtilityActions.Length == 6, "baseleft: utility identifiers / actions (official HwUtilities.Find name)");

            // ---- device service objects ----
            DeviceServiceObjectRules.ValidateServiceObjectRequest("webApplications", "read", "", O("{}"), "", false, true);
            DeviceServiceObjectRules.ValidateServiceObjectRequest("webApplications", "setDefault", "Default", O("{}"), "", true, false);
            DeviceServiceObjectRules.ValidateServiceObjectRequest("telecontrolDataPoints", "update", "DP1", O("{\"DataPointIndex\":5,\"MasterFunction\":true}"), "", true, false);
            DeviceServiceObjectRules.ValidateServiceObjectRequest("telecontrolDataPoints", "export", "", O("{}"), txt, true, false);
            DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "update", "", O("{\"Usage\":\"Runtime\",\"RemainingCertificateLifetime\":30}"), "", true, false);
            DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "setServiceGroupName", "100", O("{\"ServiceGroupName\":\"Group_1\"}"), "", true, false);
            DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "createService", "300", O("{\"ServiceGroupName\":\"G\",\"ServiceType\":\"Webserver\"}"), "", true, false);
            DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "deleteService", "300", O("{}"), "", true, false);
            check(true, "baseleft: service object requests accepted");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("webApplications", "update", "x", O("{}"), "", true, false)), "baseleft: update is not a web application action");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("webApplications", "setDefault", "", O("{}"), "", true, false)), "baseleft: setDefault without name refused");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("telecontrolDataPoints", "update", "DP1", O("{\"Address\":1}"), "", true, false)), "baseleft: unknown telecontrol property refused");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "update", "", O("{\"Usage\":\"Cloud\"}"), "", true, false)), "baseleft: unknown certificate usage refused");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "update", "", O("{\"RemainingCertificateLifetime\":95}"), "", true, false)), "baseleft: lifetime outside 10..90 refused");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "setServiceGroupName", "1", O("{\"ServiceGroupName\":\"" + new string('x', 65) + "\"}"), "", true, false)), "baseleft: service group name above 64 chars refused");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "createService", "abc", O("{\"ServiceGroupName\":\"G\",\"ServiceType\":\"Webserver\"}"), "", true, false)), "baseleft: createService needs a numeric Id");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "createService", "300", O("{\"ServiceGroupName\":\"G\"}"), "", true, false)), "baseleft: createService without ServiceType refused");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("certificateServices", "update", "", O("{\"Usage\":\"Runtime\"}"), "", false, false)), "baseleft: real update without confirmChange refused");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("telecontrolDataPoints", "export", "", O("{}"), "", true, false)), "baseleft: export without file refused");
            check(Fails<ArgumentException>(() => DeviceServiceObjectRules.ValidateServiceObjectRequest("syslog", "read", "", O("{}"), "", false, true)), "baseleft: unknown family refused");
            check(DeviceServiceObjectRules.CertificateSupportedServiceNames.SequenceEqual(new[] { "None", "OpcUaClient", "OpcUaServer", "Webserver" }) && DeviceServiceObjectRules.WebApplicationTypes.Length == 4, "baseleft: certificate / web application enum catalogs");

            // ---- object selection ----
            ObjectIdentityRules.ValidateObjectSelection("device", "[\"D\"]", "[]", "", "", "");
            ObjectIdentityRules.ValidateObjectSelection("deviceItem", "[\"D\"]", "[\"CPU\"]", "", "", "");
            ObjectIdentityRules.ValidateObjectSelection("plcBlock", "[]", "[]", "PLC_1", "Grp/FB_1", "");
            ObjectIdentityRules.ValidateObjectSelection("device", "[]", "[]", "", "", "some-identifier");
            check(true, "baseleft: object selections accepted (identifier skips the path checks)");
            check(Fails<ArgumentException>(() => ObjectIdentityRules.ValidateObjectSelection("deviceItem", "[\"D\"]", "[]", "", "", "")), "baseleft: deviceItem without itemPathJson refused");
            check(Fails<ArgumentException>(() => ObjectIdentityRules.ValidateObjectSelection("plcBlock", "[]", "[]", "PLC_1", "", "")), "baseleft: plcBlock without objectPath refused");
            check(Fails<ArgumentException>(() => ObjectIdentityRules.ValidateObjectSelection("screen", "[]", "[]", "", "", "")), "baseleft: unknown object kind refused");

            // ---- transactions ----
            var calls = ToolTransactionRules.ParseToolCalls("[{\"name\":\"ManageSyslogServers\",\"arguments\":{\"scope\":\"plc\",\"dryRun\":true}},{\"name\":\"ManagePasswordPolicy\"}]");
            check(calls.Length == 2 && calls[1].ArgumentsJson == "{}" && calls[0].ArgumentsJson.Contains("\"scope\""), "baseleft: tool calls parsed (missing arguments = {})");
            check(ToolTransactionRules.ForceRealExecution(calls[0].ArgumentsJson).Contains("\"dryRun\":false") && ToolTransactionRules.ForceRealExecution("{}") == "{\"dryRun\":false}" && ToolTransactionRules.ForceRealExecution("{\"DryRun\":true}") == "{\"DryRun\":false}", "baseleft: inner dryRun forced to false inside a transaction, added when absent (2.7.33 real-project defect)");
            check(Fails<ArgumentException>(() => ToolTransactionRules.ParseToolCalls("[]")), "baseleft: empty call list refused");
            check(Fails<ArgumentException>(() => ToolTransactionRules.ParseToolCalls("[{\"name\":\"CallTool\"}]")), "baseleft: CallTool inside a transaction refused");
            check(Fails<ArgumentException>(() => ToolTransactionRules.ParseToolCalls("[{\"name\":\"RunToolTransaction\"}]")), "baseleft: nested transaction refused");
            check(Fails<ArgumentException>(() => ToolTransactionRules.ParseToolCalls("[{\"name\":\"X\",\"arguments\":[]}]")), "baseleft: array arguments refused");
            check(Fails<ArgumentException>(() => ToolTransactionRules.ParseToolCalls("{\"name\":\"X\"}")), "baseleft: object instead of array refused");
            ToolTransactionRules.ValidateTransactionRequest("MCP edit", calls, true, false);
            check(Fails<ArgumentException>(() => ToolTransactionRules.ValidateTransactionRequest("", calls, true, false)), "baseleft: empty undo text refused (TIA refuses it too)");
            check(Fails<ArgumentException>(() => ToolTransactionRules.ValidateTransactionRequest("t", calls, false, false)), "baseleft: real transaction without confirmChange refused");

            // ---- credentials / R/H ----
            EngineeringCredentialRules.ValidateUmacCredentials("", "", ""); EngineeringCredentialRules.ValidateUmacCredentials("admin", "pw", ""); EngineeringCredentialRules.ValidateUmacCredentials("admin", "pw", "Global");
            check(true, "baseleft: UMAC credentials accepted");
            check(Fails<ArgumentException>(() => EngineeringCredentialRules.ValidateUmacCredentials("admin", "", "")), "baseleft: UMAC user without password refused");
            check(Fails<ArgumentException>(() => EngineeringCredentialRules.ValidateUmacCredentials("", "", "Project")), "baseleft: UMAC type without credentials refused");
            check(Fails<ArgumentException>(() => EngineeringCredentialRules.ValidateUmacCredentials("admin", "pw", "Local")), "baseleft: unknown UMAC user type refused");
            EngineeringCredentialRules.ValidateOnlineCredentials("", "", ""); EngineeringCredentialRules.ValidateOnlineCredentials("", "pw", ""); EngineeringCredentialRules.ValidateOnlineCredentials("user", "pw", "ProjectUser"); EngineeringCredentialRules.ValidateOnlineCredentials("", "pw", "PasswordOnly");
            check(true, "baseleft: online credentials accepted");
            check(Fails<ArgumentException>(() => EngineeringCredentialRules.ValidateOnlineCredentials("user", "", "")), "baseleft: online user without password refused");
            // 2.7.56 Connect with several TIA processes: attach by project name / first with a project / first attachable; never spawn
            var c1 = new ConnectLogic.Candidate { ProcessId = 15100, Attached = true, ProjectNames = { "AutomaticDipCoatingMachine" } };
            var c2 = new ConnectLogic.Candidate { ProcessId = 4840, Attached = true, ProjectNames = { "项目1" } };
            var c3 = new ConnectLogic.Candidate { ProcessId = 15748, Attached = true };
            var c4 = new ConnectLogic.Candidate { ProcessId = 9, Attached = false, Failure = "attach did not answer within 20000 ms" };
            check(ConnectLogic.Choose(new[] { c1, c2, c3 }, "项目1")!.ProcessId == 4840, "connect: the process holding the wanted project wins");
            check(ConnectLogic.Choose(new[] { c3, c1, c2 }, null)!.ProcessId == 15100, "connect: without a name the first process with a project wins over an empty one");
            check(ConnectLogic.Choose(new[] { c3 }, "项目1")!.ProcessId == 15748, "connect: an empty process is still bound when nothing holds the wanted project (warning, not refusal)");
            check(ConnectLogic.Choose(new[] { c4 }, null) == null, "connect: nothing attachable -> null (caller refuses instead of spawning)");
            check(ConnectLogic.Refusal(new[] { c4 }, "项目1").Contains("No new instance was started") && ConnectLogic.Refusal(new[] { c4 }, "项目1").Contains("PID 9") && ConnectLogic.Refusal(new[] { c4 }, "项目1").Contains("allowStart=true"), "connect: refusal names the processes, the missing project and the allowStart escape hatch");
            check(ConnectLogic.MissingProjectWarning(new[] { c1, c3 }, "项目1", c1).Contains("bound PID 15100"), "connect: missing-project warning names the bound process");
            check(ConnectLogic.AttachTimeoutMsPerProcess * 2 <= ConnectLogic.AttachTotalBudgetMs + 1 && ConnectLogic.AttachTotalBudgetMs < 60000, "connect: two attach waits fit the 45 s budget, the budget stays under the client's 60 s");

            // 2.7.52 TLS trust decision for FW >= 2.9 PLCs (TlsVerificationConfiguration on OnlineLegitimation)
            check(EngineeringCredentialRules.TlsSelectionToApply(true, "NonVerified") == "Trusted" && EngineeringCredentialRules.TlsSelectionToApply(true, "NonTrusted") == "Trusted", "baseleft: unverified / untrusted certificate is answered Trusted when the caller allows it");
            check(EngineeringCredentialRules.TlsSelectionToApply(true, "Trusted") == null && EngineeringCredentialRules.TlsSelectionToApply(false, "NonVerified") == null, "baseleft: already trusted or trust refused -> nothing applied");
            check(Fails<ArgumentException>(() => EngineeringCredentialRules.ValidateOnlineCredentials("", "pw", "GlobalUser")), "baseleft: GlobalUser type without user name refused");
            check(Fails<ArgumentException>(() => EngineeringCredentialRules.ValidateOnlineCredentials("user", "pw", "Admin")), "baseleft: unknown online user type refused");
            check(EngineeringCredentialRules.ValidateRhTarget("") == "" && EngineeringCredentialRules.ValidateRhTarget("primary") == "primary" && EngineeringCredentialRules.ValidateRhTarget("backup") == "backup", "baseleft: R/H targets");
            check(Fails<ArgumentException>(() => EngineeringCredentialRules.ValidateRhTarget("both")), "baseleft: unknown R/H target refused");
            check(EngineeringCredentialRules.OnlineUserTypes.Length == 6 && EngineeringCredentialRules.UmacUserTypes.SequenceEqual(new[] { "Project", "Global" }), "baseleft: user type catalogs");
        }
    }
}
