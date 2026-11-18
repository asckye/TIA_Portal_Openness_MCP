extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaOpenness.Shared;
using Xunit;

public sealed class SecretVaultFoundationBoundaryTests
{
    [Theory]
    [InlineData("19")] [InlineData("21")]
    public async Task Foundation_internal_precheck_receives_only_the_reference_then_approved_execution_receives_the_secret(string release)
    {
        using var vault = new VaultFileScope(); vault.Vault.Set("plc1", SecretVaultTests.Secret, vault.Audit);
        using var worker = new CompileWorker();
        var definition = FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == "CompilePlcSoftware"), release);
        int approvals = 0;
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), release, () => new(false, 1), (card, settings, _) => {
            approvals++; Assert.True(settings.Enabled); Assert.NotEmpty(worker.Dispatches);
            Assert.All(worker.Dispatches, call => { Assert.True((bool?)call["dryRun"]); Assert.Equal("vault:plc1", (string?)call["password"]); });
            Assert.Contains("PLC", card.Operations.Single().Target);
            return Task.FromResult(new ApprovalOutcome(card, false, null));
        });
        var request = Request("CompilePlcSoftware", new { softwarePath = "PLC", password = "vault:plc1", dryRun = false,
            confirm = true, expectedProjectFile = "C:/Fixture.ap19" });
        var result = await tool.InvokeAsync(request);
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.Equal(1, approvals); Assert.Equal(2, worker.Dispatches.Count);
        Assert.Equal(SecretVaultTests.Secret, (string?)worker.Dispatches.Last()["password"]);
        Assert.All(worker.Dispatches.SkipLast(1), call => Assert.Equal("vault:plc1", (string?)call["password"]));
        Assert.DoesNotContain(SecretVaultTests.Secret, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false, "adapter")] [InlineData(true, "adapter")]
    [InlineData(false, "worker")] [InlineData(true, "worker")]
    [InlineData(false, "mcp")] [InlineData(true, "mcp")]
    public async Task Foundation_and_CallTool_scrub_thrown_failures_with_the_resolution_scope_still_alive(bool bridge, string kind)
    {
        using var vault = new VaultFileScope(); vault.Vault.Set("plc1", SecretVaultTests.Secret, vault.Audit);
        using var targets = new OnlineTargetSettingsScope(null);
        using var worker = new OnlinePortAdmissionTests.VaultOnlineWorker { Failure = () => kind switch {
            "adapter" => new TiaMcp.Adapters.Contracts.AdapterPreconditionException("Rejected " + SecretVaultTests.Secret, "password"),
            "worker" => new WorkerOperationException("Rejected " + SecretVaultTests.Secret, -32602, "rejected-before-operation",
                JsonSerializer.Serialize(new { parameter = "password", nativeMessage = SecretVaultTests.Secret })),
            _ => new McpException("Failed " + SecretVaultTests.Secret) } };
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker,
            approvalWait: (card, _, _) => Task.FromResult(new ApprovalOutcome(card, false, null)));
        var args = new { softwarePath = "PLC_1", ipAddress = "192.0.2.10", password = "vault:plc1" };
        var result = await fixture.Call(bridge ? "CallTool" : "ConnectOnlinePlc", bridge ? new { name = "ConnectOnlinePlc", arguments = args } : args);
        Assert.True(result.IsError); Assert.Single(worker.Requests);
        Assert.Equal(SecretVaultTests.Secret, worker.Requests.Single().Password);
        Assert.DoesNotContain(SecretVaultTests.Secret, JsonSerializer.Serialize(result));
        Assert.NotNull(result.StructuredContent?["error"]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Foundation_UMC_approval_reads_the_project_server_without_a_password_and_refuses_an_unknown_destination(bool known)
    {
        using var vault = new VaultFileScope(); vault.Vault.Set("plc1", SecretVaultTests.Secret, vault.Audit);
        using var worker = new UmcWorker(known ? "umc.project.example" : null);
        var inner = PortedToolContract.Create(worker, "21").Single(tool => tool.ProtocolTool.Name == "ManageUmcUsers");
        int approvals = 0;
        var tool = new FoundationV4Tool(inner, "21", () => new(false, 1), (card, settings, _) => {
            approvals++; Assert.True(settings.Enabled); Assert.Contains("umc.project.example", card.Operations.Single().Target);
            Assert.All(worker.Credentials, value => Assert.Equal("vault:plc1", value));
            return Task.FromResult(new ApprovalOutcome(card, false, "denied"));
        });
        var result = await tool.InvokeAsync(Request("ManageUmcUsers", new { kind = "server", action = "read",
            serverPassword = "vault:plc1", dryRun = false }));
        Assert.Equal(known ? 1 : 0, approvals); Assert.True(worker.DestinationReads > 0);
        Assert.Equal(known ? "CONFIRMATION_REQUIRED" : "PRECONDITION_FAILED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.DoesNotContain(SecretVaultTests.Secret, JsonSerializer.Serialize(result));
        Assert.All(worker.Credentials, value => Assert.Equal("vault:plc1", value));
    }

    [Theory]
    [InlineData("{\"users\":[{\"password\":\"vault:plc1\"}]}", "/users/0/password")]
    [InlineData("{\"properties\":{\"password\":\"vault:plc1\"}}", "/properties/password")]
    public async Task Standalone_Foundation_refuses_nested_references_with_the_exact_pointer_before_other_admission(string json, string pointer)
    {
        using var worker = new CompileWorker();
        var definition = FoundationTools.Definitions.Single(d => d.Name == "CompilePlcSoftware");
        var tool = new FoundationV4Tool(new FoundationTool(definition, worker), "19", () => new(false, 1),
            (_, _, _) => throw new InvalidOperationException("Refused vault arguments cannot request approval."));
        var result = await tool.InvokeAsync(Request("CompilePlcSoftware", JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(pointer, (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
        Assert.Empty(worker.Dispatches);
    }

    [Theory]
    [InlineData("ManagePlcCertificate")] [InlineData("CallTool")]
    [InlineData("PreviewToolCall")] [InlineData("PreviewToolBatch")]
    public async Task Registered_Foundation_map_refusals_keep_the_leaf_pointer_through_each_entry(string dispatcher)
    {
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        var args = new JsonObject { ["devicePath"] = new JsonArray("Station"), ["itemPath"] = new JsonArray("CPU"),
            ["action"] = "read", ["properties"] = new JsonObject { ["password"] = "vault:plc1" } };
        var target = new JsonObject { ["name"] = "ManagePlcCertificate", ["arguments"] = args.DeepClone() };
        var envelope = dispatcher == "ManagePlcCertificate" ? args : dispatcher == "PreviewToolBatch"
            ? new JsonObject { ["operations"] = new JsonArray(target), ["expectedProject"] = "Fixture" } : target;
        var result = await fixture.Call(dispatcher, envelope);
        string prefix = dispatcher == "ManagePlcCertificate" ? "" : dispatcher == "PreviewToolBatch" ? "/operations/0/arguments" : "/arguments";
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(prefix + "/properties/password", (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private static RequestContext<CallToolRequestParams> Request(string name, object arguments)
        => new(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments)) } };

    private sealed class CompileWorker : IFoundationWorker
    {
        internal readonly List<JsonObject> Dispatches = new();
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject { ["ProjectFile"] = "C:/Fixture.ap19" });
            Assert.Equal("CompileSoftware", operation); Dispatches.Add(args.DeepClone().AsObject());
            bool preview = (bool?)args["dryRun"] == true;
            var result = new JsonObject { ["Executed"] = !preview, ["ProjectFile"] = "C:/Fixture.ap19", ["Messages"] = new JsonArray(),
                ["Errors"] = new JsonArray(), ["Warnings"] = new JsonArray(), ["Info"] = new JsonArray() };
            if (!preview) { result["State"] = "Success"; result["ErrorCount"] = 0; result["WarningCount"] = 0; }
            return Task.FromResult<JsonNode?>(result);
        }
        public void Dispose() { }
    }

    private sealed class UmcWorker(string? destination) : IFoundationWorker
    {
        internal int DestinationReads;
        internal readonly List<string> Credentials = new();
        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProjectFile"] = "C:/Fixture.ap21", ["IsAttached"] = true, ["ProcessId"] = 42 });
            Assert.Equal("project-security.ExecuteSecurityDeep", operation);
            var request = args["request"]!.Deserialize<TiaMcp.Adapters.Contracts.SecurityDeepRequest>()!;
            if (request.ReadVaultDestination)
            {
                DestinationReads++; Assert.Equal("", request.ServerPassword); Assert.Equal("server", request.Kind);
                Assert.Equal("read", request.Action); Assert.True(request.DryRun);
            }
            else { Assert.True(request.DryRun); Credentials.Add(request.ServerPassword); }
            return Task.FromResult<JsonNode?>(new JsonObject { ["Message"] = "Synthetic server read.", ["Meta"] = new JsonObject {
                ["vaultDestination"] = destination, ["apiCallSuccess"] = true, ["operationSuccess"] = true,
                ["dataComplete"] = true, ["dryRun"] = request.DryRun, ["mayHaveChanged"] = false } });
        }
        public void Dispose() { }
    }
}
