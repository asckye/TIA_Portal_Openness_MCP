using TiaMcp.Logic.V4.Domain;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Linq;
using System.Collections.Generic;
using System;
using TiaMcpServer.Siemens.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace TiaMcpServer.ModelContextProtocol
{
    // Only this inspection/reflection group uses this response adapter. Input
    // admission belongs to the shared catalog; native work stays in the services.
    internal static class HmiInspectionContract
    {
        internal static CallToolResult Run(string tool, bool writes, bool current, Func<object> operation,
            string? cursor = null, int pageSize = 0, int? offset = null)
        {
            try { return Map(tool, operation(), writes, current, cursor, pageSize, offset); }
            catch (Exception exception)
            {
                var cause = exception;
                while (cause.InnerException != null) cause = cause.InnerException;
                Error? rejection = cause is PortalException portal ? Rejection(portal.Code.ToString()) : null;
                return Result(tool, null, rejection ?? Failure(writes), rejection != null ? Outcome.RejectedBeforeOperation
                    : writes ? Outcome.Unknown : Outcome.ReadFailed, rejection != null ? Completeness.None : Completeness.Unknown, writes, current);
            }
        }

        private static bool? Flag(JsonObject obj, string key)
            => obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        private static int? Number(JsonObject obj, string key)
            => obj[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;

        private static Error? Rejection(string status)
        {
            ErrorDetails? details = status == "PROJECT_NOT_BOUND" ? new ProjectNotBoundDetails()
                : status == "HmiReadSessionBlocked" ? new SessionResetRequiredDetails("HMI read connection failure")
                : status == "InvalidParams" || status == "INVALID_ARGUMENT" ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                : status == "NotFound" || status == "NOT_FOUND" ? new NotFoundDetails(null)
                : status == "NotSupportedOnVersion" || status == "UNSUPPORTED_CAPABILITY" ? new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "HMI/reflection", null)
                : status == "NativeCrashRiskBlocked" || status == "ACCESS_DENIED" ? new AccessDeniedDetails("reflection", null)
                : status == "PRECONDITION_FAILED" ? new PreconditionFailedDetails("HMI/reflection admission", null) : null;
            return details == null ? null : new Error("The request could not be admitted; inspect the retained evidence.", details);
        }

        private static Error Failure(bool writes) => writes
            ? new Error("The write outcome is unconfirmed. Inspect the evidence and reset the session before further writes.",
                new OutcomeUnknownDetails("HMI/reflection operation", new Dictionary<string, JsonElement>()))
            : new Error("The read did not establish success.", new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));

        internal static CallToolResult Map(string tool, object response, bool writes, bool current,
            string? cursor = null, int pageSize = 0, int? offset = null)
        {
            var data = JsonNode.Parse(V4Json.Serialize(response))!.AsObject();
            var evidence = data["meta"] as JsonObject ?? new JsonObject();
            data.Remove("meta");
            var summary = data["message"]?.DeepClone();
            data.Remove("message");
            bool? success = evidence.ContainsKey("operationSuccess") ? Flag(evidence, "operationSuccess") : Flag(evidence, "success");
            // Nullable API verdicts must never fall back to a positive outer stamp.
            if (evidence.ContainsKey("apiCallSuccess") && Flag(evidence, "apiCallSuccess") != true) success = false;
            if (Flag(evidence, "verificationSuccess") == false && Flag(evidence, "dryRun") != true) success = false;
            bool issued = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "importAttempted") == true
                || Flag(evidence, "invocationAttempted") == true || Flag(evidence, "mayHaveWrittenFiles") == true;
            string status = evidence["v4Rejection"]?.ToString() ?? evidence["status"]?.ToString() ?? "";
            if (status == "InvalidState" && evidence.ContainsKey("tool") && !evidence.ContainsKey("apiCallSuccess")
                && !evidence.ContainsKey("mayHaveChanged")) status = "PROJECT_NOT_BOUND";
            bool knownMismatch = status == "ReadbackMismatch" && Flag(evidence, "apiCallSuccess") == true;
            bool compileFailed = tool == "CompileHmiDiagnostics" && evidence["effectiveState"]?.ToString() == "Error";
            bool unknown = writes && (Flag(evidence, "connectionUnavailable") == true || Flag(evidence, "requiresSessionReset") == true
                || evidence["effectiveState"]?.ToString() == "Unknown" || issued && success != true && !knownMismatch && !compileFailed);
            Error? error = null;
            Outcome outcome;
            if (unknown) { outcome = Outcome.Unknown; error = Failure(true); }
            else if (compileFailed) { outcome = Outcome.Failed; error = CompileResultMapping.Errors(new JsonObject { ["evidence"] = evidence.DeepClone() }) ?? Failure(false); }
            else if (knownMismatch) { outcome = Outcome.Partial; error = new Error("The script was imported but its readback differs.", new PartialFailureDetails(0, 0, 0)); }
            else if (!issued && (error = Rejection(status)) != null) outcome = Outcome.RejectedBeforeOperation;
            else if (success == true) outcome = Outcome.Succeeded;
            else if (writes && Flag(evidence, "mayHaveChanged") == false)
            { outcome = Outcome.RejectedBeforeOperation; error = Rejection("PRECONDITION_FAILED"); }
            else { outcome = writes ? Outcome.Unknown : Outcome.ReadFailed; error = Failure(writes); }

            bool incomplete = Incomplete(evidence);
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown ? Completeness.Unknown : incomplete ? Completeness.Partial
                : outcome == Outcome.ReadFailed || outcome == Outcome.Failed && !compileFailed ? Completeness.None : Completeness.Complete;
            Paging? paging = null;
            if (pageSize > 0 && evidence.ContainsKey("collectionId"))
            {
                string? next = evidence["nextCursor"]?.GetValue<string>();
                bool complete = Flag(evidence, "traversalComplete") == true;
                // Interrupted snapshots have no valid continuation; retain their
                // identity/failure evidence without inventing a completed page.
                if (complete || !string.IsNullOrEmpty(next))
                    paging = new Paging(PagingMode.Cursor, null, pageSize, null, string.IsNullOrEmpty(cursor) ? null : cursor,
                        complete ? null : next, Number(evidence, "expectedCount"), complete);
            }
            else if (pageSize > 0 && offset >= 0)
            {
                int? total = Number(evidence, "total") ?? Number(evidence, "expectedCount");
                if (total >= 0) paging = McpServer.OffsetPage(offset.Value, pageSize, total.Value);
            }
            Clean(evidence);
            var nativeFailure = NativeResultState.FindUnsuccessful(evidence, tool);
            if (nativeFailure != null && NativeResultState.TryUnsuccessful(nativeFailure, writes, out var nativeOutcome, out var nativeError, tool))
            { outcome = nativeOutcome; error = nativeError; completeness = outcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Partial; }
            // This exact page is accepted by GraphicSelectionPage[] and retains
            // the existing snapshot scope identifier, including its native label.
            if (tool == "GetUnifiedGraphicSelection") data["page"] = evidence.DeepClone();
            foreach (var pair in evidence)
                if (pair.Key != "timestamp" && pair.Key != "tool" && pair.Key != "success") data[pair.Key] = pair.Value?.DeepClone();
            if (outcome == Outcome.Succeeded) data["summary"] = summary;
            return Result(tool, data, error, outcome, completeness, writes, current, paging,
                Flag(evidence, "requiresExplicitRebind") == true || Flag(evidence, "connectionUnavailable") == true);
        }

        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonObject obj)
                return Flag(obj, "dataComplete") == false || Flag(obj, "fullObjectComplete") == false || Flag(obj, "fullContentVerified") == false
                    || Flag(obj, "incomplete") == true || Flag(obj, "truncated") == true || Flag(obj, "traversalComplete") == false
                    || obj.Any(pair => Incomplete(pair.Value));
            return node is JsonArray array && array.Any(Incomplete);
        }

        private static void Clean(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                    if (pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)
                        || pair.Key == "error" || pair.Key == "exception") obj.Remove(pair.Key);
                    else Clean(pair.Value);
            else if (node is JsonArray array) foreach (var item in array) Clean(item);
        }

        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool writes, bool current, Paging? paging = null, bool reset = false)
        {
            var warnings = new List<Warning>();
            if (data != null) warnings.AddRange(NativeResultState.Warnings(data, tool));
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation is incomplete; inspect coverage and failures.", new Dictionary<string, JsonElement>()));
            var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, reset || outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired,
                current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var mapped = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }

    [McpServerToolType]
    internal sealed class HmiInspectionTools
    {
        private readonly HmiInspectionService _hmiInspection;

        public HmiInspectionTools(HmiInspectionService service) => _hmiInspection = service;

        [McpServerTool(Name="ArchiveSavedProject"),Description("[L2][Project][FILE]Create a native compressed .zap20/.zap21 archive of an already saved project. Default dryRun=true. Rejects unsaved state, unsupported sessions and existing target files. Does not save, change project path, close the project, or test retrieval. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult ArchiveSavedProjectV4(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            bool dryRun=true)
            => HmiInspectionContract.Run("ArchiveSavedProject", !dryRun, true, () => ArchiveSavedProject(archivePath, dryRun));

        public ResponseMessage ArchiveSavedProject(
            [Description("archivePath: full path of the project archive (.zap2x) on the TIA machine.")] string archivePath,
            bool dryRun=true)
        =>_hmiInspection.ArchiveSavedProject(archivePath,dryRun);
        [McpServerTool(Name="GetUnifiedHmiButtonEvent"),Description("[L2][HMI-Unified]Read an EXISTING button event's script, global definitions, Async and content token. Never creates events. screenPath is a unique name or /Group/Screen from ListHmiScreenPaths.")]
        public CallToolResult ReadUnifiedHmiButtonEventV4(
            string softwarePath,
            string screenPath,
            [Description("buttonName: exact button item name.")] string buttonName,
            string eventType)
            => HmiInspectionContract.Run("GetUnifiedHmiButtonEvent", false, false, () => ReadUnifiedHmiButtonEvent(softwarePath, screenPath, buttonName, eventType));

        public ResponseMessage ReadUnifiedHmiButtonEvent(
            string softwarePath,
            string screenPath,
            [Description("buttonName: exact button item name.")] string buttonName,
            string eventType)
        =>_hmiInspection.ReadUnifiedHmiButtonEvent(softwarePath,screenPath,buttonName,eventType);

        [McpServerTool(Name="DeleteUnifiedHmiButtonEvent"),Description("[L2][HMI-Unified][WRITE]Delete exactly one button event. Default dryRun=true; real deletion requires a matching expectedToken from read/preview. onlyIfEmpty defaults true. No save, no other event is targeted. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DeleteUnifiedHmiButtonEventV4(
            string softwarePath,
            string screenPath,
            [Description("buttonName: exact button item name.")] string buttonName,
            string eventType,
            bool dryRun=true,
            string expectedToken="",
            [Description("onlyIfEmpty: true deletes the event only when it has no script.")] bool onlyIfEmpty=true)
            => HmiInspectionContract.Run("DeleteUnifiedHmiButtonEvent", !dryRun, true, () => DeleteUnifiedHmiButtonEvent(softwarePath, screenPath, buttonName, eventType, dryRun, expectedToken, onlyIfEmpty));

        public ResponseMessage DeleteUnifiedHmiButtonEvent(
            string softwarePath,
            string screenPath,
            [Description("buttonName: exact button item name.")] string buttonName,
            string eventType,
            bool dryRun=true,
            string expectedToken="",
            [Description("onlyIfEmpty: true deletes the event only when it has no script.")] bool onlyIfEmpty=true)
        =>_hmiInspection.DeleteUnifiedHmiButtonEvent(softwarePath,screenPath,buttonName,eventType,dryRun,expectedToken,onlyIfEmpty);

        [McpServerTool(Name="GetUnifiedHmiDynamization"),Description("[L2][HMI-Unified]Read exactly one screen item dynamization by PropertyName. Returns a bounded snapshot with coverage and a token; never creates objects.")]
        public CallToolResult ReadUnifiedHmiDynamizationV4(string softwarePath,string screenPath,string itemName,string propertyName)
            => HmiInspectionContract.Run("GetUnifiedHmiDynamization", false, false, () => ReadUnifiedHmiDynamization(softwarePath, screenPath, itemName, propertyName));

        public ResponseMessage ReadUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName)
        =>_hmiInspection.ReadUnifiedHmiDynamization(softwarePath,screenPath,itemName,propertyName);

        [McpServerTool(Name="DeleteUnifiedHmiDynamization"),Description("[L2][HMI-Unified][WRITE]Delete exactly one property dynamization; default preview only. Real deletion requires expectedToken and complete readback. Does not save or delete events. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DeleteUnifiedHmiDynamizationV4(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
            => HmiInspectionContract.Run("DeleteUnifiedHmiDynamization", !dryRun, true, () => DeleteUnifiedHmiDynamization(softwarePath, screenPath, itemName, propertyName, dryRun, expectedToken));

        public ResponseMessage DeleteUnifiedHmiDynamization(string softwarePath,string screenPath,string itemName,string propertyName,bool dryRun=true,string expectedToken="")
        =>_hmiInspection.DeleteUnifiedHmiDynamization(softwarePath,screenPath,itemName,propertyName,dryRun,expectedToken);

        [McpServerTool(Name="DeleteEmptyUnifiedHmiScreenGroup"),Description("[L2][HMI-Unified][WRITE]Delete an empty screen group by absolute /Group/Subgroup path. Default preview only. Nonempty, ambiguous or root targets are rejected; no recursive deletion or save. Native behaviorPolicy=current; V4 native acceptance is pending.")]
        public CallToolResult DeleteEmptyUnifiedHmiScreenGroupV4(string softwarePath,string groupPath,bool dryRun=true)
            => HmiInspectionContract.Run("DeleteEmptyUnifiedHmiScreenGroup", !dryRun, true, () => DeleteEmptyUnifiedHmiScreenGroup(softwarePath, groupPath, dryRun));

        public ResponseMessage DeleteEmptyUnifiedHmiScreenGroup(string softwarePath,string groupPath,bool dryRun=true)
        =>_hmiInspection.DeleteEmptyUnifiedHmiScreenGroup(softwarePath,groupPath,dryRun);

        [McpServerTool(Name="GetHmiScreenSnapshot"),Description("[L2][HMI]Read a bounded screen object graph. Prefer absolute /Group/Screen paths to avoid a project-wide name search. apiCallSuccess and dataComplete are separate; inspect failures, quarantined getters and limits. Connection faults stop traversal and block further HMI step operations until an explicit successful AttachOpenProject; inspect logs first, do not automatically rebind/retry. Not a restorable backup. maxDepth 1..12; maxNodes 1..10000. Large responses use GetExportContent.")]
        public CallToolResult ReadHmiScreenSnapshotV4(
            string softwarePath,
            string screenPath,
            int maxDepth=6,
            [Description("maxNodes: cap on nodes returned.")] int maxNodes=2000)
            => HmiInspectionContract.Run("GetHmiScreenSnapshot", false, false, () => ReadHmiScreenSnapshot(softwarePath, screenPath, maxDepth, maxNodes));

        public ResponseMessage ReadHmiScreenSnapshot(
            string softwarePath,
            string screenPath,
            int maxDepth=6,
            [Description("maxNodes: cap on nodes returned.")] int maxNodes=2000)
        =>_hmiInspection.ReadHmiScreenSnapshot(softwarePath,screenPath,maxDepth,maxNodes);

        [McpServerTool(Name="ListHmiScreenPaths"),Description("[L2][HMI]List full screen group paths, including nested folders. Paths start with / and URI-escape each name segment. offset/limit paginate the list; exact paths disambiguate same-named screens.")]
        public CallToolResult ListHmiScreenPathsV4(string softwarePath,int offset=0,int limit=100)
            => HmiInspectionContract.Run("ListHmiScreenPaths", false, false, () => ListHmiScreenPaths(softwarePath, offset, limit), offset: offset, pageSize: limit);

        public ResponseMessage ListHmiScreenPaths(string softwarePath,int offset=0,int limit=100)
        =>_hmiInspection.ListHmiScreenPaths(softwarePath,offset,limit);
    }
}
