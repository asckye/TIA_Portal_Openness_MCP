using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        private static readonly AsyncLocal<bool> McpApprovalContext = new AsyncLocal<bool>();
        private static readonly AsyncLocal<int> ApprovalPreviewDepth = new AsyncLocal<int>();
        private static readonly AsyncLocal<Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>?> ApprovalWaitOverride =
            new AsyncLocal<Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>?>();
        // P6-49 round 2 decision (2026-10-06): project save, save-as and close
        // rewrite or discard project files, so gate them even though the catalog marks them SESSION.
        private static readonly HashSet<string> SessionApprovalEntries = new HashSet<string>(StringComparer.Ordinal)
            { "SaveProject", "SaveProjectCopy", "CloseProject" };
        internal static Func<PendingApproval, ApprovalSettings, CancellationToken, Task<ApprovalOutcome>>? ApprovalWaitOverrideForTests
        { get => ApprovalWaitOverride.Value; set => ApprovalWaitOverride.Value = value; }
        private sealed class InternalPreviewScope : IDisposable
        {
            private readonly int previous = ApprovalPreviewDepth.Value;
            internal InternalPreviewScope() { ApprovalPreviewDepth.Value = previous + 1; }
            public void Dispose() => ApprovalPreviewDepth.Value = previous;
        }
        internal static IDisposable BeginReadOnlyApprovalPreview() => new InternalPreviewScope();
        internal static bool ApprovalWrite(string tool, string arguments)
        {
            var args = JsonNode.Parse(arguments)!.AsObject();
            if (BehaviorCapabilities.EntryPolicy(typeof(McpServer).Assembly, ReleaseKey, tool, BehaviorPolicy.Current) == BehaviorPolicy.SafeV4)
                return args["mode"] is JsonValue mode && mode.TryGetValue<string>(out var value) && value == "apply";
            if (TryDryRunDefault(tool, out var defaultPreview))
            {
                if (args["dryRun"] is JsonValue dryRun && dryRun.TryGetValue<bool>(out var preview))
                {
                    if (preview) return false;
                }
                else if (args.ContainsKey("dryRun") || defaultPreview) return false;
                else return ToolCatalog.IsWrite(tool) || SessionApprovalEntries.Contains(tool);
            }
            return ToolCatalog.IsWrite(tool) || SessionApprovalEntries.Contains(tool);
        }
        private static bool TryDryRunDefault(string tool, out bool defaultPreview)
        {
            defaultPreview = true;
            if (!AllToolMethods(includeUnavailable: true).TryGetValue(tool, out var method)) return false;
            var parameter = method.GetParameters().FirstOrDefault(item => item.Name == "dryRun" && item.ParameterType == typeof(bool));
            if (parameter == null) return false;
            if (!parameter.HasDefaultValue || parameter.DefaultValue is not bool value) return true;
            defaultPreview = value;
            return true;
        }
        internal static bool ApprovalResultWrite(string tool, string arguments)
        {
            if (!string.Equals(tool, "CallTool", StringComparison.OrdinalIgnoreCase)) return ApprovalWrite(tool, arguments);
            var args = JsonNode.Parse(arguments)!.AsObject();
            return args["name"] is JsonValue name && name.TryGetValue<string>(out var target)
                && !string.Equals(target, "CallTool", StringComparison.OrdinalIgnoreCase)
                && AllToolMethods(includeUnavailable: true).ContainsKey(target)
                && ApprovalWrite(target, (args["arguments"] as JsonObject)?.ToJsonString() ?? "{}");
        }
        internal static async Task<ApprovalOutcome?> WaitForApproval(string tool, string arguments, CancellationToken token)
        {
            if (tool == "SaveExportContent" && JsonNode.Parse(arguments)?["outputPath"] is JsonValue output
                && output.TryGetValue<string>(out var path) && ApprovalSettings.IsAdministrativeTarget(path))
            {
                var rejected = PendingApproval.Create("engine", ReleaseKey, tool, arguments, null, 120, TiaOpenness.Shared.AuditInvocation.CurrentRequestId);
                TiaOpenness.Shared.AuditInvocation.RecordCurrentRequest(rejected.PlanHash);
                return ApprovalClient.RejectAdministrativeWrite(rejected);
            }
            if (!ApprovalWrite(tool, arguments) || tool == "ApplyToolBatch") return null;
            var args = JsonNode.Parse(arguments)!.AsObject();
            if (BehaviorCapabilities.EntryPolicy(typeof(McpServer).Assembly, ReleaseKey, tool, BehaviorPolicy.Current) == BehaviorPolicy.SafeV4
                && (string?)args["mode"] != "apply") return null;
            var settings = ApprovalSettings.Load(ApprovalSettings.SettingsPath);
            string? identity = null;
            ApprovalBindingIdentity(ref identity);
            var pending = PendingApproval.Create("engine", ReleaseKey, tool, arguments, identity, settings.TimeoutSeconds, TiaOpenness.Shared.AuditInvocation.CurrentRequestId);
            TiaOpenness.Shared.AuditInvocation.RecordCurrentRequest(pending.PlanHash);
            if (ApprovalWaitOverride.Value is { } overrideWait) return await overrideWait(pending, settings, token).ConfigureAwait(false);
            if (settings.Enabled) ApprovalWaitSignal("begin", settings.TimeoutSeconds);
            try { return await ApprovalClient.Wait(pending, settings, token).ConfigureAwait(false); }
            finally { if (settings.Enabled) ApprovalWaitSignal("end", settings.TimeoutSeconds); }
        }
        static partial void ApprovalWaitSignal(string phase, int seconds);
        static partial void ApprovalBindingIdentity(ref string? identity);
        internal static CallToolResult ApprovalRefusal(ApprovalOutcome approval)
            => FinishApproval(V4Reject(approval.Request.Tool, new Error(approval.Request.Tool == "SaveExportContent"
                ? "MCP cannot write Workbench approval settings." : "Workbench confirmation is required before this write.",
                new ConfirmationRequiredDetails(approval.Reason!, approval.Request.PlanHash, approval.Request.RequestId))), approval);
        internal static CallToolResult ChangedApprovalRefusal(ApprovalOutcome approval)
        {
            ApprovalClient.Complete(approval, "rejected-before-operation").GetAwaiter().GetResult();
            return ApprovalRefusal(new ApprovalOutcome(approval.Request, false, "denied"));
        }
        internal static CallToolResult FinishApproval(CallToolResult result, ApprovalOutcome? approval, bool disabled = false, string? completion = null)
        {
            if (approval == null && !disabled) return result;
            var body = ResultBody(result);
            if (body == null)
            {
                if (approval != null) ApprovalClient.Complete(approval, completion ?? "unknown").GetAwaiter().GetResult();
                return result;
            }
            body = ApprovalResult.Decorate(body, disabled || approval?.Disabled == true, approval?.Request.RequestId);
            if (approval != null) ApprovalClient.Complete(approval, completion ?? (string?)body["meta"]?["outcome"] ?? "unknown").GetAwaiter().GetResult();
            return new CallToolResult { IsError = result.IsError, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } };
        }
        internal static CallToolResult ApprovedBridgeCall(string name, string arguments, Func<CallToolResult> invoke)
        {
            if (!McpApprovalContext.Value) { TiaOpenness.Shared.AuditInvocation.StartCurrent(); return invoke(); } // Local user's CLI is outside MCP.
            if (ApprovalPreviewDepth.Value > 0) return FinishApproval(invoke(), null,
                ApprovalWrite(name, arguments) && !ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled);
            var approval = WaitForApproval(name, arguments, CancellationToken.None).GetAwaiter().GetResult();
            if (approval?.Reason != null) return ApprovalRefusal(approval);
            if (approval != null && !ApprovalStillMatches(approval, arguments)) return ChangedApprovalRefusal(approval);
            TiaOpenness.Shared.AuditInvocation.StartCurrent();
            try { return FinishApproval(invoke(), approval, ApprovalWrite(name, arguments) && !ApprovalSettings.Load(ApprovalSettings.SettingsPath).Enabled); }
            catch { if (approval != null) ApprovalClient.Complete(approval, "unknown").GetAwaiter().GetResult(); throw; }
        }
        internal static bool ApprovalStillMatches(ApprovalOutcome approval, string arguments)
        {
            if (approval.Disabled) return true;
            try
            {
                string? identity = null; ApprovalBindingIdentity(ref identity);
                return PendingApproval.Create("engine", ReleaseKey, approval.Request.Tool, arguments, identity,
                    approval.Request.TimeoutSeconds).ArgumentDigest == approval.Request.ArgumentDigest;
            }
            catch (Exception) /* swallow(fail-open-guard): an unreadable identity refuses the write before execution */ { return false; }
        }
        internal static bool EnterMcpApprovalContext() { bool previous = McpApprovalContext.Value; McpApprovalContext.Value = true; return previous; }
        internal static void LeaveMcpApprovalContext(bool previous) => McpApprovalContext.Value = previous;
    }
}
