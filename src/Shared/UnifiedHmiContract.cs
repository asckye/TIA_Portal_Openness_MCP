using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.V4;
using ModelContextProtocol.Protocol;
using System.Collections.Generic;
using System.ComponentModel;
using ModelContextProtocol.Server;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    // P6-17 response mapping only. The existing services retain native calls and
    // their execution order; shared admission owns the typed wire contracts.
    internal static class UnifiedHmiContract
    {
        internal static string MappingEntries(DynamizationMapping[]? entries)
        {
            var result = new JsonArray();
            foreach (var entry in entries ?? Array.Empty<DynamizationMapping>())
            {
                var row = JsonNode.Parse(V4Json.Serialize(entry.Properties))!.AsObject();
                row["kind"] = entry.Kind;
                result.Add(row);
            }
            return result.ToJsonString();
        }

        internal static CallToolResult Run(string tool, bool writes, bool current, Func<object> operation,
            int? offset = null, int? limit = null)
        {
            try { return Map(tool, operation(), writes, current, offset, limit); }
            catch (Exception) /* swallow(privacy): escaped writes have no confirmed outcome; diagnostics stay in the existing log */
            {
                return Result(tool, null, writes ? Unknown(new JsonObject()) : NativeFailure(new JsonObject()),
                    writes ? Outcome.Unknown : Outcome.ReadFailed, Completeness.Unknown, writes, current);
            }
        }

        internal static CallToolResult Map(string tool, object response, bool writes, bool current,
            int? offset = null, int? limit = null)
        {
            var data = JsonSerializer.SerializeToNode(response, response.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            var evidence = data["meta"] as JsonObject ?? new JsonObject();
            data.Remove("meta");
            evidence.Remove("timestamp");
            if (data["data"] is JsonObject report)
            {
                data.Remove("data");
                foreach (var pair in report) data[pair.Key] = pair.Value?.DeepClone();
            }
            data["summary"] = data["message"]?.DeepClone();
            data.Remove("message");
            data["evidence"] = evidence;
            if (evidence["records"] is JsonArray records) data["items"] = records.DeepClone();
            else if (evidence["itemResults"] is JsonArray results) data["items"] = results.DeepClone();
            Clean(data);

            bool? success = Flag(evidence, "operationSuccess") ?? Flag(evidence, "success") ?? Flag(evidence, "ok") ?? Flag(data, "ok");
            if (Flag(data, "ok") == false || Flag(evidence, "ok") == false || Flag(evidence, "success") == false) success = false;
            var status = evidence["status"]?.ToString();
            var rejection = evidence["v4Rejection"]?.ToString();
            if (evidence["v4ArgumentError"] is JsonObject argumentError)
                return Result(tool, data, V4Json.Deserialize<Error>(argumentError.ToJsonString()),
                    Outcome.RejectedBeforeOperation, Completeness.None, writes, current);
            if (status == "HmiReadSessionBlocked")
                return Result(tool, data, new Error("The HMI session requires an explicit rebind.", new SessionResetRequiredDetails("HMI read session blocked")),
                    Outcome.RejectedBeforeOperation, Completeness.None, writes, current);
            if (status == "InvalidState" && !evidence.ContainsKey("apiCallSuccess")) rejection = "PROJECT_NOT_BOUND";
            if (rejection != null)
                return Result(tool, data, new Error("The HMI request was rejected before the operation.",
                    rejection == "PROJECT_NOT_BOUND" ? (ErrorDetails)new ProjectNotBoundDetails() : new NotFoundDetails(null)),
                    Outcome.RejectedBeforeOperation, Completeness.None, writes, current);
            if (evidence["applyStatus"]?.ToString() == "rejected")
                return Result(tool, data, McpServer.InvalidInput("actionKind"), Outcome.RejectedBeforeOperation, Completeness.None, writes, current);
            if (tool == "ManageUnifiedListEntries" && evidence["action"]?.ToString() != "read" && Flag(evidence, "entriesSupported") == false)
                return Result(tool, data, new Error("The public API does not expose list entry editing.", new UnsupportedCapabilityDetails(McpServer.ReleaseKey, "Unified list entries", evidence["action"]?.ToString())),
                    Outcome.RejectedBeforeOperation, Completeness.Partial, false, current);

            // A recipe's offline 'ok' is not the verdict of either native write.
            if (evidence["setMeta"] is JsonObject set)
                success = Flag(set, "operationSuccess") ?? Flag(set, "success");
            if (evidence["ensureMeta"] is JsonObject ensure && (Flag(ensure, "operationSuccess") ?? Flag(ensure, "success")) != true)
                success = false;
            if (evidence["steps"] is JsonArray steps && steps.Any(s => Flag(s, "ok") != true)) success = false;
            var items = evidence["itemResults"] as JsonArray;
            int passed = items?.Count(i => Flag(i, "success") == true) ?? 0;
            int failed = items?.Count(i => Flag(i, "success") != true) ?? 0;
            int otherFailures = (evidence["failed"] as JsonArray)?.Count ?? 0;
            if (failed > 0 || otherFailures > 0) success = false;
            bool uncertainItem = items?.Any(i => Flag(i, "success") != true && Flag(i, "mayHaveChanged") != false) == true;
            bool incomplete = Incomplete(evidence) || Incomplete(data["items"]);
            bool unconfirmed = Unconfirmed(evidence) || otherFailures > failed;
            if (writes && (unconfirmed || uncertainItem || success != true && Flag(evidence, "mayHaveChanged") != false
                && !(failed > 0 && passed > 0 && items!.All(i => Flag(i, "success") == true || Flag(i, "mayHaveChanged") == false))))
                return Result(tool, data, Unknown(evidence), Outcome.Unknown, Completeness.Unknown, writes, current);
            if (success != true)
            {
                if (writes && passed > 0 && failed > 0)
                    return Result(tool, data, new Error("Some HMI items did not complete.", new PartialFailureDetails(passed, failed, 0)),
                        Outcome.Partial, Completeness.Partial, writes, current);
                bool before = writes && Flag(evidence, "mayHaveChanged") == false;
                return Result(tool, data, before ? new Error("The HMI operation was rejected before a write.", new PreconditionFailedDetails(null, null)) : NativeFailure(evidence),
                    before ? Outcome.RejectedBeforeOperation : Outcome.ReadFailed, incomplete ? Completeness.Partial : Completeness.None, writes, current);
            }
            Paging? paging = null;
            int? total = Integer(evidence["expectedCount"]);
            if (offset >= 0 && limit > 0 && total >= 0)
            {
                int? next = Integer(evidence["nextOffset"]);
                paging = new Paging(PagingMode.Offset, offset, limit.Value, next, null, null, total, next == null);
            }
            return Result(tool, data, null, Outcome.Succeeded, incomplete ? Completeness.Partial : Completeness.Complete, writes, current, paging);
        }

        private static bool? Flag(JsonNode? node, string name)
            => node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        private static int? Integer(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;
        private static bool Unconfirmed(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Unconfirmed);
            if (node is not JsonObject obj) return false;
            return Flag(obj, "verified") == false || Flag(obj, "verifiedAbsent") == false || Flag(obj, "bindingVerified") == false
                || Flag(obj, "requiresSessionReset") == true || Flag(obj, "connectionUnavailable") == true
                || Flag(obj, "setScriptCode") == false || Flag(obj, "setGlobalDefinitionAreaScriptCode") == false || Flag(obj, "setAsync") == false
                || obj["syntaxCheckStatus"]?.ToString() == "faulted" || Integer(obj["syntaxErrorCount"]) > 0
                || obj.Any(p => Unconfirmed(p.Value));
        }
        private static bool Incomplete(JsonNode? node)
        {
            if (node is JsonArray array) return array.Any(Incomplete);
            if (node is not JsonObject obj) return false;
            return Flag(obj, "dataComplete") == false || Flag(obj, "truncated") == true || obj.Any(p => Incomplete(p.Value));
        }
        private static IReadOnlyDictionary<string, JsonElement> Evidence(JsonObject value)
            => value.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);
        private static Error Unknown(JsonObject evidence) => new Error("The HMI write outcome is unconfirmed. Inspect the evidence and reset the session before further writes.",
            new OutcomeUnknownDetails("HMI operation", Evidence(evidence)));
        private static Error NativeFailure(JsonObject evidence) => new Error("The HMI operation did not establish success.", new NativeOperationFailedDetails(null, null, Evidence(evidence)));
        private static void Clean(JsonNode? node)
        {
            if (node is JsonArray array) foreach (var child in array) Clean(child);
            if (node is not JsonObject obj) return;
            foreach (var pair in obj.ToArray())
                if (pair.Key.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0 || pair.Key.Equals("password", StringComparison.OrdinalIgnoreCase)) obj.Remove(pair.Key);
                else if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Contains("\n   ")) obj[pair.Key] = "Diagnostic details retained in the server log.";
                else Clean(pair.Value);
        }
        private static CallToolResult Result(string tool, JsonObject? data, Error? error, Outcome outcome, Completeness completeness,
            bool writes, bool current, Paging? paging = null)
        {
            var warnings = new List<Warning>();
            if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", new Dictionary<string, JsonElement>()));
            if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The returned HMI observation is incomplete; inspect the retained evidence.", new Dictionary<string, JsonElement>()));
            var execution = outcome == Outcome.RejectedBeforeOperation ? Execution.NotStarted : outcome == Outcome.Unknown ? Execution.Unknown
                : outcome == Outcome.Partial ? Execution.Partial : writes ? Execution.Completed : Execution.ReadOnly;
            var meta = new Meta(DateTimeOffset.UtcNow, McpServer.ReleaseKey, tool, Meta.Correlate(InvocationJournal.CorrelationId), outcome, execution,
                outcome == Outcome.Unknown || error?.Code == ErrorCode.SessionResetRequired
                    || Flag(data?["evidence"], "connectionUnavailable") == true || Flag(data?["evidence"], "requiresExplicitRebind") == true,
                current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings);
            var mapped = McpResult.From(Envelope.Create(data, error, meta));
            return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
                Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
        }
    }

}
