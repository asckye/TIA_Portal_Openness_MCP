extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Logic.V4;
using TiaMcp.FoundationHost;
using TiaMcpServer.Runtime;
using TiaOpenness.Shared;
using Xunit;
using VaultArguments = toolpipeline::TiaMcpServer.ModelContextProtocol.SecretVaultArguments;

public sealed class SecretVaultRound4Tests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Vault_approval_preserves_the_configured_online_plan_lifetime(bool enabled)
    {
        var settings = new ApprovalSettings(enabled, 23, 137);
        var required = VaultArguments.ApprovalSettingsFor(true, settings);
        Assert.True(required.Enabled);
        Assert.Equal(23, required.TimeoutSeconds);
        Assert.Equal(137, required.OnlinePlanLifetimeSeconds);
        Assert.Same(settings, VaultArguments.ApprovalSettingsFor(false, settings));
    }

    [Theory]
    [InlineData("19", false)] [InlineData("19", true)] [InlineData("21", false)] [InlineData("21", true)]
    public async Task UMC_consistency_default_preview_skips_login_and_approved_execution_logs_in_once(string release, bool bridge)
    {
        using var vault = new VaultFileScope(); vault.Vault.Set("plc1", SecretVaultTests.Secret, vault.Audit);
        using var worker = new ConsistencyWorker();
        int approvals = 0;
        using var fixture = new VersionControlAdmissionTests.FileToolFixture(release, worker: worker, approvalWait: (card, _, _) => {
            approvals++; Assert.Empty(worker.Checks);
            Assert.Contains("umc.project.example", card.Operations.Single().Target);
            return Task.FromResult(new ApprovalOutcome(card, false, null));
        });
        var args = new JsonObject { ["kind"] = "server", ["action"] = "checkConsistency", ["serverUserName"] = "fixture",
            ["serverPassword"] = "vault:plc1" };
        Task<ModelContextProtocol.Protocol.CallToolResult> Call() => fixture.Call(bridge ? "CallTool" : "ManageUmcUsers",
            bridge ? new { name = "ManageUmcUsers", arguments = args } : (object)args);
        var preview = await Call();
        Assert.True((bool?)preview.StructuredContent?["ok"], preview.StructuredContent?.ToJsonString());
        Assert.Contains("preview did not log in (vault credential)", preview.StructuredContent!.ToJsonString());
        Assert.Empty(worker.Checks); Assert.Equal(0, approvals);
        args["dryRun"] = false;
        var execution = await Call();
        Assert.True((bool?)execution.StructuredContent?["ok"], execution.StructuredContent?.ToJsonString());
        Assert.Equal(1, approvals);
        var check = Assert.Single(worker.Checks);
        Assert.False(check.DryRun); Assert.Equal(SecretVaultTests.Secret, check.ServerPassword);
        Assert.DoesNotContain(SecretVaultTests.Secret, JsonSerializer.Serialize(execution));
    }

    [Theory]
    [InlineData("192.168.0.1@plc-diag.example.net")]
    [InlineData("127.1")] [InlineData("0x7f000001")] [InlineData("0177.0.0.1")]
    [InlineData("plc%2ediag.example.net")] [InlineData("plc:65536")] [InlineData("plc:\t443")]
    [InlineData("[::1]:8443")]
    [InlineData("１２７.１")] [InlineData("１９２.１６８.０.１")]
    [InlineData("рlc.example.net")] [InlineData("plc．evil.net")]
    public void Parsed_destination_changes_and_userinfo_are_refused(string host)
    {
        Assert.Throws<ArgumentException>(() => RuntimeChannelsLogic.NormalizeS7WebHost(host));
        Assert.Throws<ArgumentException>(() => RuntimeChannelsLogic.CheckedCredentialEndpoint("https://" + host + "/tc"));
    }

    [Theory]
    [InlineData("PLC.example.net", "plc.example.net:443")]
    [InlineData("plc.example.net:8443", "plc.example.net:8443")]
    [InlineData("192.168.0.1:443", "192.168.0.1:443")]
    public void Effective_destination_is_the_validated_Uri_host_and_port(string host, string expected)
    {
        var endpoint = RuntimeChannelsLogic.S7WebEndpoint(host);
        Assert.Equal(expected, RuntimeChannelsLogic.CredentialAuthority(endpoint));
        Assert.Equal(endpoint.Host + ":" + endpoint.Port, RuntimeChannelsLogic.CredentialAuthority(endpoint));
        Assert.Equal(endpoint.IdnHost + ":" + endpoint.Port, RuntimeChannelsLogic.CredentialAuthority(endpoint));
    }

    [Fact]
    public void Resolution_requires_a_matching_completed_approval_and_scope_disposal_revokes_it()
    {
        using var vault = new VaultFileScope(); vault.Vault.Set("plc1", SecretVaultTests.Secret, vault.Audit);
        var args = JsonSerializer.SerializeToElement(new { password = "vault:plc1", softwarePath = "PLC" });
        void Refused(JsonElement input)
        {
            var error = VaultArguments.Prepare("ConnectOnlinePlc", input, true, out var copy, out bool changed);
            Assert.Equal("vault-approval-required", Assert.IsType<PreconditionFailedDetails>(error!.Details).Condition);
            Assert.False(changed); Assert.Equal("vault:plc1", (string?)copy["password"]);
        }
        Refused(args);
        ExecutionContext? captured = null;
        Assert.Throws<InvalidOperationException>((Action)(() => {
            using var grant = SecretVaultTests.VaultApproval("ConnectOnlinePlc", args);
            captured = ExecutionContext.Capture();
            Assert.Null(VaultArguments.Prepare("ConnectOnlinePlc", args, true, out var copy, out bool changed));
            Assert.True(changed); Assert.Equal(SecretVaultTests.Secret, (string?)copy["password"]);
            Refused(JsonSerializer.SerializeToElement(new { password = "vault:plc1", softwarePath = "OTHER" }));
            throw new InvalidOperationException("Synthetic body failure.");
        }));
        Refused(args);
        Assert.NotNull(captured);
        ExecutionContext.Run(captured, _ => Refused(args), null);
        var pending = PendingApproval.Create("test", "21", "ConnectOnlinePlc", args.GetRawText(), "{}", 1);
        using (VaultArguments.Approved("ConnectOnlinePlc", args, new ApprovalOutcome(pending, true, null))) Refused(args);
    }

    [Theory]
    [InlineData("OpenProject", "path")] [InlineData("RetrieveProjectArchive", "archivePath")]
    public void UMAC_cards_name_the_target_project_and_require_its_UMC_server_for_Global_users(string tool, string field)
    {
        var args = new JsonObject { [field] = "C:/target/new.ap21", ["umacPassword"] = "vault:plc1", ["umacUserType"] = "Project" };
        string Destination(string? server = null) => (string)JsonNode.Parse(VaultArguments.ApprovalIdentity(tool,
            JsonSerializer.SerializeToElement(args), "{\"projectPath\":\"C:/old.ap21\"}", server)!)!["vaultDestination"]!;
        Assert.Contains("C:/target/new.ap21", Destination());
        Assert.DoesNotContain("old.ap21", Destination());
        args["umacUserType"] = "Global";
        Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() => Destination());
        string identity = VaultArguments.ApprovalIdentity(tool, JsonSerializer.SerializeToElement(args), "{}", "umc.target.example")!;
        var card = PendingApproval.Create("test", "21", tool, args.ToJsonString(), identity, 1);
        Assert.Contains("C:/target/new.ap21", card.Operations.Single().Target);
        Assert.Contains("umc.target.example", card.Operations.Single().Target);
    }

    [Fact]
    public void Secondary_UMAC_vault_card_names_the_reference_and_keeps_Global_destination_refusal()
    {
        var args = new JsonObject { ["path"] = "C:/Reference/Line1.ap21", ["openMode"] = "secondary",
            ["umacPassword"] = "vault:plc1", ["umacUserType"] = "Project" };
        var identity = VaultArguments.ApprovalIdentity("OpenProject", JsonSerializer.SerializeToElement(args),
            "{\"projectPath\":\"C:/Preset/Main.ap21\"}")!;
        var card = PendingApproval.Create("test", "21", "OpenProject", args.ToJsonString(), identity, 1);
        Assert.Contains("C:/Reference/Line1.ap21", card.Operations.Single().Target);
        Assert.DoesNotContain("C:/Preset/Main.ap21", card.Operations.Single().Target);
        args["umacUserType"] = "Global";
        Assert.Throws<TiaMcp.Adapters.Contracts.AdapterPreconditionException>(() =>
            VaultArguments.ApprovalIdentity("OpenProject", JsonSerializer.SerializeToElement(args), identity));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task UMAC_server_metadata_must_belong_to_the_target_project(bool matching)
    {
        string? result = await VaultArguments.ReadUmcDestinationAsync((operation, args, _) => {
            Assert.Equal("project-security.ExecuteSecurityDeep", operation);
            var request = args["request"]!.Deserialize<TiaMcp.Adapters.Contracts.SecurityDeepRequest>()!;
            Assert.True(request.ReadVaultDestination); Assert.True(request.DryRun); Assert.Empty(request.ServerPassword);
            return Task.FromResult<JsonNode?>(new JsonObject { ["Meta"] = new JsonObject {
                ["vaultDestination"] = "umc.target.example", ["vaultDestinationProject"] = matching ? "C:/target.ap21" : "C:/other.ap21" } });
        }, CancellationToken.None, "C:/target.ap21");
        Assert.Equal(matching ? "umc.target.example" : null, result);
    }

    private sealed class ConsistencyWorker : IFoundationWorker, toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        internal readonly List<TiaMcp.Adapters.Contracts.SecurityDeepRequest> Checks = new();
        public bool Faulted => false;
        public object SessionKey { get; } = new();
        public JsonNode? Binding => new JsonObject { ["identity"] = new JsonObject { ["projectPath"] = "C:/Fixture.ap21" } };
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<toolpipeline::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject args, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine dispatch expected.");
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProjectFile"] = "C:/Fixture.ap21", ["IsAttached"] = true, ["ProcessId"] = 42 });
            Assert.Equal("project-security.ExecuteSecurityDeep", operation);
            var request = args["request"]!.Deserialize<TiaMcp.Adapters.Contracts.SecurityDeepRequest>()!;
            if (request.ReadVaultDestination) { Assert.Empty(request.ServerPassword); Assert.True(request.DryRun); }
            else
            {
                Assert.Equal("checkConsistency", request.Action); Assert.False(request.DryRun);
                Assert.Equal(SecretVaultTests.Secret, request.ServerPassword); Checks.Add(request);
            }
            return Task.FromResult<JsonNode?>(new JsonObject { ["Message"] = "Synthetic consistency result.", ["Meta"] = new JsonObject {
                ["vaultDestination"] = "umc.project.example", ["vaultDestinationProject"] = "C:/Fixture.ap21",
                ["operationSuccess"] = true, ["apiCallSuccess"] = true, ["dataComplete"] = true, ["consistent"] = true,
                ["mayHaveChanged"] = false, ["mayHaveWrittenFiles"] = false } });
        }
        public void Dispose() { }
        private sealed class Lane : IDisposable { public void Dispose() { } }
    }
}
