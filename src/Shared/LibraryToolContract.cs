using System.Text.Json;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;
using System.ComponentModel;
using ModelContextProtocol.Server;
using VciResult = TiaMcp.Adapters.Contracts.VersionControlResult;

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class LibraryToolContract
    {
        internal static void Check(Action validation)
        {
            try { validation(); }
            catch (ArgumentException error) /* swallow(privacy): legacy validators can contain caller values */
            { throw new InputFailureException(McpServer.InvalidInput(error.ParamName ?? "")); }
            catch (JsonException) /* swallow(privacy): malformed typed values are rejected before native work */
            { throw new InputFailureException(McpServer.InvalidInput("")); }
            catch (NotSupportedException) /* swallow(privacy): preserve capability rejection without native details */
            {
                throw new InputFailureException(new Error("This operation is unavailable.",
                            new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, "library-sivarc", null)));
            }
        }
        private sealed class InputFailureException : Exception
        {
            internal Error Error { get; }
            internal InputFailureException(Error error) => Error = error;
        }
        internal static CallToolResult Run(string tool, bool writes, bool current, Func<object> operation)
        {
            try { return Map(tool, operation(), writes, current); }
            catch (InputFailureException ex) { return Result(tool, null, ex.Error, Outcome.RejectedBeforeOperation, Completeness.None, current: current); }
            catch (Exception ex)
            {
                var cause = ex;
                while (cause.InnerException != null) cause = cause.InnerException;
                if (cause is PortalException portal && (portal.Code == PortalErrorCode.NotFound || portal.Code == PortalErrorCode.InvalidParams
                    || portal.Code == PortalErrorCode.InvalidState || portal.Code == PortalErrorCode.NotSupportedOnVersion))
                {
                    ErrorDetails details = portal.Code == PortalErrorCode.NotFound ? new NotFoundDetails(null)
                        : portal.Code == PortalErrorCode.InvalidParams
                            ? McpServer.ArgumentDetails(tool, TiaOpenness.Shared.HostFailurePolicy.Parameter(portal))
                        : portal.Code == PortalErrorCode.NotSupportedOnVersion ? new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, tool, null)
                        : new PreconditionFailedDetails("engineering-session", null);
                    return Result(tool, null, new Error("The engineering prerequisites were not satisfied.", details), Outcome.RejectedBeforeOperation,
                            Completeness.None, current: current);
                }
                return Result(tool, new JsonObject { ["exceptionType"] = ex.GetType().Name },
                    writes ? Unknown(HostBehavior.WorkerReplyEvidence(tool, ex))
                        : NativeFailureDiagnostics.FromException(NativeFailure(), ex),
                    writes ? Outcome.Unknown : Outcome.ReadFailed, Completeness.Unknown, current: current);
            }
        }
        private static int Count(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;
        private static bool? Flag(JsonObject obj, string key)
            => obj[key] is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        internal static CallToolResult Map(string tool, object response, bool writes, bool current = true)
        {
            string? nativeMessage = HostBehavior.SafeDiagnostic((response as ResponseMessage)?.Message);
            var data = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            HostBehavior.RemoveResponseCredentials(data);
            var evidence = data["meta"] as JsonObject ?? new JsonObject();
            data.Remove("meta");
            if (data["data"] is JsonObject report)
            {
                data.Remove("data");
                foreach (var pair in report) data[pair.Key] = pair.Value?.DeepClone();
            }
            if (response is ResponseStringList && data["items"] == null) data["items"] = new JsonArray();
            if (response is ResponseGlobalLibraryProbe || response is ResponseGlobalLibraryImport)
                foreach (string key in new[] { "members", "masterCopies", "types", "folders", "warnings", "attempts", "readbackItems" })
                    if (data.ContainsKey(key) && data[key] == null) data[key] = new JsonArray();
            data["summary"] = data["message"]?.DeepClone(); data.Remove("message");
            // Keep only the recovery advice when a failed sync summary is rebuilt without the raw native error.
            string? notVerifiedAdvice = null;
            if (tool == "SynchronizeVersionControlWorkspace" && evidence.Remove("notVerifiedAdvice", out var advice))
                notVerifiedAdvice = (string?)advice;
            data["evidence"] = evidence;
            bool? success = Flag(evidence, "operationSuccess") ?? Flag(evidence, "success") ?? Flag(data, "ok");
            if (Flag(data, "ok") == false) success = false;
            const string screenItemRefusal = TiaMcp.Adapters.Contracts.LibraryCapabilities.ScreenItemMasterCopyRefusal;
            var importRaw = data["raw"] as JsonObject;
            bool refusedScreenItemImport = tool == "ImportMasterCopyFromGlobalLibrary" && response is ResponseGlobalLibraryImport
                && Flag(data, "ok") == false && data["error"]?.ToString() == screenItemRefusal && importRaw != null
                && Flag(importRaw, "invocationAttempted") == false && Flag(importRaw, "mayHaveChanged") == false
                && Flag(evidence, "mayHaveChanged") != true && Flag(evidence, "mayHaveWrittenFiles") != true;
            if (refusedScreenItemImport)
            {
                evidence["mayHaveChanged"] = false;
                evidence["v4Rejection"] = "UNSUPPORTED_CAPABILITY";
            }
            string? state = (evidence["transferResultState"] ?? evidence["nativeState"] ?? evidence["state"] ?? evidence["result"]?["state"])?.ToString();
            bool nativeKnown = NativeResultState.Classify(evidence, tool) != TiaMcp.Adapters.Contracts.NativeStateKind.Unexpected;
            if (state != null || evidence.ContainsKey("nativeState"))
                success = NativeResultState.Succeeded(evidence, tool) && success != false;
            bool? generation = Flag(evidence, "generationPassed")
                ?? (evidence["result"] is JsonObject generationResult ? Flag(generationResult, "isGenerationSuccessful") : null);
            if (generation.HasValue) { nativeKnown = true; success = generation.Value && success != false; }
            bool issued = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "mayHaveWrittenFiles") == true;
            bool before = !issued && (writes && Flag(evidence, "mayHaveChanged") == false || evidence["v4Rejection"] != null);
            string status = evidence["v4Rejection"]?.ToString() ?? evidence["status"]?.ToString() ?? "";
            bool projectMissing = status == "PROJECT_NOT_BOUND" || status == "InvalidState" && evidence.ContainsKey("tool")
                && !evidence.ContainsKey("apiCallSuccess") && !issued;
            bool reset = writes && Flag(evidence, "writeOutcomeUnknown") == true;
            int succeeded = Count(evidence, "synchronized") + (Flag(evidence, "dryRun") == true ? 0 : Count(evidence, "mapped"));
            succeeded += (data["imported"] as JsonArray)?.Count ?? 0;
            int failed = Count(evidence, "failed") + ((data["failed"] as JsonArray)?.Count ?? 0);
            var nativeResult = evidence["result"] as JsonObject;
            succeeded += (nativeResult?["imported"] as JsonArray)?.Count ?? (nativeResult?["exportedDocuments"] as JsonArray)?.Count ?? 0;
            bool unknown = writes && (Flag(evidence, "verified") == false || Flag(evidence, "verifiedAbsent") == false
                || state != null && !nativeKnown || evidence.ContainsKey("nativeState") && state == null || reset && issued);
            if (failed > 0) { success = false; unknown |= writes && !before && !nativeKnown; }
            bool knownPartial = evidence["createdType"] is JsonObject || evidence["createdVersion"] is JsonObject
                || Flag(data, "imported") == true;
            if (writes && success != true && knownPartial && nativeKnown) succeeded = Math.Max(1, succeeded);
            string? disconnected = tool == "ConnectProjectToWorkspace" ? HostBehavior.SafeDiagnostic((string?)evidence["disconnectedFileReason"]) : null;
            Outcome outcome; Error? error = null;
            if ((HostBehavior.DocumentedRefusal(evidence)
                ?? HostBehavior
                    .KnownRefusalWithMessage(evidence, success, HardwareContract.ReleaseKey, tool, nativeMessage, field => McpServer
                        .MissingTargetDetails(tool, field))) is Error refusal)
            { outcome = Outcome.RejectedBeforeOperation; error = refusal; }
            else if (tool is ("ConnectProjectToWorkspace" or "SynchronizeVersionControlWorkspace") && writes && success == false
                && evidence["exportsIssued"] != null && Flag(evidence, "writeOutcomeUnknown") == false && evidence["v4Rejection"] == null)
            {
                bool connect = tool == "ConnectProjectToWorkspace";
                int completed = Count(evidence, connect ? "mapped" : "synchronized");
                string? diagnostic = disconnected ?? HostBehavior.SafeDiagnostic((string?)evidence["nativeMessage"]) ?? nativeMessage;
                if (completed > 0)
                {
                    outcome = Outcome.Partial;
                    string progress = connect && Count(evidence, "reused") > 0
                        ? "Some objects were mapped to the workspace." : "Some objects were exported to the workspace.";
                    error = new Error((progress + " " + (connect ? diagnostic : null)).TrimEnd(), new PartialFailureDetails(completed, failed, 0));
                }
                else if (connect && Count(evidence, "mappedAfterFailure") > 0)
                {
                    outcome = Outcome.Partial;
                    error = new Error(("Workspace mappings exist but the initial export failed. " + diagnostic).TrimEnd(), new PartialFailureDetails(0, 0, 0));
                }
                else if (Count(evidence, "filesWritten") > 0)
                {
                    outcome = Outcome.Partial;
                    error = new Error(("Workspace XML was written before the operation failed. " + diagnostic).TrimEnd(),
                        new PartialFailureDetails(0, 0, 0));
                }
                else { outcome = Outcome.Failed; error = NativeFailure(diagnostic); }
            }
            else if (HostBehavior.UnchangedNativeFailure(evidence, success))
            { outcome = writes ? Outcome.Failed : Outcome.ReadFailed; error = HostBehavior.UnchangedNativeError(evidence, nativeMessage); }
            else if (unknown || writes && success != true && !before && !nativeKnown && !projectMissing && status != "HmiReadSessionBlocked")
            {
                outcome = Outcome.Unknown;
                var retained = HostBehavior.WorkerReplyEvidence(evidence).ToDictionary(p => p.Key, p => p.Value);
                if (tool is "ManageSivarcRuleContainer" or "GenerateSivarc" && !retained.ContainsKey("nativeMessage") && nativeMessage != null)
                    retained["nativeMessage"] = JsonSerializer.SerializeToElement(nativeMessage);
                error = Unknown(retained);
            }
            else if (projectMissing)
            { outcome = Outcome.RejectedBeforeOperation; error = new Error("No project is bound.", new ProjectNotBoundDetails()); }
            else if (status == "HmiReadSessionBlocked")
            { outcome = writes ? Outcome.Unknown : Outcome.ReadFailed; error = HostBehavior.UnavailableCall(writes); }
            else if (success == true) outcome = Outcome.Succeeded;
            else if (before)
            {
                outcome = Outcome.RejectedBeforeOperation;
                ErrorDetails details = status == "NOT_FOUND" || status == "NotFound" ? McpServer.MissingTargetDetails(tool, evidence: evidence)
                    : status == "UNSUPPORTED_CAPABILITY" || status == "NotSupported" ? new UnsupportedCapabilityDetails(HardwareContract.ReleaseKey, tool, null)
                    : status == "ALREADY_EXISTS" ? new AlreadyExistsDetails(null)
                    : status == "INVALID_ARGUMENT" ? McpServer.ArgumentDetails(tool, null, evidence)
                    : new PreconditionFailedDetails((string?)evidence["condition"] ?? "engineering-before-operation", null);
                error =
                    new Error(refusedScreenItemImport ? screenItemRefusal : nativeMessage ?? "The engineering request was rejected before execution.", details);
            }
            else if (!writes) { outcome = Outcome.ReadFailed; error = NativeFailure(nativeMessage); }
            else if (succeeded > 0)
            {
                outcome = Outcome.Partial; error =
                    new Error("Some engineering operations did not complete.", new PartialFailureDetails(succeeded, Math.Max(1, failed), 0));
            }
            else { outcome = Outcome.Failed; error = NativeFailure(nativeMessage); }
            bool incomplete = Incomplete(data) || Flag(evidence, "supported") == false
                || tool == "ProbeGlobalLibrary" || tool == "ListVersionControlWorkspaces" || tool == "GetVersionControlStatus";
            if (tool == "ConnectProjectToWorkspace" && Count(evidence, "unsupported") > 0) incomplete = true;
            var completeness = outcome == Outcome.RejectedBeforeOperation ? Completeness.None
                : outcome == Outcome.Unknown || outcome == Outcome.ReadFailed ? Completeness.Unknown
                : incomplete || outcome == Outcome.Partial ? Completeness.Partial : Completeness.Complete;
            Paging? paging = null;
            if (evidence["offset"] is JsonValue offset && offset.TryGetValue<int>(out var start)
                && evidence["limit"] is JsonValue limit && limit.TryGetValue<int>(out var size)
                && (evidence["total"] ?? evidence["totalCount"] ?? evidence["expectedCount"]) is JsonValue total && total.TryGetValue<int>(out var count)
                && start >= 0 && size > 0 && count >= start) paging = McpServer.OffsetPage(start, size, count);
            var legacyStatusRows = tool == "GetVersionControlStatus" && HardwareContract.ReleaseKey is "16" or "17" or "18" or "19"
                ? evidence["objects"] as JsonArray : null;
            Clean(data, legacyStatusRows);
            var proofRows = (evidence["items"] as JsonArray)?.OfType<JsonObject>().Where(r => (string?)r["outcome"] == "verify-before-export").ToArray();
            if (!writes && tool is ("ConnectProjectToWorkspace" or "SynchronizeVersionControlWorkspace")
                && (evidence["plannedFiles"] is JsonArray || proofRows?.Length > 0))
            {
                var plan = new JsonObject { ["release"] = HardwareContract.ReleaseKey, ["tool"] = tool,
                    ["workspace"] = evidence["workspaceName"]?.DeepClone(), ["root"] = evidence["rootPath"]?.DeepClone(),
                    ["files"] = evidence["plannedFiles"]?.DeepClone(), ["items"] = data["items"]?.DeepClone() };
                if (proofRows?.Length > 0) plan["verifyBeforeExport"] = new JsonArray(proofRows.Select(r => r.DeepClone()).ToArray());
                data["planHash"] = TiaOpenness.Shared.PendingApproval.Hash(TiaOpenness.Shared.PendingApproval.Canonical(plan));
            }
            if (NativeResultState.TryUnsuccessful(evidence, writes, out var nativeOutcome, out var nativeError, tool)) {
                outcome = nativeOutcome; error = nativeError; completeness = outcome == Outcome.Unknown ? Completeness.Unknown : outcome == Outcome.Partial
                    ? Completeness.Partial : Completeness.Complete;
            }
            if (error != null && disconnected != null)
            {
                var messages = error.Message.Contains(disconnected) ? new[] { error.Message } : new[] { error.Message, disconnected };
                string message = string.Join(" ", TiaMcp.Adapters.Contracts.NativeFailureText.BoundedLines(messages, line => line));
                error = new Error(message, error.Details, error.Innererror);
            }
            if (outcome != Outcome.Succeeded)
            {
                if (tool == "SynchronizeVersionControlWorkspace" && (Count(evidence, "unverifiedEqual") > 0 || Count(evidence, "skippedDisconnected") > 0))
                {
                    string[] Names(string result) => (evidence["items"] as JsonArray ?? new()).OfType<JsonObject>()
                        .Where(row => (string?)row["outcome"] == result).Select(row => (string?)row["name"] ?? "?").ToArray();
                    data["summary"] = (VciResult.StoredEqualSummary(Count(evidence, "unverifiedEqual"))
                        + (notVerifiedAdvice ?? VciResult.NotVerifiedSummary(Names("not-verified").Select(name => (name, false, false)).ToArray()))
                        + VciResult.DisconnectedSummary(Names("skipped-disconnected"))).TrimStart();
                }
                else data.Remove("summary");
            }
            var vciRows = legacyStatusRows ?? (tool == "SynchronizeVersionControlWorkspace" ? evidence["items"] as JsonArray : null);
            var readbacks = tool == "SynchronizeVersionControlWorkspace" ? evidence["failures"] as JsonArray : null;
            var rowsByName = vciRows?.OfType<JsonObject>().ToLookup(r => (string?)r["name"] ?? "");
            var failuresByName = readbacks?.OfType<JsonObject>().ToLookup(r => (string?)r["name"] ?? "");
            if (data["items"] is JsonArray lines)
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i] is JsonValue line && line.TryGetValue<string>(out var text))
                    {
                        int endName = text.IndexOf(" | ", StringComparison.Ordinal);
                        string name = endName < 0 ? text : text.Substring(0, endName);
                        JsonObject? Match(ILookup<string, JsonObject>? lookup)
                        {
                            var matches = lookup?[name].ToArray();
                            if (matches?.Length == 1) return matches[0];
                            return matches?.FirstOrDefault(r => r["filePath"] is JsonValue path && (text + " | ").Contains(" | file=" + path + " | "));
                        }
                        var observed = Match(rowsByName);
                        if (legacyStatusRows != null && observed is JsonObject row
                            && row["compareState"]?.ToString() == "Unknown" && row["error"] is JsonValue diagnostic
                            && diagnostic.TryGetValue<string>(out var statusError))
                        {
                            string format = row["fileFormat"]?.ToString() ?? "";
                            lines[i] = $"{row["name"]} | Unknown({statusError}) | file={row["filePath"]}" + (format.Length == 0 ? "" : " | format=" + format);
                            continue;
                        }
                        string note = "";
                        var readback = observed?["outcome"]?.ToString() == "failed" ? (string?)Match(failuresByName)?["refreshMessage"] : null;
                        int cut = text.IndexOf(VciResult.RefreshSeparator, StringComparison.Ordinal);
                        if (rowsByName != null && cut >= 0)
                            text = text.Substring(0, cut);
                        if (observed?.ContainsKey("refreshMessage") == true)
                        {
                            var refresh = (string?)observed["refreshMessage"];
                            if (refresh != null) note = VciResult.RefreshSeparator + refresh;
                            if (readback != null && readback != refresh) note += VciResult.RefreshSeparator + readback;
                        }
                        lines[i] = Regex.Replace(text, @"(\| FAILED:|\| could not enumerate children:|Unknown\()(.*)$", match =>
                            tool is ("ConnectProjectToWorkspace" or "SynchronizeVersionControlWorkspace") && match.Groups[1].Value == "| FAILED:"
                                && HostBehavior.SafeDiagnostic(match.Groups[2].Value.TrimStart()) is string reason
                                ? "| FAILED: " + reason : "| Native observation or operation failed.", RegexOptions.Singleline) + note;
                    }
            return Result(tool, data, error, outcome, completeness, writes, paging, current);
        }
        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (!(node is JsonObject obj)) return false;
            bool MissingRows(string countKey, string rowsKey) => obj[countKey] is JsonValue count && count.TryGetValue<int>(out var total)
                && obj[rowsKey] is JsonArray rows && rows.Count < total;
            return Flag(obj, "dataComplete") == false || Flag(obj, "incomplete") == true || Flag(obj, "truncated") == true
                || Flag(obj, "groupsTruncated") == true || Flag(obj, "foldersTruncated") == true || obj.ContainsKey("nameNote")
                || obj["compareState"]?.ToString() == "Unknown" || Flag(obj, "supported") == false
                || MissingRows("groupCount", "groups") || MissingRows("folderCount", "folders") || MissingRows("ruleCount", "rules")
                || obj.Any(p => p.Key.EndsWith("Error", StringComparison.Ordinal) && p.Value != null || Incomplete(p.Value));
        }
        private static void Clean(JsonNode? node, JsonArray? legacyStatusRows, bool diagnostic = false)
        {
            if (node is JsonArray array) { foreach (var item in array) Clean(item, legacyStatusRows, diagnostic); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "timestamp" || pair.Key == "lastFailure"
                    || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password",
                        StringComparison.OrdinalIgnoreCase)) obj.Remove(pair.Key);
                else if (diagnostic && (pair.Key == "text" || pair.Key == "detailText") && pair.Value is JsonValue text &&
                    text.TryGetValue<string>(out var detail))
                    obj[pair.Key] = HostBehavior.SafeDiagnostic(detail);
                else if (legacyStatusRows != null && obj.Parent == legacyStatusRows && pair.Key == "error" && obj["compareState"]?.ToString() == "Unknown"
                    && pair.Value is JsonValue failure && failure.TryGetValue<string>(out var message))
                    obj[pair.Key] = HostBehavior.SafeDiagnostic(message);
                else if ((pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal)) && pair.Value != null) obj[pair.Key] =
                    "Native diagnostic details are retained in the server log.";
                else if (pair.Key is "nativeMessage" or "refreshMessage")
                    obj[pair.Key] = HostBehavior.SafeDiagnostic(pair.Value?.ToString());
                else Clean(pair.Value, legacyStatusRows, diagnostic || pair.Key is "messageData" or "detailMessageData");
            }
        }
        private static Error NativeFailure(string? nativeMessage = null) =>
            new Error(nativeMessage ?? "The engineering operation did not establish success.",
                new NativeOperationFailedDetails(null, nativeMessage, new Dictionary<string, JsonElement>()));
        private static Error Unknown(IReadOnlyDictionary<string, JsonElement>? evidence = null) =>
            new Error("The write outcome is unknown. Inspect the target before repeating this call. Never replay it automatically. GetSessionState lists " +
                "unsettled request IDs.",
                new OutcomeUnknownDetails("engineering-operation", evidence ?? new Dictionary<string, JsonElement>()));
        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool writes = false, Paging? paging = null, bool current = true)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (data?["evidence"] is JsonObject nativeEvidence) warnings.AddRange(NativeResultState.Warnings(nativeEvidence, tool));
            if (HostBehavior.ReadbackWarning(data?["evidence"] as JsonObject, outcome) is Warning warning) warnings.Add(warning);
            else if (completeness == Completeness.Partial) warnings
                .Add(new Warning(WarningCode.IncompleteData, "The observation covers only the reported scope and available fields.", new Dictionary<string,
                        JsonElement>()));
            Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution
                    .ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, HardwareContract.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging,
                    warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }

}
