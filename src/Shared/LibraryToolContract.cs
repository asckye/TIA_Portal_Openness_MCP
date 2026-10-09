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

namespace TiaMcpServer.ModelContextProtocol
{
    internal static class LibraryToolContract
    {
        internal static void Check(Action validation)
        {
            try { validation(); }
            catch (ArgumentException) /* swallow(privacy): legacy validators can contain caller values */
            { throw new InputFailure(McpServer.InvalidInput("arguments")); }
            catch (JsonException) /* swallow(privacy): malformed typed values are rejected before native work */
            { throw new InputFailure(McpServer.InvalidInput("arguments")); }
            catch (NotSupportedException) /* swallow(privacy): preserve capability rejection without native details */
            { throw new InputFailure(new Error("This operation is unavailable.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "library-sivarc", null))); }
        }
        private sealed class InputFailure : Exception
        {
            internal Error Error { get; }
            internal InputFailure(Error error) => Error = error;
        }
        internal static CallToolResult Run(string tool, bool writes, bool current, Func<object> operation)
        {
            try { return Map(tool, operation(), writes, current); }
            catch (InputFailure ex) { return Result(tool, null, ex.Error, Outcome.RejectedBeforeOperation, Completeness.None, current: current); }
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
                    return Result(tool, null, new Error("The engineering prerequisites were not satisfied.", details), Outcome.RejectedBeforeOperation, Completeness.None, current: current);
                }
                return Result(tool, new JsonObject { ["exceptionType"] = ex.GetType().Name },
                    writes ? Unknown() : NativeFailure(), writes ? Outcome.Unknown : Outcome.ReadFailed, Completeness.Unknown, current: current);
            }
        }
        private static int Count(JsonObject data, string key)
            => data[key] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;
        private static bool? Flag(JsonObject obj, string key)
            => obj[key] is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        internal static CallToolResult Map(string tool, object response, bool writes, bool current = true)
        {
            var data = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
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
            data["evidence"] = evidence;
            bool? success = Flag(evidence, "operationSuccess") ?? Flag(evidence, "success") ?? Flag(data, "ok");
            if (Flag(data, "ok") == false) success = false;
            string? state = (evidence["transferResultState"] ?? evidence["nativeState"] ?? evidence["state"] ?? evidence["result"]?["state"])?.ToString();
            bool nativeKnown = NativeResultState.Classify(evidence, tool) != TiaMcp.Adapters.Contracts.NativeStateKind.Unexpected;
            if (state != null || evidence.ContainsKey("nativeState"))
                success = NativeResultState.Succeeded(evidence, tool) && success != false;
            bool? generation = Flag(evidence, "generationPassed") ?? (evidence["result"] is JsonObject generationResult ? Flag(generationResult, "isGenerationSuccessful") : null);
            if (generation.HasValue) { nativeKnown = true; success = generation.Value && success != false; }
            bool issued = Flag(evidence, "mayHaveChanged") == true || Flag(evidence, "mayHaveWrittenFiles") == true;
            bool before = !issued && (writes && Flag(evidence, "mayHaveChanged") == false || evidence["v4Rejection"] != null);
            string status = evidence["v4Rejection"]?.ToString() ?? evidence["status"]?.ToString() ?? "";
            bool projectMissing = status == "PROJECT_NOT_BOUND" || status == "InvalidState" && evidence.ContainsKey("tool")
                && !evidence.ContainsKey("apiCallSuccess") && !issued;
            bool reset = Flag(evidence, "requiresExplicitRebind") == true || Flag(evidence, "requiresSessionReset") == true;
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
                    : status == "ALREADY_EXISTS" ? new AlreadyExistsDetails(null)
                    : status == "INVALID_ARGUMENT" ? new InvalidArgumentDetails("arguments", Array.Empty<string>())
                    : new PreconditionFailedDetails("engineering-before-operation", null);
                error = new Error("The engineering request was rejected before execution.", details);
            }
            else if (!writes) { outcome = Outcome.ReadFailed; error = NativeFailure(); }
            else if (succeeded > 0)
            { outcome = Outcome.Partial; error = new Error("Some engineering operations did not complete.", new PartialFailureDetails(succeeded, Math.Max(1, failed), 0)); }
            else { outcome = Outcome.Failed; error = NativeFailure(); }
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
            Clean(data);
            if (NativeResultState.TryUnsuccessful(evidence, writes, out var nativeOutcome, out var nativeError, tool)) { outcome = nativeOutcome; error = nativeError; completeness = outcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete; }
            if (outcome != Outcome.Succeeded) data.Remove("summary");
            if (data["items"] is JsonArray lines)
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i] is JsonValue line && line.TryGetValue<string>(out var text))
                        lines[i] = Regex.Replace(text, @"(\| FAILED:|\| could not enumerate children:|Unknown\().*$", "| Native observation or operation failed.", RegexOptions.Singleline);
            return Result(tool, data, error, outcome, completeness, writes, paging, reset, current);
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
        private static void Clean(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array) Clean(item); return; }
            if (!(node is JsonObject obj)) return;
            foreach (var pair in obj.ToArray())
            {
                if (pair.Key == "timestamp" || pair.Key == "messageData" || pair.Key == "detailMessageData" || pair.Key == "lastFailure"
                    || pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) obj.Remove(pair.Key);
                else if ((pair.Key == "error" || pair.Key.EndsWith("Error", StringComparison.Ordinal)) && pair.Value != null) obj[pair.Key] = "Native diagnostic details are retained in the server log.";
                else Clean(pair.Value);
            }
        }
        private static Error NativeFailure() => new Error("The engineering operation did not establish success.", new NativeOperationFailedDetails(null, null, new Dictionary<string, JsonElement>()));
        private static Error Unknown() => new Error("The write outcome is unknown. Inspect the retained evidence and reset the session before further writes.", new OutcomeUnknownDetails("engineering-operation", new Dictionary<string, JsonElement>()));
        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool writes = false, Paging? paging = null, bool reset = false, bool current = true)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior,
                "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (data?["evidence"] is JsonObject nativeEvidence) warnings.AddRange(NativeResultState.Warnings(nativeEvidence, tool));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation covers only the reported scope and available fields.", new Dictionary<string, JsonElement>()));
            Execution execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : outcome == Outcome.ReadFailed || outcome == Outcome.Succeeded && !writes ? Execution.ReadOnly : Execution.Completed;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome,
                execution, reset || outcome == Outcome.Unknown, current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var result = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
        }
    }

}
