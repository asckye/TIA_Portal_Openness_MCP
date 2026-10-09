using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
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
        var expectedPortedOperations = new Dictionary<string, string[]>(StringComparer.Ordinal) {
            ["GetDeviceAddressing"] = new[] { "hardware-addressing.ReadHardwareAddressing", "ReadState" },
            ["GetDeviceIpAddress"] = new[] { "hardware-addressing.ReadHardwareIpAddress", "ReadState" },
            ["GetDeviceItemIoAddresses"] = new[] { "hardware-addressing.ReadHardwareIoAddresses", "hardware-addressing.DescribeHardwareIoChildren", "ReadState" },
            ["SetDeviceAddress"] = new[] { "hardware-addressing.UpdateHardwareAddress", "ReadState" },
            ["SetDeviceItemIoAddress"] = new[] { "hardware-addressing.SetHardwareIoAddress", "hardware-addressing.ReadHardwareIoAddresses", "ReadState" }
        };
        foreach (var release in TiaVersionCatalog.All)
        foreach (var native in new[] { false, true })
        {
            var worker = new ForbiddenWorker();
            var registry = LegacyHostToolRegistry.Create(worker, release.Key, native);
            check(registry.Where(t => TiaMcp.Adapters.Contracts.PortedFamilies.Contains(t.ProtocolTool.Name)).Select(t => t.ProtocolTool.Name).Order().SequenceEqual(
                PortedToolContract.Families(release.Key).SelectMany(f => f.Tools).Order()),
                "actual registry adds exactly the reviewed ported families");
            foreach (var name in new[] { "InitializeEnvironment", "RunCapabilitySelfTest" })
            {
                var tool = registry.Single(t => t.ProtocolTool.Name == name);
                var response = await tool.InvokeAsync(Request(name));
                var text = ((TextContentBlock)response.Content.Single()).Text;
                var result = JsonNode.Parse(text)!["data"]!;
                check(response.IsError != true, "passive structural self-check succeeds");
                check(result["selectedRelease"]!["key"]!.GetValue<string>() == release.Key, "exact release identity");
                check(result["host"]!["nativeCallsDisabledByConfiguration"]!.GetValue<bool>() == !native, "configured native gate truthful");
                check(!result["upstreamResponseCompatible"]!.GetValue<bool>() && !result["nativeCertified"]!.GetValue<bool>(), "bounded candidate is not full/native compatibility");
                var roster = result["registeredTools"]!.AsArray();
                check(result["registeredToolCount"]!.GetValue<int>() == registry.Count && roster.Select(t => t!["name"]!.GetValue<string>()).Order().SequenceEqual(registry.Select(t => t.ProtocolTool.Name).Order()), "diagnostics report the complete actual registry");
                foreach (var pair in expectedPortedOperations)
                {
                    var row = roster.Single(t => (string?)t!["name"] == pair.Key)!;
                    check((string?)row["execution"] == "worker-protocol" && (string?)row["wiredOperation"] == pair.Value[0]
                        && row["wiredOperations"]!.AsArray().Select(operation => (string)operation!).SequenceEqual(pair.Value)
                        && row["objectSchemaContractValid"]!.GetValue<bool>(), "ported diagnostics disclose real module, preview and fallback wiring");
                }
                var family = result["behaviorCapabilities"]!.AsArray().SingleOrDefault(row => (string?)row!["family"] == "F19");
                check(release.Key is "20" or "21" ? family == null : family != null
                    && (string?)family["state"] == "current" && (string?)family["l5"] == "NOT RUN"
                    && family["entries"]!.AsArray().Select(entry => (string)entry!).Order().SequenceEqual(expectedPortedOperations.Keys.Order()),
                    "passive capabilities disclose the released ledger, including the old-release ported family without native acceptance claims");
                if (name == "InitializeEnvironment")
                {
                    check(result["ready"]!.GetValue<bool>() == (result["environment"]!["tiaInstallPath"] != null && result["environment"]!["opennessGroupOk"]!.GetValue<bool>()), "bootstrap readiness follows exact installation and group checks");
                    check(result["environment"]!["tiaVersionInUse"]!.GetValue<int>() == release.MajorVersion, "bootstrap retains selected release identity");
                    check(result["environment"]!["cause"] != null || result["ready"]!.GetValue<bool>(), "bootstrap readiness cause is present when not ready");
                    check(result["probes"]!["installedTia"]!.GetValue<string>() != "not-probed" && result["probes"]!["groupMembership"]!.GetValue<string>() != "not-probed", "bootstrap probes installation and group membership");
                }
                else check(result["probes"]!.AsObject().All(p => p.Value!.GetValue<string>() == "not-probed"), "self-test leaves native facts unprobed");
                check(result["sideEffects"]!.AsObject().All(p => !p.Value!.GetValue<bool>()), "no launch repair settings claims");
                check(text.Length < 32768 && !text.Contains("SECRET") && !text.Contains(Environment.CurrentDirectory), "bounded redacted result");
            }
            check(worker.Calls == 0, "native enabled or disabled diagnostics never call worker");
        }
        var missingInstall = FoundationPassiveDiagnostics.Readiness("14sp1", _ => null, () => true);
        check(!missingInstall["ready"]!.GetValue<bool>() && missingInstall["environment"]!["tiaVersionDetected"] == null
            && ((string)missingInstall["recommendedReason"]!).Contains("V14 SP1", StringComparison.Ordinal), "readiness is bound to the exact selected installation release");
        check(((string)missingInstall["recommendedFixZh"]!).Contains("请", StringComparison.Ordinal)
            && (string?)missingInstall["environment"]!["recommendedFixZh"] == (string?)missingInstall["recommendedFixZh"],
            "Foundation missing-install readiness carries Chinese guidance at both levels");
        var missingGroup = FoundationPassiveDiagnostics.Readiness("19", _ => @"C:\TIA\Portal V19", () => false);
        check(!missingGroup["ready"]!.GetValue<bool>() && missingGroup["environment"]!["opennessGroupOk"]!.GetValue<bool>() == false
            && ((string)missingGroup["recommendedFix"]!).Contains("Siemens TIA Openness", StringComparison.Ordinal), "readiness reports group-membership fix");
        check(((string)missingGroup["recommendedFixZh"]!).Contains("请", StringComparison.Ordinal)
            && ((string)missingGroup["recommendedFixZh"]!) != (string)missingGroup["recommendedFix"]!,
            "Foundation missing-group readiness keeps Chinese guidance");
        var apiFixtureRoot = Path.Combine(Path.GetTempPath(), "foundation-api-readiness-" + Guid.NewGuid().ToString("N"));
        string api19 = Path.Combine(apiFixtureRoot, "TIA_V19_PublicAPI", "V19");
        string api20 = Path.Combine(apiFixtureRoot, "TIA_V20_PublicAPI", "V20");
        Directory.CreateDirectory(api19); Directory.CreateDirectory(api20);
        File.WriteAllText(Path.Combine(api19, "Siemens.Engineering.dll"), "V19 API fixture");
        File.WriteAllText(Path.Combine(api20, "Siemens.Engineering.dll"), "V20 API fixture");
        try
        {
            int fallbackCalls = 0;
            var hostOptionReady = FoundationPassiveDiagnostics.Readiness("19", api19, "host-option", _ => {
                fallbackCalls++;
                return new FoundationPassiveDiagnostics.InstallationLocation(null, null);
            }, () => true);
            check(hostOptionReady["ready"]!.GetValue<bool>() && (string?)hostOptionReady["environment"]!["installSource"] == "host-option"
                && fallbackCalls == 0, "resolved host API directory admits the exact release before registry fallback");

            var noOption = FoundationPassiveDiagnostics.Readiness("19", null, null, _ => {
                fallbackCalls++;
                return new FoundationPassiveDiagnostics.InstallationLocation(null, null);
            }, () => true);
            check(!noOption["ready"]!.GetValue<bool>() && ((string?)noOption["cause"])?.Contains("TIA Portal V19", StringComparison.Ordinal) == true,
                "no host API option and no registry/default installation reports the exact-release cause");

            int beforeWrongRelease = fallbackCalls;
            var wrongRelease = FoundationPassiveDiagnostics.Readiness("19", api20, "host-option", _ => {
                fallbackCalls++;
                return new FoundationPassiveDiagnostics.InstallationLocation(api19, "registry");
            }, () => true);
            check(!wrongRelease["ready"]!.GetValue<bool>() && (string?)wrongRelease["environment"]!["installSource"] == "host-option"
                && fallbackCalls == beforeWrongRelease, "wrong-release host API directory refuses readiness without registry fallback");

            var environmentSource = FoundationPassiveDiagnostics.Readiness("19", api19, "environment", _ =>
                new FoundationPassiveDiagnostics.InstallationLocation(null, null), () => true);
            check(environmentSource["ready"]!.GetValue<bool>() && (string?)environmentSource["environment"]!["installSource"] == "environment",
                "readiness reports environment as the resolved API directory source");

            var registrySource = FoundationPassiveDiagnostics.Readiness("19", null, null, _ =>
                new FoundationPassiveDiagnostics.InstallationLocation(api19, "registry"), () => true);
            check(registrySource["ready"]!.GetValue<bool>() && (string?)registrySource["environment"]!["installSource"] == "registry",
                "readiness reports registry as the fallback source");
        }
        finally { Directory.Delete(apiFixtureRoot, recursive: true); }
        var contractRegistry = LegacyHostToolRegistry.Create(new ForbiddenWorker(), "17", false);
        var duplicate = contractRegistry.Concat(new[] { contractRegistry[0] }).ToArray();
        check(!FoundationPassiveDiagnostics.Inspect("17", false, duplicate)["checks"]!["uniqueRegisteredNames"]!.GetValue<bool>(), "duplicate registration is a failed check");
        check(!FoundationPassiveDiagnostics.Inspect("17", false, contractRegistry.Skip(1).ToArray())["checks"]!["foundationOperationsInSourceAllowlist"]!.GetValue<bool>(), "missing wired tool is a failed check");
        foreach (var name in expectedPortedOperations.Keys)
            check(!FoundationPassiveDiagnostics.Inspect("17", false, contractRegistry.Where(tool => tool.ProtocolTool.Name != name).ToArray())["checks"]!["foundationOperationsInSourceAllowlist"]!.GetValue<bool>(), "missing ported tool fails the structural check");
        var sourceOperations = TiaMcp.PlcWorker.WorkerOperations.Names.Concat(expectedPortedOperations.Values.SelectMany(operations => operations)).ToHashSet(StringComparer.Ordinal);
        foreach (var operation in sourceOperations.Where(operation => operation.StartsWith("hardware-addressing.", StringComparison.Ordinal)).ToArray())
        {
            var missingOperation = sourceOperations.Where(name => name != operation).ToHashSet(StringComparer.Ordinal);
            check(!FoundationPassiveDiagnostics.Inspect("17", false, contractRegistry, missingOperation)["checks"]!["foundationOperationsInSourceAllowlist"]!.GetValue<bool>(), "missing primary, preview or fallback module operation fails the structural check");
        }
        var schemaTool = contractRegistry[0].ProtocolTool;
        var savedSchema = schemaTool.InputSchema;
        schemaTool.InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = true });
        check(!FoundationPassiveDiagnostics.Inspect("17", false, contractRegistry)["checks"]!["objectSchemaContracts"]!.GetValue<bool>(), "permissive schema is a failed structural check");
        schemaTool.InputSchema = savedSchema;
        try { FoundationPassiveDiagnostics.Inspect("17", false, Enumerable.Repeat(contractRegistry[0],257).ToArray()); check(false,"oversized registry rejected"); }
        catch (InvalidOperationException) { check(true,"registry bounded before roster construction"); }
        int reads = 0;
        var guarded = FoundationPassiveDiagnosticTools.Create("17", true, () => { reads++; throw new Exception("SECRET_REGISTRY"); }).ToArray();
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
