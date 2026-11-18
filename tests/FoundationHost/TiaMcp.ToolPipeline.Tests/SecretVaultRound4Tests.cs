using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using TiaMcpServer.ModelContextProtocol;
using TiaOpenness.Shared;
using Xunit;

public sealed partial class PipelineTests
{
    [Theory]
    [InlineData("19", "GetPlcWebVars")] [InlineData("19", "GetPlcWebDiagnostics")]
    [InlineData("19", "WritePlcWebVars")] [InlineData("19", "SetPlcWebOperatingMode")]
    [InlineData("21", "GetPlcWebVars")] [InlineData("21", "GetPlcWebDiagnostics")]
    [InlineData("21", "WritePlcWebVars")] [InlineData("21", "SetPlcWebOperatingMode")]
    [InlineData("21", "ManageTeamcenterConnection")]
    public async Task Unicode_destinations_are_refused_before_approval_on_every_entry(string release, string name)
    {
        using var fixture = new Fixture(all: true, workerTool: name == "ManageTeamcenterConnection" ? name : null, runtimeRelease: release);
        using var vault = new VaultFixture();
        fixture.Wait = (_, _, _) => throw new InvalidOperationException("Unicode destinations must refuse before approval.");
        foreach (string host in new[] { "１２７.１", "１９２.１６８.０.１", "рlc.example.net", "plc．evil.net" })
        {
            var args = name == "ManageTeamcenterConnection"
                ? new JsonObject { ["action"] = "connect", ["userName"] = "fixture", ["password"] = "vault:plc1", ["instance"] = "TC",
                    ["hostUrl"] = "https://" + host + "/", ["dryRun"] = false }
                : WebArguments(name, host);
            if (name is "WritePlcWebVars" or "SetPlcWebOperatingMode") args["dryRun"] = false;
            foreach (string dispatcher in new[] { name, "CallTool", "PreviewToolCall", "PreviewToolBatch" })
            {
                var target = new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() };
                var envelope = dispatcher == name ? args : dispatcher == "PreviewToolBatch"
                    ? new JsonObject { ["operations"] = new JsonArray(target), ["expectedProject"] = "Fixture" } : target;
                var result = await fixture.Call(dispatcher, envelope.ToJsonString());
                Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
                Assert.EndsWith(name == "ManageTeamcenterConnection" ? "/hostUrl" : "/host",
                    (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
                Assert.Empty(fixture.Worker.Dispatches);
            }
        }
    }

    [Theory]
    [InlineData("tccs://TC/instance")] [InlineData("plc.example.net")]
    [InlineData("https://рlc.example.net/")]
    public async Task Teamcenter_without_a_vault_reference_preserves_legacy_host_forms(string host)
    {
        using var fixture = new Fixture(all: true, workerTool: "ManageTeamcenterConnection");
        fixture.Wait = (card, _, _) => {
            Assert.DoesNotContain("vaultDestination", card.ProjectIdentity);
            return Task.FromResult(new ApprovalOutcome(card, false, null));
        };
        var args = new JsonObject { ["action"] = "connect", ["userName"] = "fixture", ["password"] = "plain-fixture-password",
            ["instance"] = "TC", ["hostUrl"] = host, ["dryRun"] = false };
        foreach (string dispatcher in new[] { "ManageTeamcenterConnection", "CallTool" })
        {
            var result = await fixture.Call(dispatcher, dispatcher == "CallTool"
                ? new JsonObject { ["name"] = "ManageTeamcenterConnection", ["arguments"] = args.DeepClone() }.ToJsonString() : args.ToJsonString());
            Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        }
        Assert.Equal(2, fixture.Worker.Dispatches.Count(call => !call.Preview));
        Assert.All(fixture.Worker.Dispatches, call => Assert.Equal(host, (string?)call.Arguments["hostUrl"]));
    }

    [Theory]
    [InlineData("19", false)] [InlineData("19", true)] [InlineData("21", false)] [InlineData("21", true)]
    public async Task Registered_vault_tool_missing_from_approval_catalog_fails_closed(string release, bool restoreCatalog)
    {
        using var fixture = new Fixture(all: true, workerTool: "GetPlcBlockFingerprints", runtimeRelease: release);
        using var vault = new VaultFixture();
        var invoker = McpServer.ToolInvoker;
        var missing = new MissingVaultCatalog(fixture.Catalog, "GetPlcBlockFingerprints");
        McpServer.ConfigureToolBridge(missing, invoker, () => true, new HashSet<string>(missing.All.Keys));
        fixture.Wait = (_, _, _) => throw new InvalidOperationException("The missing descriptor cannot issue a vault approval.");
        var args = FingerprintArguments(); args["dryRun"] = false;
        var result = await fixture.Call("GetPlcBlockFingerprints", args.ToJsonString());
        Assert.Equal("vault-approval-required", (string?)result.StructuredContent?["error"]?["details"]?["condition"]);
        Assert.Empty(fixture.Worker.Dispatches);
        if (restoreCatalog)
        {
            McpServer.ConfigureToolBridge(fixture.Catalog, invoker, () => true, new HashSet<string>(fixture.Catalog.All.Keys));
            fixture.Wait = (card, _, _) => Task.FromResult(new ApprovalOutcome(card, false, null));
            var approved = await fixture.Call("GetPlcBlockFingerprints", args.ToJsonString());
            Assert.True((bool?)approved.StructuredContent?["ok"], approved.StructuredContent?.ToJsonString());
            Assert.Equal(VaultSecret, (string?)fixture.Worker.Dispatches.Last().Arguments["password"]);
        }
    }

    [Theory]
    [InlineData("GetPlcWebVars")] [InlineData("GetPlcWebDiagnostics")]
    [InlineData("WritePlcWebVars")] [InlineData("SetPlcWebOperatingMode")]
    [InlineData("ManageTeamcenterConnection")]
    public async Task Userinfo_destinations_are_refused_before_approval_on_every_entry(string name)
    {
        using var fixture = new Fixture(all: true, workerTool: name == "ManageTeamcenterConnection" ? name : null);
        using var vault = new VaultFixture();
        fixture.Wait = (_, _, _) => throw new InvalidOperationException("Ambiguous destinations must refuse before approval.");
        var args = name == "ManageTeamcenterConnection"
            ? new JsonObject { ["action"] = "connect", ["userName"] = "fixture", ["password"] = "vault:plc1", ["instance"] = "TC",
                ["hostUrl"] = "https://192.168.0.1@plc-diag.example.net", ["dryRun"] = false }
            : WebArguments(name, "192.168.0.1@plc-diag.example.net");
        if (name is "WritePlcWebVars" or "SetPlcWebOperatingMode") args["dryRun"] = false;
        foreach (string dispatcher in new[] { name, "CallTool", "PreviewToolCall", "PreviewToolBatch" })
        {
            var target = new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() };
            var envelope = dispatcher == name ? args : dispatcher == "PreviewToolBatch"
                ? new JsonObject { ["operations"] = new JsonArray(target), ["expectedProject"] = "Fixture" } : target;
            var result = await fixture.Call(dispatcher, envelope.ToJsonString());
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
            Assert.EndsWith(name == "ManageTeamcenterConnection" ? "/hostUrl" : "/host",
                (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
            Assert.Empty(fixture.Worker.Dispatches);
        }
    }

    [Theory]
    [InlineData("GetPlcWebVars")] [InlineData("GetPlcWebDiagnostics")]
    [InlineData("WritePlcWebVars")] [InlineData("SetPlcWebOperatingMode")]
    [InlineData("ManageTeamcenterConnection")]
    public async Task Vault_card_displays_the_parsed_effective_host_and_port(string name)
    {
        using var fixture = new Fixture(all: true, workerTool: name == "ManageTeamcenterConnection" ? name : null);
        using var vault = new VaultFixture();
        int approvals = 0;
        fixture.Wait = (card, _, _) => {
            approvals++;
            string destination = (string)JsonNode.Parse(card.ProjectIdentity)!["vaultDestination"]!;
            Assert.Contains("plc-diag.example.net:443", destination);
            Assert.DoesNotContain("PLC-DIAG", destination);
            Assert.DoesNotContain("/tc", destination);
            Assert.Contains("plc-diag.example.net:443", card.Operations.Single().Target);
            return Task.FromResult(new ApprovalOutcome(card, false, "denied"));
        };
        var args = name == "ManageTeamcenterConnection"
            ? new JsonObject { ["action"] = "connect", ["userName"] = "fixture", ["password"] = "vault:plc1", ["instance"] = "TC",
                ["hostUrl"] = "https://PLC-DIAG.example.net:443/tc", ["dryRun"] = false }
            : WebArguments(name, "PLC-DIAG.example.net:443");
        if (name == "WritePlcWebVars") { args["dryRun"] = false; args["confirmWrite"] = true; }
        if (name == "SetPlcWebOperatingMode") { args["dryRun"] = false; args["confirmModeChange"] = true; }
        var result = await fixture.Call("CallTool", new JsonObject { ["name"] = name, ["arguments"] = args }.ToJsonString());
        Assert.Equal(1, approvals);
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.All(fixture.Worker.Dispatches, call => Assert.Equal("vault:plc1", (string?)call.Arguments["password"]));
    }

    private sealed class MissingVaultCatalog : IToolCatalogView
    {
        public IReadOnlyDictionary<string, ToolDescriptor> All { get; }
        public IReadOnlyDictionary<string, ToolDescriptor> IncludingUnavailable => All;
        public IReadOnlyList<ToolDescriptor> Lite => All.Values.ToArray();
        public JsonArray BehaviorCapabilities { get; }
        public ToolDescriptor? Find(string name, bool includeUnavailable = false) => All.GetValueOrDefault(name);
        internal MissingVaultCatalog(IToolCatalogView source, string missing)
        {
            All = source.All.Where(pair => pair.Key != missing).ToDictionary(pair => pair.Key, pair => pair.Value);
            BehaviorCapabilities = source.BehaviorCapabilities;
        }
    }
}
