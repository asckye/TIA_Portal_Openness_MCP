extern alias foundationhost;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

public sealed partial class PipelineTests
{
    private const string VaultSecret = "vault:plaintext-that-looks-like-a-reference";
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory, InlineData("19", false), InlineData("19", true), InlineData("21", false), InlineData("21", true)]
    public async Task Vault_resolution_preserves_hidden_PLC_preset_provenance_through_direct_and_bridge_dispatch(string release, bool bridge)
    {
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints", runtimeRelease: release,
            presets: new Dictionary<string, string> { ["softwarePath"] = "PLC_1" });
        using var vault = new VaultFixture();
        var args = FingerprintArguments(); args.Remove("softwarePath"); args["dryRun"] = false;
        new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
        int approvals = 0;
        fixture.Wait = (card, settings, _) => {
            approvals++; Assert.True(settings.Enabled); Assert.Contains("PLC_1", card.ParametersJson);
            Assert.All(fixture.Worker.Dispatches, call => Assert.Equal("vault:plc1", (string?)call.Arguments["password"]));
            return Task.FromResult(new ApprovalOutcome(card, false, null));
        };
        fixture.Worker.Payload = VaultSecret;
        string name = bridge ? "CallTool" : "GetPlcBlockFingerprints";
        var input = bridge ? new JsonObject { ["name"] = "GetPlcBlockFingerprints", ["arguments"] = args } : args;
        var tool = fixture.Tool(name);
        var request = new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.ToJsonString()) } };
        var result = await CanonicalParameterTool.InvokeWithDefaults(tool, request, CancellationToken.None);
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.Equal(1, approvals);
        Assert.All(fixture.Worker.Dispatches, call => Assert.Equal("PLC_1", (string?)call.Arguments["softwarePath"]));
        Assert.Equal(VaultSecret, (string?)fixture.Worker.Dispatches.Last().Arguments["password"]);
        Assert.DoesNotContain(VaultSecret, JsonSerializer.Serialize(result, ReadableJson));
    }

    [Theory]
    [InlineData("19", false)] [InlineData("19", true)]
    [InlineData("21", false)] [InlineData("21", true)]
    public async Task Vault_dispatch_prechecks_keep_references_and_only_approved_execution_receives_plaintext(string release, bool bridge)
    {
        // The declared fingerprint input is shared by these releases; force the worker implementation to exercise the engine boundary.
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints", runtimeRelease: release);
        using var vault = new VaultFixture();
        var args = FingerprintArguments();
        args["dryRun"] = false;
        new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
        var cards = new List<PendingApproval>();
        fixture.Wait = (card, settings, _) => {
            Assert.True(settings.Enabled);
            Assert.All(fixture.Worker.Dispatches, call => Assert.Equal("vault:plc1", (string?)call.Arguments["password"]));
            Assert.Contains("192.0.2.1", card.Operations.Single().Target);
            Assert.Contains("vault:plc1", card.ParametersJson);
            Assert.DoesNotContain(VaultSecret, JsonSerializer.Serialize(card));
            cards.Add(card);
            return Task.FromResult(new ApprovalOutcome(card, false, null));
        };
        fixture.Worker.Payload = VaultSecret;
        var result = await fixture.Call(bridge ? "CallTool" : "GetPlcBlockFingerprints", bridge
            ? new JsonObject { ["name"] = "GetPlcBlockFingerprints", ["arguments"] = args }.ToJsonString() : args.ToJsonString());
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.Single(cards);
        Assert.True(fixture.Worker.Dispatches.Count >= 2);
        Assert.Equal(VaultSecret, (string?)fixture.Worker.Dispatches.Last().Arguments["password"]);
        Assert.All(fixture.Worker.Dispatches.SkipLast(1), call => {
            Assert.True(call.Preview || call.ReadOnlyScope);
            Assert.Equal("vault:plc1", (string?)call.Arguments["password"]);
        });
        Assert.DoesNotContain(VaultSecret, JsonSerializer.Serialize(result));
        Assert.Contains("••••", result.StructuredContent!.ToJsonString(ReadableJson));
    }

    [Theory]
    [InlineData("19", false)] [InlineData("19", true)] [InlineData("21", false)] [InlineData("21", true)]
    public async Task Vault_denial_prevents_engine_and_bridge_execution_even_when_general_approval_is_disabled(string release, bool bridge)
    {
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints", runtimeRelease: release);
        using var vault = new VaultFixture();
        new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
        int approvals = 0;
        fixture.Wait = (card, settings, _) => {
            Assert.True(settings.Enabled); approvals++;
            return Task.FromResult(new ApprovalOutcome(card, false, "denied"));
        };
        var args = FingerprintArguments(); args["dryRun"] = false;
        var result = await fixture.Call(bridge ? "CallTool" : "GetPlcBlockFingerprints", bridge
            ? new JsonObject { ["name"] = "GetPlcBlockFingerprints", ["arguments"] = args }.ToJsonString() : args.ToJsonString());
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(1, approvals);
        Assert.All(fixture.Worker.Dispatches, call => {
            Assert.True(call.Preview || call.ReadOnlyScope); Assert.Equal("vault:plc1", (string?)call.Arguments["password"]);
        });
    }

    [Theory]
    [InlineData("GetPlcWebVars", "direct")] [InlineData("GetPlcWebDiagnostics", "direct")]
    [InlineData("GetPlcWebVars", "CallTool")] [InlineData("GetPlcWebDiagnostics", "CallTool")]
    [InlineData("GetPlcWebVars", "RunReadOnlyToolBatch")] [InlineData("GetPlcWebDiagnostics", "RunReadOnlyToolBatch")]
    public async Task Vault_Web_reads_require_approval_and_display_the_host(string name, string dispatcher)
    {
        // ONLINE tools are currently excluded from BatchRead; this fixture admits one to exercise the item approval boundary.
        using var fixture = new Fixture(all: true, batchReadTool: dispatcher == "RunReadOnlyToolBatch" ? name : null);
        using var vault = new VaultFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        string host = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        new ApprovalSettings(false, 1).Save(ApprovalSettings.SettingsPath);
        int approvals = 0;
        fixture.Wait = (card, settings, _) => {
            Assert.Equal(name, card.Tool); Assert.True(settings.Enabled);
            Assert.Contains(host, card.Operations.Single().Target); approvals++;
            return Task.FromResult(new ApprovalOutcome(card, false, "denied"));
        };
        var args = WebArguments(name, host);
        var target = new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() };
        var envelope = dispatcher == "direct" ? args : dispatcher == "RunReadOnlyToolBatch"
            ? new JsonObject { ["operations"] = new JsonArray(target) } : target;
        var result = await fixture.Call(dispatcher == "direct" ? name : dispatcher, envelope.ToJsonString());
        Assert.Contains("CONFIRMATION_REQUIRED", result.StructuredContent!.ToJsonString());
        Assert.Equal(1, approvals); Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("GetPlcWebVars")] [InlineData("GetPlcWebDiagnostics")]
    [InlineData("WritePlcWebVars")] [InlineData("SetPlcWebOperatingMode")]
    public async Task Vault_Web_previews_do_not_open_a_connection_or_log_in(string name)
    {
        using var fixture = new Fixture(all: true);
        using var vault = new VaultFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var args = WebArguments(name, "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
        using var preview = McpServer.BeginReadOnlyApprovalPreview();
        var result = await McpServer.CallToolAsync(name, new ToolArguments(JsonSerializer.SerializeToElement(args)));
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.Contains("preview did not log in (vault credential)", result.StructuredContent!.ToJsonString());
        Assert.False((bool?)result.StructuredContent["data"]?["loggedIn"]);
        Assert.False(listener.Pending()); Assert.Empty(fixture.Worker.Dispatches);
    }

    public static IEnumerable<object[]> RefusedVaultDestinations()
    {
        foreach (string dispatcher in new[] { "CallTool", "PreviewToolCall", "PreviewToolBatch" })
        foreach (var (target, json, pointer) in new[] {
            ("ConnectOnlinePlc", "{\"users\":[{\"password\":\"vault:plc1\"}]}", "/users/0/password"),
            ("ManagePlcCertificate", "{\"properties\":{\"password\":\"vault:plc1\"}}", "/properties/password"),
            ("ManageSyslogServers", "{\"attributes\":{\"token\":\"vault:plc1\"}}", "/attributes/token"),
            ("ManageTeamcenterWorkflow", "{\"customAttributes\":{\"secret\":\"vault:plc1\"}}", "/customAttributes/secret"),
            ("ManageUnifiedListEntries", "{\"entry\":{\"authorization\":\"vault:plc1\"}}", "/entry/authorization"),
            ("ManageUnifiedListEntries", "{\"entryKey\":\"vault:plc1\"}", "/entryKey"),
            ("DescribeObject", "{\"objectPath\":[{\"key\":\"vault:plc1\"}]}", "/objectPath/0/key"),
            ("ApplyToolBatch", "{\"previewToken\":\"vault:plc1\"}", "/previewToken") })
            yield return new object[] { dispatcher, target, json, pointer };
    }

    [Theory, MemberData(nameof(RefusedVaultDestinations))]
    public async Task Dispatchers_refuse_noncredential_references_before_approval_with_the_pointer(string dispatcher, string name, string json, string pointer)
    {
        using var fixture = new Fixture(all: true);
        fixture.Wait = (_, _, _) => throw new InvalidOperationException("Refused arguments must never request approval.");
        var target = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(json) };
        var envelope = dispatcher == "PreviewToolBatch"
            ? new JsonObject { ["operations"] = new JsonArray(target), ["expectedProject"] = "Fixture" } : target;
        var result = await fixture.Call(dispatcher, envelope.ToJsonString());
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal((dispatcher == "PreviewToolBatch" ? "/operations/0/arguments" : "/arguments") + pointer,
            (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
        Assert.Empty(fixture.Worker.Dispatches);
    }

    [Theory]
    [InlineData("19")] [InlineData("21")]
    public async Task Preview_dispatchers_keep_valid_credential_references_and_never_request_approval(string release)
    {
        using var fixture = new Fixture(all: true, workerTool: "ManagePlcProtection", runtimeRelease: release);
        using var vault = new VaultFixture();
        BatchBinding(fixture, release);
        fixture.Wait = (_, _, _) => throw new InvalidOperationException("Previews must not request approval.");
        var args = new JsonObject { ["devicePath"] = new JsonArray("Station"), ["password"] = "vault:plc1", ["dryRun"] = true };
        var target = new JsonObject { ["name"] = "ManagePlcProtection", ["arguments"] = args };
        var preflight = await fixture.Call("PreviewToolCall", target.ToJsonString());
        Assert.True((bool?)preflight.StructuredContent?["ok"], preflight.StructuredContent?.ToJsonString());
        Assert.Empty(fixture.Worker.Dispatches);
        var preview = await fixture.Call("PreviewToolBatch", new JsonObject {
            ["operations"] = new JsonArray(target.DeepClone()), ["expectedProject"] = "Fixture" }.ToJsonString());
        Assert.True((bool?)preview.StructuredContent?["ok"], preview.StructuredContent?.ToJsonString());
        Assert.NotNull(preview.StructuredContent?["data"]?["previewToken"]);
        var dispatch = Assert.Single(fixture.Worker.Dispatches);
        Assert.True(dispatch.Preview || dispatch.ReadOnlyScope);
        Assert.Equal("vault:plc1", (string?)dispatch.Arguments["password"]);
        Assert.DoesNotContain(VaultSecret, JsonSerializer.Serialize(preview));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Admission_refuses_undecryptable_records_before_approval(bool bridge)
    {
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints");
        using var vault = new VaultFixture(damaged: true);
        fixture.Wait = (_, _, _) => throw new InvalidOperationException("Damaged records must refuse before approval.");
        var args = FingerprintArguments(); args["dryRun"] = false;
        var result = await fixture.Call(bridge ? "CallTool" : "GetPlcBlockFingerprints", bridge
            ? new JsonObject { ["name"] = "GetPlcBlockFingerprints", ["arguments"] = args }.ToJsonString() : args.ToJsonString());
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Contains("secret-vault-unreadable", result.StructuredContent!.ToJsonString());
        Assert.Empty(fixture.Worker.Dispatches);
    }

    [Theory]
    [InlineData(false, "adapter")] [InlineData(true, "adapter")]
    [InlineData(false, "mcp")] [InlineData(true, "mcp")]
    [InlineData(false, "worker")] [InlineData(true, "worker")]
    public async Task Vault_scope_scrubs_exceptions_through_engine_and_bridge_catches(bool bridge, string exception)
    {
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints");
        using var vault = new VaultFixture();
        fixture.Worker.ThrowAfterResolution = () => exception switch {
            "adapter" => new AdapterPreconditionException("Rejected " + VaultSecret, "password"),
            "worker" => new foundationhost::TiaMcp.FoundationHost.WorkerOperationException("Rejected " + VaultSecret, -32602,
                "rejected-before-operation"), _ => new McpException("Failed " + VaultSecret) };
        var args = FingerprintArguments(); args["dryRun"] = false;
        var result = await fixture.Call(bridge ? "CallTool" : "GetPlcBlockFingerprints", bridge
            ? new JsonObject { ["name"] = "GetPlcBlockFingerprints", ["arguments"] = args }.ToJsonString() : args.ToJsonString());
        Assert.True(result.IsError); Assert.NotEmpty(fixture.Worker.Dispatches);
        Assert.Equal(VaultSecret, (string?)fixture.Worker.Dispatches.Last().Arguments["password"]);
        Assert.DoesNotContain(VaultSecret, JsonSerializer.Serialize(result));
        Assert.NotNull(result.StructuredContent?["error"]);
    }

    [Theory]
    [InlineData("19", false)] [InlineData("19", true)] [InlineData("21", false)] [InlineData("21", true)]
    public async Task ApplyToolBatch_keeps_references_during_revalidation_and_resolves_each_item_only_after_approval(string release, bool approve)
    {
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints", runtimeRelease: release);
        using var vault = new VaultFixture();
        var binding = BatchBinding(fixture, release);
        var args = FingerprintArguments();
        CallToolResult preview;
        using (McpServer.BeginReadOnlyApprovalPreview())
            preview = await McpServer.CallToolAsync("GetPlcBlockFingerprints", new ToolArguments(JsonSerializer.SerializeToElement(args)));
        Assert.True((bool?)preview.StructuredContent?["ok"], preview.StructuredContent?.ToJsonString());
        string token = StoreVaultPlan("GetPlcBlockFingerprints", args, preview, binding);
        int approvals = 0;
        fixture.Wait = (card, _, _) => {
            approvals++; Assert.Equal(3, fixture.Worker.Dispatches.Count);
            Assert.All(fixture.Worker.Dispatches, call => {
                Assert.True(call.Preview || call.ReadOnlyScope); Assert.Equal("vault:plc1", (string?)call.Arguments["password"]);
            });
            return Task.FromResult(new ApprovalOutcome(card, false, approve ? null : "denied"));
        };
        var result = await McpServer.ApplyToolBatchAsync(token);
        Assert.Equal(1, approvals);
        Assert.Equal(approve ? 4 : 3, fixture.Worker.Dispatches.Count);
        if (approve)
        {
            Assert.Equal(VaultSecret, (string?)fixture.Worker.Dispatches.Last().Arguments["password"]);
            Assert.True((bool?)result.StructuredContent?["data"]?["items"]?[0]?["result"]?["ok"], result.StructuredContent?.ToJsonString());
        }
        else Assert.Contains("CONFIRMATION_REQUIRED", result.StructuredContent!.ToJsonString());
        Assert.DoesNotContain(VaultSecret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Web_write_precheck_and_batch_revalidation_skip_login_and_reach_the_approval_gate()
    {
        using var fixture = new Fixture(all: true);
        using var vault = new VaultFixture();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var binding = BatchBinding(fixture, "21");
        var args = WebArguments("SetPlcWebOperatingMode", "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
        args["confirmModeChange"] = true;
        CallToolResult preview;
        using (McpServer.BeginReadOnlyApprovalPreview())
            preview = await McpServer.CallToolAsync("SetPlcWebOperatingMode", new ToolArguments(JsonSerializer.SerializeToElement(args)));
        Assert.True((bool?)preview.StructuredContent?["ok"], preview.StructuredContent?.ToJsonString());
        string token = StoreVaultPlan("SetPlcWebOperatingMode", args, preview, binding);
        int approvals = 0;
        fixture.Wait = (card, _, _) => {
            approvals++; Assert.False(listener.Pending());
            return Task.FromResult(new ApprovalOutcome(card, false, "denied"));
        };
        var applied = await McpServer.ApplyToolBatchAsync(token);
        Assert.Contains("CONFIRMATION_REQUIRED", applied.StructuredContent!.ToJsonString());
        Assert.Equal(1, approvals); Assert.False(listener.Pending());
    }

    private static JsonObject BatchBinding(Fixture fixture, string release)
    {
        int major = TiaMcp.Versioning.TiaVersionCatalog.Get(release).MajorVersion;
        var binding = new JsonObject { ["identity"] = new JsonObject { ["tiaMajorVersion"] = major, ["processId"] = 123,
            ["processStartUtc"] = "2026-11-17T00:00:00Z", ["projectPath"] = "C:/Fixture.ap" + major,
            ["projectName"] = "Fixture", ["generation"] = 1 } };
        fixture.Worker.Binding = binding;
        fixture.Worker.BatchStatus = new JsonObject { ["session"] = new JsonObject { ["isConnected"] = true, ["project"] = "Fixture" },
            ["binding"] = binding.DeepClone(), ["readiness"] = new JsonObject { ["ready"] = true } };
        return binding;
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task UMC_approval_names_the_project_server_and_rechecks_it_before_releasing_the_password(bool bridge, bool changed)
    {
        using var fixture = new Fixture(all: true, workerTool: "ManageUmcUsers");
        using var vault = new VaultFixture();
        fixture.Worker.UmcDestination = "umc.project.example";
        int approvals = 0;
        fixture.Wait = (card, _, _) => {
            approvals++; Assert.Contains("umc.project.example", card.ProjectIdentity);
            Assert.Contains("umc.project.example", card.Operations.Single().Target);
            Assert.All(fixture.Worker.Dispatches, call => {
                Assert.True(call.Preview || call.ReadOnlyScope); Assert.Equal("vault:plc1", (string?)call.Arguments["serverPassword"]);
            });
            if (changed) fixture.Worker.UmcDestination = "changed.project.example";
            return Task.FromResult(new ApprovalOutcome(card, false, null));
        };
        var args = new JsonObject { ["kind"] = "server", ["action"] = "read", ["serverUserName"] = "fixture",
            ["serverPassword"] = "vault:plc1", ["dryRun"] = false };
        var result = await fixture.Call(bridge ? "CallTool" : "ManageUmcUsers", bridge
            ? new JsonObject { ["name"] = "ManageUmcUsers", ["arguments"] = args }.ToJsonString() : args.ToJsonString());
        Assert.Equal(1, approvals); Assert.NotEmpty(fixture.Worker.DestinationReads);
        Assert.All(fixture.Worker.DestinationReads, call => {
            var request = call["request"]!.Deserialize<SecurityDeepRequest>()!;
            Assert.Equal("server", request.Kind); Assert.Equal("read", request.Action);
            Assert.Equal("", request.ServerPassword); Assert.True(request.DryRun); Assert.True(request.ReadVaultDestination);
        });
        if (changed)
        {
            Assert.All(fixture.Worker.Dispatches, call => Assert.Equal("vault:plc1", (string?)call.Arguments["serverPassword"]));
            Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        }
        else Assert.Equal(VaultSecret, (string?)fixture.Worker.Dispatches.Last().Arguments["serverPassword"]);
    }

    [Fact]
    public async Task UMC_without_an_identified_project_server_refuses_before_approval()
    {
        using var fixture = new Fixture(all: true, workerTool: "ManageUmcUsers");
        using var vault = new VaultFixture();
        fixture.Wait = (_, _, _) => throw new InvalidOperationException("No server is known; no approval may be requested.");
        var result = await fixture.Call("ManageUmcUsers", "{\"serverPassword\":\"vault:plc1\",\"dryRun\":false}");
        Assert.Equal("vault-destination-unavailable", (string?)result.StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Empty(fixture.Worker.Dispatches); Assert.NotEmpty(fixture.Worker.DestinationReads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Nonfoundation_CallTool_scrubs_resolved_rebinding_errors_and_exceptions(bool throws)
    {
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints");
        using var vault = new VaultFixture();
        var invoker = new VaultBindingFailure(McpServer.ToolInvoker, throws);
        McpServer.ConfigureToolBridge(fixture.Catalog, invoker, () => true, new HashSet<string>(fixture.Catalog.All.Keys));
        var args = FingerprintArguments(); args["dryRun"] = false;
        var result = await fixture.Call("CallTool", new JsonObject { ["name"] = "GetPlcBlockFingerprints", ["arguments"] = args }.ToJsonString());
        Assert.True(result.IsError); Assert.Equal(1, invoker.ResolvedBinds);
        Assert.DoesNotContain(VaultSecret, JsonSerializer.Serialize(result));
        Assert.All(fixture.Worker.Dispatches, call => {
            Assert.True(call.Preview || call.ReadOnlyScope); Assert.Equal("vault:plc1", (string?)call.Arguments["password"]);
        });
    }

    private sealed class VaultBindingFailure(IToolInvoker inner, bool throws) : IToolInvoker
    {
        internal int ResolvedBinds;
        public Error? Bind(string name, ToolArguments args, out IBoundToolCall? call)
        {
            if (args.Json.TryGetProperty("password", out var password) && password.GetString() == VaultSecret)
            {
                ResolvedBinds++; call = null;
                if (throws) throw new AdapterPreconditionException("Binding rejected " + VaultSecret, "password");
                return new Error("Binding rejected " + VaultSecret, new InvalidArgumentDetails("password", Array.Empty<string>()));
            }
            return inner.Bind(name, args, out call);
        }
        public ToolInvocationResult Invoke(string name, ToolArguments arguments, bool preview) => inner.Invoke(name, arguments, preview);
        public Task<ToolInvocationResult> InvokeAsync(string name, ToolArguments arguments, bool preview) => inner.InvokeAsync(name, arguments, preview);
        public Error? ValidateArguments(ToolDescriptor tool, JsonElement arguments, JsonElement schema, bool typedFamiliesOnly = false)
            => inner.ValidateArguments(tool, arguments, schema, typedFamiliesOnly);
        public ModelContextProtocol.Server.McpServerTool CreateTool(ToolDescriptor tool) => inner.CreateTool(tool);
    }

    private static string StoreVaultPlan(string name, JsonObject args, CallToolResult preview, JsonObject binding)
        => EngineHostConfiguration.Current.BatchPlans.Add(new BatchPlanStore.Plan {
            Project = "Fixture", State = BatchPlanStore.BindingState(binding),
            Operations = new JsonArray(new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }),
            Previews = new JsonArray(preview.StructuredContent!.DeepClone()) }, DateTime.UtcNow);

    private static JsonObject FingerprintArguments() => new() { ["softwarePath"] = "PLC", ["targetIpAddress"] = "192.0.2.1",
        ["password"] = "vault:plc1", ["dryRun"] = true };

    private static JsonObject WebArguments(string name, string host)
    {
        var args = new JsonObject { ["host"] = host, ["username"] = "fixture", ["password"] = "vault:plc1", ["timeoutMs"] = 500 };
        if (name == "GetPlcWebVars") args["vars"] = new JsonArray("Tag");
        if (name == "WritePlcWebVars") args["writes"] = new JsonArray(new JsonObject { ["name"] = "Tag", ["value"] = 1 });
        if (name == "SetPlcWebOperatingMode") args["mode"] = "STOP";
        return args;
    }

    private sealed class VaultFixture : IDisposable
    {
        private readonly byte[]? previous;
        internal VaultFixture(bool damaged = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SecretVault.VaultPath)!);
            previous = File.Exists(SecretVault.VaultPath) ? File.ReadAllBytes(SecretVault.VaultPath) : null;
            File.WriteAllText(SecretVault.VaultPath, JsonSerializer.Serialize(new { version = 1,
                secrets = new Dictionary<string, string> {
                    ["plc1"] = damaged ? "AQID" : Convert.ToBase64String(CurrentUserProtection.Transform(System.Text.Encoding.UTF8.GetBytes(VaultSecret), true))
                } }));
        }
        public void Dispose()
        {
            if (previous == null) File.Delete(SecretVault.VaultPath); else File.WriteAllBytes(SecretVault.VaultPath, previous);
        }
    }
}
