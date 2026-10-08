using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;

namespace TiaMcp.FoundationHost;

internal static class FoundationV4Result
{
    internal static CallToolResult ImportCandidate(Envelope envelope) => DeviceCandidate(envelope);
    internal static CallToolResult DeviceCandidate(Envelope envelope)
    {
        var result = McpResult.From(envelope);
        return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
            Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
    }
    internal static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","additionalProperties":false,"required":["schemaVersion","ok","data","error","meta"],"properties":{
          "schemaVersion":{"type":"integer","const":4},"ok":{"type":"boolean"},
          "data":{"type":["object","null"]},"error":{"type":["object","null"]},
          "meta":{"type":"object","additionalProperties":false,"required":["timestamp","releaseKey","tool","requestId","outcome","execution","requiresSessionReset","behaviorPolicy","completeness","paging","warnings"],"properties":{
            "timestamp":{"type":"string"},"releaseKey":{"type":["string","null"]},"tool":{"type":"string"},"requestId":{"type":"string"},
            "outcome":{"enum":["succeeded","rejected-before-operation","read-failed","failed","partial","unknown"]},
            "execution":{"enum":["not-started","read-only","completed","partial","unknown"]},"requiresSessionReset":{"type":"boolean"},
            "behaviorPolicy":{"enum":["not-applicable","current","safe-v4"]},"completeness":{"enum":["complete","partial","none","unknown"]},
            "paging":{"type":["object","null"]},"warnings":{"type":"array","items":{"type":"object"}}}}}}
        """);
    private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();
    internal static Error Invalid(string parameter) => new("Invalid Foundation argument. Nothing was executed.", new InvalidArgumentDetails(parameter, Array.Empty<string>()));
    internal static CallToolResult Reject(string release, string name, string id, Error error, bool current = false)
        => Wire(release, name, id, null, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, error, current, error.Code == ErrorCode.SessionResetRequired);
    internal static CallToolResult ReadinessUnavailable(string release, string name, string id, JsonObject readiness)
    {
        var environment = Object(readiness["environment"]);
        var data = new JsonObject { ["environment"] = environment };
        string cause = readiness["cause"]?.GetValue<string>() ?? "TIA Openness environment is not ready.";
        string fix = readiness["recommendedFix"]?.GetValue<string>() ?? "Run `tia doctor` to inspect the TIA Openness installation.";
        string fixZh = readiness["recommendedFixZh"]?.GetValue<string>() ?? fix;
        environment["ready"] = false;
        environment["cause"] = cause;
        environment["recommendedFix"] = fix;
        environment["recommendedFixZh"] = fixZh;
        return Wire(release, name, id, data, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None,
            new Error(cause + " " + fix, new ResourceUnavailableDetails("tia-openness-environment")), false);
    }
    internal static CallToolResult HostFailure(string release, string name, string id)
        => Wire(release, name, id, null, Outcome.ReadFailed, Execution.ReadOnly, Completeness.None,
            new Error("Foundation host operation failed.", new InternalErrorDetails(id)), false);

    internal static bool IsMutation(Definition definition, JsonObject args) =>
        definition.Arguments.Any(p => p.Name == "dryRun") ? args["dryRun"]?.GetValue<bool>() == false
        : definition.ResponseMember is "Connection" or "Bind" or "Disconnect";

    internal static bool PreviewHasNoEffect(JsonNode? body) => HostBehavior.PreviewHasNoEffect(body);

    internal static Error WorkerRejection(Exception exception)
    {
        var worker = exception as WorkerOperationException;
        string? message = HostBehavior.AdmissionDiagnostic(exception, worker?.Code, worker?.Outcome, worker?.ExceptionType);
        string? parameter = worker?.Parameter ?? TiaOpenness.Shared.HostFailurePolicy.Parameter(exception);
        var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(exception, false, true,
            (exception as WorkerOperationException)?.Outcome, (exception as WorkerOperationException)?.Code);
        return HostBehavior.FailureError(kind, parameter, Empty, message);
    }

    internal static CallToolResult Failure(string release, string name, string id, bool dispatched, bool mutation, JsonNode? evidence, Exception? exception = null)
    {
        var data = evidence == null ? null : Object(evidence);
        var worker = exception as WorkerOperationException;
        string? exceptionType = exception?.GetType().Name;
        if (worker?.EvidenceJson != null && worker.EvidenceJson != "null")
        {
            var retained = JsonNode.Parse(worker.EvidenceJson);
            data ??= new JsonObject();
            data["evidence"] = retained;
            if ((string?)retained?["recoveryStatus"] == "backup-skipped")
            {
                data["recoveryStatus"] = "backup-skipped";
                data["recoveryDirectory"] = retained?["recoveryDirectory"]?.DeepClone();
                data["recoveryWarning"] = retained?["recoveryWarning"]?.DeepClone();
                data["recoverySkipped"] = retained?["recoverySkipped"]?.DeepClone();
            }
            exceptionType = (string?)retained?["exceptionType"];
        }
        if (exception?.Data["foundationRequestSent"] is false) dispatched = false;
        if (!dispatched && exception?.Data["workerLimitBytes"] is int workerLimit)
            return Reject(release, name, id, new Error("Worker request exceeds its byte limit. Nothing was executed.", new LimitExceededDetails("arguments", workerLimit, null)), true);
        if (!dispatched && exception?.Data["foundationSessionPoisoned"] is true)
            return Reject(release, name, id, HostBehavior.SessionReset(), true);
        exception ??= new InvalidOperationException();
        var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(exception, dispatched, !mutation, worker?.Outcome, worker?.Code, exceptionType);
        string? parameter = worker?.Parameter ?? TiaOpenness.Shared.HostFailurePolicy.Parameter(exception);
        var details = HostBehavior.FailureEvidence(exceptionType, exception.Message);
        string? reason = TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.ExceptionReason(exception);
        return Wire(release, name, id, data, HostBehavior.OutcomeOf(kind), HostBehavior.ExecutionOf(kind), HostBehavior.CompletenessOf(kind),
            HostBehavior.FailureError(kind, parameter, details,
                HostBehavior.AdmissionDiagnostic(exception, worker?.Code, worker?.Outcome, worker?.ExceptionType),
                reason), true, kind == TiaOpenness.Shared.HostFailureKind.Unknown);
    }

    internal static CallToolResult Worker(string release, Definition definition, string id, JsonObject args, JsonNode? raw, JsonNode? validated)
    {
        string name = FoundationV4Tool.Name(definition.Name);
        var data = Object(raw);
        HostBehavior.ExportPreview(data);
        bool mutation = IsMutation(definition, args);
        bool preview = args["dryRun"]?.GetValue<bool>() == true;
        bool reset = Flag(data, "requiresSessionReset") == true;
        if (definition.ResponseMember == "BatchImport") return ImportCandidate(PlcBatchImportResultMapping.Result(data, release, name, id, preview));
        string? status = Text(data, "status");
        if (raw == null || mutation && Flag(data, "executed") == null && definition.ResponseMember is not ("Connection" or "Disconnect"))
            return Failure(release, name, id, true, mutation, raw);
        Outcome outcome = HostBehavior.OperationOutcome(status != "failed", status == "outcome-unknown", false, mutation);
        Error? error = null;
        if (definition.ResponseMember == "BatchExport")
        {
            var items = data["items"]!.AsArray();
            int succeeded = 0, failed = 0, skipped = 0;
            int? causeIndex = null;
            bool unknown = false;
            var mapped = new JsonArray();
            foreach (var item in items.OfType<JsonObject>())
            {
                string? state = Text(item, "status");
                var childOutcome = state is "not-attempted" or "inconsistent" ? Outcome.RejectedBeforeOperation
                    : state == "failed" && (Text(item, "failure") == "native-outcome-uncertain" || definition.ResponseMember == "BatchExport") ? Outcome.Unknown
                    : state is "failed" or "inconsistent" ? (preview || Flag(item, "attempted") == false ? Outcome.RejectedBeforeOperation : Outcome.Failed) : Outcome.Succeeded;
                if (childOutcome == Outcome.Succeeded) succeeded++;
                else if (state == "not-attempted") skipped++;
                else { failed++; if (state == "failed") causeIndex ??= mapped.Count; }
                unknown |= childOutcome == Outcome.Unknown;
                Error? childError = childOutcome == Outcome.Unknown ? Unknown()
                    : state == "not-attempted" ? new Error("Not executed after an earlier failure.", new NotExecutedDetails(causeIndex))
                    : childOutcome != Outcome.Succeeded ? NativeFailure(item) : null;
                var child = EnvelopeFor(release, name, id, item, childOutcome, Execute(childOutcome, mutation),
                    childOutcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete, childError, true, childOutcome == Outcome.Unknown);
                mapped.Add(new JsonObject { ["index"] = mapped.Count, ["target"] = Text(item, "objectPath") ?? Text(item, "relativePath"), ["result"] = JsonNode.Parse(V4Json.Serialize(child)) });
            }
            data["items"] = mapped;
            outcome = unknown ? Outcome.Unknown : failed + skipped == 0 ? Outcome.Succeeded
                : succeeded > 0 ? Outcome.Partial
                : mapped.All(x => (string?)x!["result"]!["meta"]!["execution"] == "not-started") ? Outcome.RejectedBeforeOperation : Outcome.Failed;
            if (outcome == Outcome.Partial) error = new Error("Some batch items did not complete.", new PartialFailureDetails(succeeded, failed, skipped));
        }
        if (definition.ResponseMember == "Compile" && !preview
            && ((data["errorCount"]?.GetValue<int>() ?? 0) > 0 || Text(data, "state") == "Error" || data["errors"] is JsonArray { Count: > 0 }))
            outcome = Outcome.Failed;
        if (outcome == Outcome.Unknown) error = Unknown();
        else if (outcome is Outcome.Failed or Outcome.RejectedBeforeOperation) error = definition.ResponseMember == "Compile" ? CompileResultMapping.Errors(data) ?? NativeFailure(data) : NativeFailure(data);
        // Preserve the void API's observations without inventing a native return value.
        if (definition.Operation == "GenerateBlocksFromExternalSource" && release == "14sp1")
        {
            data["nativeResult"] = null;
            data["observation"] = new JsonObject { ["source"] = data["resultBasis"]?.DeepClone(),
                ["objectsBefore"] = data["objectsBefore"]?.DeepClone(), ["objectsAfter"] = data["objectsAfter"]?.DeepClone(),
                ["observedChanges"] = data["observedChanges"]?.DeepClone() };
        }
        var validationData = Object(validated);
        bool incomplete = Incomplete(data) || Flag(data["meta"] as JsonObject, "complete") == false
            || validationData["meta"]?["unavailableAttributes"] is JsonArray { Count: > 0 }
            || definition.ResponseMember == "RuntimeQuery" || data["unavailableAttributes"] is JsonArray { Count: > 0 };
        return Wire(release, name, id, data, outcome, Execute(outcome, mutation && Flag(data, "executed") != false),
            outcome == Outcome.Unknown ? Completeness.Unknown : incomplete ? Completeness.Partial : Completeness.Complete,
            error, true, reset || outcome == Outcome.Unknown);
    }

    internal static CallToolResult Host(string release, string name, string id, JsonNode? raw, bool candidate, bool isError = false,
        IReadOnlyDictionary<string, JsonElement>? args = null)
    {
        var data = name == "GetToolUsage" && (string?)raw?["usage"]?["profile"] == "full-engine"
            ? raw!.DeepClone().AsObject() : Object(raw);
        // Collapse the legacy wrapper while retaining all domain and candidate evidence.
        if (data["data"] is JsonObject nested)
        {
            foreach (var pair in nested.ToArray()) if (!data.ContainsKey(pair.Key)) data[pair.Key] = pair.Value?.DeepClone();
            data.Remove("data");
        }
        if (data["usage"] is JsonObject usage) data = usage.DeepClone().AsObject();
        if (name == "InitializeEnvironment" || name == "RunCapabilitySelfTest")
            data["behaviorCapabilities"] = BehaviorCapabilities.Table(typeof(FoundationV4Result).Assembly, release);
        Paging? paging = null;
        if (name == "GetToolUsage" && (data["totalLines"] ?? data["totalMatches"]) is JsonValue totalValue)
        {
            int total = Math.Max(totalValue.GetValue<int>(), data["toolCount"]?.GetValue<int>() ?? 0);
            int offset = args != null && args.TryGetValue("offset", out var from) ? from.GetInt32() : 0;
            int limit = args != null && args.TryGetValue("limit", out var size) ? size.GetInt32() : 80;
            if (offset > total) return Reject(release, name, id, Invalid("offset"));
            bool complete = offset + limit >= total;
            paging = new Paging(PagingMode.Offset, offset, limit, complete ? null : offset + limit, null, null, total, complete);
            data.Remove("offset"); data.Remove("nextOffset"); data.Remove("nextToolOffset");
        }
        var success = !isError && (Flag(data, "success") ?? Flag(data, "ok") ?? Flag(data, "valid") ?? Flag(data["checks"] as JsonObject, "passed") ?? name == "GetToolUsage");
        return Wire(release, name, id, data, success ? Outcome.Succeeded : Outcome.ReadFailed, Execution.ReadOnly,
            name is "InitializeEnvironment" or "RunCapabilitySelfTest" ? Completeness.Partial : Completeness.Complete,
            success ? null : NativeFailure(data), false, false, candidate, paging);
    }

    private static Error Unknown() => new("The operation outcome is unknown; do not replay the request.", new OutcomeUnknownDetails("worker", Empty));
    private static Error NativeFailure(JsonObject data) => new("The operation did not establish success; inspect the returned result.",
        new NativeOperationFailedDetails(null, Text(data, "summary"), data.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value))));
    private static Execution Execute(Outcome outcome, bool mutation) => HostBehavior.ExecutionOf(outcome, mutation);
    private static bool? Flag(JsonObject? data, string key) => data?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static string? Text(JsonObject data, string key) => data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Incomplete(JsonNode? node) => node is JsonArray rows ? rows.Any(Incomplete)
        : node is JsonObject obj && (obj["unavailableAttributes"] is JsonArray { Count: > 0 }
            || Flag(obj, "truncated") == true || Flag(obj, "complete") == false || obj.Any(p => Incomplete(p.Value)));
    internal static JsonObject Object(JsonNode? raw) => Fields(raw) switch {
        JsonObject obj => obj, JsonArray items => new JsonObject { ["items"] = items },
        JsonNode value => new JsonObject { ["value"] = value }, _ => new JsonObject() };
    private static JsonNode? Fields(JsonNode? raw)
    {
        if (raw is JsonArray rows) return new JsonArray(rows.Select(Fields).ToArray());
        if (raw is not JsonObject obj) return raw?.DeepClone();
        var data = new JsonObject();
        foreach (var pair in obj)
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(pair.Key);
            data[key == "message" ? "summary" : key] = key is "value" or "evidence" ? pair.Value?.DeepClone() : Fields(pair.Value);
        }
        return data;
    }
    private static Envelope EnvelopeFor(string release, string name, string id, JsonObject? data, Outcome outcome,
        Execution execution, Completeness completeness, Error? error, bool current, bool reset, bool candidate = false, Paging? paging = null)
    {
        var warnings = new List<Warning>();
        if (current) warnings.Add(new Warning(WarningCode.UnverifiedBehavior, "Native behavior retains the current policy; V4 native acceptance is pending.", Empty));
        if (candidate) warnings.Add(new Warning(WarningCode.CandidateOnly, "Candidate output only; target schema, import and program semantics remain unverified.", Empty));
        if (HostBehavior.BackupSkipped(data) is Warning backup) warnings.Add(backup);
        if (current && data?["warnings"] is JsonArray { Count: > 0 })
            warnings.Add(new Warning(WarningCode.NativeWarning, "Native diagnostics include warnings; see data.warnings.", Empty));
        if (completeness == Completeness.Partial) warnings.Add(new Warning(WarningCode.IncompleteData, "The observation is incomplete; retain the declared scope and unprobed fields.", Empty));
        return BehaviorCapabilities.Disclose(Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, name, id, outcome, execution, reset,
            current ? BehaviorPolicy.Current : BehaviorPolicy.NotApplicable, completeness, paging, warnings)));
    }
    private static CallToolResult Wire(string release, string name, string id, JsonObject? data, Outcome outcome,
        Execution execution, Completeness completeness, Error? error, bool current, bool reset = false, bool candidate = false, Paging? paging = null)
    {
        var mapped = McpResult.From(EnvelopeFor(release, name, id, data, outcome, execution, completeness, error, current, reset, candidate, paging));
        return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
            Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
    }
}
