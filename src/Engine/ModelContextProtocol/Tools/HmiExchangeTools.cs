using TiaMcpServer.Siemens.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    // Interpret this family's execution evidence without inferring success from message text.
    internal static class HmiExchangeContract
    {
        internal static CallToolResult Run(string tool, bool writes, bool current, Func<object> operation)
        {
            try { return Map(tool, operation(), writes, current); }
            catch (Exception ex)
            {
                Exception cause = ex;
                while (cause.InnerException != null) cause = cause.InnerException;
                var evidence = new JsonObject { ["exceptionType"] = cause.GetType().Name };
                var rejection = cause is PortalException portal ? Rejection(portal.Code.ToString()) : null;
                var outcome = rejection != null ? Outcome.RejectedBeforeOperation : writes ? Outcome.Unknown : Outcome.ReadFailed;
                return Result(tool, new JsonObject { ["evidence"] = evidence }, rejection ?? Failure(outcome, evidence),
                    outcome, outcome == Outcome.RejectedBeforeOperation ? Completeness.None : Completeness.Unknown, writes, current);
            }
        }

        private static Error? Rejection(string? status)
        {
            ErrorDetails? details = status == "InvalidState" ? new ProjectNotBoundDetails()
                : status == "NotFound" ? new NotFoundDetails(null)
                : status == "NotSupportedOnVersion" ? new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "HMI exchange", null)
                : status == "HmiReadSessionBlocked" ? new SessionResetRequiredDetails("HMI read session is blocked.") : null;
            return details == null ? null : new Error("The HMI request could not start.", details);
        }

        internal static CallToolResult Map(string tool, object response, bool writes, bool current)
        {
            var data = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var evidence = data["meta"] as JsonObject ?? new JsonObject();
            data.Remove("meta");
            evidence.Remove("timestamp");
            data["summary"] = data["message"]?.DeepClone();
            data.Remove("message");
            data["evidence"] = evidence;
            Clean(data);

            bool? Flag(string key) => Bool(evidence[key]);
            string? status = (string?)evidence["status"];
            bool? success = Flag("operationSuccess") ?? Flag("success");
            if (Flag("success") == false || Flag("nativeSuccess") == false) success = false;
            bool changed = Flag("mayHaveChanged") == true;
            bool files = Flag("mayHaveWrittenFiles") == true || Flag("partialArtifactExists") == true;
            bool incomplete = Flag("dataComplete") == false || Flag("fullContentVerified") == false
                || evidence["nodeIdError"] != null || tool == "ListHmiTagTables" || tool == "ListHmiTags" || tool == "ListHmiConnections"
                || tool == "ExchangeUnifiedScriptModules" && Flag("nativeSuccess") == true
                || (evidence["modules"] as JsonArray)?.Count == 500 || (evidence["modulesAfter"] as JsonArray)?.Count == 500
                || (evidence["directoryListing"] as JsonArray)?.Count == 500 || (evidence["importCandidates"] as JsonArray)?.Count == 200
                || (evidence["displayNames"] as JsonArray)?.Count == 2000 || (evidence["displayNamesAfter"] as JsonArray)?.Count == 2000;
            bool unknownWrite = writes && (Flag("writeOutcomeUnknown") == true || Flag("requiresSessionReset") == true
                || Flag("verifiedAbsent") == false || (success != true && changed && Flag("nativeSuccess") != true)
                || (success != true && files && Flag("apiCallSuccess") != true && Flag("partialArtifactExists") != true));
            var rejection = Rejection(status);
            Outcome outcome;
            Error? error = null;
            if (status == "HmiReadSessionBlocked" || rejection != null && !changed && !files)
            {
                outcome = Outcome.RejectedBeforeOperation;
                error = rejection;
            }
            else if (unknownWrite)
                outcome = Outcome.Unknown;
            else if (success != true)
            {
                bool step = tool == "DeleteHmiTag" || tool == "ExchangeUnifiedTags" || tool == "ExchangeUnifiedScriptModules" || tool == "ImportUnifiedOpcUaAlarms";
                outcome = files ? Outcome.Partial
                    : step && evidence["tool"] != null && success == false && !changed && (writes || Flag("readStarted") != true) ? Outcome.RejectedBeforeOperation
                    : !writes ? Outcome.ReadFailed
                    : changed && Flag("nativeSuccess") == true ? ((int?)evidence["actualCount"] > 0 ? Outcome.Partial : Outcome.Failed)
                    : evidence["capability"] != null ? Outcome.Failed : Outcome.Unknown;
                if ((string?)evidence["capability"] == "unsupported")
                {
                    outcome = Outcome.RejectedBeforeOperation;
                    error = Rejection("NotSupportedOnVersion");
                }
                if (outcome == Outcome.RejectedBeforeOperation && error == null)
                    error = new Error("The HMI request was rejected before the operation.", new PreconditionFailedDetails("HMI operation prerequisites", null));
            }
            else outcome = Outcome.Succeeded;

            if (evidence["items"] is JsonArray rows)
            {
                evidence.Remove("items");
                var items = new JsonArray();
                foreach (var row in rows)
                {
                    var item = row!.AsObject();
                    var child = Map(tool, new ResponseMessage { Meta = item["evidence"]!.DeepClone().AsObject() }, writes, current);
                    items.Add(new JsonObject { ["index"] = items.Count, ["target"] = item["target"]?.DeepClone(), ["result"] = child.StructuredContent!.DeepClone() });
                }
                data["items"] = items;
                var outcomes = items.Select(i => (string)i!["result"]!["meta"]!["outcome"]!).ToArray();
                int succeeded = outcomes.Count(o => o == "succeeded"), failed = outcomes.Length - succeeded;
                if (unknownWrite || outcomes.Contains("unknown")) outcome = Outcome.Unknown;
                else if (outcomes.Contains("partial") || succeeded > 0 && failed > 0)
                {
                    outcome = Outcome.Partial;
                    error = new Error("Some HMI operations did not complete.", succeeded > 0 && failed > 0
                        ? new PartialFailureDetails(succeeded, failed, 0) : new PartialFailureDetails(0, 0, 0));
                }
                else if (failed > 0) outcome = outcomes.All(o => o == "rejected-before-operation") ? Outcome.RejectedBeforeOperation : Outcome.Failed;
                if (failed > 0 || items.Any(i => (string?)i!["result"]!["meta"]!["completeness"] == "partial")) incomplete = true;
            }
            else if ((data["failed"] as JsonArray)?.Count > 0)
                outcome = writes ? Outcome.Unknown : Outcome.ReadFailed;

            if (outcome == Outcome.Unknown || outcome != Outcome.Succeeded && error == null) error = Failure(outcome, evidence);
            if (outcome == Outcome.Succeeded) error = null;
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete || outcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete;
            return Result(tool, data, error, outcome, completeness, writes, current);
        }

        private static bool? Bool(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
        private static IReadOnlyDictionary<string, JsonElement> Evidence(JsonObject evidence)
            => evidence.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);
        private static Error Failure(Outcome outcome, JsonObject evidence)
            => outcome == Outcome.Unknown ? new Error("The HMI write outcome is unconfirmed; inspect the evidence and reset the session before further writes.", new OutcomeUnknownDetails("HMI exchange", Evidence(evidence)))
                : outcome == Outcome.Partial ? new Error("The HMI operation left partial results.", new PartialFailureDetails(0, 0, 0))
                : outcome == Outcome.RejectedBeforeOperation ? new Error("The HMI operation did not start.", new PreconditionFailedDetails("HMI operation prerequisites", null))
                : new Error("The HMI operation did not establish success.", new NativeOperationFailedDetails(null, null, Evidence(evidence)));

        private static void Clean(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Clean(child); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) obj.Remove(pair.Key);
                else if (pair.Value is JsonValue v && v.TryGetValue<string>(out var text) && text.Contains("\n   "))
                    obj[pair.Key] = "Diagnostic details retained in the server log.";
                else Clean(pair.Value);
            }
        }

        private static CallToolResult Result(string tool, JsonObject data, Error? error, Outcome outcome, Completeness completeness, bool writes, bool current)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation is incomplete; inspect the retained evidence.", new Dictionary<string, JsonElement>()));
            var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, null, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()), Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }

    [McpServerToolType]
    internal sealed class HmiExchangeTools
    {
        private readonly HmiExchangeService _service;

        public HmiExchangeTools(HmiExchangeService service) => _service = service;

        #region plc software - HmiExchange

        [McpServerTool(Name = "ListHmiScreens"), Description("[L2][HMI] List all screen names in an HMI (Classic or Unified), including all nested screen folders/groups. Returns screen names, not folder paths; screen operations resolve these names recursively. Requires: Connect + OpenProject. softwarePath from GetProjectTree. Use before EnsureUnifiedHmiScreen/ExportHmiScreen to confirm which screens exist.")]
        public CallToolResult GetHmiScreensV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
            => HmiExchangeContract.Run("ListHmiScreens", false, false, () => GetHmiScreens(softwarePath));

        public ResponseStringList GetHmiScreens(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = _service.GetHmiScreens(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI screens listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI screens for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListHmiTagTables"), Description("[L2][HMI]List HMI tag table names (Classic/Unified, best-effort)")]
        public CallToolResult GetHmiTagTablesV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
            => HmiExchangeContract.Run("ListHmiTagTables", false, false, () => GetHmiTagTables(softwarePath));

        public ResponseStringList GetHmiTagTables(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = _service.GetHmiTagTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI tag tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI tag tables for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListHmiTags"), Description("[L2][HMI]List HMI tag names (best-effort). If tagTableName empty, returns tags found at root collection if available.")]
        public CallToolResult GetHmiTagsV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: optional tag table name to list tags from")] string tagTableName = "")
            => HmiExchangeContract.Run("ListHmiTags", false, false, () => GetHmiTags(softwarePath, tagTableName));

        public ResponseStringList GetHmiTags(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: optional tag table name to list tags from")] string tagTableName = "")
        {
            try
            {
                var items = _service.GetHmiTags(softwarePath, tagTableName);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI tags listed for '{softwarePath}' (table='{tagTableName}')",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI tags for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListHmiConnections"), Description("[L2][HMI]List HMI connection names (Classic/Unified, best-effort)")]
        public CallToolResult GetHmiConnectionsV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
            => HmiExchangeContract.Run("ListHmiConnections", false, false, () => GetHmiConnections(softwarePath));

        public ResponseStringList GetHmiConnections(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = _service.GetHmiConnections(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI connections listed for '{softwarePath}'",
                        Items = items,
                        Meta = ResponseMeta.Basic(DateTime.Now, true)
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI connections for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiScreen"), Description("[L2][HMI]Export one HMI screen to a file (best-effort; requires Openness export support) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportHmiScreenV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("screenName: the screen name to export")] string screenName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\screen.xml)")] string exportPath)
            => HmiExchangeContract.Run("ExportHmiScreen", true, true, () => ExportHmiScreen(softwarePath, screenName, exportPath));

        public ResponseExportFile ExportHmiScreen(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("screenName: the screen name to export")] string screenName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\screen.xml)")] string exportPath)
        {
            try
            {
                var export = _service.ExportHmiScreen(softwarePath, screenName, exportPath);
                return new ResponseExportFile
                {
                    Message = export["success"]!.GetValue<bool>() ? "Native export completed" : "Native export failed or unsupported",
                    ExportPath = export["path"]?.ToString() ?? exportPath,
                    Meta = export
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI screen '{screenName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI screen '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiTagTable"), Description("[L2][HMI]Export one HMI tag table to a file (best-effort; requires Openness export support) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportHmiTagTableV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: the tag table name to export")] string tagTableName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\tagtable.xml)")] string exportPath)
            => HmiExchangeContract.Run("ExportHmiTagTable", true, true, () => ExportHmiTagTable(softwarePath, tagTableName, exportPath));

        public ResponseExportFile ExportHmiTagTable(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: the tag table name to export")] string tagTableName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\tagtable.xml)")] string exportPath)
        {
            try
            {
                var export = _service.ExportHmiTagTable(softwarePath, tagTableName, exportPath);
                return new ResponseExportFile
                {
                    Message = export["success"]!.GetValue<bool>() ? "Native export completed" : "Native export failed or unsupported",
                    ExportPath = export["path"]?.ToString() ?? exportPath,
                    Meta = export
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI tag table '{tagTableName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiConnection"), Description("[L2][HMI]Export one HMI connection to a file (best-effort; Classic/Unified via reflection) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportHmiConnectionV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("connectionName: the HMI connection name to export")] string connectionName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\connection.xml)")] string exportPath)
            => HmiExchangeContract.Run("ExportHmiConnection", true, true, () => ExportHmiConnection(softwarePath, connectionName, exportPath));

        public ResponseExportFile ExportHmiConnection(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("connectionName: the HMI connection name to export")] string connectionName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\connection.xml)")] string exportPath)
        {
            try
            {
                var export = _service.ExportHmiConnection(softwarePath, connectionName, exportPath);
                return new ResponseExportFile
                {
                    Message = export["success"]!.GetValue<bool>() ? "Native export completed" : "Native export failed or unsupported",
                    ExportPath = export["path"]?.ToString() ?? exportPath,
                    Meta = export
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI connection '{connectionName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI connection '{connectionName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiProgram"), Description("[L2][HMI]Batch export HMI screens/tagtables into a directory (best-effort) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportHmiProgramV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("exportDir: directory to write exported files into")] string exportDir,
            [Description("exportScreens: default true")] bool exportScreens = true,
            [Description("exportTagTables: default true")] bool exportTagTables = true)
            => HmiExchangeContract.Run("ExportHmiProgram", true, true, () => ExportHmiProgram(softwarePath, exportDir, exportScreens, exportTagTables));

        public ResponseBatchExport ExportHmiProgram(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("exportDir: directory to write exported files into")] string exportDir,
            [Description("exportScreens: default true")] bool exportScreens = true,
            [Description("exportTagTables: default true")] bool exportTagTables = true)
        {
            try
            {
                var res = _service.ExportHmiProgram(softwarePath, exportDir, exportScreens, exportTagTables);
                if (res != null)
                {
                    return new ResponseBatchExport
                    {
                        Message = $"HMI program export finished; see Failed list. Destination: '{exportDir}'",
                        Exported = res.Value.Exported,
                        Failed = res.Value.Failed,
                        Meta = ResponseMeta.Basic(DateTime.Now, res.Value.Failed.Count == 0, ("operationSuccess", res.Value.Failed.Count == 0), ("items", res.Value.Items))
                    };
                }
                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI program: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiScreen"), Description("[L2][HMI]Import one HMI screen XML file into an HMI program (best-effort; Classic/Unified via reflection) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportHmiScreenV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional screen group path inside HMI (use empty for root)")] string folderPath,
            [Description("importPath: full file path of exported screen XML")] string importPath)
            => HmiExchangeContract.Run("ImportHmiScreen", true, true, () => ImportHmiScreen(softwarePath, folderPath, importPath));

        public ResponseMessage ImportHmiScreen(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional screen group path inside HMI (use empty for root)")] string folderPath,
            [Description("importPath: full file path of exported screen XML")] string importPath)
        {
            try
            {
                _service.ImportHmiScreen(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI screen imported from '{importPath}'" + (_service.LastImportNotes != null ? " - " + _service.LastImportNotes : ""),
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI screen from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI screen: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiTagTable"), Description("[L2][HMI]Import one HMI tag table XML file into an HMI program (best-effort; Classic/Unified via reflection) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportHmiTagTableV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional tag table group path inside HMI (use empty for root)")] string folderPath,
            [Description("importPath: full file path of exported tag table XML")] string importPath)
            => HmiExchangeContract.Run("ImportHmiTagTable", true, true, () => ImportHmiTagTable(softwarePath, folderPath, importPath));

        public ResponseMessage ImportHmiTagTable(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional tag table group path inside HMI (use empty for root)")] string folderPath,
            [Description("importPath: full file path of exported tag table XML")] string importPath)
        {
            try
            {
                _service.ImportHmiTagTable(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI tag table imported from '{importPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI tag table from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiConnection"), Description("[L2][HMI]Import one HMI connection XML file into an HMI program (best-effort; Classic/Unified via reflection) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportHmiConnectionV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("importPath: full file path of exported HMI connection XML")] string importPath)
            => HmiExchangeContract.Run("ImportHmiConnection", true, true, () => ImportHmiConnection(softwarePath, importPath));

        public ResponseMessage ImportHmiConnection(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("importPath: full file path of exported HMI connection XML")] string importPath)
        {
            try
            {
                _service.ImportHmiConnection(softwarePath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI connection imported from '{importPath}'",
                    Meta = ResponseMeta.Basic(DateTime.Now, true)
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI connection from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI connection: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiScreensFromDirectory"), Description("[L2][HMI]Batch import HMI screen .xml files from a directory (best-effort) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportHmiScreensFromDirectoryV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional screen group path inside HMI (use empty for root)")] string folderPath,
            [Description("dir: directory containing exported screen XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
            => HmiExchangeContract.Run("ImportHmiScreensFromDirectory", true, true, () => ImportHmiScreensFromDirectory(softwarePath, folderPath, dir, regexName, overwrite));

        public ResponseImportBatch ImportHmiScreensFromDirectory(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional screen group path inside HMI (use empty for root)")] string folderPath,
            [Description("dir: directory containing exported screen XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = _service.ImportHmiScreensFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} HMI screens from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = result.Meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI screens from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiTagTablesFromDirectory"), Description("[L2][HMI]Batch import HMI tag table .xml files from a directory (best-effort) Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportHmiTagTablesFromDirectoryV4(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional tag table group path inside HMI (use empty for root)")] string folderPath,
            [Description("dir: directory containing exported tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
            => HmiExchangeContract.Run("ImportHmiTagTablesFromDirectory", true, true, () => ImportHmiTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite));

        public ResponseImportBatch ImportHmiTagTablesFromDirectory(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional tag table group path inside HMI (use empty for root)")] string folderPath,
            [Description("dir: directory containing exported tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = _service.ImportHmiTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} HMI tag tables from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = result.Meta
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI tag tables from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
