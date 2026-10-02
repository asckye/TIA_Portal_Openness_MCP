using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.LegacyHost;
using TiaMcp.Versioning;

internal static class PassiveHostDiagnosticsTests
{
    private sealed class ForbiddenWorker : IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token) { Calls++; throw new Exception("SECRET_NATIVE_CALL"); }
        public void Dispose() { }
    }
    internal static async Task Run(IMcpServer server, Action<bool,string> check)
    {
        RequestContext<CallToolRequestParams> Request(string name, string json = "{}") => new(server) { Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(json) } };
        foreach (var release in TiaVersionCatalog.All)
        foreach (var native in new[] { false, true })
        {
            var worker = new ForbiddenWorker();
            var registry = LegacyHostToolRegistry.Create(worker, release.Key, native);
            foreach (var name in new[] { "Bootstrap", "RunCapabilitySelfTest" })
            {
                var tool = registry.Single(t => t.ProtocolTool.Name == name);
                var response = await tool.InvokeAsync(Request(name));
                var text = ((TextContentBlock)response.Content.Single()).Text;
                var result = JsonNode.Parse(text)!;
                check(response.IsError != true, "passive structural self-check succeeds");
                check(result["selectedRelease"]!["key"]!.GetValue<string>() == release.Key, "exact release identity");
                check(result["host"]!["nativeCallsDisabledByConfiguration"]!.GetValue<bool>() == !native, "configured native gate truthful");
                check(!result["upstreamResponseCompatible"]!.GetValue<bool>() && !result["nativeCertified"]!.GetValue<bool>(), "bounded candidate is not full/native compatibility");
                check(result["registeredToolCount"]!.GetValue<int>() == registry.Count && result["registeredTools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order().SequenceEqual(registry.Select(t => t.ProtocolTool.Name).Order()), "actual full registry roster");
                check(result["probes"]!.AsObject().All(p => p.Value!.GetValue<string>() == "not-probed"), "all native facts explicitly unprobed");
                check(result["sideEffects"]!.AsObject().All(p => !p.Value!.GetValue<bool>()), "no launch repair settings claims");
                check(text.Length < 32768 && !text.Contains("SECRET") && !text.Contains(Environment.CurrentDirectory), "bounded redacted result");
            }
            check(worker.Calls == 0, "native enabled or disabled diagnostics never call worker");
        }
        var contractRegistry = LegacyHostToolRegistry.Create(new ForbiddenWorker(), "17", false);
        var duplicate = contractRegistry.Concat(new[] { contractRegistry[0] }).ToArray();
        check(!LegacyHostPassiveDiagnostics.Inspect("17", false, duplicate)["checks"]!["uniqueRegisteredNames"]!.GetValue<bool>(), "duplicate registration is a failed check");
        check(!LegacyHostPassiveDiagnostics.Inspect("17", false, contractRegistry.Skip(1).ToArray())["checks"]!["foundationOperationsInSourceAllowlist"]!.GetValue<bool>(), "missing wired tool is a failed check");
        var schemaTool = contractRegistry[0].ProtocolTool;
        var savedSchema = schemaTool.InputSchema;
        schemaTool.InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = true });
        check(!LegacyHostPassiveDiagnostics.Inspect("17", false, contractRegistry)["checks"]!["objectSchemaContracts"]!.GetValue<bool>(), "permissive schema is a failed structural check");
        schemaTool.InputSchema = savedSchema;
        try { LegacyHostPassiveDiagnostics.Inspect("17", false, Enumerable.Repeat(contractRegistry[0],257).ToArray()); check(false,"oversized registry rejected"); }
        catch (InvalidOperationException) { check(true,"registry bounded before roster construction"); }
        int reads = 0;
        var guarded = LegacyHostPassiveDiagnosticTools.Create("17", true, () => { reads++; throw new Exception("SECRET_REGISTRY"); }).ToArray();
        foreach (var json in new[] { "{\"connectIfNeeded\":true}", "{\"includeProjectTree\":true}", "{\"inspectPortalProcesses\":true}", "{\"selfConnect\":true}", "{\"expectedPlcSoftwarePath\":\"SECRET_PATH\"}", "{\"expectedHmiSoftwarePath\":\"SECRET_PATH\"}", "{\"connectIfNeeded\":null}", "{\"connectIfNeeded\":\"false\"}", "{\"SECRET_UNKNOWN\":false}" })
        {
            try { await guarded[1].InvokeAsync(Request("RunCapabilitySelfTest", json)); check(false, "unsupported args must fail"); }
            catch (McpException ex) { check(ex.ErrorCode == McpErrorCode.InvalidParams && !ex.Message.Contains("SECRET"), "unsupported options rejected redacted"); }
        }
        try { await guarded[0].InvokeAsync(Request("Bootstrap", "{\"connectIfNeeded\":false}")); check(false,"bootstrap args must fail"); }
        catch (McpException ex) { check(ex.ErrorCode == McpErrorCode.InvalidParams,"bootstrap has no arguments"); }
        check(reads == 0,"unsupported flags rejected before even registry inspection");
        var normal = LegacyHostToolRegistry.Create(new ForbiddenWorker(), "17", false).Single(t => t.ProtocolTool.Name == "RunCapabilitySelfTest");
        check((await normal.InvokeAsync(Request("RunCapabilitySelfTest", "{\"connectIfNeeded\":false,\"includeProjectTree\":false,\"inspectPortalProcesses\":false}"))).IsError != true,"explicit safe defaults accepted");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await guarded[1].InvokeAsync(Request("RunCapabilitySelfTest"),cancelled.Token); check(false,"cancellation required"); }
        catch(OperationCanceledException) { check(reads == 0,"cancel before registry inspection"); }
    }
}
