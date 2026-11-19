extern alias toolpipeline;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.WorkerChannel;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Engine = toolpipeline::TiaMcp.FoundationHost;
using Service = toolpipeline::TiaMcpServer.Siemens.Services.OnlineDownloadService;

public sealed class OnlinePortAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Foundation_vault_credentials_resolve_once_before_the_ported_body(bool bridge)
    {
        using var vault = new VaultFileScope();
        const string password = "vault:literal-password";
        vault.Vault.Set("plc1", password, vault.Audit);
        using var targets = new OnlineTargetSettingsScope(null);
        using var worker = new VaultOnlineWorker();
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker,
            approvalWait: (card, settings, _) => {
                Assert.True(settings.Enabled); Assert.Contains("192.0.2.10", card.Operations.Single().Target);
                return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(card, false, null)); });
        var arguments = new { softwarePath = "PLC_1", ipAddress = "192.0.2.10", password = "vault:plc1" };
        var result = await fixture.Call(bridge ? "CallTool" : "ConnectOnlinePlc",
            bridge ? new { name = "ConnectOnlinePlc", arguments } : (object)arguments);
        Assert.Equal(password, worker.Password);
        Assert.True((bool?)result.StructuredContent!["ok"], result.StructuredContent.ToJsonString());
        Assert.DoesNotContain(password, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Vault_credentials_reach_the_online_worker_and_native_echoes_are_scrubbed(bool bridge, bool fail)
    {
        using var vault = new VaultFileScope();
        vault.Vault.Set("plc1", SecretVaultTests.Secret, vault.Audit);
        using var targets = new OnlineTargetSettingsScope(null);
        using var worker = new VaultOnlineWorker(fail);
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21", worker: worker,
            approvalWait: (card, _, _) => Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(card, false, null)));
        var arguments = new { softwarePath = "PLC_1", ipAddress = "192.0.2.10", password = "vault:plc1" };
        var result = await fixture.Call(bridge ? "CallTool" : "ConnectOnlinePlc",
            bridge ? new { name = "ConnectOnlinePlc", arguments } : (object)arguments);
        Assert.Equal(SecretVaultTests.Secret, worker.Password);
        Assert.Equal(2, worker.Calls);
        string response = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(SecretVaultTests.Secret, response);
        if (fail)
        {
            Assert.True(result.IsError);
            Assert.Contains("••••", result.StructuredContent!.ToJsonString(new JsonSerializerOptions {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        else Assert.True((bool?)result.StructuredContent!["ok"]);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Missing_or_unreadable_vault_references_refuse_before_approval_and_worker(bool bridge, bool unreadable)
    {
        using var vault = new VaultFileScope();
        if (unreadable) File.WriteAllText(TiaOpenness.Shared.SecretVault.VaultPath, "invalid-vault-record");
        using var targets = new OnlineTargetSettingsScope(null);
        using var fixture = new VersionControlAdmissionTests.FileToolFixture("21");
        int approvals = 0;
        var previous = Host.McpServer.ApprovalWaitOverrideForTests;
        Host.McpServer.ApprovalWaitOverrideForTests = (card, _, _) =>
        { approvals++; return Task.FromResult(new TiaOpenness.Shared.ApprovalOutcome(card, false, "denied")); };
        try
        {
            var arguments = new { softwarePath = "PLC_1", ipAddress = "192.0.2.10", password = "vault:plc1" };
            var result = await fixture.Call(bridge ? "CallTool" : "ConnectOnlinePlc",
                bridge ? new { name = "ConnectOnlinePlc", arguments } : (object)arguments);
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]?["code"]);
            Assert.EndsWith("/password", (string?)result.StructuredContent["error"]?["details"]?["parameter"]);
            Assert.Contains("vault:plc1", (string?)result.StructuredContent["error"]?["message"]);
            if (unreadable) Assert.Contains("secret-vault-unreadable", result.StructuredContent.ToJsonString());
            Assert.Equal(0, fixture.Worker.Calls);
            Assert.Equal(0, approvals);
        }
        finally { Host.McpServer.ApprovalWaitOverrideForTests = previous; }
    }

    internal sealed class VaultOnlineWorker(bool fail = false, string? diagnostic = null) : IFoundationWorker, Engine.IEngineWorker
    {
        internal int Calls;
        internal string? Password;
        internal readonly List<OnlineRequest> Requests = new();
        internal Func<Exception>? Failure;
        public bool Faulted => false;
        public object SessionKey { get; } = new();
        public JsonNode? Binding => new JsonObject { ["identity"] = new JsonObject { ["projectPath"] = "C:/Fixture/P.ap21" } };
        public JsonObject Snapshot() => new() { ["readiness"] = new JsonObject { ["ready"] = true } };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("No restart expected.");
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lane());
        public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("No engine dispatch expected.");
        private sealed class Lane : IDisposable { public void Dispose() { } }
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProjectFile"] = "C:/Fixture/P.ap21", ["ProcessId"] = 42, ["IsAttached"] = true });
            Assert.Equal("plc-online.ExecuteOnline", operation);
            var request = arguments["request"]!.Deserialize<OnlineRequest>()!;
            Password = request.Password;
            Requests.Add(request);
            if (Failure != null) throw Failure();
            var result = new JsonObject { ["MayHaveChanged"] = !fail, ["OutcomeUnknown"] = false,
                ["Value"] = new JsonObject { ["State"] = fail ? "Protected" : request.Operation == "DownloadPlc" ? "Success" : "Online",
                    ["IsOnline"] = !fail, ["Ok"] = !fail,
                    ["Message"] = fail ? "Synthetic authentication failed: " + Password : "Connected.",
                    ["Meta"] = new JsonObject { ["success"] = !fail, ["mayHaveChanged"] = !fail, ["writeOutcomeKnown"] = true,
                        ["error"] = fail ? "Synthetic authentication failed: " + Password : null } } };
            if (diagnostic != null) result["Value"]!["Meta"]!["diagnostic"] = diagnostic + Password;
            return Task.FromResult<JsonNode?>(result);
        }
        public void Dispose() { }
    }

    [Fact]
    public void Snapshot_description_distinguishes_release_availability_of_both_services()
    {
        var method = typeof(Host.OnlineDataTools).GetMethod("ManagePlcDataBlockSnapshotV4")!;
        var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
        Assert.Contains("read and exportSnapshot use InterfaceSnapshot (V15.1 and later)", description);
        Assert.Contains("createSnapshot and the two load actions use ValueService, which exists only in V21", description);
        Assert.Contains("refused on V15.1–V20 before TIA is called", description);
        Assert.DoesNotContain("V20 returns NotSupported", description);
    }

    public static IEnumerable<object[]> ValueServiceRefusalCases =>
        from release in new[] { "15.1", "16", "17", "18", "19", "20" }
        from action in new[] { "createSnapshot", "loadSnapshotAsActualValues", "loadStartValuesAsActualValues" }
        select new object[] { release, action };

    [Theory, MemberData(nameof(ValueServiceRefusalCases))]
    public void Pre_V21_ValueService_actions_refuse_before_worker_dispatch(string release, string action)
    {
        string previous = Engine.EngineHostConfiguration.ReleaseKey;
        try
        {
            Engine.EngineHostConfiguration.ReleaseKey = release;
            int calls = 0;
            var service = new Service((_, _) => { calls++; throw new InvalidOperationException("Unexpected dispatch"); }, () => "fixture.ap" + release);
            Assert.Throws<NotSupportedException>(() => service.ManagePlcDataBlockSnapshot("PLC_1", "DB1", action));
            Assert.Equal(0, calls);
        }
        finally { Engine.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Theory]
    [InlineData("20", "ManagePlcDataBlockSnapshot", "{\"softwarePath\":\"PLC_1\",\"blockPath\":\"DB1\",\"action\":\"createSnapshot\"}")]
    [InlineData("16", "ConnectOnlinePlc", "{\"softwarePath\":\"PLC_1\",\"userName\":\"fixture\"}")]
    [InlineData("14sp1", "ConnectOnlinePlc", "{\"softwarePath\":\"PLC_1\",\"ipAddress\":\"127.0.0.1\"}")]
    [InlineData("18", "GeneratePlcLoadableFile", "{\"softwarePath\":\"PLC_1\",\"objectPaths\":[\"Unit1\"],\"objectKind\":\"units\",\"targetOption\":\"Plc\",\"filePath\":\"C:/fixture/unit.loadable\"}")]
    public async Task Missing_online_actions_are_refused_before_even_managed_worker_state(string release, string name, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var session = new HardwareAddressingAdmissionTests.SessionFixture(worker, release);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        if (release == "14sp1" && name == "ConnectOnlinePlc")
        {
            Assert.DoesNotContain(tools, tool => tool.ProtocolTool.Name == "ConnectOnlinePlc");
            Assert.Contains(tools, tool => tool.ProtocolTool.Name == "GetOnlineState");
            Assert.Equal(0, worker.Calls);
            return;
        }
        var tool = tools.Single(t => t.ProtocolTool.Name == name);
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(
            DispatchProxy.Create<IMcpServer, VersionControlAdmissionTests.ServerProxy>()) {
                Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) } });
        if (release == "18" && name == "GeneratePlcLoadableFile")
        {
            Assert.Equal("PRECONDITION_FAILED", (string?)result.StructuredContent!["error"]?["code"]);
            Assert.Equal(1, worker.Calls); // Admitted API; managed ReadState finds no project.
            Assert.NotEqual("unknown", (string?)result.StructuredContent["meta"]!["outcome"]);
            return;
        }
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent!["error"]?["code"]);
        Assert.NotEqual("unknown", (string?)result.StructuredContent["meta"]!["outcome"]);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public void V20_parameter_upload_refuses_before_worker_dispatch()
    {
        string previous = Engine.EngineHostConfiguration.ReleaseKey;
        try
        {
            Engine.EngineHostConfiguration.ReleaseKey = "20";
            int calls = 0;
            var service = new Service((_, _) => { calls++; throw new InvalidOperationException("Unexpected dispatch"); }, () => "fixture.ap20");
            Assert.Throws<NotSupportedException>(() => service.UploadDeviceParameters("[]", "[]", "127.0.0.1"));
            Assert.Equal(0, calls);
        }
        finally { Engine.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Theory]
    [InlineData("SafetyProgram")]
    [InlineData("differentTargetConfiguration")]
    [InlineData("ExpandDownload")]
    [InlineData("LoadIdentificationData")]
    public void Opt_in_prompts_cannot_be_answered_through_promptAnswers(string prompt)
    {
        string previous = Engine.EngineHostConfiguration.ReleaseKey;
        try
        {
            Engine.EngineHostConfiguration.ReleaseKey = "21";
            int calls = 0;
            var service = new Service((_, _) => { calls++; throw new InvalidOperationException("Unexpected dispatch"); }, () => "fixture.ap21");
            var error = Assert.Throws<ArgumentException>(() => service.DownloadToPlc("PLC_1", promptAnswersJson: "{\"" + prompt + "\":\"AcceptAll\"}"));
            Assert.Contains("=true", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, calls);
        }
        finally { Engine.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Theory]
    [InlineData("createSnapshot")]
    [InlineData("loadSnapshotAsActualValues")]
    [InlineData("loadStartValuesAsActualValues")]
    public void V20_ValueService_actions_refuse_before_worker_dispatch(string action)
    {
        string previous = Engine.EngineHostConfiguration.ReleaseKey;
        try
        {
            Engine.EngineHostConfiguration.ReleaseKey = "20";
            int calls = 0;
            var service = new Service((_, _) => { calls++; throw new InvalidOperationException("Unexpected dispatch"); }, () => "fixture.ap20");
            Assert.Throws<NotSupportedException>(() => service.ManagePlcDataBlockSnapshot("PLC_1", "DB1", action));
            Assert.Equal(0, calls);
        }
        finally { Engine.EngineHostConfiguration.ReleaseKey = previous; }
    }

    [Theory]
    [InlineData("DownloadPlc")]
    [InlineData("UploadStationFromPlc")]
    [InlineData("UploadDeviceParameters")]
    [InlineData("GeneratePlcLoadableFile")]
    public void Online_transfer_operations_use_the_long_operation_budget(string tool)
    {
        var request = new JsonObject { ["Operation"] = tool, ["DryRun"] = false };
        var arguments = new JsonObject { ["request"] = request, ["dryRun"] = false };
        var policy = new WorkerTimeoutPolicy();
        Assert.Equal(WorkerTimeoutPolicy.Defaults["transfer"], policy.ForPlc("plc-online.ExecuteOnline", arguments));
        arguments["dryRun"] = true;
        Assert.Equal(WorkerTimeoutPolicy.Defaults["default"], policy.ForPlc("plc-online.ExecuteOnline", arguments));
    }
}
