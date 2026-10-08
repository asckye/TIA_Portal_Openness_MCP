using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcp.Logic.ModelContextProtocol;
using TiaMcp.WorkerChannel;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;

namespace TiaMcp.LegacyHost;

internal sealed class EngineSlice(EngineCatalog catalog, IEngineWorker worker)
{
    private bool locked;
    internal Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>> Wait { get; set; } = (request, settings, token) => ApprovalClient.Wait(request, settings, token);
    internal Action<string>? Stage { get; set; }
    internal IEnumerable<McpServerTool> Tools(string profile) => catalog.Tools
        .Where(t => EngineCatalog.Slice.Contains(t.Name) && (profile == "full" || catalog.LiteTools.Contains(t.Name)))
        .Select(t => (McpServerTool)new EngineCatalogTool(t, this));

    internal async Task<CallToolResult> Invoke(Tool tool, JsonObject args, CancellationToken token)
    {
        string name = tool.Name;
        var descriptor = catalog.Descriptors[name];
        bool classifiedWrite = (string?)descriptor["classification"]?["operation"] is "WRITE" or "ONLINE-WRITE";
        bool hasPreview = (bool?)descriptor["dryRun"]?["present"] == true;
        bool candidate = descriptor["candidateFamily"] != null && args["mode"] is JsonValue mode && mode.TryGetValue<string>(out var action) && action == "apply";
        bool write = HostBehavior.ApprovalWrite(name, args, candidate, classifiedWrite, hasPreview, (bool?)descriptor["dryRun"]?["default"] ?? true);
        string id = Guid.NewGuid().ToString("N");
        using var audit = AuditInvocation.Begin(write, "engine", "21", name, id);
        using var correlation = InvocationJournal.UseCorrelation(id);
        Stage?.Invoke("journal");
        using var journal = InvocationJournal.Observe(id, name, "engine", "21", classifiedWrite, () => args.ToJsonString());
        ApprovalOutcome? approval = null;
        bool disabled = write && !ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled;
        CallToolResult Finish(CallToolResult result)
        {
            var body = result.StructuredContent?.DeepClone();
            bool changed = RecoveryHints.Attach(body, (target, release, operation) => Example(target, release, operation));
            if (approval != null || disabled) { body = ApprovalResult.Decorate(body!, disabled || approval?.Disabled == true, approval?.Request.RequestId); changed = true; }
            if (changed) result = EngineResult.Body(body!, result.IsError);
            if (approval != null) ApprovalClient.Complete(approval, (string?)body?["meta"]?["outcome"] ?? "unknown").GetAwaiter().GetResult();
            audit?.Complete(result.StructuredContent?.ToJsonString());
            journal.Complete(() => JsonSerializer.Serialize(result, global::ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            return result;
        }
        try
        {
            var admission = Admission(tool, descriptor, args);
            if (admission != null) return Finish(EngineResult.Reject(name, admission));
            bool local = name is "BuildPlcUdt" or "GetExportContent" or "ListExportHandles";
            CallToolResult? Reset() => !local && SessionBehavior.RequiresReset(locked || worker.Faulted, true) ? EngineResult.Reject(name, HostBehavior.SessionReset()) : null;
            var reset = Reset();
            if (reset != null) return Finish(reset);
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            Stage?.Invoke("precheck");
            if (settings.Enabled && write) CallerInputFiles.Validate(name, args);
            if (HostBehavior.NeedsPrecheck(false, write, settings.Enabled, hasPreview || candidate))
            {
                var previewArgs = args.DeepClone().AsObject();
                previewArgs[candidate ? "mode" : "dryRun"] = candidate ? JsonValue.Create("preview") : JsonValue.Create(true);
                using var previewLane = await worker.Acquire(token).ConfigureAwait(false);
                using var previewAudit = AuditInvocation.ReadOnlyPreview();
                CallerInputFiles.Validate(name, previewArgs);
                var preview = await worker.Invoke(id, name, previewArgs, true, token).ConfigureAwait(false);
                if (preview.Result.StructuredContent?["ok"]?.GetValue<bool>() != true)
                    return Finish(EngineResult.Body(ApprovalPrecheck.Mark(preview.Result.StructuredContent!, id), true));
                if (HostBehavior.PreviewHasNoEffect(preview.Result.StructuredContent)) return Finish(preview.Result);
            }
            Stage?.Invoke("approval");
            if (write)
            {
                var pending = PendingApproval.Create("engine", "21", name, args.ToJsonString(), worker.Binding?.ToJsonString(), settings.TimeoutSeconds, id);
                AuditInvocation.RecordCurrentRequest(pending.PlanHash);
                approval = await Wait(pending, settings, token).ConfigureAwait(false);
                if (approval.Reason != null) return Finish(EngineResult.Reject(name, HostBehavior.Confirmation(approval.Reason, pending.PlanHash, id)));
            }
            using var lane = local ? null : await worker.Acquire(token).ConfigureAwait(false);
            Stage?.Invoke("session");
            reset = Reset();
            if (reset != null) return Finish(reset);
            if (approval != null && !approval.Disabled && PendingApproval.Create("engine", "21", name, args.ToJsonString(), worker.Binding?.ToJsonString(), settings.TimeoutSeconds).ArgumentDigest != approval.Request.ArgumentDigest)
                return Finish(EngineResult.Reject(name, HostBehavior.Confirmation("denied", approval.Request.PlanHash, id)));
            Stage?.Invoke("caller-input");
            AuditInvocation.StartCurrent();
            CallerInputFiles.Validate(name, args);
            token.ThrowIfCancellationRequested();
            Stage?.Invoke("invoke");
            EngineReply reply = local ? new EngineReply(EngineLocalTools.Invoke(name, args), false, null, null, null)
                : await worker.Invoke(id, name, args, false, token).ConfigureAwait(false);
            CallToolResult result = local && name is "GetExportContent" or "ListExportHandles" ? reply.Result : ResponseGuardTool.Shrink(reply.Result, name, "");
            Stage?.Invoke("outcome");
            var body = reply.Result.StructuredContent;
            bool native = reply.NativeCallIssued || (bool?)body?["data"]?["nativeIssued"] == true || (bool?)body?["data"]?["evidence"]?["nativeOutcomeUnknown"] == true;
            bool unknown = (string?)body?["meta"]?["outcome"] == "unknown" || (bool?)body?["data"]?["nativeOutcomeUnknown"] == true;
            if (!local && SessionBehavior.LocksSession(native, unknown, write || (string?)descriptor["classification"]?["operation"] is "FILE" or "EXECUTE")) locked = true;
            Stage?.Invoke("finish");
            return Finish(result);
        }
        catch (OperationCanceledException) /* swallow(privacy): pre-dispatch cancellation returns the stable V4 code without exception text */
        { return Finish(EngineResult.Reject(name, new Error("The request was cancelled before dispatch.", new CancelledDetails("tool-queue")))); }
        catch (ChannelLimitException) /* swallow(privacy): the byte limit is public protocol data; the rejected payload and exception text are not */
        { return Finish(EngineResult.Reject(name, new Error("Worker request exceeds its byte limit.", new LimitExceededDetails("arguments", ChannelLimits.RequestBytes, null)))); }
        catch (Exception error)
        {
            bool issued = error is ChannelFault { OutcomeUnknown: true };
            var kind = HostFailurePolicy.Classify(error, issued, !write);
            var evidence = HostBehavior.FailureEvidence(error.GetType().Name, error.Message);
            if (issued && write) locked = true;
            return Finish(EngineResult.Wire(Envelope.Create(new JsonObject { ["evidence"] = JsonSerializer.SerializeToNode(evidence) },
                HostBehavior.FailureError(kind, HostFailurePolicy.Parameter(error), evidence, HostBehavior.AdmissionDiagnostic(error)),
                new Meta(DateTimeOffset.UtcNow, "21", name, id, HostBehavior.OutcomeOf(kind), HostBehavior.ExecutionOf(kind), issued && write,
                    BehaviorPolicy.NotApplicable, HostBehavior.CompletenessOf(kind), null, Array.Empty<Warning>()))));
        }
    }

    private JsonObject? Example(string name, string release, string operation)
    {
        var tool = catalog.Tools.FirstOrDefault(t => t.Name == name);
        if (release != "21" || tool == null) return null;
        var example = ToolExamples.Find(name);
        return ToolUsageCatalog.Describe(name, "21", "full-engine", (string?)catalog.Descriptors[name]["rawDescription"] + ToolUsageCatalog.Hint(name),
            JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject(), example?.ArgumentsJson, example?.Note, operation, catalog.Tools.Select(t => t.Name))["example"] as JsonObject;
    }

    private static Error? Admission(Tool tool, JsonObject descriptor, JsonObject args)
    {
        JsonElement json;
        try { json = new ToolArguments(JsonSerializer.SerializeToElement(args)).Json; }
        catch (InputRejection rejected) { return rejected.ToError("arguments"); }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args) if (!seen.Add(arg.Key)) return McpServer.InvalidInput("arguments");
        foreach (var p in descriptor["parameters"]!.AsArray())
        {
            string name = (string)p!["name"]!;
            if (!json.TryGetProperty(name, out var value)) continue;
            string clr = (string)p["clrType"]!;
            Type? type = Type.GetType(clr) ?? typeof(ToolArguments).Assembly.GetType(clr);
            if (type != null && TypedToolInput.For(type) is { } typed)
            { var failure = typed.Validate(value, name); if (failure != null) return failure; }
            // JSON Schema integers are wider than CLR integers. Match BindV4Call's
            // conversion refusal before approval or host-only dispatch.
            if (type != null && (type.IsPrimitive || type == typeof(decimal) || type == typeof(string)))
            {
                try { _ = JsonSerializer.Deserialize(value.GetRawText(), type); }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException || ex is OverflowException) /* swallow(privacy): scalar conversion failures use the engine's value-free argument refusal */
                { return McpServer.InvalidInput(name); }
            }
        }
        var error = new InputSchema(tool.InputSchema).Validate(json, "arguments");
        if (error != null) return error;
        return null;
    }
}

internal sealed class EngineCatalogTool(Tool tool, EngineSlice slice) : McpServerTool
{
    public override Tool ProtocolTool => tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        => new(slice.Invoke(tool, JsonSerializer.SerializeToNode(request.Params?.Arguments ?? new Dictionary<string, JsonElement>())!.AsObject(), cancellationToken));
}
