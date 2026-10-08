using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Domain;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcpServer.Siemens.Services;
namespace TiaMcpServer.ModelContextProtocol
{
    // Optional-package services retain the native calls and record typed failure evidence.
    internal static class OptionalPackageContract
    {
        private static bool? Flag(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;

        internal static bool Issued(JsonObject data) => Flag(data, "mayHaveChanged") == true
            || Flag(data, "mayHaveWrittenFiles") == true || Flag(data, "mayHaveExternalEffects") == true
            || Flag(data, "operationStarted") == true;

        internal static void RecordFailure(JsonObject data, Exception exception)
        {
            var cause = exception.GetBaseException();
            data["failureType"] = cause.GetType().Name;
            if (Issued(data)) return;
            data["operationNotStarted"] = true;
            string? code = cause is PortalException portal ? portal.Code switch
            {
                PortalErrorCode.InvalidParams => "INVALID_ARGUMENT",
                PortalErrorCode.NotFound => "NOT_FOUND",
                PortalErrorCode.NotSupportedOnVersion => "UNSUPPORTED_CAPABILITY",
                PortalErrorCode.InvalidState => "PRECONDITION_FAILED",
                _ => null
            } : cause is ArgumentException ? "INVALID_ARGUMENT"
                : cause is NotSupportedException ? "UNSUPPORTED_CAPABILITY"
                : cause is FileNotFoundException || cause is DirectoryNotFoundException ? "NOT_FOUND"
                : cause is IOException ? "IO_FAILED" : cause is InvalidOperationException ? "PRECONDITION_FAILED" : null;
            if (code != null) data["rejectionCode"] = code;
        }

        internal static CallToolResult Run(string tool, bool writes, Func<ResponseMessage> operation, int? offset = null, int? limit = null)
        {
            try { return Map(tool, operation(), writes, offset, limit); }
            catch (Exception ex)
            {
                var evidence = new JsonObject();
                RecordFailure(evidence, ex);
                return Map(tool, new ResponseMessage { Meta = evidence }, writes, offset, limit);
            }
        }

        internal static CallToolResult Map(string tool, ResponseMessage response, bool writes, int? offset = null, int? limit = null)
        {
            var data = response.Meta?.DeepClone().AsObject() ?? new JsonObject();
            bool issued = Issued(data);
            bool reset = Flag(data, "requiresExplicitRebind") == true;
            bool? success = Flag(data, "operationSuccess") ?? Flag(data, "success");
            string status = data["status"]?.ToString() ?? "";
            string? rejection = data["rejectionCode"]?.ToString();
            if (!issued && status == "InvalidState" && !data.ContainsKey("apiCallSuccess")) rejection = "PROJECT_NOT_BOUND";
            if (status == "HmiReadSessionBlocked") rejection = "SESSION_RESET_REQUIRED";
            bool nativeKnown = Flag(data, "nativeResult").HasValue;
            if (Flag(data, "nativeResult") == false) success = false;
            bool stateRequired = (tool == "RunTestSuiteCase" || tool == "ExchangePlcSupervisions") && writes && rejection == null;
            string? state = data["nativeState"]?.ToString();
            bool stateKnown = new[] { "Success", "Information", "Info", "Warning", "Error", "Failed", "Failure" }.Contains(state);
            if (stateRequired || data.ContainsKey("nativeState"))
            {
                nativeKnown |= stateKnown;
                success = stateKnown && state != "Error" && state != "Failed" && state != "Failure" && success != false;
            }
            bool verificationFailed = Flag(data, "expectedPresenceVerified") == false || Flag(data, "verifiedAbsent") == false;
            if (verificationFailed) success = false;
            bool notStarted = Flag(data, "operationNotStarted") == true;
            bool unknown = rejection == null && writes && !notStarted && (stateRequired && !stateKnown
                || reset && issued || success != true && !nativeKnown && !verificationFailed);
            int imported = (data["imported"] as JsonArray)?.Count ?? 0;
            bool knownPartial = verificationFailed && imported > 0
                || success == false && nativeKnown && (data["file"] is JsonObject || data["output"] is JsonObject);
            Outcome outcome = unknown ? Outcome.Unknown : rejection != null ? Outcome.RejectedBeforeOperation
                : success == true ? Outcome.Succeeded : knownPartial ? Outcome.Partial
                : writes && nativeKnown || verificationFailed ? Outcome.Failed : writes && !notStarted ? Outcome.Unknown : Outcome.ReadFailed;
            Error? error = outcome == Outcome.Succeeded ? null : new Error("The optional-package operation did not establish success.",
                outcome == Outcome.Unknown ? new OutcomeUnknownDetails("optional-package-operation", new Dictionary<string, JsonElement>())
                : outcome == Outcome.Partial ? new PartialFailureDetails(imported, imported > 0 ? 1 : 0, 0)
                : outcome == Outcome.RejectedBeforeOperation ? Rejection(rejection!, tool)
                : new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
            bool incomplete = Incomplete(data) || tool == "ExchangeCfcCharts" && (data["action"]?.ToString() == "import" || data["action"]?.ToString() == "export")
                || data.ContainsKey("contentVerified") && Flag(data, "contentVerified") != true;
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete || outcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete;
            Paging? paging = null;
            if (offset.HasValue && limit.HasValue && data["expectedCount"] is JsonValue count && count.TryGetValue<int>(out var total)
                && offset >= 0 && limit > 0) paging = McpServer.OffsetPage(offset.Value, limit.Value, total);
            Clean(data);
            if (outcome == Outcome.Succeeded) data["summary"] = response.Message;
            if (data["records"] is JsonArray records) { data.Remove("records"); data["items"] = records; }
            var warnings = new List<Warning> { new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()) };
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData,
                "Only the reported fields and native evidence were observed; content semantics are not asserted.", new Dictionary<string, JsonElement>()));
            if (state == "Warning") warnings.Add(new Warning(WarningCode.NativeWarning,
                "The native operation returned a warning state.", new Dictionary<string, JsonElement>()));
            Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, reset || outcome == Outcome.Unknown, BehaviorPolicy.Current, completeness, paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }

