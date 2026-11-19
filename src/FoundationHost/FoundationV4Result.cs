#if TIA_FOUNDATION_TEST_HOST
extern alias toolpipeline;
using LateCalls = toolpipeline::TiaMcp.FoundationHost.LateCalls;
using HostMcp = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using UnifiedInputAdmission = toolpipeline::TiaMcpServer.Siemens.Services.UnifiedPortService;
#else
using HostMcp = TiaMcpServer.ModelContextProtocol.McpServer;
using UnifiedInputAdmission = TiaMcpServer.Siemens.Services.UnifiedPortService;
#endif
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.FoundationHost;

internal static class FoundationV4Result
{
    internal static CallToolResult RestrictArguments(CallToolResult result, JsonElement schema, JsonElement arguments)
    {
        if (result.StructuredContent is not JsonObject body || (string?)body["error"]?["code"] != "INVALID_ARGUMENT") return result;
        var error = V4Json.Deserialize<Error>(body["error"]!.ToJsonString());
        var restricted = new InputSchema(schema).Restrict(error, arguments, body["data"] as JsonObject);
        restricted = UnifiedInputAdmission.UnknownRuntimeChangeError((string?)body["meta"]?["tool"], arguments, error) ?? restricted;
        // Vault admission must identify the exact refused position, including caller-chosen map keys.
        if (error.Details is InvalidArgumentDetails { Expected: "declared credential parameter" } vaultPointer)
            restricted = new Error(restricted.Message, vaultPointer);
        body["error"] = JsonNode.Parse(V4Json.Serialize(restricted));
        return new CallToolResult { IsError = result.IsError, StructuredContent = body,
            Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
    }

    internal static bool Classified(JsonNode? body) => body is JsonObject source && source["meta"] is JsonObject meta && meta["outcome"] is JsonValue outcome
        && outcome.TryGetValue<string>(out var value)
            && value is "succeeded" or "rejected-before-operation" or "read-failed" or "failed" or "partial" or "unknown" or "in-progress";
    internal static CallToolResult InProgress(string release, string name, JsonObject running)
        => LateCalls.InProgress(release, name, running);
    internal static CallToolResult LateFailure(string release, string name, string id)
        => Wire(release, name, id, null, Outcome.Unknown, Execution.Unknown, Completeness.Unknown, Unknown(), true);
    internal static CallToolResult WarnStatusUnavailable(CallToolResult result)
    {
        if (result.StructuredContent?["meta"] is JsonObject meta && (meta["warnings"] ??= new JsonArray()) is JsonArray warnings)
        {
            warnings.Add(JsonNode.Parse(V4Json.Serialize(new Warning(WarningCode.IncompleteData,
                "The worker returned its result, but the subsequent session status read failed.", Empty))));
            var content = result.Content.ToList();
            var text = new TextContentBlock { Text = result.StructuredContent.ToJsonString() };
            if (content.FirstOrDefault() is TextContentBlock) content[0] = text;
            else content.Insert(0, text);
            result.Content = content;
        }
        return result;
    }

    internal static CallToolResult WorkerFailure(CallToolResult result, string release, string name, string id, bool mutation, Exception? failure)
    {
        if (failure?.Data["workerPortalLost"] is string)
            return Failure(release, name, id, true, mutation, (result.StructuredContent as JsonObject)?["data"], failure);
        if (failure == null || result.IsError != true || Classified(result.StructuredContent)
            || failure is not WorkerOperationException && !failure.Data.Contains("foundationRequestSent")) return result;
        return Failure(release, name, id, failure.Data["foundationRequestSent"] is not false, mutation,
            (result.StructuredContent as JsonObject)?["data"], failure);
    }
    internal static CallToolResult ImportCandidate(Envelope envelope) => DeviceCandidate(envelope);
    internal static CallToolResult DeviceCandidate(Envelope envelope)
    {
        var result = McpResult.From(envelope);
        return new CallToolResult { IsError = result.IsError, StructuredContent = JsonNode.Parse(result.StructuredContent.GetRawText()),
            Content = new[] { new TextContentBlock { Text = result.Content[0].Text } } };
    }
    internal static readonly JsonElement Schema = EnvelopeSchema.Value;
    internal static JsonObject? NativeDiagnostics(JsonNode? reply)
    {
        if (reply is not JsonObject value) return null;
        var failure = value["Failure"] as JsonObject;
        var failureEvidence = failure?["Evidence"] as JsonObject;
        string? message = (failureEvidence == null ? null : Text(failureEvidence, "nativeMessage"))
            ?? (failure == null ? null : Text(failure, "Message")) ?? Text(value, "nativeMessage") ?? Text(value, "error");
        if (!string.IsNullOrWhiteSpace(message))
        {
            string? type = failure == null ? Text(value, "exceptionType") ?? Text(value, "nativeExceptionType")
                : failureEvidence == null ? null : Text(failureEvidence, "exceptionType") ?? Text(failureEvidence, "nativeExceptionType");
            int colon = message.IndexOf(':');
            if (type == null && failure == null && colon > 0 && colon < 256
                && message[..colon].All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '+')
                && message[..colon].EndsWith("Exception", StringComparison.Ordinal)) type = message[..colon];
            type = new NativeInnerError(type, null, null).ExceptionType;
            return new JsonObject { ["exceptionType"] = type,
                ["hResult"] = NativeHResult(failure == null ? value["hResult"] ?? value["nativeHResult"] : failureEvidence?["hResult"]),
                ["nativeMessage"] = HostBehavior.SafeDiagnostic(message) };
        }
        foreach (string member in new[] { "Meta", "Value", "Step", "Import" })
            if (NativeDiagnostics(value[member]) is { } diagnostic) return diagnostic;
        return null;
    }

