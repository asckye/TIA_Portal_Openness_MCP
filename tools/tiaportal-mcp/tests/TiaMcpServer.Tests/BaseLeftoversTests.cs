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
    internal static class BaseLeftoversTests
    {
        private static bool Fails<T>(Action a) where T : Exception { try { a(); return false; } catch (T) { return true; } }
        private static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

        internal static void Run(Action<bool, string> check)
        {
            var temp = Path.GetTempPath();                       // rooted on every OS (offline-checks runs on ubuntu)
            string xml = Path.Combine(temp, "mcp-export.xml"), psc = Path.Combine(temp, "mcp-card.psc"), txt = Path.Combine(temp, "points.txt");

            // ---- hardware utilities ----
            BaseLeftoversLogic.ValidateHardwareUtilityRequest("list", "", "[]", "", "", true);
            BaseLeftoversLogic.ValidateHardwareUtilityRequest("normalizeTypeIdentifier", "OrderNumber:6ES7 510-1SK03-0AB0/V4.1", "[]", "", "", true);
            BaseLeftoversLogic.ValidateHardwareUtilityRequest("exportOpcUa", "", "[\"D\"]", xml, "", true);
            BaseLeftoversLogic.ValidateHardwareUtilityRequest("exportCardReaderPsc", "", "[\"D\"]", psc, "secret", false);
            check(true, "baseleft: hardware utility requests accepted");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateHardwareUtilityRequest("findModuleTypes", "", "[]", "", "", true)), "baseleft: findModuleTypes without typeIdentifier refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateHardwareUtilityRequest("list", "x", "[]", "", "", true)), "baseleft: typeIdentifier on list refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateHardwareUtilityRequest("exportOpcUa", "", "[\"D\"]", "relative.xml", "", true)), "baseleft: relative export path refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateHardwareUtilityRequest("exportOpcUa", "", "[\"D\"]", psc, "", true)), "baseleft: OPC UA export must be .xml");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateHardwareUtilityRequest("exportCardReaderPsc", "", "[\"D\"]", xml, "", true)), "baseleft: PSC export must be .psc");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateHardwareUtilityRequest("exportOpcUa", "", "[\"D\"]", xml, "pw", true)), "baseleft: password outside PSC export refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateHardwareUtilityRequest("exportOpcUa", "", "[]", xml, "", true)), "baseleft: export without device path refused");
            check(BaseLeftoversLogic.OpcUaExportProviderId == "OPCUAExportProvider" && BaseLeftoversLogic.HardwareUtilityActions.Length == 6, "baseleft: utility identifiers / actions (official HwUtilities.Find name)");

            // ---- device service objects ----
            BaseLeftoversLogic.ValidateServiceObjectRequest("webApplications", "read", "", O("{}"), "", false, true);
            BaseLeftoversLogic.ValidateServiceObjectRequest("webApplications", "setDefault", "Default", O("{}"), "", true, false);
            BaseLeftoversLogic.ValidateServiceObjectRequest("telecontrolDataPoints", "update", "DP1", O("{\"DataPointIndex\":5,\"MasterFunction\":true}"), "", true, false);
            BaseLeftoversLogic.ValidateServiceObjectRequest("telecontrolDataPoints", "export", "", O("{}"), txt, true, false);
            BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "update", "", O("{\"Usage\":\"Runtime\",\"RemainingCertificateLifetime\":30}"), "", true, false);
            BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "setServiceGroupName", "100", O("{\"ServiceGroupName\":\"Group_1\"}"), "", true, false);
            BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "createService", "300", O("{\"ServiceGroupName\":\"G\",\"ServiceType\":\"Webserver\"}"), "", true, false);
            BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "deleteService", "300", O("{}"), "", true, false);
            check(true, "baseleft: service object requests accepted");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("webApplications", "update", "x", O("{}"), "", true, false)), "baseleft: update is not a web application action");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("webApplications", "setDefault", "", O("{}"), "", true, false)), "baseleft: setDefault without name refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("telecontrolDataPoints", "update", "DP1", O("{\"Address\":1}"), "", true, false)), "baseleft: unknown telecontrol property refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "update", "", O("{\"Usage\":\"Cloud\"}"), "", true, false)), "baseleft: unknown certificate usage refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "update", "", O("{\"RemainingCertificateLifetime\":95}"), "", true, false)), "baseleft: lifetime outside 10..90 refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "setServiceGroupName", "1", O("{\"ServiceGroupName\":\"" + new string('x', 65) + "\"}"), "", true, false)), "baseleft: service group name above 64 chars refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "createService", "abc", O("{\"ServiceGroupName\":\"G\",\"ServiceType\":\"Webserver\"}"), "", true, false)), "baseleft: createService needs a numeric Id");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "createService", "300", O("{\"ServiceGroupName\":\"G\"}"), "", true, false)), "baseleft: createService without ServiceType refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("certificateServices", "update", "", O("{\"Usage\":\"Runtime\"}"), "", false, false)), "baseleft: real update without confirmChange refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("telecontrolDataPoints", "export", "", O("{}"), "", true, false)), "baseleft: export without file refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateServiceObjectRequest("syslog", "read", "", O("{}"), "", false, true)), "baseleft: unknown family refused");
            check(BaseLeftoversLogic.CertificateSupportedServiceNames.SequenceEqual(new[] { "None", "OpcUaClient", "OpcUaServer", "Webserver" }) && BaseLeftoversLogic.WebApplicationTypes.Length == 4, "baseleft: certificate / web application enum catalogs");

            // ---- object selection ----
            BaseLeftoversLogic.ValidateObjectSelection("device", "[\"D\"]", "[]", "", "", "");
            BaseLeftoversLogic.ValidateObjectSelection("deviceItem", "[\"D\"]", "[\"CPU\"]", "", "", "");
            BaseLeftoversLogic.ValidateObjectSelection("plcBlock", "[]", "[]", "PLC_1", "Grp/FB_1", "");
            BaseLeftoversLogic.ValidateObjectSelection("device", "[]", "[]", "", "", "some-identifier");
            check(true, "baseleft: object selections accepted (identifier skips the path checks)");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateObjectSelection("deviceItem", "[\"D\"]", "[]", "", "", "")), "baseleft: deviceItem without itemPathJson refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateObjectSelection("plcBlock", "[]", "[]", "PLC_1", "", "")), "baseleft: plcBlock without objectPath refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateObjectSelection("screen", "[]", "[]", "", "", "")), "baseleft: unknown object kind refused");

            // ---- transactions ----
            var calls = BaseLeftoversLogic.ParseToolCalls("[{\"name\":\"ManageSyslogServers\",\"arguments\":{\"scope\":\"plc\",\"dryRun\":true}},{\"name\":\"ManagePasswordPolicy\"}]");
            check(calls.Length == 2 && calls[1].ArgumentsJson == "{}" && calls[0].ArgumentsJson.Contains("\"scope\""), "baseleft: tool calls parsed (missing arguments = {})");
            check(BaseLeftoversLogic.ForceRealExecution(calls[0].ArgumentsJson).Contains("\"dryRun\":false") && BaseLeftoversLogic.ForceRealExecution("{}") == "{\"dryRun\":false}" && BaseLeftoversLogic.ForceRealExecution("{\"DryRun\":true}") == "{\"DryRun\":false}", "baseleft: inner dryRun forced to false inside a transaction, added when absent (2.7.33 real-project defect)");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ParseToolCalls("[]")), "baseleft: empty call list refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ParseToolCalls("[{\"name\":\"CallTool\"}]")), "baseleft: CallTool inside a transaction refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ParseToolCalls("[{\"name\":\"RunToolsInTransaction\"}]")), "baseleft: nested transaction refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ParseToolCalls("[{\"name\":\"X\",\"arguments\":[]}]")), "baseleft: array arguments refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ParseToolCalls("{\"name\":\"X\"}")), "baseleft: object instead of array refused");
            BaseLeftoversLogic.ValidateTransactionRequest("MCP edit", calls, true, false);
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateTransactionRequest("", calls, true, false)), "baseleft: empty undo text refused (TIA refuses it too)");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateTransactionRequest("t", calls, false, false)), "baseleft: real transaction without confirmChange refused");

            // ---- credentials / R/H ----
            BaseLeftoversLogic.ValidateUmacCredentials("", "", ""); BaseLeftoversLogic.ValidateUmacCredentials("admin", "pw", ""); BaseLeftoversLogic.ValidateUmacCredentials("admin", "pw", "Global");
            check(true, "baseleft: UMAC credentials accepted");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateUmacCredentials("admin", "", "")), "baseleft: UMAC user without password refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateUmacCredentials("", "", "Project")), "baseleft: UMAC type without credentials refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateUmacCredentials("admin", "pw", "Local")), "baseleft: unknown UMAC user type refused");
            BaseLeftoversLogic.ValidateOnlineCredentials("", "", ""); BaseLeftoversLogic.ValidateOnlineCredentials("", "pw", ""); BaseLeftoversLogic.ValidateOnlineCredentials("user", "pw", "ProjectUser"); BaseLeftoversLogic.ValidateOnlineCredentials("", "pw", "PasswordOnly");
            check(true, "baseleft: online credentials accepted");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateOnlineCredentials("user", "", "")), "baseleft: online user without password refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateOnlineCredentials("", "pw", "GlobalUser")), "baseleft: GlobalUser type without user name refused");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateOnlineCredentials("user", "pw", "Admin")), "baseleft: unknown online user type refused");
            check(BaseLeftoversLogic.ValidateRhTarget("") == "" && BaseLeftoversLogic.ValidateRhTarget("primary") == "primary" && BaseLeftoversLogic.ValidateRhTarget("backup") == "backup", "baseleft: R/H targets");
            check(Fails<ArgumentException>(() => BaseLeftoversLogic.ValidateRhTarget("both")), "baseleft: unknown R/H target refused");
            check(BaseLeftoversLogic.OnlineUserTypes.Length == 6 && BaseLeftoversLogic.UmacUserTypes.SequenceEqual(new[] { "Project", "Global" }), "baseleft: user type catalogs");
        }
    }
}
