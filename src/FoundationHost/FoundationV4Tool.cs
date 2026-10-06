using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Construction;
using TiaMcp.Logic.V4.Domain;
using TiaMcp.Logic.V4.Inputs;

namespace TiaMcp.LegacyHost;

// Only this boundary is registered. The existing host implementations retain their
// worker protocol and bounded parsers; legacy names are not callable aliases.
internal sealed class FoundationV4Tool : McpServerTool
{
    // P6-49 round 2 decision (2026-10-06): gate project save, save-as and close
    // even though the catalog marks them SESSION. Disconnect remains a session operation.
    private static readonly HashSet<string> SessionApprovalEntries = new(StringComparer.Ordinal)
        { "SaveProject", "CloseProject" };
    internal static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AddDeviceWithFallback"] = "CreateHardwareDevice", ["AttachToOpenProject"] = "AttachOpenProject",
        ["Bootstrap"] = "InitializeEnvironment", ["Connect"] = "ConnectPortal", ["Disconnect"] = "DisconnectPortal",
        ["DiagnosePortalConnectReadiness"] = "GetPortalConnectionReadiness", ["GetState"] = "GetSessionState",
        ["GetProject"] = "GetProjectInfo", ["CompileSoftware"] = "CompilePlcSoftware", ["CompileAndDiagnosePlc"] = "CompilePlcDiagnostics",
        ["GetBlocks"] = "ListPlcBlocks", ["GetBlockInfo"] = "GetPlcBlockInfo", ["GetBlocksWithHierarchy"] = "GetPlcBlockHierarchy",
        ["GetTypes"] = "ListPlcTypes", ["GetTypeInfo"] = "GetPlcTypeInfo", ["GetPlcTagTables"] = "ListPlcTagTables",
        ["GetPlcWatchTables"] = "ListPlcWatchTables", ["GetPlcExternalSources"] = "ListPlcExternalSources",
        ["GetTechnologyObjects"] = "ListTechnologyObjects", ["ReadPlcTags"] = "ListPlcTags",
        ["ReadPlcUserConstants"] = "ListPlcUserConstants", ["ReadPlcSystemConstants"] = "ListPlcSystemConstants",
        ["ExportBlock"] = "ExportPlcBlock", ["ExportBlocks"] = "ExportPlcBlocks", ["ExportType"] = "ExportPlcType",
        ["ExportTypes"] = "ExportPlcTypes", ["ImportBlock"] = "ImportPlcBlock", ["ImportType"] = "ImportPlcType",
        ["ImportBlocksFromDirectory"] = "ImportPlcBlocksFromDirectory",
        ["BuildPlcUdtXml"] = "BuildPlcUdt", ["BuildPlcTagTableXml"] = "BuildPlcTagTable",
        ["BuildPlcGlobalDbXml"] = "BuildPlcGlobalDb", ["BuildStructuredTextXml"] = "BuildStructuredText",
        ["BuildFlgNetCallXml"] = "BuildFlgNetCall", ["ComposePlcFcBlockXml"] = "BuildPlcFcBlock",
        ["ComposePlcFbBlockXml"] = "BuildPlcFbBlock", ["ComposePlcLadFcBlockXml"] = "BuildPlcLadFcBlock",
        ["BuildPlcSymbolManifestFromXmlPath"] = "BuildPlcSymbolManifestFromPath",
        ["ExportAsDocuments"] = "ExportPlcBlockDocuments", ["ExportBlocksAsDocuments"] = "ExportPlcBlocksDocuments",
        ["ImportFromDocuments"] = "ImportPlcBlockDocuments", ["ImportBlocksFromDocuments"] = "ImportPlcBlocksDocuments"
    };
    internal static string Name(string source) => Names.TryGetValue(source, out var name) ? name : source;
    internal static string Guidance(string text)
    {
        foreach (var pair in Names.OrderByDescending(p => p.Key.Length))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b" + pair.Key + @"\b", pair.Value);
        return text.Replace("Returns the existing PascalCase array", "Returns declarations in V4 data.items");
    }

    private readonly McpServerTool inner;
    private readonly string release;
    private readonly Tool tool;
    private readonly bool deviceCandidate;
    private readonly bool importCandidate;
    private readonly bool exportCandidate;
    private readonly bool sessionCandidate;
    private readonly bool saveCloseCandidate;
    private readonly bool sourceCandidate;
    private readonly bool compileCandidate;
    private readonly Func<TiaOpenness.Shared.ApprovalSettings>? approvalSettings;
    private readonly Func<TiaOpenness.Shared.PendingApproval, TiaOpenness.Shared.ApprovalSettings,
        CancellationToken, Task<TiaOpenness.Shared.ApprovalOutcome>>? approvalWait;
    private string? parameter;
    private Func<JsonElement, (string? Json, Error? Error)>? convert;

    internal FoundationV4Tool(McpServerTool inner, string release) : this(inner, release, null) { }

    internal FoundationV4Tool(McpServerTool inner, string release, Func<string, BehaviorPolicy>? policyForTest,
        Func<TiaOpenness.Shared.ApprovalSettings>? approvalSettings = null,
        Func<TiaOpenness.Shared.PendingApproval, TiaOpenness.Shared.ApprovalSettings,
            CancellationToken, Task<TiaOpenness.Shared.ApprovalOutcome>>? approvalWait = null)
    {
        this.inner = inner; this.release = release;
        this.approvalSettings = approvalSettings;
        this.approvalWait = approvalWait;
        var source = inner.ProtocolTool;
        deviceCandidate = source.Name == "AddDeviceWithFallback" && release == "19"
            && (policyForTest?.Invoke("P6-DEVICE") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-DEVICE")) == BehaviorPolicy.SafeV4;
        importCandidate = PlcImportContract.Entries.Contains(Name(source.Name), StringComparer.Ordinal)
            && (policyForTest?.Invoke("P6-IMPORT") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-IMPORT")) == BehaviorPolicy.SafeV4;
        exportCandidate = PlcExportContract.Entries.Contains(Name(source.Name), StringComparer.Ordinal)
            && (policyForTest?.Invoke("P6-EXPORT") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-EXPORT")) == BehaviorPolicy.SafeV4;
        sessionCandidate = SessionCandidateContract.Entries.Contains(Name(source.Name), StringComparer.Ordinal)
            && (policyForTest?.Invoke("P6-SESSION") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-SESSION")) == BehaviorPolicy.SafeV4;
        saveCloseCandidate = SaveCloseContract.Entries.Contains(Name(source.Name), StringComparer.Ordinal)
            && (policyForTest?.Invoke("P6-CLOSE") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-CLOSE")) == BehaviorPolicy.SafeV4;
        sourceCandidate = SourceContract.Entries.Contains(Name(source.Name), StringComparer.Ordinal)
            && (policyForTest?.Invoke("P6-SOURCE") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-SOURCE")) == BehaviorPolicy.SafeV4;
        compileCandidate = CompileContract.Entries.Contains(Name(source.Name), StringComparer.Ordinal)
            && (policyForTest?.Invoke("P6-COMPILE") ?? BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-COMPILE")) == BehaviorPolicy.SafeV4;
        var schema = JsonNode.Parse(source.InputSchema.GetRawText())!.AsObject();
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
            case "BuildPlcUdtXml": Construction<UdtSpec>("udt"); break;
            case "BuildPlcTagTableXml": Construction<PlcTagTableSpec>("tagTable"); break;
            case "BuildPlcGlobalDbXml": Construction<GlobalDbSpec>("globalDb"); break;
            case "BuildStructuredTextXml": Construction<StructuredTextSpec>("structuredText"); break;
            case "BuildFlgNetCallXml": Construction<FlgNetCallSpec>("flgNet"); break;
            case "ComposePlcFcBlockXml": Construction<FcBlockSpec>("fcBlock"); break;
            case "ComposePlcFbBlockXml": Construction<FbBlockSpec>("fbBlock"); break;
            case "ComposePlcLadFcBlockXml": Construction<LadFcBlockSpec>("ladFcBlock"); break;
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
        string description = Guidance(source.Description ?? "");
        if (parameter != null) description = description.Replace(parameter + "Json", parameter)
            + " Supply a typed object/array with exact camelCase fields, never a JSON string. Existing Foundation budgets and output-release limits apply.";
        if (parameter == "artifacts") description = "Offline dependency-first import planning. artifacts is a typed array of {id, target?, priority?, dependencies?}; 1..256 unique IDs, no missing or cyclic dependencies. No worker or file operations.";
        if (deviceCandidate)
        {
            schema = JsonNode.Parse(new DeviceCreationContract().InputSchema.GetRawText())!.AsObject();
            schema["properties"]!["family"]!["enum"] = new JsonArray("S7-1200", "S7-1500");
            description = "[PLC foundation][WRITE] Exact catalog device creation, restricted to CPU 1211C 6ES7211-1BE40-0XB0 and CPU 1513 6ES7513-1AM03-0AB0. Default preview; apply needs confirm=true, expectedPlanHash and expectedProjectFile. One Create, no fallback, save, compile or download. Test/accepted behaviorPolicy=safe-v4.";
        }
        if (importCandidate)
        {
            schema = JsonNode.Parse(PlcImportContract.Schema(Name(source.Name), release).GetRawText())!.AsObject();
            description = "[PLC foundation][WRITE] Same-release PLC import candidate. Preview returns original file hashes, exact target groups, complete inventory, overwrite capability and plan. Apply requires confirmation, expectedPlanHash and expectedProjectFile. Directory importOrder is explicit. One import per item with content readback; stop on first failure, unknown requires session reset. No source rewrite, compile, save, retry or rollback. Test/accepted behaviorPolicy=safe-v4.";
        }
        if (exportCandidate)
        {
            schema = JsonNode.Parse(PlcExportContract.Schema(Name(source.Name), release).GetRawText())!.AsObject();
            description = "[PLC foundation][WRITE] Reviewed export candidate. Default preview lists objects and destination identities without writes. Apply requires confirmation, expectedPlanHash and expectedProjectFile. One export per item in synchronous worker dispatch, sibling staging then host publication, content/path readback. Stop and retain staging on failure; unknown requires session reset. No scope expansion. Test/accepted behaviorPolicy=safe-v4.";
        }
        if (sessionCandidate)
        {
            schema = JsonNode.Parse(SessionCandidateContract.Schema(Name(source.Name)).GetRawText())!.AsObject();
            description = SessionCandidateContract.Description;
        }
        if (saveCloseCandidate)
        {
            schema = JsonNode.Parse(SaveCloseContract.Schema(Name(source.Name)).GetRawText())!.AsObject();
            description = SaveCloseContract.Description;
        }
        if (compileCandidate) { schema = JsonNode.Parse(CompileContract.Schema(Name(source.Name)).GetRawText())!.AsObject(); description = CompileContract.Description; }
        if (sourceCandidate) { schema = JsonNode.Parse(SourceContract.Schema(Name(source.Name)).GetRawText())!.AsObject(); description = SourceContract.Description; }
        tool = new Tool { Name = Name(source.Name), Description = description
            + (inner is FoundationTool { IsNative: true } && !deviceCandidate && !importCandidate && !exportCandidate && !sessionCandidate && !saveCloseCandidate && !sourceCandidate && !compileCandidate ? " Native behaviorPolicy=current; V4 native acceptance is pending." : ""),
            InputSchema = JsonSerializer.SerializeToElement(schema), OutputSchema = FoundationV4Result.Schema };
    }

    public override Tool ProtocolTool => tool;
    private bool Candidate => deviceCandidate || importCandidate || exportCandidate || sessionCandidate || saveCloseCandidate || sourceCandidate || compileCandidate;
    private bool SessionApprovalApply(JsonObject args)
    {
        if (!SessionApprovalEntries.Contains(tool.Name)) return false;
        if (saveCloseCandidate) return (string?)args["mode"] == "apply";
        return !args.TryGetPropertyValue("dryRun", out var dryRun) || dryRun is not JsonValue value
            || !value.TryGetValue<bool>(out var preview) || !preview;
    }
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var inputArguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        var arguments = JsonSerializer.SerializeToNode(inputArguments) as JsonObject ?? new JsonObject();
        bool write = inner is FoundationTool { IsWrite: true } || Candidate && arguments["mode"]?.ToString() == "apply"
            || SessionApprovalApply(arguments);
        var settings = approvalSettings?.Invoke() ?? TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
        TiaOpenness.Shared.ApprovalOutcome? approval = null;
        using var audit = TiaOpenness.Shared.AuditInvocation.Begin(write, "foundation", release, tool.Name);
        var result = await InvokeCoreAsync(request, cancellationToken, settings, value => approval = value);
        var body = result.StructuredContent ?? JsonNode.Parse((result.Content.FirstOrDefault() as TextContentBlock)?.Text ?? "null");
        if (body != null && (approval != null || write && !settings.Enabled))
        {
            body = TiaOpenness.Shared.ApprovalResult.Decorate(body, write && !settings.Enabled, approval?.Request.RequestId);
            result = new CallToolResult { IsError = result.IsError, StructuredContent = body, Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
            if (approval != null) await TiaOpenness.Shared.ApprovalClient.Complete(approval,
                (string?)body["error"]?["code"] == "CANCELLED" ? "unknown" : (string?)body["meta"]?["outcome"] ?? "unknown");
        }
        if (audit != null) audit.Complete(result.StructuredContent?.ToJsonString() ?? (result.Content.FirstOrDefault() as TextContentBlock)?.Text);
        return result;
    }

    private async ValueTask<CallToolResult> InvokeCoreAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken,
        TiaOpenness.Shared.ApprovalSettings settings, Action<TiaOpenness.Shared.ApprovalOutcome> capture)
    {
        string id = Meta.Correlate(null);
        var args = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        using var journal = TiaMcpServer.ModelContextProtocol.InvocationJournal.Observe(id, tool.Name, "foundation", release,
            inner is FoundationTool { JournalIsWrite: true }, () => JsonSerializer.Serialize(args));
        CallToolResult Recorded(CallToolResult result)
        {
            journal.Complete(() => JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions));
            return result;
        }
        try
        {
            var validation = new InputContract<ToolArguments>(new InputSchema(tool.InputSchema), new InputBudget())
                .Read(JsonSerializer.SerializeToElement(args), "arguments");
            if (validation.Error != null && compileCandidate) return Recorded(FoundationV4Result.ImportCandidate(CompileSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted)));
            if (validation.Error != null && sourceCandidate) return Recorded(FoundationV4Result.ImportCandidate(SourceSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted)));
            if (validation.Error != null && saveCloseCandidate) return Recorded(FoundationV4Result.ImportCandidate(SaveCloseSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted)));
            if (validation.Error != null && sessionCandidate) return Recorded(FoundationV4Result.ImportCandidate(SessionCandidateSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted)));
            if (validation.Error != null && exportCandidate) return Recorded(FoundationV4Result.ImportCandidate(PlcExportSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted)));
            if (validation.Error != null && importCandidate) return Recorded(FoundationV4Result.ImportCandidate(PlcImportSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted)));
            if (validation.Error != null) return Recorded(deviceCandidate
                ? FoundationV4Result.DeviceCandidate(DeviceCreationSession.Result(release, tool.Name, id, null, validation.Error, Outcome.RejectedBeforeOperation, Execution.NotStarted))
                : FoundationV4Result.Reject(release, tool.Name, id, validation.Error, inner is FoundationTool { IsNative: true }));
            bool preview = Candidate
                && (!args.TryGetValue("mode", out var mode) || mode.GetString() != "apply");
            bool sessionApprovalApply = SessionApprovalApply(JsonSerializer.SerializeToNode(args) as JsonObject ?? new JsonObject());
            if ((inner is FoundationTool { IsWrite: true } || Candidate || sessionApprovalApply) && !preview)
            {
                var pending = TiaOpenness.Shared.PendingApproval.Create("foundation", release, tool.Name, JsonSerializer.Serialize(args),
                    (inner as FoundationTool)?.ApprovalIdentity, settings.TimeoutSeconds, id);
                var approval = approvalWait == null
                    ? await TiaOpenness.Shared.ApprovalClient.Wait(pending, settings, cancellationToken)
                    : await approvalWait(pending, settings, cancellationToken);
                capture(approval);
                if (approval.Reason != null) return Recorded(FoundationV4Result.Reject(release, tool.Name, id,
                    new Error("Workbench confirmation is required before this write.", new ConfirmationRequiredDetails(approval.Reason, pending.PlanHash, id))));
                if (!approval.Disabled && pending.ArgumentDigest != TiaOpenness.Shared.PendingApproval.Create("foundation", release, tool.Name,
                    JsonSerializer.Serialize(args), (inner as FoundationTool)?.ApprovalIdentity, settings.TimeoutSeconds).ArgumentDigest)
                {
                    await TiaOpenness.Shared.ApprovalClient.Complete(approval, "rejected-before-operation");
                    return Recorded(FoundationV4Result.Reject(release, tool.Name, id,
                        new Error("Target changed while awaiting confirmation.", new ConfirmationRequiredDetails("denied", pending.PlanHash, id))));
                }
            }
            if (compileCandidate) return Recorded(await ((FoundationTool)inner).InvokeCompileCandidateAsync(args, release, tool.Name, id, cancellationToken));
            if (sourceCandidate) return Recorded(await ((FoundationTool)inner).InvokeSourceCandidateAsync(args, release, tool.Name, id, cancellationToken));
            if (saveCloseCandidate) return Recorded(await ((FoundationTool)inner).InvokeSaveCloseCandidateAsync(args, release, tool.Name, id, cancellationToken));
            if (sessionCandidate) return Recorded(await ((FoundationTool)inner).InvokeSessionCandidateAsync(args, release, tool.Name, id, cancellationToken));
            if (deviceCandidate) return Recorded(await ((FoundationTool)inner).InvokeDeviceCandidateAsync(args, release, id, cancellationToken));
            if (importCandidate) return Recorded(await ((FoundationTool)inner).InvokeImportCandidateAsync(args, release, tool.Name, id, cancellationToken));
            if (exportCandidate) return Recorded(await ((FoundationTool)inner).InvokeExportCandidateAsync(args, release, tool.Name, id, cancellationToken));
            var adapted = new Dictionary<string, JsonElement>(args, StringComparer.Ordinal);
            if (inner is FoundationTool nativePath && args.TryGetValue("softwarePath", out var softwarePath)
                && BehaviorCapabilities.Select(typeof(FoundationV4Tool).Assembly, release, "P6-SOURCE") == BehaviorPolicy.SafeV4)
            {
                var target = await nativePath.ResolveSourcePathAsync(release, tool.Name, id, softwarePath.GetString()!, cancellationToken);
                if (!target.Ok) return Recorded(FoundationV4Result.ImportCandidate(target));
                adapted["softwarePath"] = JsonSerializer.SerializeToElement(target.Data!.Value.GetProperty("softwarePath").GetString());
            }
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
                if (inner is FoundationTool foundation) return Recorded(await foundation.InvokeV4Async(request, release, id, cancellationToken));
                var result = await inner.InvokeAsync(request, cancellationToken);
                var body = JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text);
                return Recorded(FoundationV4Result.Host(release, tool.Name, id, body, parameter != null && parameter != "artifacts", result.IsError == true, args));
            }
            finally { request.Params = original; }
        }
        catch (OperationCanceledException) /* swallow(privacy): cancellation is represented by the stable V4 code, without exception text */
        { return Recorded(FoundationV4Result.Reject(release, tool.Name, id, new Error("Request cancelled before completion.", new CancelledDetails("host")))); }
        catch (McpException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
        { return Recorded(FoundationV4Result.Reject(release, tool.Name, id, FoundationV4Result.Invalid("arguments"))); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        { return Recorded(FoundationV4Result.Reject(release, tool.Name, id, FoundationV4Result.Invalid("arguments"))); }
        catch (Exception) /* swallow(privacy): return a value-free V4 failure; implementation details are not protocol data */
        { return Recorded(FoundationV4Result.HostFailure(release, tool.Name, id)); }
    }
}
