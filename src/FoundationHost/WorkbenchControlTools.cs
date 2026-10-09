using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaOpenness.Shared;

namespace TiaMcp.FoundationHost;

internal static class WorkbenchControlTools
{
    internal static readonly string[] Names = { "ShowWorkbenchPage", "ShowWorkbenchBlock", "ShowWorkbenchCall",
        "ShowWorkbenchLadder", "ShowWorkbenchAtlas", "GetWorkbenchState", "GetWorkbenchSelection", "PrefillWorkbenchForm" };
    internal static IEnumerable<McpServerTool> Create(IFoundationWorker worker, string release,
        WorkbenchControlClient? client = null) => Names.Select((name, index) => (McpServerTool)new WorkbenchControlTool(
            name, (WorkbenchControlOperation)index, release, WorkbenchControlSession.For(worker), client ?? new()));
}

internal sealed class WorkbenchControlTool : McpServerTool
{
    private readonly Tool tool;
    private readonly WorkbenchControlOperation operation;
    private readonly string release;
    private readonly WorkbenchControlSession session;
    private readonly WorkbenchControlClient client;
    internal WorkbenchControlTool(string name, WorkbenchControlOperation operation, string release,
        WorkbenchControlSession session, WorkbenchControlClient client)
    {
        _ = TiaMcp.Versioning.TiaVersionCatalog.Get(release);
        this.operation = operation; this.release = release; this.session = session; this.client = client;
        bool read = operation is WorkbenchControlOperation.ReadState or WorkbenchControlOperation.ReadSelection;
        string[] descriptions = {
            "Show an allowed Workbench page. Human activity, modal dialogs and the UI control switch can refuse the request.",
            "Locate an exact software and block path in the already loaded Workbench tree for the bound project.",
            "Show an ordinary call by its V4 requestId. Pending approval rows are refused.",
            "Locate a block and optionally open a ladder render registered in this MCP session. Without a render, return the human next step.",
            "Show the atlas locator and optionally open an atlas render registered in this MCP session. Without a render, return the human next step.",
            "Read the frozen Workbench state, including the read-only UI control switch. session.source identifies the Workbench bridge session.",
            "Read frozen Workbench selection with offset paging, up to 256 checked blocks.",
            "Set or clear an allowed UI form prefill owned by this MCP session. A human keeps or clears it; success awaits confirmation." };
        tool = new() { Name = name, Description = "[L1][Workbench][" + (read ? "READ" : "UI") + "] " + descriptions[(int)operation]
            + " Uses an already running local Workbench; no TIA calls or automatic launch.",
            InputSchema = Schema(operation), OutputSchema = FoundationV4Result.Schema,
            Annotations = new() { ReadOnlyHint = read, DestructiveHint = false, OpenWorldHint = false } };
    }
    public override Tool ProtocolTool => tool;
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        using var actor = ActorScope.EnterCall(request.Server?.SessionId, request.Server);
        return await InvokeV4Async(request, Meta.Correlate(null), cancellationToken);
    }
    internal async ValueTask<CallToolResult> InvokeV4Async(RequestContext<CallToolRequestParams> request, string id, CancellationToken token)
    {
        var args = JsonSerializer.SerializeToElement(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
        var validation = new InputContract<ToolArguments>(new InputSchema(tool.InputSchema), new InputBudget()).Read(args, "arguments");
        if (validation.Error != null) return FoundationV4Result.Reject(release, tool.Name, id, validation.Error);
        WorkbenchControlArguments arguments;
        try { arguments = (WorkbenchControlArguments)JsonSerializer.Deserialize(args.GetRawText(),
            WorkbenchControlProtocol.ArgumentType(operation), WorkbenchControlProtocol.Json)!; }
        catch (JsonException) /* swallow(privacy): expose only the validated public argument boundary */
        { return FoundationV4Result.Reject(release, tool.Name, id, FoundationV4Result.Invalid("arguments")); }
        string? renderId = arguments is WorkbenchDisplayLadderArguments ladder ? ladder.RenderRequestId
            : (arguments as WorkbenchDisplayAtlasArguments)?.RenderRequestId;
        if (renderId != null)
        {
            var target = arguments as WorkbenchDisplayLadderArguments;
            var resolved = session.Resolve(renderId, target == null ? WorkbenchRenderKind.Atlas : WorkbenchRenderKind.Ladder,
                target?.SoftwarePath, target?.BlockPath);
            if (resolved.Error != null) return FoundationV4Result.Reject(release, tool.Name, id, resolved.Error);
            if (target != null) target.Artifact = resolved.Artifact;
            else ((WorkbenchDisplayAtlasArguments)arguments).Artifact = resolved.Artifact;
        }
        string label = request.Server?.ClientInfo?.Name ?? "";
        var control = new WorkbenchControlRequest { RequestId = id, Operation = operation, Arguments = arguments,
            Origin = new() { ReleaseKey = release, HostProcessId = Environment.ProcessId, McpSession = session.SessionKey,
                ClientName = label.Length > 256 ? label[..256] : label, BoundProjectFile = session.Project } };
        control.DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(control.TimeoutSeconds);
        int frameBytes = JsonSerializer.SerializeToUtf8Bytes(control, WorkbenchControlProtocol.Json).Length;
        if (frameBytes > WorkbenchControlFrames.MaximumBytes)
            return FoundationV4Result.Reject(release, tool.Name, id, new("Workbench control input exceeds the frame budget.",
                new LimitExceededDetails("arguments", WorkbenchControlFrames.MaximumBytes, frameBytes)));
        try { return Map(await client.Send(control, token).ConfigureAwait(false), release, tool.Name, id, operation); }
        catch (OperationCanceledException) /* swallow(privacy): cancellation never exposes private transport exception text */
        { return FoundationV4Result.Reject(release, tool.Name, id, new("Workbench request cancelled.", new CancelledDetails("workbench-control"))); }
    }
    internal static CallToolResult Map(WorkbenchControlResponse response, string release, string name, string id, WorkbenchControlOperation operation)
    {
        bool read = operation is WorkbenchControlOperation.ReadState or WorkbenchControlOperation.ReadSelection;
        bool done = response.Status == WorkbenchControlStatus.Done;
        var refusal = response.Refusal;
        ErrorDetails? details = refusal?.Code switch
        {
            WorkbenchControlError.ResourceUnavailable => new ResourceUnavailableDetails(refusal.Resource ?? "workbench-control"),
            WorkbenchControlError.AccessDenied => new AccessDeniedDetails("workbench-control", refusal.Target),
            WorkbenchControlError.UnsupportedCapability => new UnsupportedCapabilityDetails(release, refusal.Capability ?? "workbench-control.v1", refusal.Condition),
            WorkbenchControlError.PreconditionFailed => new PreconditionFailedDetails(refusal.Condition, refusal.Target),
            WorkbenchControlError.IdentityMismatch => new IdentityMismatchDetails(refusal.Target, refusal.Expected, refusal.Actual),
            WorkbenchControlError.NotFound => new NotFoundDetails(refusal.Target),
            WorkbenchControlError.TargetAmbiguous => new TargetAmbiguousDetails(refusal.Target, refusal.Candidates),
            WorkbenchControlError.Timeout => new TimeoutDetails(refusal.Stage ?? (read ? "workbench-read" : "workbench-ui")),
            WorkbenchControlError.InvalidArgument => new InvalidArgumentDetails(refusal.Target, Array.Empty<string>()),
            WorkbenchControlError.InternalError => new InternalErrorDetails(id),
            _ => null
        };
        var data = done ? JsonSerializer.SerializeToNode(response.Data, WorkbenchControlProtocol.Json)!.AsObject()
            : new JsonObject { ["workbench"] = JsonSerializer.SerializeToNode(response.Workbench, WorkbenchControlProtocol.Json),
                ["controlRefusal"] = JsonSerializer.SerializeToNode(refusal, WorkbenchControlProtocol.Json) };
        Paging? paging = null;
        if (done && response.Data?.Paging is WorkbenchPaging page)
        {
            bool complete = (long)page.Offset + page.Limit >= page.Total;
            paging = new(PagingMode.Offset, page.Offset, page.Limit, complete ? null : page.Offset + page.Limit,
                null, null, page.Total, complete);
            data.Remove("paging");
        }
        bool failed = response.Status == WorkbenchControlStatus.Failed && refusal?.Code != WorkbenchControlError.Timeout;
        var envelope = Envelope.Create(data, done ? null : new Error("Workbench control " + (failed ? "failed." : "was refused."),
            details ?? new InternalErrorDetails(id)), new Meta(DateTimeOffset.UtcNow, release, name, id,
            done ? Outcome.Succeeded : failed ? Outcome.Failed : Outcome.RejectedBeforeOperation,
            failed ? Execution.Completed : done ? read ? Execution.ReadOnly : Execution.Completed : Execution.NotStarted,
            false, BehaviorPolicy.NotApplicable, done ? Completeness.Complete : Completeness.None,
            paging, Array.Empty<Warning>()));
        return FoundationV4Result.ImportCandidate(envelope);
    }
    private static JsonElement Schema(WorkbenchControlOperation operation)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required };
        JsonObject Text(int length = 4096) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = length };
        JsonObject Hex() => new() { ["type"] = "string", ["pattern"] = "^[0-9a-f]{32}$" };
        JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()) };
        void Add(string name, JsonObject value, bool mandatory = true)
        {
            value["description"] = name switch {
                "page" => "Allowed Workbench page; protected approval and permission pages are excluded.",
                "softwarePath" => "Exact software path in the already loaded Workbench tree for the bound project.",
                "blockPath" => "Exact escaped block path in the already loaded Workbench tree.",
                "requestId" => "32 lowercase hexadecimal characters identifying an ordinary V4 call row.",
                "renderRequestId" => "Request ID of a successful render registered in this MCP session; no arbitrary file path.",
                "offset" => "Zero-based offset in the frozen Workbench checked-block selection.",
                "limit" => "Maximum checked blocks in this page, from 1 to 256; default 100.",
                "form" => "Allowed form receiving the prefill; execution and approval forms are excluded.",
                "mode" => "Set an awaiting-human-confirmation prefill or clear this MCP session's prefill.",
                "fields" => "Closed fields for the selected form; clear requires an empty object.",
                _ => throw new InvalidOperationException("Unknown Workbench argument.") };
            properties[name] = value; if (mandatory) required.Add(name);
        }
        switch (operation)
        {
            case WorkbenchControlOperation.DisplayPage: Add("page", Enum("overview", "blocks", "versionControl", "calls", "audit", "environment", "log")); break;
            case WorkbenchControlOperation.DisplayBlock: case WorkbenchControlOperation.DisplayLadder:
                Add("softwarePath", Text()); Add("blockPath", Text());
                if (operation == WorkbenchControlOperation.DisplayLadder) Add("renderRequestId", Hex(), false); break;
            case WorkbenchControlOperation.DisplayCall: Add("requestId", Hex()); break;
            case WorkbenchControlOperation.DisplayAtlas: Add("renderRequestId", Hex(), false); break;
            case WorkbenchControlOperation.ReadSelection:
                Add("offset", new() { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = int.MaxValue, ["default"] = 0 }, false);
                Add("limit", new() { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 256, ["default"] = 100 }, false); break;
            case WorkbenchControlOperation.PrefillForm:
                Add("form", Enum("inspectionRules", "blockFilter", "blockSelection")); Add("mode", Enum("set", "clear"));
                Add("fields", new() { ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JsonObject { ["namePattern"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256,
                            ["description"] = "Inspection name pattern, awaiting human confirmation." },
                        ["filter"] = new JsonObject { ["type"] = "string", ["maxLength"] = 256,
                            ["description"] = "Block tree filter text, awaiting human confirmation." },
                        ["softwarePath"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 4096,
                            ["description"] = "Exact software path owning the selected blocks." },
                        ["blockPaths"] = new JsonObject { ["type"] = "array", ["maxItems"] = 256, ["items"] = Text(),
                            ["description"] = "Exact escaped block paths to prefill, awaiting human confirmation." } } });
                var branches = new JsonArray();
                foreach (string form in new[] { "inspectionRules", "blockFilter", "blockSelection" })
                    foreach (string mode in new[] { "set", "clear" })
                    {
                        string[] fields = mode == "clear" ? Array.Empty<string>() : form == "inspectionRules" ? new[] { "namePattern" }
                            : form == "blockFilter" ? new[] { "filter" } : new[] { "softwarePath", "blockPaths" };
                        var fieldProperties = new JsonObject();
                        foreach (string field in fields) fieldProperties[field] = properties["fields"]!["properties"]![field]!.DeepClone();
                        branches.Add(new JsonObject { ["properties"] = new JsonObject { ["form"] = new JsonObject { ["const"] = form },
                            ["mode"] = new JsonObject { ["const"] = mode }, ["fields"] = new JsonObject { ["type"] = "object",
                                ["additionalProperties"] = false, ["properties"] = fieldProperties, ["required"] = new JsonArray(fields.Select(field => (JsonNode)JsonValue.Create(field)!).ToArray()) } } });
                    }
                schema["oneOf"] = branches; break;
        }
        return JsonSerializer.SerializeToElement(schema);
    }
}
