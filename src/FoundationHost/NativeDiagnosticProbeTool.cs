#if TIA_FOUNDATION_TEST_HOST
extern alias toolpipeline;
using DeclaredHost = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;
using EngineHostConfiguration = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration;
#else
using DeclaredHost = TiaMcpServer.ModelContextProtocol.McpServer;
#endif
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.FoundationHost;

internal sealed class NativeDiagnosticProbeTool(IFoundationSessionWorker worker, string release = "") : McpServerTool
{
    private const string TestObjectPrefix = "MCP_RC_";
    private static readonly string[] ExportFields = { "plcPath", "sourceCulture", "targetCulture" };
    private static readonly string[] MappingFields = { "ownerColumn", "categoryColumn", "textListCategory", "alarmCategory" };
    private static readonly string[] StringFields = ExportFields.Concat(MappingFields).Concat(new[] { "probe", "sheetName", "softwarePath",
        "blockPath", "name", "sourceKind", "sourcePath", "libraryName", "expectedProjectFile", "textListNames", "cultures" }).ToArray();
    private static readonly string[] ReplyFields = { "status", "installedPatch", "tiaUpdateLevel", "tiaProductVersion", "sivarcProductVersion",
        "sivarcLicenceState", "ownerRows", "textListRows", "alarmTextRows", "tagDefinitionCount", "textDefinitionCount", "tagFound", "textFound",
        "tagItemsRead", "textItemsRead", "tagTestNames", "textTestNames", "created", "exportState", "fileBytes", "worksheetColumnCounts", "steps",
        "exceptionType" };
    private static readonly Tool Definition = new() {
        Name = "ProbeNativeCrash", Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false },
        InputSchema = JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "probe" },
            properties = StringFields.Select(name => new KeyValuePair<string, object>(name, new { type = "string" }))
                .Append(new KeyValuePair<string, object>("dryRun", new { type = "boolean", @default = true })).ToDictionary(p => p.Key, p => p.Value),
            additionalProperties = false })
    };
    public override Tool ProtocolTool => Definition;

    internal Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> Handler(HostSession session)
        => (request, token) => request.Params?.Name == Definition.Name ? session.Invoke(this, request, false, token)
            : throw new McpException($"Unknown tool: '{request.Params?.Name}'", null, McpErrorCode.InvalidParams);

    private static bool Exactly(IReadOnlyDictionary<string, JsonElement> args, string[] required, params string[] optional)
        => required.All(args.ContainsKey) && args.Keys.All(k => k == "probe" || required.Contains(k) || optional.Contains(k))
            && args.Where(p => p.Key != "dryRun").All(p => p.Value.ValueKind == JsonValueKind.String)
            && (!args.TryGetValue("dryRun", out var dryRun) || dryRun.ValueKind is JsonValueKind.True or JsonValueKind.False);

    internal static bool HasTestObjectName(string path)
        => path.Replace('\\', '/').TrimEnd('/').Split('/').Last().StartsWith(TestObjectPrefix, StringComparison.Ordinal);

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var args = request.Params?.Arguments;
        var data = new JsonObject();
        var input = new JsonObject();
        string? probe = args != null && args.TryGetValue("probe", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string[] block = { "softwarePath", "blockPath" };
        string? operation = probe switch {
            "portal-info" when args!.Count == 1 => "diagnostics.ReadProbePortalInfo",
            "alarm-projecttexts-scan" when ExportFields.All(args!.ContainsKey) && (!MappingFields.Any(args.ContainsKey) || MappingFields.All(args.ContainsKey))
                && args.Keys.All(k => k is "probe" or "sheetName" || ExportFields.Contains(k) || MappingFields.Contains(k))
                && args.Values.All(v => v.ValueKind == JsonValueKind.String) => "diagnostics.ReadProbeProjectTexts",
            "sivarc-count" or "sivarc-item" when Exactly(args!, block) => "diagnostics.ReadProbeSivarc",
            "sivarc-find" when Exactly(args!, block.Append("name").ToArray()) => "diagnostics.ReadProbeSivarc",
            "mastercopy-noexclusive" when Exactly(args!, new[] { "sourceKind", "softwarePath", "sourcePath" }, "libraryName", "dryRun",
                "expectedProjectFile") => "diagnostics.ExecuteProbeMasterCopy",
            "alarm-export-overload" when Exactly(args!, new[] { "softwarePath", "textListNames", "cultures" })
                => "diagnostics.ExportProbeAlarmTextLists",
            _ => null
        };
        bool dryRun = args == null || !args.TryGetValue("dryRun", out var preview) || preview.ValueKind != JsonValueKind.False;
        // The host refuses first, so a disabled probe opens no approval and reaches no worker.
        if (Environment.GetEnvironmentVariable("TIA_MCP_CRASH_PROBES") != "1")
            data["status"] = "disabled";
        else if (operation == null)
            data["status"] = "unsupported";
        else if (operation == "diagnostics.ExecuteProbeMasterCopy" && (!HasTestObjectName(args!["sourcePath"].GetString()!)
            || !dryRun && string.IsNullOrWhiteSpace(args.TryGetValue("expectedProjectFile", out var file) ? file.GetString() : null)))
            data["status"] = "invalid-argument";
        else
        {
            foreach (var pair in args!.Where(p => p.Key is not ("dryRun" or "expectedProjectFile")
                && (p.Key != "probe" || operation == "diagnostics.ReadProbeSivarc"))) input[pair.Key] = pair.Value.GetString();
            if (operation == "diagnostics.ExecuteProbeMasterCopy") input["dryRun"] = dryRun;
            if (operation == "diagnostics.ExportProbeAlarmTextLists")
            {
                string? root = TiaOpenness.Shared.DataLocations.Current.OutputRoot;
                if (root == null) data["status"] = "no-output-root";
                else input["exportPath"] = Path.Combine(root, "crash-probes", Guid.NewGuid().ToString("N"), "alarm-textlists.xlsx");
            }
            if (data["status"] == null) await Run(request, operation, input, dryRun, data, cancellationToken).ConfigureAwait(false);
        }
        return new CallToolResult { IsError = (string?)data["status"] is not ("ok" or "layout-only" or "preview"), StructuredContent = data,
            Content = new[] { new TextContentBlock { Text = data.ToJsonString() } } };
    }

    private async Task Run(RequestContext<CallToolRequestParams> request, string operation, JsonObject input, bool dryRun, JsonObject data,
        CancellationToken cancellationToken)
    {
        using var shared = worker.EnterRequest(request);
        string id = Guid.NewGuid().ToString("N");
        using var scope = FoundationWorkerRequest.Use(id, Definition.Name);
        TiaOpenness.Shared.ApprovalOutcome? approval = null;
        if (operation == "diagnostics.ExecuteProbeMasterCopy" && !dryRun)
        {
            // The host approval wait used by other writes; the worker then checks confirm and the bound project.
            var settings = TiaOpenness.Shared.ApprovalSettings.Load(TiaOpenness.Shared.ApprovalSettings.SettingsPath);
            var pending = TiaOpenness.Shared.PendingApproval.Create(DeclaredHost.InvocationHostTag, release, Definition.Name,
                JsonSerializer.Serialize(request.Params!.Arguments), string.IsNullOrEmpty(worker.ApprovalIdentity) ? null : worker.ApprovalIdentity,
                settings.TimeoutSeconds, id);
            pending.Deadline = DateTimeOffset.UtcNow.AddSeconds(pending.TimeoutSeconds);
            var wait = DeclaredHost.ApprovalWaitOverrideForTests;
            approval = await TiaOpenness.Shared.ApprovalStatusBoard.Wait(EngineHostConfiguration.CurrentOrNull == null ? worker
                : DeclaredHost.CurrentApprovalSession(), pending,
                () => wait == null ? TiaOpenness.Shared.ApprovalClient.Wait(pending, settings, cancellationToken)
                    : wait(pending, settings, cancellationToken)).ConfigureAwait(false);
            data["approval"] = approval.Disabled ? "disabled" : approval.Reason ?? "granted";
            if (approval.Reason != null)
            {
                data["status"] = "approval-denied";
                return;
            }
            input["confirm"] = true;
            input["expectedProjectFile"] = request.Params.Arguments!["expectedProjectFile"].GetString();
        }
        string outcome = "unknown";
        try
        {
            var reply = await worker.Call(operation, input, cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new InvalidDataException();
            foreach (var field in ReplyFields)
                if (reply.ContainsKey(field)) data[field] = reply[field]?.DeepClone();
            if (operation == "diagnostics.ReadProbeProjectTexts" && reply["worksheetHeaders"] is JsonObject headers)
                data["worksheetHeaders"] = headers.DeepClone();
            outcome = (string?)data["status"] switch { "ok" => "succeeded", "create-failed" => "failed", _ => "rejected-before-operation" };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            data["status"] = "read-failed";
            data["exceptionType"] = error.GetBaseException().GetType().FullName;
        }
        finally
        {
            if (approval != null) await TiaOpenness.Shared.ApprovalClient.Complete(approval, outcome).ConfigureAwait(false);
        }
    }
}