    internal static CallToolResult ReconcileWorker(CallToolResult result, JsonObject? diagnostics, string? tool = null,
        Exception? lastFailure = null)
    {
        if (result.StructuredContent is not JsonObject source) return result;
        var body = (JsonObject)source.DeepClone();
        if ((string?)body["error"]?["code"] == "OUTCOME_UNKNOWN" && lastFailure is WorkerOperationException { Outcome: "unknown" }
            && body["error"]?["details"]?["evidence"] is JsonObject retained
            && tool != null && HostBehavior.WorkerReplyEvidence(tool, lastFailure) is { } unknownEvidence)
            foreach (var field in unknownEvidence) retained[field.Key] = JsonSerializer.SerializeToNode(field.Value);
        if (diagnostics != null && body["error"] is JsonObject error && (string?)error["code"] == "NATIVE_OPERATION_FAILED"
            && error["details"] is JsonObject details)
        {
            var evidence = details["evidence"] as JsonObject ?? new JsonObject();
            foreach (var pair in diagnostics) evidence[pair.Key] = pair.Value?.DeepClone();
            if (body["data"] is JsonObject diagnosticData)
            {
                foreach (var target in new[] { diagnosticData, diagnosticData["evidence"] as JsonObject })
                    if (target != null)
                        foreach (string field in new[] { "exceptionType", "hResult" })
                            if (target.ContainsKey(field)) target[field] = diagnostics[field]?.DeepClone();
            }
            // Error detail variants have fixed fields. Retain diagnostics in data for variants without evidence.
            if (details.ContainsKey("evidence")) details["evidence"] = evidence;
            else
            {
                var data = body["data"] as JsonObject ?? new JsonObject();
                data["nativeDiagnostics"] = diagnostics.DeepClone(); body["data"] = data;
            }
            if (details.ContainsKey("nativeMessage")) details["nativeMessage"] = diagnostics["nativeMessage"]?.DeepClone();
            var native = new NativeOperationFailedDetails((string?)details["nativeCode"], (string?)details["nativeMessage"],
                evidence.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)));
            error["details"] = JsonNode.Parse(V4Json.Serialize(native));
            error["innererror"] = JsonNode.Parse(V4Json.Serialize(new NativeInnerError((string?)diagnostics["exceptionType"],
                NativeHResult(diagnostics["hResult"]), (string?)body["meta"]?["requestId"])));
        }
        HostBehavior.SanitizeEvidence(body["data"], evidence: false);
        HostBehavior.SanitizeEvidence(body["error"]?["details"]);
        return new CallToolResult { IsError = result.IsError, StructuredContent = body,
            Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
    }
    private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();
    private static int? NativeHResult(JsonNode? value)
    {
        if (value == null) return null;
        var number = JsonSerializer.SerializeToElement(value);
        return number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out int code) ? code : null;
    }
    internal static Error Invalid(string parameter) =>
        new("Invalid Foundation argument. Nothing was executed.", new InvalidArgumentDetails(parameter, Array.Empty<string>()));
    internal static CallToolResult Reject(string release, string name, string id, Error error, bool current = false)
        =>
            Wire(release, name, id, null, Outcome.RejectedBeforeOperation, Execution.NotStarted, Completeness.None, error, current);
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
    internal static CallToolResult HostSessionState(string release, string id, JsonObject data)
        => Wire(release, "GetSessionState", id, data, Outcome.Succeeded,
            Execution.ReadOnly, Completeness.Partial, null, true);

    internal static CallToolResult HostFailure(string release, string name, string id)
        => Wire(release, name, id, null, Outcome.ReadFailed, Execution.ReadOnly, Completeness.None,
            new Error("Foundation host operation failed.", new InternalErrorDetails(id)), false);

    internal static bool IsMutation(Definition definition, JsonObject args) =>
        definition.Arguments.Any(p => p.Name == "dryRun") ? args["dryRun"]?.GetValue<bool>() == false
        : definition.ResponseMember is "Connection" or "Bind" or "Disconnect";

    internal static bool PreviewHasNoEffect(JsonNode? body) => HostBehavior.PreviewHasNoEffect(body);

    internal static CallToolResult UnknownScriptImport(string release, string id, IReadOnlyDictionary<string, JsonElement> facts,
        bool priorMutation)
    {
        var data = JsonSerializer.SerializeToNode(facts)!.AsObject();
        if (priorMutation) data["earlierMutationMayHaveChanged"] = true;
        return Wire(release, "SetUnifiedGlobalScript", id, data, Outcome.Unknown, Execution.Unknown, Completeness.Unknown,
            priorMutation ? EarlierMutation(facts) : HostBehavior.FailureError(TiaOpenness.Shared.HostFailureKind.Unknown, null, facts), true);
    }

    private static Error EarlierMutation(IReadOnlyDictionary<string, JsonElement> evidence) =>
        new("An earlier operation may have changed state. Do not repeat this call. Read GetCallResult(requestId) and inspect the target.",
            new OutcomeUnknownDetails("tool-call", evidence));

    private static CallToolResult PortalLossFailure(string release, string name, string id, JsonObject? data,
        WorkerOperationException failure, string lost)
    {
        var facts = (HostBehavior.WorkerReplyEvidence(name, failure) ?? Empty).ToDictionary(pair => pair.Key, pair => pair.Value);
        facts["portalLost"] = JsonSerializer.SerializeToElement(HostBehavior.SafeDiagnostic(lost));
        if (name == "SetUnifiedGlobalScript") data = JsonSerializer.SerializeToNode(facts)!.AsObject();
        else if (failure.EvidenceJson != null)
        {
            var retained = JsonNode.Parse(failure.EvidenceJson);
            HostBehavior.SanitizeEvidence(retained);
            (data ??= new JsonObject())["evidence"] = retained;
        }
        bool priorMutation = TiaOpenness.Shared.HostFailurePolicy.PriorMutation(failure);
        if (priorMutation) (data ??= new JsonObject())["earlierMutationMayHaveChanged"] = true;
        const string message = "The TIA process exited during this call. The session is lost. "
            + "Use ConnectIsolatedPortal to start a new instance, or call DisconnectPortal then ConnectPortal to select an existing one.";
        if (failure.Outcome == "unknown" || priorMutation)
            return Wire(release, name, id, data, Outcome.Unknown, Execution.Unknown, Completeness.Unknown,
                new Error(message + " Do not repeat this call. Read GetCallResult(requestId) and inspect the target.",
                    new OutcomeUnknownDetails("tool-call", facts, "tia-process-exited")), true);
        return Wire(release, name, id, data, Outcome.ReadFailed, Execution.ReadOnly, Completeness.None,
            new Error(message, new NativeOperationFailedDetails(null, message, facts)), true);
    }

    internal static Error WorkerRejection(Exception exception)
    {
        var worker = exception as WorkerOperationException;
        string? message = HostBehavior.AdmissionDiagnostic(exception, worker?.Code, worker?.Outcome, worker?.ExceptionType);
        string? parameter = worker?.Parameter ?? TiaOpenness.Shared.HostFailurePolicy.Parameter(exception);
        var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(exception, false, true,
            (exception as WorkerOperationException)?.Outcome, (exception as WorkerOperationException)?.Code);
        return HostBehavior.FailureError(kind, parameter, Empty, message);
    }

    internal static CallToolResult Failure(string release, string name, string id, bool dispatched, bool mutation, JsonNode? evidence, Exception? exception =
            null)
    {
        var data = evidence == null ? null : Object(evidence);
        if (exception is WorkerOperationException portalFailure && exception.Data["workerPortalLost"] is string lost)
            return PortalLossFailure(release, name, id, data, portalFailure, lost);
        if (data?["failure"] is JsonObject workerDiagnostic)
            data["failure"] = JsonNode.Parse(NativeFailureDiagnostics.Evidence(JsonSerializer.SerializeToElement(workerDiagnostic)).GetRawText());
        if (exception != null && TiaOpenness.Shared.HostFailurePolicy.PriorMutation(exception))
        {
            if (name == "SetUnifiedGlobalScript")
                return UnknownScriptImport(release, id, HostBehavior.WorkerReplyEvidence(name, exception) ?? Empty, true);
            data ??= new JsonObject();
            data["earlierMutationMayHaveChanged"] = true;
            return Wire(release, name, id, data, Outcome.Unknown, Execution.Unknown, Completeness.Unknown, EarlierMutation(Empty), true);
        }
        var worker = exception as WorkerOperationException;
        string? exceptionType = worker == null ? exception?.GetType().Name : worker.ExceptionType;
        int? hResult = worker == null ? exception?.HResult : null;
        if (worker?.EvidenceJson != null && worker.EvidenceJson != "null")
        {
            var retained = JsonNode.Parse(worker.EvidenceJson);
            if (retained is JsonObject retainedObject && retainedObject["nativeMessage"] is JsonValue nativeMessage)
                retainedObject["nativeMessage"] = HostBehavior.SafeDiagnostic(nativeMessage.GetValue<string>());
            HostBehavior.SanitizeEvidence(retained);
            data ??= new JsonObject();
            data["evidence"] = retained;
            if (name == "ImportPlcBlock" && worker.KnownNoMutation && retained is JsonObject importEvidence
                && (bool?)importEvidence["verifiedAbsent"] == true && (bool?)importEvidence["mayHaveChanged"] == false)
                return Wire(release, name, id, data, Outcome.Failed, Execution.Completed, Completeness.Complete,
                    HostBehavior.UnchangedNativeError(importEvidence), true);
            if ((string?)retained?["recoveryStatus"] == "backup-skipped")
            {
                data["recoveryStatus"] = "backup-skipped";
                data["recoveryDirectory"] = retained?["recoveryDirectory"]?.DeepClone();
                data["recoveryWarning"] = retained?["recoveryWarning"]?.DeepClone();
                data["recoverySkipped"] = retained?["recoverySkipped"]?.DeepClone();
            }
            exceptionType = (string?)retained?["exceptionType"];
            hResult = NativeHResult(retained?["hResult"]);
        }
        if (exception?.Data["foundationRequestSent"] is false) dispatched = false;
        if (!dispatched && exception?.Data["workerLimitBytes"] is int workerLimit)
            return Reject(release, name, id,
                    new Error("Worker request exceeds its byte limit. Nothing was executed.", new LimitExceededDetails("arguments", workerLimit, null)), true);
        if (!dispatched && exception?.GetBaseException() is NotSupportedException unavailable)
            return Reject(release, name, id, UnsupportedCapabilityDetails.ErrorFor(release, name, null,
                HostBehavior.SafeDiagnostic(unavailable.Message)), true);
        if (worker is { Outcome: "rejected-before-operation", ExceptionType: nameof(NotSupportedException) })
            return Reject(release, name, id, UnsupportedCapabilityDetails.ErrorFor(release, name, null, HostBehavior.SafeDiagnostic(worker.Message)), true);
        exception ??= new InvalidOperationException();
        string? condition = worker?.Condition ?? TiaOpenness.Shared.HostFailurePolicy.Precondition(exception)?.Condition;
        if ((!dispatched || worker?.KnownNoMutation == true) && condition == TiaMcp.Adapters.Contracts.PlcImportOptionPolicy.UnsupportedCondition)
            return Reject(release, name, id, new Error("The selected release or XML object does not support the requested import options.",
                new UnsupportedCapabilityDetails(release, "PLC-Software", name)), true);
        var kind = TiaOpenness.Shared.HostFailurePolicy.Classify(exception, dispatched, !mutation, worker?.Outcome, worker?.Code, exceptionType);
        string? parameter = worker?.Parameter ?? TiaOpenness.Shared.HostFailurePolicy.Parameter(exception);
        bool nativeDiagnostic = !TiaOpenness.Shared.HostFailurePolicy.SuppressAdmissionDiagnostic(exception)
            && (worker == null ? exception is IOException : worker.Outcome != "rejected-before-operation");
        bool nativeFailure = kind == TiaOpenness.Shared.HostFailureKind.ReadFailed;
        var details = HostBehavior.FailureEvidence(exceptionType,
            dispatched && nativeDiagnostic && (nativeFailure || exceptionType == nameof(IOException)) ? exception.Message : null,
            native: worker != null, hResult: nativeFailure ? hResult : null);
        string? reason = TiaMcp.Adapters.Contracts.Candidates.SessionPrimitives.ExceptionReason(exception);
        bool readCancelled = dispatched && !mutation && kind == TiaOpenness.Shared.HostFailureKind.Cancelled;
        if (kind == TiaOpenness.Shared.HostFailureKind.Unknown)
        {
            details = HostBehavior.WorkerReplyEvidence(name, exception, details) ?? details;
            if (name == "SetUnifiedGlobalScript") return UnknownScriptImport(release, id, details, false);
        }
        if (kind == TiaOpenness.Shared.HostFailureKind.Unknown && exception.Data["workerRecoveryMessage"] is string recoveryMessage)
            return Wire(release, name, id, data, Outcome.Unknown, Execution.Unknown, Completeness.Unknown,
                new Error(HostBehavior.SafeDiagnostic(recoveryMessage)!, new OutcomeUnknownDetails("tool-call", details, reason)), true);
        return Wire(release, name, id, data, readCancelled ? Outcome.ReadFailed : HostBehavior.OutcomeOf(kind),
            readCancelled ? Execution.ReadOnly : HostBehavior.ExecutionOf(kind), HostBehavior.CompletenessOf(kind),
            HostBehavior.FailureError(kind, parameter, details,
                TiaOpenness.Shared.HostFailurePolicy.SuppressAdmissionDiagnostic(exception) ? null : HostBehavior
                    .AdmissionDiagnostic(exception, worker?.Code, worker?.Outcome, worker?.ExceptionType)
                    ?? (dispatched && nativeDiagnostic ? HostBehavior.SafeDiagnostic(exception.Message) : null),
                reason, dispatched ? "tool-call" : "tool-queue", worker?.Condition ?? TiaOpenness.Shared.HostFailurePolicy.Precondition(exception)?.Condition,
                resolveMissingTarget: field => HostMcp.MissingTargetDetails(name, field)),
            true);
    }

    internal static CallToolResult Worker(string release, Definition definition, string id, JsonObject args, JsonNode? raw, JsonNode? validated)
    {
        string name = definition.Name;
        var data = definition.ResponseMember == "BlockPage" ? ValidateBlockPage(raw, args) : Object(raw);
        if (definition.ResponseMember == "Connection" && data["previousOwnerState"] is JsonValue previous)
        {
            data["previousOwner"] = V17ProjectEnvelope.PreviousOwner(previous, data["previousOwnerRequests"] as JsonArray);
            data.Remove("previousOwnerState");
            data.Remove("previousOwnerRequests");
        }
        HostBehavior.ExportPreview(data);
        if (definition.ResponseMember == "HardwareCatalog")
        {
            const int pageCharacters = 35000;
            int offset = (int?)args["offset"] ?? 0;
            var items = data["items"]!.AsArray();
            int length = 0, count = 0;
            foreach (var item in items)
            {
                int size = item!.ToJsonString().Length + 1;
                if (length + size > pageCharacters) break;
                length += size;
                count++;
            }
            if (count == 0 && items.Count != 0)
                return Reject(release, name, id, new Error("One hardware catalog entry exceeds the response page budget.",
                    new LimitExceededDetails("catalog-entry", pageCharacters, items[0]!.ToJsonString().Length)));
            var meta = data["meta"]!.AsObject();
            bool clipped = count < items.Count;
            bool more = clipped || count > 0 && (string?)meta["truncationReason"] is "result-limit" or "response-character-budget";
            bool complete = !clipped && (bool?)meta["complete"] == true;
            if (clipped)
            {
                while (items.Count > count) items.RemoveAt(items.Count - 1);
                data["count"] = count;
                meta["complete"] = false; meta["truncated"] = true; meta["truncationReason"] = "response-page";
            }
            int? next = more && offset + count < 1000 ? offset + count : null;
            return Wire(release, name, id, data, Outcome.Succeeded, Execution.ReadOnly,
                complete ? Completeness.Complete : Completeness.Partial, null, true,
                paging: new Paging(PagingMode.Offset, offset, (int?)args["limit"] ?? 50, next, null, null, null, next == null));
        }
        if (definition.ResponseMember == "BlockPage")
        {
            int offset = data["offset"]!.GetValue<int>(), limit = args["limit"]!.GetValue<int>();
            string? next = data["nextCursor"]?.GetValue<string>();
            int total = data["total"]!.GetValue<int>();
            data.Remove("offset"); data.Remove("nextCursor"); data.Remove("total");
            return Wire(release, name, id, data, Outcome.Succeeded, Execution.ReadOnly, Completeness.Complete, null, true,
                paging
                    : new Paging(PagingMode.Cursor, null, limit, null, string.IsNullOrEmpty((string?)args["cursor"]) ? null : (string?)args["cursor"], next,
                        total, next == null));
        }
        bool mutation = IsMutation(definition, args);
        bool preview = args["dryRun"]?.GetValue<bool>() == true;
        if (definition.ResponseMember == "Compile")
        {
            bool mismatch = Flag(data, "scopeMismatch") == true;
            data.Remove("scopeMismatch");
            if (Text(data, "scope") is "block" or "group") data["compiledRoots"] ??= new JsonArray();
            else data.Remove("compiledRoots");
            if (!preview && mismatch)
                return Wire(release, name, id, data, Outcome.Failed, Execution.Completed, Completeness.Complete,
                    new Error("TIA returned compiler messages rooted outside the selected PLC. Inspect compiledRoots.",
                        new PreconditionFailedDetails("compile-scope-mismatch", null, "softwarePath")), true);
        }
        if (definition.ResponseMember == "BatchImport") return ImportCandidate(PlcBatchImportResultMapping.Result(data, release, name, id, preview));
        string? status = Text(data, "status");
        if (raw == null || mutation && (Flag(data, "executed") ?? Flag(data, "attempted")) == null
            && definition.ResponseMember is not ("Connection" or "Disconnect"))
            return Failure(release, name, id, true, mutation, raw);
        Outcome outcome = HostBehavior.OperationOutcome(status != "failed", status == "outcome-unknown" || mutation && status == "unknown", false, mutation);
        Error? error = null;
        if (name == "ExportTechnologyObject" && status == "failed" && CanonicalBatchTools.CurrentExportRoot.Value != null)
        {
            // The validated worker reply closes the export. A transport timeout could still publish a file later.
            bool absent = CallerInputFiles.ExportObservation.Absent((string)args["exportPath"]!);
            data["outputFileAbsent"] = absent;
            if (!absent) { data["outcomeUnknown"] = true; outcome = Outcome.Unknown; }
        }
        Paging? previewPage = null;
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
                    : state is "failed" or "inconsistent" ? (preview || Flag(item, "attempted") == false ? Outcome.RejectedBeforeOperation : Outcome.Failed)
                        : Outcome.Succeeded;
                if (childOutcome == Outcome.Succeeded) succeeded++;
                else if (state == "not-attempted") skipped++;
                else { failed++; if (state == "failed") causeIndex ??= mapped.Count; }
                unknown |= childOutcome == Outcome.Unknown;
                Error? childError = childOutcome == Outcome.Unknown ? Unknown()
                    : state == "not-attempted" ? new Error("Not executed after an earlier failure.", new NotExecutedDetails(causeIndex))
                    : childOutcome != Outcome.Succeeded ? NativeFailure(item) : null;
                var child = EnvelopeFor(release, name, id, item, childOutcome, Execute(childOutcome, mutation),
                    childOutcome == Outcome.Unknown ? Completeness.Unknown : Completeness.Complete, childError, true);
                mapped
                    .Add(new JsonObject {
                        ["index"] = mapped.Count, ["target"] = Text(item, "objectPath") ?? Text(item, "relativePath"), ["result"] = JsonNode
                            .Parse(V4Json.Serialize(child))
                    });
            }
            data["items"] = mapped;
            outcome = unknown ? Outcome.Unknown : failed + skipped == 0 ? Outcome.Succeeded
                : succeeded > 0 ? Outcome.Partial
                : mapped.All(x => (string?)x!["result"]!["meta"]!["execution"] == "not-started") ? Outcome.RejectedBeforeOperation : Outcome.Failed;
            if (outcome == Outcome.Partial) error = new Error("Some batch items did not complete.", new PartialFailureDetails(succeeded, failed, skipped));
            if (name == "ExportPlcTypes" && preview)
            {
                int offset = (int?)args["offset"] ?? 0, limit = (int?)args["limit"] ?? 32;
                if (offset > mapped.Count) return Reject(release, name, id, Invalid("offset"));
                var page = new JsonArray();
                int characters = 0;
                foreach (var row in mapped.Skip(offset).Take(limit))
                {
                    int size = row!.ToJsonString().Length + 1;
                    if (characters + size > 35000)
                    {
                        if (page.Count == 0) return Reject(release, name, id, new Error("One type export preview row exceeds the response page budget.",
                            new LimitExceededDetails("type-export-preview-row", 35000, size)));
                        break;
                    }
                    page.Add(row.DeepClone());
                    characters += size;
                }
                int end = offset + page.Count;
                data["items"] = page;
                previewPage = new Paging(PagingMode.Offset, offset, limit, end < mapped.Count ? end : null, null, null, mapped.Count, end == mapped.Count);
            }
        }
        if (definition.ResponseMember == "Compile" && !preview
            && ((data["errorCount"]?.GetValue<int>() ?? 0) > 0 || Text(data, "state") == "Error" || data["errors"] is JsonArray { Count: > 0 }))
            outcome = Outcome.Failed;
        if (definition.ResponseMember == "Compile" && !preview && data["state"] != null
            && TiaMcp.Adapters.Contracts.NativeResultStates.Classify(TiaMcp.Adapters.Contracts.NativeResultStates.Compiler, Text(data, "state"))
                == TiaMcp.Adapters.Contracts.NativeStateKind.Unexpected)
            outcome = Outcome.Failed;
        if (outcome == Outcome.Unknown) error = Unknown();
        else if (outcome is Outcome.Failed or Outcome.RejectedBeforeOperation) error = definition.ResponseMember == "Compile" ? CompileResultMapping
            .Errors(data) ?? NativeFailure(data) : NativeFailure(data);
        if (definition.ResponseMember is "DocumentExport" or "BatchDocumentExport" && data["nativeResult"] is JsonObject native)
        {
            var evidence = new JsonObject { ["nativeStateType"] = native["enumType"]?.DeepClone() };
            NativeResultState.Record(evidence, native["state"]?.ToString(), false);
            evidence["recoveryDirectory"] = data["recoveryDirectory"]?.DeepClone();
            data["nativeResultEvidence"] = evidence;
            if (NativeResultState.TryUnsuccessful(evidence, true, out var nativeOutcome, out var nativeError))
            {
                outcome = nativeOutcome == Outcome.Unknown ? Outcome.Failed : nativeOutcome;
                error = nativeOutcome == Outcome.Unknown ? NativeFailure(evidence) : nativeError;
            }
        }
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
            error, true, paging: previewPage);
    }

    internal static JsonObject ValidateBlockPage(JsonNode? raw, JsonObject args)
    {
        var data = Object(raw);
        bool Integer(string key, out int value)
        {
            value = 0;
            return data[key] is JsonValue scalar && scalar.TryGetValue(out value);
        }
        bool String(JsonNode? node, out string? value)
        {
            value = null;
            return node is JsonValue scalar && scalar.TryGetValue(out value);
        }
        int limit = args["limit"]!.GetValue<int>();
        if (raw is not JsonObject || data["items"] is not JsonArray items
            || !Integer("offset", out int offset) || !Integer("total", out int total)
            || offset < 0 || total < offset || items.Count > limit || items.Count > total - offset
            || !data.ContainsKey("nextCursor")) throw new InvalidDataException("Worker returned an invalid block page.");
        bool more = offset + items.Count < total;
        if (data["nextCursor"] != null && (!String(data["nextCursor"], out var cursor) || string.IsNullOrEmpty(cursor) || cursor.Length > 100)
            || more != (data["nextCursor"] != null) || more && items.Count == 0
            || string.IsNullOrEmpty((string?)args["cursor"]) && offset != 0)
            throw new InvalidDataException("Worker returned inconsistent block page totals or continuation.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (item is not JsonObject row || !String(row["path"], out var path) || string.IsNullOrEmpty(path) || !paths.Add(path))
                throw new InvalidDataException("Worker returned invalid block page items.");
        return data;
    }

    internal static CallToolResult Host(string release, string name, string id, JsonNode? raw, bool candidate, bool isError = false,
        IReadOnlyDictionary<string, JsonElement>? args = null)
    {
        string? usageName = (string?)raw?["usage"]?["toolName"];
        bool portedUsage = usageName != null && TiaMcp.Adapters.Contracts.PortedFamilies.Contains(usageName)
            && TiaMcpServer.ModelContextProtocol.ToolExecution.Table.TryGetValue(usageName, out var execution)
            && (execution == "host" || execution == "foundation" || TiaMcp.Adapters.Contracts.PortedFamilies.ForTool(usageName).Name is "F27" or "F28");
        bool rawUsage = name == "GetToolUsage" && portedUsage;
        var data = rawUsage ? raw!.DeepClone().AsObject() : Object(raw, preserveArguments: name == "GetToolUsage");
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
        if (name is "InitializeEnvironment" or "RunCapabilitySelfTest" && data["registeredToolCount"] is JsonValue count)
        {
            int offset = args != null && args.TryGetValue("offset", out var from) ? from.GetInt32() : 0;
            int limit = args != null && args.TryGetValue("limit", out var size) ? size.GetInt32() : FoundationPassiveDiagnostics.RosterPageLimit;
            int? next = data["nextOffset"]?.GetValue<int>();
            paging = new Paging(PagingMode.Offset, offset, limit, next, null, null, count.GetValue<int>(), next == null);
            data.Remove("nextOffset");
        }
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
        var success = !isError
            && (Flag(data, "success") ?? Flag(data, "ok") ?? Flag(data, "valid") ?? Flag(data["checks"] as JsonObject, "passed") ?? name == "GetToolUsage");
        if (name == "CheckProductUpdate" && !success && args != null && args.TryGetValue("repository", out var repository)
            && !TiaMcpServer.ModelContextProtocol.UpdateLogic.IsValidRepository(repository.GetString()))
            return Reject(release, name, id, new Error("The operation did not establish success; inspect the returned result.",
                new InvalidArgumentDetails("repository", Array.Empty<string>())));
        return Wire(release, name, id, data, success ? Outcome.Succeeded : Outcome.ReadFailed, Execution.ReadOnly,
            name is "InitializeEnvironment" or "RunCapabilitySelfTest" ? Completeness.Partial : Completeness.Complete,
            success ? null : NativeFailure(data), false, candidate, paging);
    }

    private static Error Unknown() => new("The operation outcome is unknown. Do not repeat this call. Read GetCallResult(requestId).",
        new OutcomeUnknownDetails("worker", Empty));
    private static Error NativeFailure(JsonObject data)
    {
        string? syntax = data["messages"] is JsonArray messages
            ? string.Join("; ", messages.Select(node => node is JsonValue value && value.TryGetValue<string>(out var message) ? message : null)
                .Where(message => message?.StartsWith("Line number ", StringComparison.Ordinal) == true).Take(3)) : null;
        string? summary = Text(data, "summary");
        string? diagnostic = HostBehavior.SafeDiagnostic(string.IsNullOrWhiteSpace(summary) ? syntax : summary);
        return new Error(diagnostic ?? "The operation did not establish success; inspect the returned result.",
            new NativeOperationFailedDetails(null, diagnostic, data.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value))));
    }
    private static Execution Execute(Outcome outcome, bool mutation) => HostBehavior.ExecutionOf(outcome, mutation);
    private static bool? Flag(JsonObject? data, string key) => data?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static string? Text(JsonObject data, string key) => data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Incomplete(JsonNode? node) => node is JsonArray rows ? rows.Any(Incomplete)
        : node is JsonObject obj && (obj["unavailableAttributes"] is JsonArray { Count: > 0 }
            || Flag(obj, "truncated") == true || Flag(obj, "complete") == false || obj.Any(p => Incomplete(p.Value)));
    internal static JsonObject Object(JsonNode? raw, bool preserveArguments = false) => Fields(raw, preserveArguments) switch {
        JsonObject obj => obj, JsonArray items => new JsonObject { ["items"] = items },
        JsonNode value => new JsonObject { ["value"] = value }, _ => new JsonObject() };
    private static JsonNode? Fields(JsonNode? raw, bool preserveArguments = false)
    {
        if (raw is JsonArray rows) return new JsonArray(rows.Select(row => Fields(row, preserveArguments)).ToArray());
        if (raw is not JsonObject obj) return raw?.DeepClone();
        // Machine parameter names belong to the package schema, including their case.
        if ((string?)obj["schema"] == "tiamcp.machine/1") return obj.DeepClone();
        var data = new JsonObject();
        foreach (var pair in obj)
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(pair.Key);
            // Usage arguments are caller inputs, so their exact schema keys and nested property names must survive result mapping.
            data[key == "message" ? "summary" : key] = key is "value" or "evidence" || preserveArguments && key == "arguments"
                ? pair.Value?.DeepClone() : Fields(pair.Value, preserveArguments);
        }
        return data;
    }
    private static Envelope EnvelopeFor(string release, string name, string id, JsonObject? data, Outcome outcome,
        Execution execution, Completeness completeness, Error? error, bool current, bool candidate = false, Paging? paging = null)
    {
        var policy = BehaviorCapabilities.EntryPolicy(typeof(FoundationV4Result).Assembly, release, name, BehaviorPolicy.NotApplicable);
        current = policy == BehaviorPolicy.Current;
        if (outcome == Outcome.Unknown) data?.Remove("recovery");
        var warnings = new List<Warning>();
        if (name == "ConnectPortal" && (string?)data?["previousOwner"]?["leaseState"] is "BUSY" or "UNCERTAIN" or "ACTIVE")
            warnings.Add(new Warning(WarningCode.PreviousOwnerUnclean,
                "The previous owner's record is unclean. Inspect its state, these request IDs and their targets before repeating calls.",
                new Dictionary<string, JsonElement> { ["previousOwner"] = JsonSerializer.SerializeToElement(data!["previousOwner"]) }));
        if (error?.Code == ErrorCode.UnsupportedCapability)
            warnings.Add(new Warning(WarningCode.NativeCapabilityLimit, "The selected release or native API does not provide this capability.", Empty));
        if (data != null && name is "CompilePlcSoftware" or "CompilePlcDiagnostics" && data["state"]?.ToString() == "Warning")
            warnings.AddRange(NativeResultState.Warnings(new JsonObject { ["nativeState"] = "Warning",
                ["nativeStateType"] = TiaMcp.Adapters.Contracts.NativeResultStates.Compiler }));
        if (current) warnings
            .Add(new Warning(WarningCode.UnverifiedBehavior, BehaviorCapabilities.CurrentDisclosure, Empty));
        if (candidate) warnings
            .Add(new Warning(WarningCode.CandidateOnly, "Candidate output only; target schema, import and program semantics remain unverified.", Empty));
        if (HostBehavior.BackupSkipped(data) is Warning backup) warnings.Add(backup);
        if (current && data?["warnings"] is JsonArray { Count: > 0 })
            warnings.Add(new Warning(WarningCode.NativeWarning, "Native diagnostics include warnings; see data.warnings.", Empty));
        if (completeness == Completeness.Partial) warnings
            .Add(new Warning(WarningCode.IncompleteData, "The observation is incomplete; retain the declared scope and unprobed fields.", Empty));
        return BehaviorCapabilities.Disclose(Envelope.Create(data, error, new Meta(DateTimeOffset.UtcNow, release, name, id, outcome, execution,
            policy, completeness, paging, warnings)));
    }
    internal static CallToolResult Composite(string release, string name, string id, JsonObject data, Outcome outcome,
        Execution execution, Error? error = null)
        =>
            Wire(release, name, id, data, outcome, execution, outcome == Outcome.Unknown ? Completeness.Unknown : outcome == Outcome.Partial ? Completeness
                .Partial : Completeness.Complete, error, true);

    private static CallToolResult Wire(string release, string name, string id, JsonObject? data, Outcome outcome,
        Execution execution, Completeness completeness, Error? error, bool current, bool candidate = false, Paging? paging = null)
    {
        var mapped = McpResult.From(EnvelopeFor(release, name, id, data, outcome, execution, completeness, error, current, candidate, paging));
        return new CallToolResult { IsError = mapped.IsError, StructuredContent = JsonNode.Parse(mapped.StructuredContent.GetRawText()),
            Content = new[] { new TextContentBlock { Text = mapped.Content[0].Text } } };
    }
}
