#if TIA_FOUNDATION_TEST_HOST
extern alias enginehost;
using DeclaredToolMetadata = enginehost::TiaMcpServer.ModelContextProtocol.DeclaredToolMetadata;
using ProgressHeartbeat = enginehost::TiaMcpServer.ModelContextProtocol.ProgressHeartbeat;
using ReleaseCatalogData = enginehost::TiaMcp.FoundationHost.ReleaseCatalogData;
using UnifiedInputAdmission = enginehost::TiaMcpServer.Siemens.Services.UnifiedPortService;
#else
using DeclaredToolMetadata = TiaMcpServer.ModelContextProtocol.DeclaredToolMetadata;
using ProgressHeartbeat = TiaMcpServer.ModelContextProtocol.ProgressHeartbeat;
using UnifiedInputAdmission = TiaMcpServer.Siemens.Services.UnifiedPortService;
#endif
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.FoundationHost;

// Only this boundary is registered. The existing host implementations retain their
// worker protocol and bounded parsers; legacy names are not callable aliases.
internal sealed class FoundationV4Tool : McpServerTool
{
    private readonly McpServerTool inner;
    private readonly string release;
    private readonly Tool tool;
    private readonly Func<TiaOpenness.Shared.ApprovalSettings>? approvalSettings;
    private readonly Func<TiaOpenness.Shared.PendingApproval, TiaOpenness.Shared.ApprovalSettings,
        CancellationToken, Task<TiaOpenness.Shared.ApprovalOutcome>>? approvalWait;
    private readonly Func<JsonObject>? readinessForTest;
    private readonly bool sharedSession;
    private string? parameter;
    private Func<JsonElement, (string? Json, Error? Error)>? convert;

    internal FoundationV4Tool(McpServerTool inner, string release,
        Func<TiaOpenness.Shared.ApprovalSettings>? approvalSettings = null,
        Func<TiaOpenness.Shared.PendingApproval, TiaOpenness.Shared.ApprovalSettings,
            CancellationToken, Task<TiaOpenness.Shared.ApprovalOutcome>>? approvalWait = null,
        Func<JsonObject>? readinessForTest = null, bool sharedSession = false)
    {
        if (inner is OfflineXmlTool xmlTool) inner = xmlTool.ForRelease(release);
        else if (inner is OfflineCompositionTool compositionTool) inner = compositionTool.ForRelease(release);
        if (inner is FoundationTool blocks && inner.ProtocolTool.Name == "ListPlcBlocks") inner = blocks.ForBlockPaging();
        this.inner = inner; this.release = release;
        this.approvalSettings = approvalSettings;
        this.approvalWait = approvalWait;
        this.readinessForTest = readinessForTest;
        this.sharedSession = sharedSession || inner is FoundationTool { SharedWorker: true };
        var source = inner.ProtocolTool;
        if (inner is WorkbenchControlTool) { tool = source; return; }
        var schema = JsonNode.Parse(source.InputSchema.GetRawText())!.AsObject();
        PlcSoftwareUnitSchema.Apply(schema, source.Name, release);
        if (inner is FoundationTool { Ported: true })
        {
            tool = new Tool { Name = source.Name, Title = source.Title,
                Description = source.Description + " Native behaviorPolicy=current; V4 native acceptance is pending.",
                InputSchema = JsonSerializer.SerializeToElement(schema), OutputSchema = source.OutputSchema, Annotations = source.Annotations, Meta = source
                    .Meta };
            return;
        }
        var properties = schema["properties"]!.AsObject();
        JsonElement? typedSchema = null;
        void Construction<T>(string key) where T : ConstructionSpec
        {
            parameter = key;
            var contract = new ConstructionInput<T>(ConstructionProfile.Foundation);
            typedSchema = contract.Schema;
            convert = value => { var input = contract.Read(value, key); return input.Error != null
                ? (null, input.Error) : (ConstructionAdapter.ToBuilderJson(input.Value!), null); };
        }
        switch (source.Name)
        {
            case "BuildPlcUdt": Construction<UdtSpec>("udt"); break;
            case "BuildPlcTagTable": Construction<PlcTagTableSpec>("tagTable"); break;
            case "BuildPlcGlobalDb": Construction<GlobalDbSpec>("globalDb"); break;
            case "BuildStructuredText": Construction<StructuredTextSpec>("structuredText"); break;
            case "BuildFlgNetCall": Construction<FlgNetCallSpec>("flgNet"); break;
            case "BuildPlcFcBlock": Construction<FcBlockSpec>("fcBlock"); break;
            case "BuildPlcFbBlock": Construction<FbBlockSpec>("fbBlock"); break;
            case "BuildPlcLadFcBlock": Construction<LadFcBlockSpec>("ladFcBlock"); break;
            case "PlanArtifactImportOrder":
                parameter = "artifacts";
                var contract = DomainValidation.Contract<Artifact[]>();
                typedSchema = contract.Schema;
                convert = value => { var input = contract.Read(value, "artifacts"); return input.Error != null
                    ? (null, input.Error) : (V4Json.Serialize(input.Value), null); };
                break;
        }
        if (parameter != null)
        {
            properties.Remove(parameter + "Json");
            properties[parameter] = JsonNode.Parse(typedSchema!.Value.GetRawText());
            var required = schema["required"]!.AsArray();
            for (int i = 0; i < required.Count; i++)
                if (required[i]!.GetValue<string>() == parameter + "Json") required[i] = parameter;
        }
        string description = source.Description ?? "";
        if (parameter != null) description = description.Replace(parameter + "Json", parameter)
            + " Supply a typed object/array with exact camelCase fields, never a JSON string. Existing Foundation budgets and output-release limits apply.";
        if (parameter == "artifacts") description =
            "Offline dependency-first import planning. artifacts is a typed array of {id, target?, priority?, dependencies?}; 1..256 unique IDs, no missing " +
            "or cyclic dependencies. No worker or file operations.";
        tool = new Tool { Name = source.Name, Description = description
            + (inner is FoundationTool { IsNative: true } ? " Native behaviorPolicy=current; V4 native acceptance is pending." : ""),
            InputSchema = JsonSerializer.SerializeToElement(schema), OutputSchema = FoundationV4Result.Schema };
    }

