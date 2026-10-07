using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using System.Text.Json;
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
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using TiaMcpServer.Siemens.Services;

namespace TiaMcpServer.ModelContextProtocol
{
    // Contract boundary for alarms, OPC UA, technology objects and software units.
    // The services retain native execution; only their observed evidence is mapped here.
    internal static class EngineeringToolContract
    {
        internal static string Json<T>(T value) => V4Json.Serialize(value);
        internal static string Json<T>(AttributeMap<T>? value)
            => V4Json.Serialize(value ?? new AttributeMap<T>(new Dictionary<string, T>()));
        internal static string Value(NativeValue value, string action)
        {
            if (value.Kind == JsonValueKind.Undefined) return "null";
            if (action == "setParameter" && (value.Kind == JsonValueKind.Object || value.Kind == JsonValueKind.Array))
                throw new InputFailure(McpServer.InvalidInput("value"));
            return V4Json.Serialize(value);
        }
        private sealed class InputFailure : Exception
        {
            internal Error Error { get; }
            internal InputFailure(Error error) => Error = error;
        }
        internal static CallToolResult Run(string tool, bool writes, Func<object> operation)
        {
            try { return Map(tool, operation(), writes); }
            catch (InputFailure ex) { return Result(tool, null, ex.Error, Outcome.RejectedBeforeOperation, Completeness.None); }
            catch (Exception ex)
            {
                var cause = ex;
                while (cause.InnerException != null) cause = cause.InnerException;
                if (cause is PortalException portal && (portal.Code == PortalErrorCode.NotFound || portal.Code == PortalErrorCode.InvalidParams
                    || portal.Code == PortalErrorCode.InvalidState || portal.Code == PortalErrorCode.NotSupportedOnVersion))
                {
                    ErrorDetails details = portal.Code == PortalErrorCode.NotFound ? new NotFoundDetails(null)
                        : portal.Code == PortalErrorCode.InvalidParams ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                        : portal.Code == PortalErrorCode.NotSupportedOnVersion ? new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null)
                        : new PreconditionFailedDetails("engineering-session", null);
                    return Result(tool, null, new Error("The engineering prerequisites were not satisfied.", details), Outcome.RejectedBeforeOperation, Completeness.None);
                }
                return PlcToolContract.Failure(tool, ex, writes, true);
            }
        }
        private static bool? Flag(JsonObject obj, string key)
            => obj[key] is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        internal static CallToolResult Map(string tool, object response, bool writes)
        {
            var data = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var evidence = data["meta"] as JsonObject ?? new JsonObject();
            data.Remove("meta");
            if (data["data"] is JsonObject report)
            {
                data.Remove("data");
                foreach (var pair in report) data[pair.Key] = pair.Value?.DeepClone();
            }
            data["summary"] = data["message"]?.DeepClone(); data.Remove("message");
            data["evidence"] = evidence;
            bool? success = Flag(evidence, "operationSuccess") ?? Flag(evidence, "success") ?? Flag(data, "ok");
            if (Flag(data, "ok") == false) success = false;
            string? state = (evidence["nativeState"] ?? evidence["state"] ?? evidence["result"]?["state"])?.ToString();
            bool nativeKnown = state == "Success" || state == "Warning" || state == "Error" || state == "Failure";
            if (state != null || evidence.ContainsKey("nativeState"))
                success = nativeKnown && state != "Error" && state != "Failure" && success != false;
            bool issued = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "mayHaveWrittenFiles") == true;
            bool before = !issued && (writes && Flag(evidence, "mayHaveChanged") == false || evidence["v4Rejection"] != null);
            string status = evidence["v4Rejection"]?.ToString() ?? evidence["status"]?.ToString() ?? "";
            bool projectMissing = status == "PROJECT_NOT_BOUND" || status == "InvalidState" && evidence.ContainsKey("tool")
                && !evidence.ContainsKey("apiCallSuccess") && !issued;
            bool reset = Flag(evidence, "requiresExplicitRebind") == true || Flag(evidence, "requiresSessionReset") == true;
            int succeeded = (data["imported"] as JsonArray)?.Count ?? 0;
            int failed = (data["failed"] as JsonArray)?.Count ?? 0;
            var nativeResult = evidence["result"] as JsonObject;
            succeeded += (nativeResult?["imported"] as JsonArray)?.Count ?? (nativeResult?["exportedDocuments"] as JsonArray)?.Count ?? 0;
            bool unknown = writes && (Flag(evidence, "verified") == false || Flag(evidence, "verifiedAbsent") == false
                || state != null && !nativeKnown || evidence.ContainsKey("nativeState") && state == null || reset && issued);
            if (failed > 0) { success = false; unknown |= writes && !before && !nativeKnown; }
            Outcome outcome; Error? error = null;
            if (unknown || writes && success != true && !before && !nativeKnown && !projectMissing && status != "HmiReadSessionBlocked")
            { outcome = Outcome.Unknown; error = Unknown(); }
            else if (projectMissing)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("No project is bound.", new ProjectNotBoundDetails()); }
            else if (status == "HmiReadSessionBlocked")
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("The session requires an explicit reset.", new SessionResetRequiredDetails("engineering-session")); }
            else if (success == true) outcome = Outcome.Succeeded;
            else if (before)
            {
                outcome = Outcome.RejectedBeforeOperation;
                ErrorDetails details = status == "NOT_FOUND" || status == "NotFound" ? new NotFoundDetails(null)
                    : status == "UNSUPPORTED_CAPABILITY" || status == "NotSupported" ? new UnsupportedCapabilityDetails(McpServer.ReleaseKey, tool, null)
                    : status == "INVALID_ARGUMENT" ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                    : new PreconditionFailedDetails("engineering-before-operation", null);
                error = new Error("The engineering request was rejected before execution.", details);
            }
            else if (!writes) { outcome = Outcome.ReadFailed; error = NativeFailure(); }
            else if (succeeded > 0)
            { outcome = Outcome.Partial; error = new Error("Some engineering operations did not complete.", new PartialFailureDetails(succeeded, Math.Max(1, failed), 0)); }
            else { outcome = Outcome.Failed; error = NativeFailure(); }
            bool incomplete = Incomplete(data) || Flag(evidence, "supported") == false;
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete || outcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete;
            Paging? paging = null;
            if (evidence["offset"] is JsonValue offset && offset.TryGetValue<int>(out var start)
                && evidence["limit"] is JsonValue limit && limit.TryGetValue<int>(out var size)
                && (evidence["total"] ?? evidence["totalCount"]) is JsonValue total && total.TryGetValue<int>(out var count)
                && start >= 0 && size > 0 && count >= start) paging = McpServer.OffsetPage(start, size, count);
            Clean(data);
            if (outcome != Outcome.Succeeded) data.Remove("summary");
            return Result(tool, data, error, outcome, completeness, writes, paging, reset);
        }
        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            bool MissingRows(string countKey, string rowsKey) => obj[countKey] is JsonValue count && count.TryGetValue<int>(out var total)
                && obj[rowsKey] is JsonArray rows && rows.Count < total;
            return Flag(obj, "dataComplete") == false || Flag(obj, "incomplete") == true || Flag(obj, "truncated") == true
                || Flag(obj, "groupsTruncated") == true || obj.ContainsKey("nameNote")
                || MissingRows("objectCount", "technologicalObjects") || MissingRows("parameterCount", "parameters")
                || obj["motionViews"] is JsonObject views && obj["tree"]?["objectCount"] is JsonValue objects && objects.TryGetValue<int>(out var objectCount) && views.Count < objectCount
                // Unit content lists have a fixed 200-name observation budget and no total.
                || obj.Any(p => p.Value is JsonArray names && names.Count == 200 && new[] { "blocks", "types", "groups", "documents", "tagTables", "externalSources", "system", "user", "systemBlockGroups" }.Contains(p.Key))
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) && p.Value != null || Incomplete(p.Value));
        }
        private static void Clean(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Clean(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "timestamp" || pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure"
                    || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) obj.Remove(pair.Key);
                else if (pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal)) obj[pair.Key] = "Native diagnostic details are retained in the server log.";
                else Clean(pair.Value);
            }
        }
        private static Error NativeFailure() => new Error("The engineering operation did not establish success.", new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error Unknown() => new Error("The write outcome is unknown. Inspect the retained evidence and reset the session before further writes.", new OutcomeUnknownDetails("engineering-operation", new Dictionary<string, JsonElement>()));
        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool writes = false, Paging? paging = null, bool reset = false)
        {
            var warnings = new List<Warning> { new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()) };
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation covers only the reported scope and available fields.", new Dictionary<string, JsonElement>()));
            Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, reset || outcome == Outcome.Unknown, BehaviorPolicy.Current, completeness, paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }

    [McpServerToolType]
    internal sealed class AlarmsTools
    {
        private readonly AlarmsService _service;

        public AlarmsTools(AlarmsService service) => _service = service;

        [McpServerTool(Name = "ExportAlarmClasses"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:ConnectPortal+OpenProject]" +
            " Export PLC alarm classes to a file. Alarm classes define severity, acknowledgment behavior, and display colors for alarms." +
            " The exported file can be edited and re-imported to update alarm class configurations." +
            " Use before bulk alarm class updates to create a backup. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportAlarmClassesV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the export, e.g. 'C:\\Temp\\AlarmClasses.xml'")] string exportPath)
            => EngineeringToolContract.Run("ExportAlarmClasses", true, () => ExportAlarmClasses(softwarePath, exportPath));

        public ResponseMessage ExportAlarmClasses(
            string softwarePath,
            string exportPath)
        {
            try { return _service.ExportAlarmClasses(softwarePath, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm classes: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportAlarmClasses"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:ConnectPortal+OpenProject]" +
            " Import PLC alarm classes from a previously exported file." +
            " Overwrites existing alarm class definitions. Run CompilePlcSoftware after import. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportAlarmClassesV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to import from")] string importPath)
            => EngineeringToolContract.Run("ImportAlarmClasses", true, () => ImportAlarmClasses(softwarePath, importPath));

        public ResponseMessage ImportAlarmClasses(
            string softwarePath,
            string importPath)
        {
            try { return _service.ImportAlarmClasses(softwarePath, importPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing alarm classes: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmTextLists"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:ConnectPortal+OpenProject]" +
            " Export all PLC alarm text lists to an XLSX (Excel) file." +
            " Text lists contain the text strings shown for each alarm condition." +
            " Supports multi-language projects — all configured languages are exported." +
            " Typical use: export → translate in Excel → ImportAlarmTextLists. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportAlarmTextListsV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the XLSX output, e.g. 'C:\\Temp\\AlarmTexts.xlsx'")] string exportPath)
            => EngineeringToolContract.Run("ExportAlarmTextLists", true, () => ExportAlarmTextLists(softwarePath, exportPath));

        public ResponseMessage ExportAlarmTextLists(
            string softwarePath,
            string exportPath)
        {
            try { return _service.ExportAlarmTextLists(softwarePath, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm text lists: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportAlarmTextLists"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:ConnectPortal+OpenProject]" +
            " Import PLC alarm text lists from an XLSX file." +
            " The file must match the format exported by ExportAlarmTextLists." +
            " Run CompilePlcSoftware after import to validate alarm configuration. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportAlarmTextListsV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to the XLSX file")] string importPath)
            => EngineeringToolContract.Run("ImportAlarmTextLists", true, () => ImportAlarmTextLists(softwarePath, importPath));

        public ResponseMessage ImportAlarmTextLists(
            string softwarePath,
            string importPath)
        {
            try { return _service.ImportAlarmTextLists(softwarePath, importPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing alarm text lists: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmInstanceTexts"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:ConnectPortal+OpenProject]" +
            " Export PLC alarm instance texts to an XLSX file." +
            " Instance texts are the alarm messages tied to specific FB/FC instances (e.g. Motor_01.AlarmText)." +
            " Options control what additional columns are included in the export." +
            " Typical use: export → fill in alarm descriptions → ImportPlcAlarmInstanceTexts (PlcAlarmTextProvider.ImportInstanceTextsFromXlsx). Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExportAlarmInstanceTextsV4(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the XLSX output")] string exportPath,
            [Description("includeInfoText: include the Info Text column (default: true)")] bool includeInfoText = true,
            [Description("includeAdditionalTexts: include Additional Texts columns (default: true)")] bool includeAdditionalTexts = true,
            [Description("includeAlarmClass: include the Alarm Class column (default: true)")] bool includeAlarmClass = true)
            => EngineeringToolContract.Run("ExportAlarmInstanceTexts", true, () => ExportAlarmInstanceTexts(softwarePath, exportPath, includeInfoText, includeAdditionalTexts, includeAlarmClass));

        public ResponseMessage ExportAlarmInstanceTexts(
            string softwarePath,
            string exportPath,
            bool includeInfoText = true,
            bool includeAdditionalTexts = true,
            bool includeAlarmClass = true)
        {
            try { return _service.ExportAlarmInstanceTexts(softwarePath, exportPath, includeInfoText, includeAdditionalTexts, includeAlarmClass); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm instance texts: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }


        [McpServerTool(Name="ExchangePlcAlarmTextLists"), Description("[L2][PLC-Alarms][WRITE] Native XLSX exchange of PLC alarm text lists through PlcAlarmTextListProvider of the exact PLC or of a unit (unitName + unitKind): export writes a NEW absolute .xlsx (every user text list in every project language, or the filtered overload with textListNames + cultures, both required together; TIA refuses system text lists and inactive languages natively) and returns the TextListXlsxResult state, log file and file hash; import (importOption None refuses existing lists, Override replaces their entries) needs confirmImport=true besides dryRun=false and an Offline PLC. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ExchangePlcAlarmTextListsXlsxV4(
            string softwarePath,
            [Description("action: the operation to perform - export | import.")] string action,
            string filePath,
            string unitName="",
            [Description("unitKind: which unit collection unitName refers to - unit | safety.")] string unitKind="unit",
            [Description("textListNames: Array of text list names ('[]' = all).")] string[] textListNames = null!,
            [Description("cultures: Array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all active languages).")] string[] cultures = null!,
            [Description("importOption: import option - None | Override.")] string importOption="None",
            [Description("confirmImport: must be true together with dryRun=false to import (imports replace project data).")] bool confirmImport=false,
            bool dryRun=true)
            => EngineeringToolContract.Run("ExchangePlcAlarmTextLists", !dryRun, () => ExchangePlcAlarmTextListsXlsx(softwarePath, action, filePath, unitName, unitKind, EngineeringToolContract.Json(textListNames ?? Array.Empty<string>()), EngineeringToolContract.Json(cultures ?? Array.Empty<string>()), importOption, confirmImport, dryRun));

        public ResponseMessage ExchangePlcAlarmTextListsXlsx(
            string softwarePath,
            string action,
            string filePath,
            string unitName="",
            string unitKind="unit",
            string textListNamesJson="[]",
            string culturesJson="[]",
            string importOption="None",
            bool confirmImport=false,
            bool dryRun=true)
            => _service.ExchangePlcAlarmTextListsXlsx(softwarePath,action,filePath,unitName,unitKind,textListNamesJson,culturesJson,importOption,confirmImport,dryRun);

        [McpServerTool(Name="ImportPlcAlarmInstanceTexts"), Description("[L2][PLC-Alarms][WRITE] Native PlcAlarmTextProvider.ImportInstanceTextsFromXlsx for one exact PLC from an existing absolute xlsx and a JSON array of exact project culture names (each must be a project language). Returns native state and log file path; text changes need separate export/readback. Default preview; execution requires Offline PLC. No save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ImportPlcAlarmInstanceTextsV4(
            string softwarePath,
            string filePath,
            [Description("cultures: Array of language tags, e.g. ['en-US','zh-CN'] ('[]' = all active languages).")] string[] cultures,
            bool dryRun=true)
            => EngineeringToolContract.Run("ImportPlcAlarmInstanceTexts", !dryRun, () => ImportPlcAlarmInstanceTexts(softwarePath, filePath, EngineeringToolContract.Json(cultures), dryRun));

        public ResponseMessage ImportPlcAlarmInstanceTexts(
            string softwarePath,
            string filePath,
            string culturesJson,
            bool dryRun=true)
            => _service.ImportPlcAlarmInstanceTexts(softwarePath,filePath,culturesJson,dryRun);

        [McpServerTool(Name="ManagePlcAlarmTextList"), Description("[L2][PLC-Alarms][WRITE] read/createFromMasterCopy/delete PLC alarm text lists (PlcAlarmTextlistGroup system+user lists; entries are not exposed by Openness). read paginates Name/ID/ListRange, optionally one exact name. createFromMasterCopy needs an open library name and exact master copy path; copyMode ThrowIfExists/Rename/Replace optional. delete refuses system lists and requires confirmDelete=true besides dryRun=false; absence verified. Default preview, Offline PLC for execution; no save/compile/download. Current native policy; V4 safety behavior is not yet accepted.")]
        public CallToolResult ManagePlcAlarmTextListV4(
            [Description("softwarePath: PLC software path from GetProjectTree, e.g. 'PLC_1'.")] string softwarePath,
            [Description("action: read | createFromMasterCopy | delete.")] string action="read",
            [Description("name: exact alarm text list name (delete) or the name to give the copy (createFromMasterCopy).")] string name="",
            [Description("libraryName: for createFromMasterCopy - global library name ('' = the project library).")] string libraryName="",
            [Description("masterCopyPath: for createFromMasterCopy - 'Folder/Name' of the master copy.")] string masterCopyPath="",
            [Description("copyMode: for createFromMasterCopy - ThrowIfExists | Rename | Replace.")] string copyMode="",
            [Description("confirmDelete: must be true together with dryRun=false to delete.")] bool confirmDelete=false,
            [Description("offset: first list to return (paging).")] int offset=0,
            [Description("limit: maximum lists to return.")] int limit=100,
            [Description("dryRun: true (default) previews; false executes.")] bool dryRun=true)
            => EngineeringToolContract.Run("ManagePlcAlarmTextList", !dryRun && action != "read" && action != "list", () => ManagePlcAlarmTextList(softwarePath, action, name, libraryName, masterCopyPath, copyMode, confirmDelete, offset, limit, dryRun));

        public ResponseMessage ManagePlcAlarmTextList(
            string softwarePath,
            string action="read",
            string name="",
            string libraryName="",
            string masterCopyPath="",
            string copyMode="",
            bool confirmDelete=false,
            int offset=0,
            int limit=100,
            bool dryRun=true)
            => _service.ManagePlcAlarmTextList(softwarePath,action,name,libraryName,masterCopyPath,copyMode,confirmDelete,offset,limit,dryRun);
    }
}
