using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.Siemens;
using static TiaMcpServer.ModelContextProtocol.McpServer;

namespace TiaMcpServer.ModelContextProtocol
{
    [McpServerToolType]
    internal sealed class HostMetaTools
    {
        [McpServerTool(Name = "RunOnlineMonitoringSafetySelfTest"), Description("[L0][Diagnostics]Run a static, read-only safety self-test for online monitoring guardrails. It does not connect to TIA Portal, open projects, modify watch tables, write PLC values, or expose forced-value operations.")]
        public CallToolResult RunOnlineMonitoringSafetySelfTestV4()
            => SessionToolContract.Run("RunOnlineMonitoringSafetySelfTest", false, false, () => RunOnlineMonitoringSafetySelfTest());

        public ResponseSafetySelfTest RunOnlineMonitoringSafetySelfTest()
        {
            var items = new List<CapabilitySelfTestItem>();

            void Add(string id, string name, bool pass, string detail)
            {
                items.Add(new CapabilitySelfTestItem
                {
                    Id = id,
                    Name = name,
                    Status = pass ? "pass" : "fail",
                    Detail = detail
                });
            }

            try
            {
                var toolNames = HostToolServices.Observe("tool-names", new JsonObject())!.AsArray().Select(n => n!.GetValue<string>());
                var toolNameList = toolNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                // Read-only getters (Get*) may legitimately reference force tables (e.g. GetPlcForceTables
                // lists names). The safety red line is that no force-EXECUTION/WRITE tool is exposed.
                // ManageWatchForceTableWebAccess only edits the offline web-server access rules of watch / force
                // tables (WatchAndForceTableAccessManager); it neither forces nor writes a value, so the name heuristic must not flag it.
                var offlineConfigurationTools = new[] { "ManageWatchForceTableWebAccess" };
                var forbiddenToolNames = toolNameList
                    .Where(x => x.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0
                             && !x.StartsWith("Get", StringComparison.OrdinalIgnoreCase)
                             && !offlineConfigurationTools.Contains(x, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                Add(
                    "safety.no-force-tools",
                    "No force-write MCP tools exposed",
                    forbiddenToolNames.Count == 0,
                    forbiddenToolNames.Count == 0
                        ? $"Checked {toolNameList.Count} MCP tools; no force-write tool exposed (read-only force-table getters allowed)."
                        : "Forbidden force-write tool names: " + string.Join(", ", forbiddenToolNames));

                var requiredTools = new[]
                {
                    "ListPlcWatchTables",
                    "ExportPlcWatchTable",
                    "ExportPlcWatchTablesToDirectory",
                    "ProbePlcMonitorOnlineCapabilities",
                    "PlanOnlineReadOnlyMonitoring",
                    "RunOnlineMonitoringSafetySelfTest"
                };
                var missingTools = requiredTools
                    .Where(x => !toolNameList.Contains(x, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                Add(
                    "safety.required-readonly-tools",
                    "Required read-only monitoring tools are present",
                    missingTools.Count == 0,
                    missingTools.Count == 0
                        ? "Read-only watch-table discovery/export/probe/self-test tools are present."
                        : "Missing required tools: " + string.Join(", ", missingTools));

                var reflection = HostToolServices.Observe("reflection", new JsonObject())!.AsObject();
                var guardMethod = (bool?)reflection["guard"] == true ? new object() : null;
                Add(
                    "safety.reflection-hard-deny",
                    "Reflection hard-deny guard exists",
                    guardMethod != null,
                    guardMethod != null
                        ? "Portal reflection bridge has a private hard-deny guard."
                        : "Portal reflection bridge hard-deny guard was not found.");

                if (guardMethod != null)
                {
                    var forceDenied = InvokeReflectionDenyGuard(guardMethod, "Block", "PLC_1/Main", "ForceValue");
                    var watchCreateDenied = InvokeReflectionDenyGuard(guardMethod, "WatchTable", "PLC_1/Watch", "Create");
                    var watchWriteDenied = InvokeReflectionDenyGuard(guardMethod, "Monitor", "PLC_1/Watch", "WriteValue");
                    var readAllowed = InvokeReflectionDenyGuard(guardMethod, "Block", "PLC_1/Main", "GetAttribute");
                    Add(
                        "safety.reflection-hard-deny-semantics",
                        "Reflection hard-deny guard blocks unsafe operations",
                        !string.IsNullOrWhiteSpace(forceDenied) &&
                        !string.IsNullOrWhiteSpace(watchCreateDenied) &&
                        !string.IsNullOrWhiteSpace(watchWriteDenied) &&
                        string.IsNullOrWhiteSpace(readAllowed),
                        $"forceDenied={DescribeGuardResult(forceDenied)}; watchCreateDenied={DescribeGuardResult(watchCreateDenied)}; watchWriteDenied={DescribeGuardResult(watchWriteDenied)}; normalReadAllowed={string.IsNullOrWhiteSpace(readAllowed)}.");
                }

                var describeService = (bool?)reflection["DescribeService"] == true ? new object() : null;
                var invokeService = (bool?)reflection["InvokeService"] == true ? new object() : null;
                var invokeObject = (bool?)reflection["InvokeObject"] == true ? new object() : null;
                Add(
                    "safety.reflection-entrypoints",
                    "Reflection entrypoints available for guarded inspection",
                    describeService != null && invokeService != null && invokeObject != null,
                    $"DescribeService={(describeService != null ? "present" : "missing")}; InvokeService={(invokeService != null ? "present" : "missing")}; InvokeObject={(invokeObject != null ? "present" : "missing")}.");

                var probeMethod = (bool?)reflection["probe"] == true ? new object() : null;
                Add(
                    "safety.online-probe-readonly",
                    "Online capability probe is discovery-only",
                    probeMethod != null,
                    probeMethod != null
                        ? "Probe method exists. It is intended for API-surface discovery only, not online transition or value writes."
                        : "ProbePlcMonitorOnlineCapabilities was not found.");

                var ok = items.All(i => i.Status == "pass");
                var policy = OnlineMonitoringPolicy.GetOnlineMonitoringSafetyPolicy();
                return new ResponseSafetySelfTest
                {
                    Ok = ok,
                    Items = items,
                    Policy = policy,
                    Message = ok ? "Online monitoring safety self-test passed" : "Online monitoring safety self-test failed",
                    Meta = ResponseMeta.Basic(DateTime.Now, ok, ("checkedTools", toolNameList.Count))
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running online monitoring safety self-test: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GenerateAcceptanceReport"), Description("[L0][Reports]Generate a read-only acceptance report for the current MCP/TIA environment. The default mode does not attach to TIA or write to the project; it writes Markdown/JSON report files to outputDirectory.")]
        public Task<CallToolResult> GenerateAcceptanceReportV4(
            [Description("Directory where the Markdown and JSON reports will be written. Empty means a temp directory under %TEMP%.")] string outputDirectory = "",
            [Description("When true, call ConnectPortal during self-test if the server is not connected. This may attach to TIA Portal.")] bool connectIfNeeded = false,
            [Description("When true, include project tree output if a project is open.")] bool includeProjectTree = false,
            [Description("When true, enumerate TIA process/project details. This may be slow if TIA is contended.")] bool inspectPortalProcesses = false,
            [Description("Optional report title.")] string title = "TIA MCP Acceptance Report")
            => SessionToolContract.RunAsync("GenerateAcceptanceReport", true, false, () => GenerateAcceptanceReport(outputDirectory, connectIfNeeded, includeProjectTree, inspectPortalProcesses, title));

        public async Task<ResponseAcceptanceReport> GenerateAcceptanceReport(
            [Description("Directory where the Markdown and JSON reports will be written. Empty means a temp directory under %TEMP%.")] string outputDirectory = "",
            [Description("When true, call ConnectPortal during self-test if the server is not connected. This may attach to TIA Portal.")] bool connectIfNeeded = false,
            [Description("When true, include project tree output if a project is open.")] bool includeProjectTree = false,
            [Description("When true, enumerate TIA process/project details. This may be slow if TIA is contended.")] bool inspectPortalProcesses = false,
            [Description("Optional report title.")] string title = "TIA MCP Acceptance Report")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    outputDirectory = TiaOpenness.Shared.DataLocations.Current.ReportsDirectory;
                }

                Directory.CreateDirectory(outputDirectory);
                var operationId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var markdownPath = Path.Combine(outputDirectory, $"tia_mcp_acceptance_{operationId}.md");
                var jsonPath = Path.Combine(outputDirectory, $"tia_mcp_acceptance_{operationId}.json");

                var selfTest = await RunCapabilitySelfTest(
                    connectIfNeeded: connectIfNeeded,
                    includeProjectTree: includeProjectTree,
                    inspectPortalProcesses: inspectPortalProcesses);
                var safetySelfTest = RunOnlineMonitoringSafetySelfTest();

                var markdown = BuildAcceptanceReportMarkdown(title, operationId, selfTest, safetySelfTest);
                File.WriteAllText(markdownPath, markdown);

                var response = new ResponseAcceptanceReport
                {
                    Ok = selfTest.Ok == true && safetySelfTest.Ok == true,
                    OperationId = operationId,
                    OutputDirectory = outputDirectory,
                    MarkdownPath = markdownPath,
                    JsonPath = jsonPath,
                    SelfTest = selfTest,
                    SafetySelfTest = safetySelfTest,
                    Message = selfTest.Ok == true && safetySelfTest.Ok == true ? "Acceptance report generated" : "Acceptance report generated with failures",
                    Meta = ResponseMeta.Basic(DateTime.Now, selfTest.Ok == true && safetySelfTest.Ok == true)
                };

                var json = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                File.WriteAllText(jsonPath, json);

                return response;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error generating acceptance report: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GenerateErrorReport"), Description("[L0][Reports]Generate a standardized Markdown/JSON error report. This is file/report generation only; it does not touch TIA Portal or modify projects.")]
        public CallToolResult GenerateErrorReportV4(
            [Description("Machine-readable error code, for example CompileError, HmiBindingError, TiaSessionContention, UnexpectedOpennessError.")] string errorCode,
            [Description("Short human-readable summary.")] string summary,
            [Description("Detailed error text, stack trace, compile message, or diagnostic context.")] string detail = "",
            [Description("Comma-separated next actions.")] string recommendedNextActions = "",
            [Description("Severity: info, warn, error, critical.")] string severity = "error",
            [Description("Directory where the Markdown and JSON reports will be written. Empty means a temp directory under %TEMP%.")] string outputDirectory = "")
            => SessionToolContract.Run("GenerateErrorReport", true, false, () => GenerateErrorReport(errorCode, summary, detail, recommendedNextActions, severity, outputDirectory));

        public ResponseErrorReport GenerateErrorReport(
            [Description("Machine-readable error code, for example CompileError, HmiBindingError, TiaSessionContention, UnexpectedOpennessError.")] string errorCode,
            [Description("Short human-readable summary.")] string summary,
            [Description("Detailed error text, stack trace, compile message, or diagnostic context.")] string detail = "",
            [Description("Comma-separated next actions.")] string recommendedNextActions = "",
            [Description("Severity: info, warn, error, critical.")] string severity = "error",
            [Description("Directory where the Markdown and JSON reports will be written. Empty means a temp directory under %TEMP%.")] string outputDirectory = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    outputDirectory = Path.Combine(TiaOpenness.Shared.DataLocations.Current.ReportsDirectory, "errors");
                }

                Directory.CreateDirectory(outputDirectory);
                var operationId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var safeCode = Regex.Replace(string.IsNullOrWhiteSpace(errorCode) ? "UnknownError" : errorCode.Trim(), @"[^A-Za-z0-9_.-]+", "_");
                var markdownPath = Path.Combine(outputDirectory, $"tia_mcp_error_{safeCode}_{operationId}.md");
                var jsonPath = Path.Combine(outputDirectory, $"tia_mcp_error_{safeCode}_{operationId}.json");
                var actions = SplitRecommendedActions(recommendedNextActions);
                if (actions.Count == 0)
                {
                    actions = GetDefaultRecommendedActions(errorCode);
                }

                var response = new ResponseErrorReport
                {
                    Ok = true,
                    OperationId = operationId,
                    ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "UnknownError" : errorCode.Trim(),
                    Severity = string.IsNullOrWhiteSpace(severity) ? "error" : severity.Trim(),
                    Summary = summary,
                    OutputDirectory = outputDirectory,
                    MarkdownPath = markdownPath,
                    JsonPath = jsonPath,
                    RecommendedNextActions = actions,
                    Message = "Error report generated",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };

                File.WriteAllText(markdownPath, BuildErrorReportMarkdown(response, detail));
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    response.OperationId,
                    response.ErrorCode,
                    response.Severity,
                    response.Summary,
                    Detail = detail,
                    response.RecommendedNextActions,
                    GeneratedAt = DateTime.Now.ToString("O")
                }, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(jsonPath, json);

                return response;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error generating error report: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        public Task<ResponseCapabilitySelfTest> RunCapabilitySelfTest(
            [Description("When true, call ConnectPortal before checks if the server is not connected. This is read-only but attaches to TIA Portal.")] bool connectIfNeeded = false,
            [Description("When true, include GetProjectTree output if a project is open.")] bool includeProjectTree = false,
            [Description("When true, enumerate TIA Portal process/project details. This may attach to running TIA processes and can be slow on a contended workstation.")] bool inspectPortalProcesses = false,
            [Description("Expected PLC software path for ValidateAutomationContext.")] string expectedPlcSoftwarePath = "PLC_1",
            [Description("Expected HMI software path for ValidateAutomationContext.")] string expectedHmiSoftwarePath = "HMI_RT_1")
        => CapabilitySelfTestLogic.Run(
                () => (bool)HostToolServices.Observe("group")!,
                () => HostToolServices.Observe("session.ListPortalProcessProjects")!.AsArray().Select(n => n!.GetValue<string>()),
                () => JsonSerializer.Deserialize<State>(HostToolServices.Observe("session.GetState")!.ToJsonString())!,
                () => (bool)HostToolServices.Observe("session.ConnectPortal")!,
                (plc, hmi) => JsonSerializer.Deserialize<ResponseMessage>(HostToolServices.Observe("session.ValidateAutomationContext", new JsonObject { ["plc"] = plc, ["hmi"] = hmi })!.ToJsonString())!,
                () => (string)HostToolServices.Observe("session.GetProjectTree")!,
                connectIfNeeded, includeProjectTree, inspectPortalProcesses, expectedPlcSoftwarePath, expectedHmiSoftwarePath);

        private static string? InvokeReflectionDenyGuard(object guardMethod, string resultKind, string resultPath, string methodName)
        {
            return (string?)HostToolServices.Observe("reflection-deny", new JsonObject { ["kind"] = resultKind, ["path"] = resultPath, ["method"] = methodName });
        }

        private static string DescribeGuardResult(string? result)
        {
            return string.IsNullOrWhiteSpace(result) ? "allow" : "deny";
        }

        private static string BuildAcceptanceReportMarkdown(string title, string operationId, ResponseCapabilitySelfTest selfTest, ResponseSafetySelfTest safetySelfTest)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# " + (string.IsNullOrWhiteSpace(title) ? "TIA MCP Acceptance Report" : title));
            sb.AppendLine();
            sb.AppendLine("- OperationId: `" + operationId + "`");
            sb.AppendLine("- GeneratedAt: `" + DateTime.Now.ToString("O") + "`");
            sb.AppendLine("- Overall: `" + (selfTest.Ok == true && safetySelfTest.Ok == true ? "PASS" : "CHECK") + "`");
            sb.AppendLine();
            sb.AppendLine("## Self Test");
            sb.AppendLine();
            sb.AppendLine("| Id | Status | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (var item in selfTest.Items ?? Array.Empty<CapabilitySelfTestItem>())
            {
                sb.AppendLine("| " + EscapeMarkdownTable(item.Id) + " | " + EscapeMarkdownTable(item.Status) + " | " + EscapeMarkdownTable(item.Detail) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("## Online Monitoring Safety");
            sb.AppendLine();
            sb.AppendLine("| Id | Status | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (var item in safetySelfTest.Items ?? Array.Empty<CapabilitySelfTestItem>())
            {
                sb.AppendLine("| " + EscapeMarkdownTable(item.Id) + " | " + EscapeMarkdownTable(item.Status) + " | " + EscapeMarkdownTable(item.Detail) + " |");
            }

            sb.AppendLine();
            sb.AppendLine("### Safety Policy");
            sb.AppendLine();
            foreach (var policy in safetySelfTest.Policy ?? Array.Empty<string>())
            {
                sb.AppendLine("- " + policy);
            }

            if (!string.IsNullOrWhiteSpace(selfTest.ProjectTree))
            {
                sb.AppendLine();
                sb.AppendLine("## Project Tree");
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine(selfTest.ProjectTree);
                sb.AppendLine("```");
            }

            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine();
            sb.AppendLine("- This report is read-only and does not prove PLC compile or HMI binding unless those checks are explicitly added to the workflow.");
            sb.AppendLine("- Treat `warn` and `skip` entries as deployment notes, not product-ready validation.");
            return sb.ToString();
        }

        private static string EscapeMarkdownTable(string? value)
        {
            return (value ?? string.Empty)
                .Replace("|", "\\|")
                .Replace("\r", " ")
                .Replace("\n", "<br>");
        }

        private static List<string> SplitRecommendedActions(string recommendedNextActions)
        {
            if (string.IsNullOrWhiteSpace(recommendedNextActions))
            {
                return new List<string>();
            }

            return recommendedNextActions
                .Split(new[] { '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private static List<string> GetDefaultRecommendedActions(string? errorCode)
        {
            switch ((errorCode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "invalidparams":
                    return new List<string> { "Check required parameters and path spelling.", "Read live project tree before retrying.", "Use a resolver tool when the target path is ambiguous." };
                case "notconnected":
                    return new List<string> { "Run ConnectPortal.", "Check TIA Portal is installed and accessible.", "Run RunCapabilitySelfTest in minimal mode." };
                case "projectnotopen":
                    return new List<string> { "AttachOpenProject or OpenProject before writing.", "Run GetSessionState and GetProjectTree.", "Avoid opening a project already opened by another session." };
                case "preconditionfailed":
                    return new List<string> { "Run the required preflight sequence.", "Read back the target object before writing.", "Use dry-run where available." };
                case "notfound":
                    return new List<string> { "Search the live project tree for the target.", "Use full qualified paths for blocks/types.", "Report candidate matches instead of guessing." };
                case "ambiguouspath":
                    return new List<string> { "List candidates and choose one exact path.", "Avoid single block names when groups may contain duplicates.", "Use GetPlcBlockHierarchy before exporting/importing blocks." };
                case "unsupportedtiaversion":
                    return new List<string> { "Confirm TIA Portal V21 is installed.", "Restart the server with --tia-major-version 21.", "Check installed Openness assemblies." };
                case "opennesspermissiondenied":
                    return new List<string> { "Add the user to Siemens TIA Openness group.", "Sign out or restart after changing group membership.", "Run TiaMcp.Engine.V21.exe doctor for the full environment report." };
                case "hardwarecatalognotfound":
                    return new List<string> { "Run SearchHardwareCatalog or SearchInstalledGsdDevices.", "Use MLFB/order number and installed catalog version.", "Do not fall back from third-party hardware to Siemens devices." };
                case "importschemaerror":
                    return new List<string> { "Validate XML is well formed.", "Compare against a same-version TIA export.", "Do not mix SCL source syntax with Openness XML syntax." };
                case "compileerror":
                    return new List<string> { "Export the failed block/type for inspection.", "Search existing tags, DBs, UDTs, and block interfaces before adding variables.", "Fix the smallest object and run CompilePlcDiagnostics again." };
                case "hmibindingerror":
                    return new List<string> { "Read HMI screens, tag tables, tags, and connections.", "Verify the HMI tag is PLC-backed, not only internal.", "Read back dynamization and button script properties after binding." };
                case "reflectionriskblocked":
                    return new List<string> { "Describe the object/service first.", "Confirm method signature and parameter types.", "Use allowWrite only after read-only discovery and backup/export." };
                case "saveblocked":
                    return new List<string> { "Compile or validate before saving.", "Review warnings/failures in the report.", "Save only after readback succeeds unless the user explicitly asks otherwise." };
                case "tiasessioncontention":
                    return new List<string> { "Do not run write-capable CLI probes in parallel with an active MCP session.", "Use the already-running MCP server or restart it cleanly.", "Stop only the probe process you launched." };
                case "unexpectedopennesserror":
                    return new List<string> { "Capture the native exception details.", "Classify the failure before retrying.", "Prefer a small sacrificial project probe before touching a real project." };
                default:
                    return new List<string> { "Capture the exact tool, parameters, and native error.", "Run readback diagnostics before retrying.", "Generate an acceptance or environment report if the failure may be machine-specific." };
            }
        }

        private static string BuildErrorReportMarkdown(ResponseErrorReport report, string detail)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# TIA MCP Error Report");
            sb.AppendLine();
            sb.AppendLine("- OperationId: `" + report.OperationId + "`");
            sb.AppendLine("- GeneratedAt: `" + DateTime.Now.ToString("O") + "`");
            sb.AppendLine("- ErrorCode: `" + report.ErrorCode + "`");
            sb.AppendLine("- Severity: `" + report.Severity + "`");
            sb.AppendLine("- Summary: " + (string.IsNullOrWhiteSpace(report.Summary) ? "(none)" : report.Summary));
            sb.AppendLine();
            sb.AppendLine("## Detail");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.AppendLine(detail ?? string.Empty);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Recommended Next Actions");
            sb.AppendLine();
            var actions = report.RecommendedNextActions?.ToList() ?? new List<string>();
            if (actions.Count == 0)
            {
                sb.AppendLine("- No recommended action was provided.");
            }
            else
            {
                foreach (var action in actions)
                {
                    sb.AppendLine("- " + action);
                }
            }
            return sb.ToString();
        }

    }
}
