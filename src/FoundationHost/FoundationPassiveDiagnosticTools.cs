using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.FoundationHost;

internal static class FoundationPassiveDiagnosticTools
{
    internal static IEnumerable<McpServerTool> Create(string release, bool nativeSessionConfigured, Func<IReadOnlyList<McpServerTool>> registry,
        Func<JsonObject>? readinessProvider = null) => new McpServerTool[] {
        new PassiveDiagnosticTool("Bootstrap", release, nativeSessionConfigured, registry, readinessProvider),
        new PassiveDiagnosticTool("RunCapabilitySelfTest", release, nativeSessionConfigured, registry, readinessProvider)
    };
}
internal sealed class PassiveDiagnosticTool : McpServerTool
{
    private static readonly string[] Flags = { "connectIfNeeded", "includeProjectTree", "inspectPortalProcesses" };
    private readonly string release;
    private readonly bool nativeSessionConfigured;
    private readonly Func<IReadOnlyList<McpServerTool>> registry;
    private readonly Func<JsonObject>? readinessProvider;
    private readonly Tool tool;
    internal PassiveDiagnosticTool(string name, string release, bool nativeSessionConfigured, Func<IReadOnlyList<McpServerTool>> registry,
        Func<JsonObject>? readinessProvider = null)
    {
        this.release = release; this.nativeSessionConfigured = nativeSessionConfigured; this.registry = registry;
        this.readinessProvider = readinessProvider;
        var properties = new JsonObject();
        if (name == "RunCapabilitySelfTest") foreach (var flag in Flags) properties[flag] = new JsonObject { ["type"] = "boolean", ["default"] = false, ["enum"] = new JsonArray(false), ["description"] = "Only false is supported. Native checks/attach are unavailable in this passive host contract." };
        string description = name == "Bootstrap"
            ? "[read-only Foundation readiness] Reports the exact selected TIA release installation and matching Openness API, current Siemens TIA Openness group membership, readiness cause and fix, plus managed host registration diagnostics. No worker call, TIA launch, attach, repair or persistent settings change."
            : "[passive host candidate; upstream response incompatible] Managed registration/schema/source-protocol diagnostics only. No worker call, launch, attach, group inspection/repair, filesystem or persistent settings. Installed API/SDK, permission and native readiness remain not-probed. Native flags and automation-context options are unsupported.";
        tool = new Tool { Name = name, Description = description, InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false, ["required"] = new JsonArray() }) };
    }
    public override Tool ProtocolTool => tool;
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Validate before calling even the pure registry delegate. Do not echo untrusted arguments.
        if (request.Params?.Arguments is { } arguments)
            foreach (var pair in arguments)
                if (tool.Name != "RunCapabilitySelfTest" || !Flags.Contains(pair.Key, StringComparer.Ordinal) || pair.Value.ValueKind != JsonValueKind.False)
                    throw new McpException("Unsupported passive diagnostic argument. Bootstrap accepts no arguments; RunCapabilitySelfTest permits only connectIfNeeded, includeProjectTree and inspectPortalProcesses set to false. Native/selfConnect and automation-context options are unsupported.", null, McpErrorCode.InvalidParams);
        try
        {
            var result = FoundationPassiveDiagnostics.Inspect(release, nativeSessionConfigured, registry());
            if (tool.Name == "Bootstrap") FoundationPassiveDiagnostics.AddReadiness(result, readinessProvider?.Invoke() ?? FoundationPassiveDiagnostics.Readiness(release));
            else result["recommendedNextTool"] = null;
            return ValueTask.FromResult(new CallToolResult { IsError = !result["checks"]!["passed"]!.GetValue<bool>(), Content = new List<ContentBlock> { new TextContentBlock { Text = result.ToJsonString() } } });
        }
        catch (Exception) { throw new McpException("Passive host diagnostic contract inspection failed.", null, McpErrorCode.InternalError); }
    }
}