    public override Tool ProtocolTool => DeclaredToolMetadata.V4(tool, foundation: true);
    private bool IsApprovalWrite(JsonObject args)
    {
        var properties = inner.ProtocolTool.InputSchema.GetProperty("properties");
        bool hasDryRun = properties.TryGetProperty("dryRun", out var schema);
        bool defaultPreview = !hasDryRun || !schema.TryGetProperty("default", out var defaultValue)
            || defaultValue.ValueKind != JsonValueKind.False;
        return HostBehavior.ApprovalWrite(tool.Name, args,
            inner is FoundationTool { IsWrite: true } || inner is GenerationTool generation && generation.ApprovalWrite(args), hasDryRun, defaultPreview);
    }
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        using var heartbeat = ProgressHeartbeat.Begin(request);
        using var actor = TiaOpenness.Shared.ActorScope.EnterCall(request.Server?.SessionId, request.Server);
        if (inner is WorkbenchControlTool control)
        {
            string controlId = Meta.Correlate(sharedSession ? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId : null);
            using var controlJournal = TiaMcpServer.ModelContextProtocol.InvocationJournal
                .Observe(controlId, tool.Name, ReleaseCatalogData.Releases[release].FoundationInvocationTag, release,
                false, () => JsonSerializer.Serialize(request.Params?.Arguments));
            var controlResult = await control.InvokeV4Async(request, controlId, cancellationToken);
            controlJournal.Complete(() => JsonSerializer.Serialize(controlResult, McpJsonUtilities.DefaultOptions));
            return controlResult;
        }
        using var sharedStagingSession = (inner as FoundationTool)?.EnterSharedRequest(request);
        using var stagingSession = (inner as FoundationTool)?.EnterStagingRequest();
        var inputArguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        var arguments = JsonSerializer.SerializeToNode(inputArguments) as JsonObject ?? new JsonObject();
        bool write = IsApprovalWrite(arguments);
        var settings = approvalSettings?.Invoke() ?? TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        TiaOpenness.Shared.ApprovalOutcome? approval = null;
        string id = Meta.Correlate(TiaOpenness.Shared.AuditInvocation.CurrentRequestId
            ?? (sharedSession
                ? TiaMcpServer.ModelContextProtocol.InvocationJournal.CorrelationId : null));
        using var audit = TiaOpenness.Shared.AuditInvocation.Begin(write, ReleaseCatalogData.Releases[release].FoundationInvocationTag, release, tool.Name, id);
        var result = await InvokeCoreAsync(request, cancellationToken, settings, value => approval = value, id, audit, write);
        var body = result.StructuredContent ?? JsonNode.Parse((result.Content.FirstOrDefault() as TextContentBlock)?.Text ?? "null");
        if (TiaMcp.Logic.ModelContextProtocol.RecoveryHints.Attach(body, (name, key, operation) =>
        {
            if (name != tool.Name || key != release) return null;
            var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
            var usage = TiaOpenness.Shared.ToolUsageCatalog.Describe(name, key, "plc-foundation", tool.Description ?? "", schema);
            if (operation.Length == 0) return usage["example"] as JsonObject;
            if (!usage["operations"]!.AsArray().Any(row => (string?)row!["operation"] == operation)) return null;
            return TiaOpenness.Shared.ToolUsageCatalog
                .Describe(name, key, "plc-foundation", tool.Description ?? "", schema, operation: operation)["example"] as JsonObject;
        }))
            result = new CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body!.ToJsonString() } } };
        if ((string?)body?["meta"]?["outcome"] == "unknown" && inner is FoundationTool { RequiresProjectSession: true } uncertain
            && (write || TiaMcpServer.ModelContextProtocol.ToolTaxonomy.OperationOf(tool.Name, null).Operation is "FILE" or "EXECUTE"))
            uncertain.MarkSessionUncertain();
        if ((string?)body?["meta"]?["outcome"] == "unknown")
        {
            body = EnvelopeMutation.SessionReset(body!, inner is FoundationTool { RequiresProjectSession: true, SessionRequiresReset: true });
            result = new CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
        }
        if (body != null && (approval != null || write && !settings.Enabled))
        {
            body = TiaOpenness.Shared.ApprovalResult.Decorate(body, write && !settings.Enabled, approval?.Request.RequestId);
            result = new CallToolResult {
                IsError = result.IsError, StructuredContent = body, Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
            if (approval != null) await TiaOpenness.Shared.ApprovalClient.Complete(approval,
                (string?)body["error"]?["code"] == "CANCELLED" ? "unknown" : (string?)body["meta"]?["outcome"] ?? "unknown");
        }
        if (audit != null) audit.Complete(result.StructuredContent?.ToJsonString() ?? (result.Content.FirstOrDefault() as TextContentBlock)?.Text);
        if (inner is FoundationTool producingTool) result = producingTool.ParkWorkerResult(result, tool.Name);
        return result;
    }

    private async ValueTask<CallToolResult> InvokeCoreAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken,
        TiaOpenness.Shared.ApprovalSettings settings, Action<TiaOpenness.Shared.ApprovalOutcome> capture,
        string id, TiaOpenness.Shared.AuditInvocation? audit, bool write, bool internalPrecheck = false)
    {
        var args = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        using var journal = internalPrecheck ? null : TiaMcpServer.ModelContextProtocol.InvocationJournal
            .Observe(id, tool.Name, ReleaseCatalogData.Releases[release].FoundationInvocationTag, release,
            inner is FoundationTool { JournalIsWrite: true }, () => JsonSerializer.Serialize(args));
        using var workerRequest = FoundationWorkerRequest.Use(id, tool.Name);
        CallToolResult Recorded(CallToolResult result)
        {
            if (inner is FoundationTool artifactProducer)
                artifactProducer.ControlSession
                    .Observe(tool.Name, request.Params?.Arguments ?? new Dictionary<string, JsonElement>(), result.StructuredContent);
            journal?.Complete(() => JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions));
            return result;
        }
        IDisposable? lane = null;
        try
        {
            var argumentsInput = JsonSerializer.SerializeToElement(args);
            if (UnifiedInputAdmission.ValidateNavigation(tool.Name, tool.InputSchema, argumentsInput) is { } navigationError)
                return Recorded(FoundationV4Result.Reject(release, tool.Name, id, navigationError, inner is FoundationTool { IsNative: true }));
            var validation =
                new InputContract<ToolArguments>(new InputSchema(tool.InputSchema), tool.Name == "StageImportFiles"
                    ? new InputBudget(characters: 40 * 1024 * 1024, items: 4096) : new InputBudget())
                .Read(argumentsInput, "arguments");
            if (validation.Error != null)
            {
                var error = tool.Name == "GeneratePlcLoadableFile"
                    ? new InputSchema(tool.InputSchema).Validate(argumentsInput, "arguments") ?? validation.Error : validation.Error;
                return Recorded(FoundationV4Result.Reject(release, tool.Name, id, error, inner is FoundationTool { IsNative: true }));
            }
            try { TiaOpenness.Shared.ApprovalOutputTargets.ValidateWhitespace(tool.Name, JsonSerializer.SerializeToNode(args)!.AsObject()); }
            catch (ArgumentException error)
            {
                return Recorded(FoundationV4Result.Reject(release, tool.Name, id,
                    new Error("Filesystem targets must not have leading or trailing whitespace.",
                        new InvalidArgumentDetails(error.ParamName ?? "arguments", Array.Empty<string>()))));
            }
            if (SoftwareUnitCapabilityError(release, tool.Name, args) is { } unitCapability)
                return Recorded(FoundationV4Result.Reject(release, tool.Name, id, unitCapability, true));
            try { CallerInputFiles.RefuseHumanSettingsTargets(JsonSerializer.SerializeToNode(args)!.AsObject()); }
            catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException error)
            { return Recorded(FoundationV4Result.Reject(release, tool.Name, id, new Error(error.Message,
                new PreconditionFailedDetails("human-only-native-cross-reference-setting", error.ParamName)))); }
            if (write && FoundationHostPorts.GenerationFence(tool.Name, JsonSerializer.Serialize(args)) is { } fenced)
                return Recorded(fenced);
            if (inner is FoundationTool { Ported: true } ported && ported.RefusePortedCapability(args) is { } unavailable)
                return Recorded(unavailable);
            if (tool.Name is not ("DisconnectPortal" or "GetSessionState")
                && inner is FoundationTool { RequiresProjectSession: true, SessionRequiresReset: true } resetTool
                && (!resetTool.Ported || PortedToolContract.WiredOperations(tool.Name).Length != 0))
                return Recorded(FoundationV4Result.Reject(release, tool.Name, id,
                    HostBehavior.SessionReset(), true));
            if (tool.Name != "DisconnectPortal" && inner is FoundationTool { RequiresTia: true } nativeTool
                    && (nativeTool.UsesProductionWorker || readinessForTest != null))
            {
                await nativeTool.PrepareReadiness(cancellationToken).ConfigureAwait(false);
                var readiness = readinessForTest?.Invoke() ?? FoundationPassiveDiagnostics.Readiness(release);
                if (!readiness["ready"]!.GetValue<bool>())
                    return Recorded(FoundationV4Result.ReadinessUnavailable(release, tool.Name, id, readiness));
            }
            // V20/V21 with their bundled PlcWorker answer like the engine (P8-04j2): its session and process reads stopped at
            // native admission without a ready TIA environment. A ready environment dispatches exactly as before.
            else
                if (ReleaseCatalogData.Releases[release].MergeWorkerReadiness && inner is FoundationTool { IsRuntimeQuery: true, UsesBundledPlcWorker: true
                } runtimeQuery)
            {
                await runtimeQuery.PrepareReadiness(cancellationToken).ConfigureAwait(false);
                if (!(readinessForTest?.Invoke() ?? FoundationPassiveDiagnostics.Readiness(release))["ready"]!.GetValue<bool>())
                    return Recorded(FoundationV4Result.Reject(release, tool.Name, id,
                        HostBehavior.FailureError(TiaOpenness.Shared.HostFailureKind.Precondition, null, new Dictionary<string, JsonElement>()), true));
            }
            TiaOpenness.Shared.PendingApproval? pending = null;
            TiaOpenness.Shared.ApprovalOutcome? approval = null;
            if (write && inner is not GenerationTool { OwnsApproval: true })
            {
                pending = TiaOpenness.Shared.PendingApproval
                    .Create(ReleaseCatalogData.Releases[release].FoundationInvocationTag, release, tool.Name, JsonSerializer.Serialize(args),
                    (inner as FoundationTool)?.ApprovalIdentity, settings.TimeoutSeconds, id);
                audit?.RecordRequest(pending.PlanHash);
                if (HostBehavior.NeedsPrecheck(internalPrecheck, write, settings.Enabled, true))
                {
                    var precheckArguments = new Dictionary<string, JsonElement>(args, StringComparer.Ordinal);
                    bool hasPreview = tool.InputSchema.GetProperty("properties").TryGetProperty("dryRun", out _);
                    if (hasPreview)
                    {
                        CallToolResult? refusal = null;
                        try
                        {
                            if (inner is FoundationTool applyTool) applyTool.ValidateApplyArguments(args);
                        }
                        catch (ArgumentException ex)
                        {
                            refusal = FoundationV4Result
                                .Reject(release, tool.Name, id,
                                    new Error(ex.Message, new InvalidArgumentDetails(ex.ParamName ?? "arguments", Array.Empty<string>())),
                                    inner is FoundationTool { IsNative: true });
                        }
                        if (refusal == null)
                        {
                            precheckArguments["dryRun"] = JsonSerializer.SerializeToElement(true);
                            var originalRequest = request.Params;
                            request.Params = new CallToolRequestParams { Name = tool.Name, Arguments = precheckArguments };
                            try
                            {
                                using var readOnlyAudit = TiaOpenness.Shared.AuditInvocation.ReadOnlyPreview();
                                refusal = await InvokeCoreAsync(request, cancellationToken, settings, _ => { }, id, null, false, true);
                            }
                            finally { request.Params = originalRequest; }
                        }
                        var blocked = HostBehavior.PreviewApplyRefusal(refusal.StructuredContent);
                        blocked ??= HostBehavior.BatchApplyRefusal(tool.Name, JsonSerializer.SerializeToNode(args)!.AsObject());
                        blocked ??= HostBehavior.BatchPreviewRefusal(tool.Name, JsonSerializer.SerializeToNode(args)!.AsObject(), refusal.StructuredContent);
                        if (blocked != null) refusal = FoundationV4Result.Reject(release, tool.Name, id, blocked, true);
                        if (refusal.StructuredContent?["ok"]?.GetValue<bool>() != true)
                        {
                            var marked = TiaOpenness.Shared.ApprovalPrecheck.Mark(refusal.StructuredContent!, id);
                            return Recorded(new CallToolResult { IsError = true, StructuredContent = marked,
                                Content = new[] { new TextContentBlock { Text = marked.ToJsonString() } } });
                        }
                        if (FoundationV4Result.PreviewHasNoEffect(refusal.StructuredContent)) return Recorded(refusal);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                // The request was created before the precheck so the audit records it first; the confirmation window opens
                // only now, as on the engine host. Otherwise a slow preview consumed the configured seconds, and a precheck
                // longer than the window expired the request before Workbench could show it.
                pending.Deadline = DateTimeOffset.UtcNow.AddSeconds(pending.TimeoutSeconds);
                approval = approvalWait == null
                    ? await TiaOpenness.Shared.ApprovalClient.Wait(pending, settings, cancellationToken)
                    : await approvalWait(pending, settings, cancellationToken);
                capture(approval);
                if (approval.Reason != null) return Recorded(FoundationV4Result.Reject(release, tool.Name, id,
                    HostBehavior.Confirmation(approval.Reason, pending.PlanHash, id,
                        inner is not FoundationTool { IsWrite: true } && inner is not GenerationTool
                            && tool.Name != "ManagePlcGitRepository"
                            && CallerInputFiles.WritesOutsideOutputRoot(tool.Name, JsonSerializer.SerializeToNode(args)!.AsObject()))));
            }
            if (inner is FoundationTool targetLaneTool)
            {
                lane = await targetLaneTool.AcquireLane(cancellationToken);
                targetLaneTool.ActivateLane(lane);
            }
            if (tool.Name is not ("DisconnectPortal" or "GetSessionState")
                && inner is FoundationTool { RequiresProjectSession: true, SessionRequiresReset: true } resetAfterLane
                && (!resetAfterLane.Ported || PortedToolContract.WiredOperations(tool.Name).Length != 0))
                return Recorded(FoundationV4Result.Reject(release, tool.Name, id,
                    HostBehavior.SessionReset(), true));
            if (write && FoundationHostPorts.GenerationFence(tool.Name, JsonSerializer.Serialize(args), dispatched: true) is { } dispatchFenced)
                return Recorded(dispatchFenced);
            if (await FoundationHostPorts.BeforeDispatchAsync().ConfigureAwait(false) is { } batchRefusal) return Recorded(batchRefusal);
            if (approval != null && !approval.Disabled && pending!.ArgumentDigest != TiaOpenness.Shared.PendingApproval
                    .Create(ReleaseCatalogData.Releases[release].FoundationInvocationTag, release, tool.Name,
                    JsonSerializer.Serialize(args), (inner as FoundationTool)?.ApprovalIdentity, settings.TimeoutSeconds).ArgumentDigest)
            {
                await TiaOpenness.Shared.ApprovalClient.Complete(approval, "rejected-before-operation");
                return Recorded(FoundationV4Result.Reject(release, tool.Name, id,
                    new Error("Target changed while awaiting confirmation.", new ConfirmationRequiredDetails("denied", pending.PlanHash, id))));
            }
            var adapted = new Dictionary<string, JsonElement>(args, StringComparer.Ordinal);
            if (parameter != null)
            {
                var input = convert!(args[parameter]);
                if (input.Error != null) return Recorded(FoundationV4Result.Reject(release, tool.Name, id, input.Error));
                adapted.Remove(parameter);
                adapted[parameter + "Json"] = JsonSerializer.SerializeToElement(input.Json);
            }
            var original = request.Params;
            request.Params = new CallToolRequestParams { Name = inner.ProtocolTool.Name, Arguments = adapted };
            try
            {
                if (inner is not GenerationTool { OwnsApproval: true }) audit?.Start();
                if (inner is FoundationTool foundation) return Recorded(await foundation.InvokeV4Async(request, release, id, cancellationToken));
                var result = await inner.InvokeAsync(request, cancellationToken);
                JsonNode? body;
                try
                {
                    body = inner is GenerationTool
                        ? result.StructuredContent ?? JsonNode.Parse((result.Content.FirstOrDefault() as TextContentBlock)?.Text ?? "null")
                        : JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text);
                }
                catch (JsonException) /* swallow(privacy): SDK error text is an internal failure, not client data or invalid input */
                { return Recorded(FoundationV4Result.HostFailure(release, tool.Name, id)); }
                if (inner is GenerationTool) return Recorded(result);
                return Recorded(FoundationV4Result
                        .Host(release, tool.Name, id, body, parameter != null && parameter != "artifacts", result.IsError == true, args));
            }
            finally { request.Params = original; }
        }
        catch (OperationCanceledException) /* swallow(privacy): cancellation is represented by the stable V4 code, without exception text */
        {
            return Recorded(FoundationV4Result
                    .Reject(release, tool.Name, id,
                        new Error(sharedSession ? "Request cancelled before dispatch." : "Request cancelled before completion.",
                            new CancelledDetails(sharedSession ? "tool-queue" : "host"))));
        }
        catch (TiaMcp.Adapters.Contracts.AdapterPreconditionException ex)
        { return Recorded(FoundationV4Result.Failure(release, tool.Name, id, false, write, null, ex)); }
        catch (WorkerOperationException ex) when (ex.Condition == "worker-read-running")
        { return Recorded(FoundationV4Result.Failure(release, tool.Name, id, false, write, null, ex)); }
        catch (McpException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
        { return Recorded(FoundationV4Result.Reject(release, tool.Name, id, FoundationV4Result.Invalid("arguments"))); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        { return Recorded(FoundationV4Result.Reject(release, tool.Name, id, FoundationV4Result.Invalid("arguments"))); }
        catch (Exception) /* swallow(privacy): return a value-free V4 failure; implementation details are not protocol data */
        { return Recorded(FoundationV4Result.HostFailure(release, tool.Name, id)); }
        finally { lane?.Dispose(); }
    }

    internal static Error? SoftwareUnitCapabilityError(string release, string tool, IReadOnlyDictionary<string, JsonElement> args)
    {
        bool import = PlcSoftwareUnitSchema.IsImport(tool);
        bool scoped = import || PlcSoftwareUnitSchema.IsExport(tool)
            || tool is "ListPlcTags" or "ListPlcUserConstants" or "ListPlcSystemConstants" or "AnalyzePlcCrossReferences";
        if (!scoped) return null;
        string unit = args.TryGetValue("unitName", out var selected) ? selected.GetString() ?? "" : "";
        if (args.TryGetValue("includeUnits", out var all) && all.GetBoolean())
        {
            if (unit != "")
                return new Error("unitName and includeUnits cannot be combined.", new InvalidArgumentDetails("includeUnits", Array.Empty<string>()));
            unit = "*";
        }
        string[] Options(string key) => args.TryGetValue(key, out var options) && options.ValueKind != JsonValueKind.Null
            ? options.EnumerateArray().Select(option => option.GetString()!).ToArray() : Array.Empty<string>();
        string? unsupported = TiaMcp.Adapters.Contracts.PlcImportOptionPolicy.Unsupported(release,
            TiaMcp.Adapters.Contracts.Candidates.CandidateImportNames.IsDocuments(tool) ? "documents" : tool.Contains("Tag") ? "TagTable" : "block",
            import ? Options("swImportOptions") : null, import ? Options("importOptions") : null, unit);
        return unsupported == null ? null : new Error(unsupported, new UnsupportedCapabilityDetails(release, "software-unit-import-options", tool));
    }
}
