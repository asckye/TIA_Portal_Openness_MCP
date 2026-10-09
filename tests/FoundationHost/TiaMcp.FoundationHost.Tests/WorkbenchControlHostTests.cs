extern alias enginehost;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4;
using TiaOpenness.Shared;
using Xunit;
using Taxonomy = TiaMcpServer.ModelContextProtocol.ToolTaxonomy;
using Descriptor = enginehost::TiaMcpServer.ModelContextProtocol.ToolDescriptor;

public sealed class WindowsWorkbenchTheoryAttribute : TheoryAttribute
{
    public WindowsWorkbenchTheoryAttribute()
    { if (!OperatingSystem.IsWindows()) Skip = "Named pipe SID/ACL/image checks require Windows."; }
}

public sealed class WorkbenchControlHostTests
{
    private sealed class Worker : IFoundationSessionWorker, enginehost::TiaMcp.FoundationHost.IEngineWorker
    {
        internal string Project = "C:/Projects/Fixture.ap21";
        internal bool EngineIdentity;
        public string ApprovalIdentity => EngineIdentity ? JsonSerializer.Serialize(new { identity = new { projectPath = Project } })
            : JsonSerializer.Serialize(new { ProjectFile = Project });
        public bool Poisoned => true;
        public bool Faulted => false;
        public JsonNode? Binding => JsonNode.Parse(ApprovalIdentity);
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => throw new Exception("Worker snapshot is forbidden.");
        public Task<JsonObject> Status(CancellationToken token) => throw new Exception("Worker status is forbidden.");
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new Exception("Worker restart is forbidden.");
        public Task<IDisposable> Acquire(CancellationToken token) => throw new Exception("Worker lane is forbidden.");
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token) => throw new Exception("Engine dispatch is forbidden.");
        public bool Bundled => true;
        public bool SharedSession => false;
        public TiaMcp.Logic.ModelContextProtocol.ImportStagingSession? StagingOwner => null;
        public IDisposable? EnterRequest(RequestContext<CallToolRequestParams> request) => throw new Exception("Worker request is forbidden.");
        public Task<IDisposable?> AcquireLane(CancellationToken token) => throw new Exception("Lane is forbidden.");
        public void ActivateLane(IDisposable? lane) => throw new Exception("Lane is forbidden.");
        public void MarkUncertain() => throw new Exception("Session lock is forbidden.");
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token) => throw new Exception("Native dispatch is forbidden.");
        public void Dispose() { }
    }
    internal static JsonObject Arguments(int operation) => operation switch
    {
        0 => new() { ["page"] = "overview" },
        1 or 3 => new() { ["softwarePath"] = "PLC", ["blockPath"] = "Motion/FB_Axis" },
        2 => new() { ["requestId"] = new string('a', 32) },
        7 => new() { ["form"] = "inspectionRules", ["mode"] = "set", ["fields"] = new JsonObject { ["namePattern"] = "^FB_" } },
        _ => new()
    };
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments)
        => new(DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) { Params = new() { Name = name,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } };
    private static FoundationV4Tool Tool(int index, Worker worker, WorkbenchControlClient client)
        => new(WorkbenchControlTools.Create(worker, "21", client).ElementAt(index), "21", null,
            () => throw new Exception("Approval settings are forbidden."), (_, _, _) => throw new Exception("Approval wait is forbidden."),
            () => throw new Exception("Readiness probe is forbidden."));
    public static IEnumerable<object[]> Operations() => Enumerable.Range(0, 8).Select(index => new object[] { index });
    public static IEnumerable<object[]> Refusals() => Enum.GetValues<WorkbenchControlError>().SelectMany(code => new[] {
        new object[] { (int)code, false }, new object[] { (int)code, true } });

    // The in-test server consumes the actual framed request and returns framed
    // responses. Independent buffers model a duplex connection without a socket.
    private sealed class FakeServer : Stream, IWorkbenchConnection
    {
        internal readonly MemoryStream Written = new();
        internal WorkbenchControlRequest? Request;
        internal Func<WorkbenchControlRequest, WorkbenchControlResponse> Reply = request => new() { RequestId = request.RequestId,
            Status = WorkbenchControlStatus.Done, Workbench = new() { Version = "fixture" }, Data = new() { Page = WorkbenchPage.Overview } };
        internal bool Identity = true, Verified, Hang, Malformed, WrongId, WrongVersion;
        internal Exception? ConnectError;
        private MemoryStream? response;
        public Stream Stream => this;
        public Task Connect(CancellationToken token) { Written.SetLength(0); response = null; return ConnectError == null ? Task.CompletedTask : Task.FromException(ConnectError); }
        public bool MatchesServer(string sid, string image) { Assert.Equal("fixture-sid", sid); Assert.Equal("C:/bundle/runtime/studio/TiaOpenness.exe", image); Verified = Identity; return Identity; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (Hang) await Task.Delay(Timeout.Infinite, token);
            if (response == null)
            {
                Written.Position = 0; Request = await WorkbenchControlFrames.Read<WorkbenchControlRequest>(Written, token);
                var reply = Reply(Request); if (WrongId) reply.RequestId = new string('f', 32);
                response = new();
                if (Malformed) response.Write(new byte[] { 0, 0, 0, 0 });
                else await WorkbenchControlFrames.Write(response, reply, token);
                if (WrongVersion)
                {
                    string json = System.Text.Encoding.UTF8.GetString(response.ToArray()[4..]).Replace("\"version\":1", "\"version\":2");
                    byte[] payload = System.Text.Encoding.UTF8.GetBytes(json); response.SetLength(0);
                    response.Write(BitConverter.GetBytes(payload.Length)); response.Write(payload);
                }
                response.Position = 0;
            }
            return await response.ReadAsync(buffer, offset, count, token);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { Assert.True(Verified); return Written.WriteAsync(buffer, offset, count, token); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    private static WorkbenchControlClient Client(FakeServer server) => new(() => server, () => "fixture-sid", () => "C:/bundle/runtime/studio/TiaOpenness.exe");

    [Theory, MemberData(nameof(Operations))]
    public async Task Every_operation_uses_closed_frames_and_bypasses_approval_audit_worker_and_lane(int index)
    {
        using var worker = new Worker { EngineIdentity = index % 2 == 0 }; using var server = new FakeServer();
        string audit = DataLocations.Current.AuditDirectory;
        Directory.CreateDirectory(audit);
        var before = Directory.GetFiles(audit).ToDictionary(path => path, File.ReadAllBytes);
        var result = await Tool(index, worker, Client(server)).InvokeAsync(Request(WorkbenchControlTools.Names[index], Arguments(index)));
        Assert.True((bool?)result.StructuredContent?["ok"]);
        Assert.Equal(server.Request!.RequestId, (string?)result.StructuredContent?["meta"]?["requestId"]);
        Assert.Equal(Environment.ProcessId, server.Request.Origin.HostProcessId);
        Assert.Equal(worker.Project, server.Request.Origin.BoundProjectFile);
        Assert.Matches("^[0-9a-f]{16}$", server.Request.Origin.McpSession);
        Assert.Equal((WorkbenchControlOperation)index, server.Request.Operation);
        Assert.InRange((server.Request.DeadlineUtc - DateTimeOffset.UtcNow).TotalSeconds, 0, index is 5 or 6 ? 2 : 5);
        Assert.Equal("not-applicable", (string?)result.StructuredContent?["meta"]?["behaviorPolicy"]);
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(audit).Order());
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }
    [Theory, MemberData(nameof(Refusals))]
    public async Task Every_typed_error_preserves_details_and_status_without_session_lock(int errorCode, bool failed)
    {
        await TypedError((WorkbenchControlError)errorCode, failed);
    }
    private async Task TypedError(WorkbenchControlError code, bool failed)
    {
        using var worker = new Worker(); using var server = new FakeServer { Reply = request => new() {
            RequestId = request.RequestId, Status = failed ? WorkbenchControlStatus.Failed : WorkbenchControlStatus.Refused,
            Workbench = new() { Version = "fixture", Contracts = new[] { 1, 2 } },
            Refusal = new() { Code = code, Condition = "workbench-user-active", Target = "project", Candidates = new[] { "A", "B" },
                Expected = "expected", Actual = "actual", Capability = "workbench-control.v1", Stage = "workbench-ui", DiagnosticId = request.RequestId } } };
        var result = await Tool(0, worker, Client(server)).InvokeAsync(Request("ShowWorkbenchPage", Arguments(0)));
        var body = result.StructuredContent!;
        failed &= code != WorkbenchControlError.Timeout;
        string expected = JsonSerializer.SerializeToElement(code, WorkbenchControlProtocol.Json).GetString()!;
        Assert.Equal(expected, (string?)body["error"]?["code"]);
        Assert.Equal(failed ? "failed" : "rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal(failed ? "completed" : "not-started", (string?)body["meta"]?["execution"]);
        Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
        Assert.Equal("workbench-user-active", (string?)body["data"]?["controlRefusal"]?["condition"]);
        Assert.Equal(2, body["data"]!["workbench"]!["contracts"]!.AsArray().Count);
        if (code == WorkbenchControlError.TargetAmbiguous) Assert.Equal(2, body["error"]!["details"]!["candidates"]!.AsArray().Count);
        if (code == WorkbenchControlError.Timeout) Assert.Equal("workbench-ui", (string?)body["error"]?["details"]?["stage"]);
        if (code == WorkbenchControlError.InternalError) Assert.Equal((string?)body["meta"]?["requestId"], (string?)body["error"]?["details"]?["diagnosticId"]);
    }
    [Theory, MemberData(nameof(Operations))]
    public async Task Absent_workbench_is_resource_unavailable_without_approval_or_launch(int index)
    {
        using var worker = new Worker(); using var server = new FakeServer { ConnectError = new TimeoutException("private-path") };
        var result = await Tool(index, worker, Client(server)).InvokeAsync(Request(WorkbenchControlTools.Names[index], Arguments(index)));
        Assert.Equal("RESOURCE_UNAVAILABLE", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(0, server.Written.Length); Assert.DoesNotContain("private-path", result.StructuredContent!.ToJsonString());
    }
    [Theory, InlineData(false), InlineData(true)]
    public async Task Identity_failure_writes_no_bytes(bool denied)
    {
        using var worker = new Worker(); using var server = new FakeServer { Identity = false };
        var client = denied ? new WorkbenchControlClient(() => server, () => throw new UnauthorizedAccessException("private"), () => "unused") : Client(server);
        var result = await Tool(0, worker, client).InvokeAsync(Request("ShowWorkbenchPage", Arguments(0)));
        Assert.Equal("ACCESS_DENIED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("pipe-owner", (string?)result.StructuredContent?["error"]?["details"]?["target"]);
        Assert.Equal(0, server.Written.Length);
    }
    [Theory, InlineData(0), InlineData(1), InlineData(2)]
    public async Task Malformed_mismatched_and_wrong_version_responses_fail_with_host_diagnostic(int kind)
    {
        using var worker = new Worker(); using var server = new FakeServer { Malformed = kind == 0, WrongId = kind == 1, WrongVersion = kind == 2 };
        var result = await Tool(5, worker, Client(server)).InvokeAsync(Request("GetWorkbenchState", new()));
        Assert.Equal("INTERNAL_ERROR", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("failed", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
    }
    [Theory, InlineData(0), InlineData(5)]
    public async Task Missing_response_times_out_at_the_correct_stage(int index)
    {
        using var server = new FakeServer { Hang = true };
        var request = new WorkbenchControlRequest { RequestId = new string('a', 32), Operation = (WorkbenchControlOperation)index,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(40), Arguments = index == 0 ? new WorkbenchDisplayPageArguments() : new WorkbenchReadStateArguments(),
            Origin = new() { ReleaseKey = "21", HostProcessId = Environment.ProcessId, McpSession = new string('a', 16) } };
        var result = WorkbenchControlTool.Map(await Client(server).Send(request, default), "21", WorkbenchControlTools.Names[index], request.RequestId, request.Operation);
        Assert.Equal("TIMEOUT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(index == 0 ? "workbench-ui" : "workbench-read", (string?)result.StructuredContent?["error"]?["details"]?["stage"]);
    }
    [Fact]
    public async Task Selection_paging_moves_to_meta_and_retains_bridge_source_and_read_only_switch()
    {
        using var worker = new Worker(); using var server = new FakeServer { Reply = request => new() { RequestId = request.RequestId,
            Workbench = new() { Version = "fixture" }, Data = new() { Paging = new() { Offset = 0, Limit = 1, Total = 2 },
                Workbench = new() { Version = "fixture", ControlEnabled = false }, Session = new() { Source = WorkbenchSessionSource.WorkbenchBridge } } } };
        var result = await Tool(6, worker, Client(server)).InvokeAsync(Request("GetWorkbenchSelection", new() { ["limit"] = 1 }));
        Assert.False((bool?)result.StructuredContent?["meta"]?["paging"]?["complete"]);
        Assert.Equal(1, (int?)result.StructuredContent?["meta"]?["paging"]?["nextOffset"]);
        Assert.False((bool?)result.StructuredContent?["data"]?["workbench"]?["controlEnabled"]);
        Assert.Equal("workbench-bridge", (string?)result.StructuredContent?["data"]?["session"]?["source"]);
        Assert.Null(result.StructuredContent?["data"]?["paging"]);
    }
    [Theory, InlineData("mcp"), InlineData("settings"), InlineData("approvals")]
    public async Task Protected_pages_are_rejected_before_connect(string page)
    {
        using var worker = new Worker(); using var server = new FakeServer();
        var result = await Tool(0, worker, Client(server)).InvokeAsync(Request("ShowWorkbenchPage", new() { ["page"] = page }));
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]); Assert.Equal(0, server.Written.Length);
    }
    [Theory, InlineData("inspectionRules", "namePattern"), InlineData("blockFilter", "filter"), InlineData("blockSelection", "softwarePath")]
    public async Task Prefill_closed_fields_set_clear_and_no_execution(string form, string field)
    {
        using var worker = new Worker(); using var server = new FakeServer();
        var fields = new JsonObject { [field] = "PLC" }; if (form == "blockSelection") fields["blockPaths"] = new JsonArray("Main");
        foreach (string mode in new[] { "set", "clear" })
        {
            var result = await Tool(7, worker, Client(server)).InvokeAsync(Request("PrefillWorkbenchForm", new() { ["form"] = form, ["mode"] = mode,
                ["fields"] = mode == "set" ? fields.DeepClone() : new JsonObject() }));
            Assert.True((bool?)result.StructuredContent?["ok"]);
        }
        var invalid = await Tool(7, worker, Client(server)).InvokeAsync(Request("PrefillWorkbenchForm", new() { ["form"] = form, ["mode"] = "set", ["fields"] = new JsonObject { ["execute"] = true } }));
        Assert.Equal("INVALID_ARGUMENT", (string?)invalid.StructuredContent?["error"]?["code"]);
    }
    private sealed class EmptyCatalog : enginehost::TiaMcpServer.ModelContextProtocol.IToolCatalogView
    {
        public IReadOnlyDictionary<string, Descriptor> All { get; } = new Dictionary<string, Descriptor>();
        public IReadOnlyDictionary<string, Descriptor> IncludingUnavailable => All;
        public IReadOnlyList<Descriptor> Lite => Array.Empty<Descriptor>();
        public JsonArray BehaviorCapabilities => new();
        public Descriptor? Find(string name, bool includeUnavailable = false) => null;
    }
    [Fact]
    public async Task Oversized_closed_prefill_is_rejected_before_connecting()
    {
        using var worker = new Worker();
        var client = new WorkbenchControlClient(() => throw new Exception("Connection is forbidden."));
        var paths = new JsonArray(Enumerable.Range(0, 100).Select(index => (JsonNode)JsonValue.Create(new string('a', 4090) + index)!).ToArray());
        var result = await Tool(7, worker, client).InvokeAsync(Request("PrefillWorkbenchForm", new() { ["form"] = "blockSelection",
            ["mode"] = "set", ["fields"] = new JsonObject { ["softwarePath"] = "PLC", ["blockPaths"] = paths } }));
        Assert.Equal("LIMIT_EXCEEDED", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
    }

    [Theory, InlineData("14sp1"), InlineData("15.1"), InlineData("16"), InlineData("17"), InlineData("18"), InlineData("19"), InlineData("20"), InlineData("21")]
    public void All_releases_and_both_host_catalogs_publish_the_eight_classified_tools(string release)
    {
        using var worker = new Worker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        foreach (string name in WorkbenchControlTools.Names)
        {
            Assert.Single(tools, tool => tool.ProtocolTool.Name == name);
            Assert.False(Taxonomy.UsesOpennessLane(name)); Assert.True(Taxonomy.IsSafeWithoutTia(name));
            Assert.Equal("Workbench", Taxonomy.For(name).Domain); Assert.Equal("session", Taxonomy.CategoryOf("Workbench"));
        }
        var essentials = WorkbenchControlTools.Names.ToHashSet(StringComparer.Ordinal);
        var catalog = new enginehost::TiaMcp.FoundationHost.SharedToolCatalog(new EmptyCatalog(), tools, essentials);
        foreach (string name in WorkbenchControlTools.Names)
        {
            Assert.Contains(catalog.Lite, tool => tool.Name == name);
            Assert.Equal("foundation", catalog.All[name].Execution);
            Assert.Equal("Workbench", catalog.All[name].Classification.Domain);
            if (!name.StartsWith("Get")) { Assert.False(catalog.All[name].Classification.BatchRead); Assert.False(catalog.All[name].Classification.BatchWrite); }
        }
    }
    [Fact]
    public void Bundle_image_resolution_uses_the_gui_in_development_and_delivery()
    {
        string root = FindRoot();
        Assert.Equal(Path.Combine(root, "src", "Studio", "Gui", "bin", "Release", "net10.0-windows", "TiaOpenness.exe"),
            BundleLayout.WorkbenchControlImagePath(root, Path.Combine(root, "src", "FoundationHost", "bin", "Release", "net10.0")));
        Assert.Equal(Path.Combine(root, "runtime", "studio", "TiaOpenness.exe"), BundleLayout.WorkbenchControlImagePath(root, Path.Combine(root, "runtime", "v21")));
    }
    private static string FindRoot() { for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) return dir.FullName; throw new Exception("Missing root."); }

    [Theory, InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7), InlineData(8), InlineData(9)]
    public void Render_registry_checks_session_project_exact_target_kind_provenance_and_file_identity(int scenario)
    {
        string directory = Path.Combine(FindRoot(), "bin-build", "refactor", "P8-20c", "artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var worker = new Worker { EngineIdentity = scenario % 2 == 0 };
        var registry = WorkbenchControlSession.For(worker);
        using var actor = ActorScope.EnterCall("owner");
        string input = Path.Combine(directory, "Main.xml"), output = Path.Combine(directory, "Main.html"), id = new string('a', 32);
        File.WriteAllText(input, "<Document><SW.Blocks.FC><AttributeList><Name>Main</Name><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>");
        try
        {
            if (scenario != 9) registry.Observe("ExportPlcBlock", new Dictionary<string, JsonElement> {
                ["exportPath"] = JsonSerializer.SerializeToElement(input), ["softwarePath"] = JsonSerializer.SerializeToElement("PLC"),
                ["blockPath"] = JsonSerializer.SerializeToElement("Motion/Main") }, JsonNode.Parse("{\"ok\":true,\"meta\":{\"execution\":\"completed\"},\"data\":{}}"));
            var rendered = TiaMcpServer.ModelContextProtocol.PlcProgramRenderer.Write(input, output, false, "21", id);
            Assert.True(rendered.Ok);
            registry.Observe("RenderPlcBlock", new Dictionary<string, JsonElement>(), JsonNode.Parse(V4Json.Serialize(rendered)));
            if (scenario == 3) worker.Project = "C:/Projects/Other.ap21";
            if (scenario == 6) File.AppendAllText(output, "changed");
            if (scenario == 7) File.Delete(output);
            using var other = scenario == 1 ? ActorScope.EnterCall("another-session") : null;
            // Nested actor scopes intentionally retain identity. A genuine second
            // MCP session is represented by another session-owned worker.
            var targetRegistry = scenario == 1 ? WorkbenchControlSession.For(new Worker()) : registry;
            var result = targetRegistry.Resolve(scenario == 8 ? new string('b', 32) : id,
                scenario == 2 ? WorkbenchRenderKind.Atlas : WorkbenchRenderKind.Ladder,
                scenario == 4 ? "OtherPLC" : "PLC", scenario == 5 ? "Other/Main" : "Motion/Main");
            if (scenario == 0) { Assert.Null(result.Error); Assert.Equal(output, result.Artifact!.Path); }
            else Assert.Equal(scenario is 1 or 7 or 8 ? ErrorCode.NotFound : ErrorCode.IdentityMismatch, result.Error!.Code);
        }
        finally
        {
            Assert.StartsWith(Path.Combine(FindRoot(), "bin-build", "refactor", "P8-20c"), Path.GetFullPath(directory));
            Directory.Delete(directory, true);
        }
    }

    [Theory, InlineData(0, false), InlineData(0, true), InlineData(1, false), InlineData(1, true), InlineData(2, false), InlineData(2, true),
        InlineData(3, false), InlineData(3, true), InlineData(4, false), InlineData(4, true), InlineData(7, false), InlineData(7, true)]
    public void UI_batch_targets_are_refused_before_binding_or_native_state(int index, bool preview)
    {
        using var worker = new Worker();
        var tools = WorkbenchControlTools.Create(worker, "21").ToArray();
        var catalog = new enginehost::TiaMcp.FoundationHost.SharedToolCatalog(new EmptyCatalog(), tools, WorkbenchControlTools.Names.ToHashSet());
        using var engine = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(new enginehost::TiaMcp.FoundationHost.EngineSessionContext(worker));
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
        enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ConfigureToolBridge(catalog, new NeverInvoker(), () => false, new HashSet<string>());
        var error = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ValidateBatch(new[] {
            new TiaMcp.Logic.V4.Inputs.ToolCall(WorkbenchControlTools.Names[index], new TiaMcp.Logic.V4.Inputs.ToolArguments(JsonSerializer.SerializeToElement(Arguments(index)))) }, preview, out _);
        Assert.Equal(ErrorCode.UnsupportedCapability, error!.Code);
        Assert.Equal("workbench-ui-batch", ((UnsupportedCapabilityDetails)error.Details).Capability);
    }
    private sealed class NeverInvoker : enginehost::TiaMcpServer.ModelContextProtocol.IToolInvoker
    {
        public Error? Bind(string name, TiaMcp.Logic.V4.Inputs.ToolArguments arguments, out enginehost::TiaMcpServer.ModelContextProtocol.IBoundToolCall? invocation)
            => throw new Exception("Batch binding is forbidden.");
        public enginehost::TiaMcpServer.ModelContextProtocol.ToolInvocationResult Invoke(string name, TiaMcp.Logic.V4.Inputs.ToolArguments arguments, bool preview) => throw new Exception("Batch dispatch is forbidden.");
        public Error? ValidateArguments(Descriptor tool, JsonElement arguments, JsonElement schema, bool typedFamiliesOnly = false) => throw new Exception("Batch validation is forbidden.");
        public McpServerTool CreateTool(Descriptor descriptor) => throw new Exception("Batch factory is forbidden.");
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(7)]
    public void Stored_UI_batch_is_refused_before_approval_revalidation_or_native_state(int index)
    {
        using var worker = new Worker();
        var context = new enginehost::TiaMcp.FoundationHost.EngineSessionContext(worker);
        using var engine = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(context);
        enginehost::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = "21";
        var tools = WorkbenchControlTools.Create(worker, "21").ToArray();
        var catalog = new enginehost::TiaMcp.FoundationHost.SharedToolCatalog(new EmptyCatalog(), tools, WorkbenchControlTools.Names.ToHashSet());
        enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ConfigureToolBridge(catalog, new NeverInvoker(), () => false, new HashSet<string>());
        var plan = new TiaMcpServer.ModelContextProtocol.BatchPlanStore.Plan { Operations = new JsonArray(new JsonObject {
            ["name"] = WorkbenchControlTools.Names[index], ["arguments"] = Arguments(index) }) };
        string token = context.BatchPlans.Add(plan, DateTime.UtcNow);
        var result = enginehost::TiaMcpServer.ModelContextProtocol.McpServer.ApplyToolBatch(token).StructuredContent!;
        Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)result["error"]?["code"]);
        Assert.Equal("workbench-ui-batch", (string?)result["error"]?["details"]?["capability"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }

    private sealed class RealConnection(string name) : IWorkbenchConnection
    {
        private readonly NamedPipeClientStream pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        public Stream Stream => pipe;
        public Task Connect(CancellationToken token) => pipe.ConnectAsync(WorkbenchControlPipe.ConnectTimeoutMilliseconds, token);
        public bool MatchesServer(string sid, string image) => WorkbenchControlPipe.ServerMatches(pipe, sid, image);
        public void Dispose() => pipe.Dispose();
    }
    [WindowsWorkbenchTheory, MemberData(nameof(Operations))]
    public async Task Real_fake_workbench_server_receives_each_operation_with_matching_process_identity(int index)
    {
        string name = "TiaMcp.Workbench.fixture." + Guid.NewGuid().ToString("N");
        using var pipe = WorkbenchControlPipe.CreateServer(name, LocalPipeSecurity.CurrentSid, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var serving = Task.Run(async () => {
            await pipe.WaitForConnectionAsync(timeout.Token);
            var request = await WorkbenchControlFrames.Read<WorkbenchControlRequest>(pipe, timeout.Token);
            Assert.True(WorkbenchControlPipe.PeerMatches(pipe, LocalPipeSecurity.CurrentSid, Environment.ProcessPath!));
            await WorkbenchControlFrames.Write(pipe, new WorkbenchControlResponse { RequestId = request.RequestId,
                Workbench = new() { Version = "fixture" }, Data = new() { Page = WorkbenchPage.Overview } }, timeout.Token);
        });
        using var worker = new Worker();
        var client = new WorkbenchControlClient(() => new RealConnection(name), () => LocalPipeSecurity.CurrentSid, () => Environment.ProcessPath!);
        var result = await Tool(index, worker, client).InvokeAsync(Request(WorkbenchControlTools.Names[index], Arguments(index)), timeout.Token);
        Assert.True((bool?)result.StructuredContent?["ok"]);
        await serving;
    }
}