        private static ErrorDetails Rejection(string code, string tool) => code switch
        {
            "PROJECT_NOT_BOUND" => new ProjectNotBoundDetails(),
            "SESSION_RESET_REQUIRED" => new SessionResetRequiredDetails("optional-package-session"),
            "INVALID_ARGUMENT" => new InvalidArgumentDetails("arguments", Array.Empty<string>()),
            "UNSUPPORTED_CAPABILITY" => new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null),
            "NOT_FOUND" => new NotFoundDetails(null),
            "IO_FAILED" => new IoFailedDetails("optional-package-file", null),
            _ => new PreconditionFailedDetails("optional-package-operation", null)
        };

        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            return Flag(obj, "dataComplete") == false || Flag(obj, "truncated") == true
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) || Incomplete(p.Value));
        }

        private static void Clean(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Clean(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "timestamp" || pair.Key == "tool" || pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure"
                    || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0) obj.Remove(pair.Key);
                else if (pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal)) obj[pair.Key] = "Native diagnostic details are retained in the server log.";
                else Clean(pair.Value);
            }
        }
    }

    // Typed CFC option package (PlcSoftware.GetService<ChartProviderS7>; identical on V20 / V21).
    [McpServerToolType]
    internal sealed class CfcTools
    {
        private readonly CfcService _cfc;

        public CfcTools(CfcService cfc) => _cfc = cfc;

        [McpServerTool(Name="ExchangeCfcCharts"), Description("[L2][PLC-Software][WRITE] Typed CFC chart exchange of one PLC software (ChartProviderS7): export (ChartProvider.CompleteExport(path, modelVersion e.g. V2.0, filter, unattended) - every chart with block types, task assignment and run sequence, written as XML packed in a ZIP such as Chart1.xml.zip), selectiveExport (SelectiveExport(path, chartNames [..], modelVersion, filter, unattended) - the named charts only), import (Import(path, modelVersion, filter, unattended, deleteAtTarget) - the ZIP from a complete or selective export; deleteAtTarget removes charts missing from the file), exportInstructionData (ChartProviderS7.ExportInstructionData(path)). Official requirements: PLC offline (import requires the confirmed Offline state), password-protected charts are skipped by the exports, the imported block types must exist and be compiled. Output files are new and verified by size / SHA-256; export additionally reports the chart inventory parsed from the ZIP. selectiveExport and exportInstructionData first run a CompleteExport preflight into a temporary ZIP and are refused when the PLC has no charts or a named chart is missing (2.7.42 real project, CPU 1510SP F with CFC installed but no chart folder: ExportInstructionData took TIA Portal V21 down; skipChartPreflight=true bypasses the check at that risk). Default preview; no save / compile / download. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ExchangeCfcCharts(
            string softwarePath,
            [Description("action: the operation to perform - export | selectiveExport | import | exportInstructionData.")] string action,
            [Description("filePath: absolute ZIP containing the native chart XML (e.g. Charts.xml.zip); exportInstructionData writes a plain file. New output or existing import file.")] string filePath,
            [Description("modelVersion: CFC data-model version string, e.g. 'V2.0'.")] string modelVersion="",
            [Description("filter: filter text ('' = all).")] long filter=0,
            [Description("unattended: true answers CFC prompts automatically.")] bool unattended=true,
            [Description("deleteAtTarget: true deletes charts at the target that the import does not contain.")] bool deleteAtTarget=false,
            bool dryRun=true,
            [Description("chartNames: Array of chart paths.")] string[] chartNames = null!,
            [Description("skipChartPreflight: true skips the chart-folder preflight (only when the PLC is known to have charts).")] bool skipChartPreflight=false)
            => OptionalPackageContract.Run("ExchangeCfcCharts", !dryRun, () => _cfc.ExchangeCfcCharts(softwarePath,action,filePath,modelVersion,filter,unattended,deleteAtTarget,dryRun,V4Json.Serialize(chartNames ?? Array.Empty<string>()),skipChartPreflight));
        [McpServerTool(Name="ManageCfcChartProtection"), Description("[L2][PLC-Software][WRITE] CFC chart password of one chart (ChartProviderS7 on the PLC software): read (GetChartProtection -> password hash, empty = unprotected), add (AddChartProtection(chartName, newHashedPassword - the hash as TIA displays it)), change (ChangeChartProtection(chartName, currentPassword as SecureString, newHashedPassword)), remove (RemoveChartProtection(chartName, currentPassword)). Official: the password only protects against unintentional editing (no know-how protection); native bool reported and the hash read back. Passwords are never logged. Every action first verifies the chart through a CompleteExport preflight (modelVersion, default V2.0) and is refused when the PLC has no charts or the name is unknown - GetChartProtection of an unknown chart took TIA Portal V21 down on the 2.7.42 real project (skipChartPreflight=true bypasses the check at that risk). Default preview; no automatic save. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ManageCfcChartProtection(
            string softwarePath,
            [Description("chartName: chart path 'Root/Sub' (exact names).")] string chartName,
            [Description("action: the operation to perform - read | add | change | remove.")] string action="read",
            [Description("currentPassword: the password currently set; never logged.")] string currentPassword="",
            [Description("newHashedPassword: the new password value as the CFC API expects it; never logged.")] string newHashedPassword="",
            bool dryRun=true,
            [Description("modelVersion: CFC data-model version string, e.g. 'V2.0'.")] string modelVersion="V2.0",
            [Description("skipChartPreflight: true skips the chart-folder preflight (only when the PLC is known to have charts).")] bool skipChartPreflight=false)
            => OptionalPackageContract.Run("ManageCfcChartProtection", !dryRun && action != "read", () => _cfc.ManageCfcChartProtection(softwarePath,chartName,action,currentPassword,newHashedPassword,dryRun,modelVersion,skipChartPreflight));
    }
}
