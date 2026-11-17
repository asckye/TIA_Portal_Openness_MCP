extern alias toolpipeline;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using Xunit;
using Metadata = TiaMcpServer.ModelContextProtocol.ToolMetadata;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol.McpServer;

[Collection(BundleSelectionCollection.Name)]
public sealed class WritePreviewCoverageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executed_writes_keep_execution_scope_without_preview_claims(bool asynchronous)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var args = new { anchorDeviceItemPath = "PLC/CPU", subnetType = "PN", subnetName = "PN" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, () => throw new InvalidOperationException());
        Assert.False((bool?)preview.StructuredContent?["data"]?["targetVerified"]);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        CallToolResult Execute() => Host.V4Result("EnsureSubnet", new JsonObject { ["scope"] = "Native subnet readback", ["applied"] = true });
        var result = asynchronous
            ? await Host.RunWritePreviewAsync("EnsureSubnet", false, args, () => Task.FromResult(Execute()), hash, "C:/Projects/original.ap21")
            : Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "C:/Projects/original.ap21");
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent!.ToJsonString());
        Assert.True((bool?)result.StructuredContent?["data"]?["applied"]);
        Assert.Null(result.StructuredContent?["data"]?["targetVerified"]);
        Assert.Equal("Native subnet readback", (string?)result.StructuredContent?["data"]?["scope"]);
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
    }

    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();

    [Theory, MemberData(nameof(Releases))]
    public async Task Apply_batch_fills_each_items_previewed_plan(string release)
    {
        using var fixture = new BatchPreviewFixture(release);
        var preview = await fixture.Preview(new JsonObject { ["name"] = "Motor" },
            new JsonObject { ["name"] = "Pump", ["expectedPlanHash"] = "", ["expectedProjectFile"] = "" });
        var applied = fixture.Apply(preview);
        Assert.True((bool?)applied["ok"], applied.ToJsonString());
        var previews = preview["data"]!["items"]!.AsArray();
        var results = applied["data"]!["items"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.NotEqual((string?)previews[0]!["result"]!["data"]!["planHash"], (string?)previews[1]!["result"]!["data"]!["planHash"]);
        for (int i = 0; i < results.Count; i++)
        {
            var data = results[i]!["result"]!["data"]!;
            Assert.Equal((string?)previews[i]!["result"]!["data"]!["planHash"], (string?)data["planHash"]);
            Assert.False((bool?)data["dryRun"]);
            Assert.False((bool?)data["confirm"]);
        }
        Assert.Equal(1, fixture.Executions["Motor"]);
        Assert.Equal(1, fixture.Executions["Pump"]);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Apply_batch_fills_native_preview_plan(string release)
    {
        using var fixture = new BatchPreviewFixture(release, native: true);
        var preview = await fixture.Preview(new JsonObject { ["name"] = "Motor", ["confirm"] = true });
        var applied = fixture.Apply(preview);
        Assert.True((bool?)applied["ok"], applied.ToJsonString());
        var data = applied["data"]!["items"]![0]!["result"]!["data"]!;
        Assert.Equal((string?)preview["data"]!["items"]![0]!["result"]!["data"]!["planHash"], (string?)data["planHash"]);
        Assert.True((bool?)data["confirm"]);
        Assert.Equal(2, fixture.NativePreviews);
        Assert.Equal(1, fixture.Executions["Motor"]);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Apply_batch_keeps_caller_supplied_plan_and_project(string release)
    {
        using var fixture = new BatchPreviewFixture(release);
        var original = await fixture.Preview(new JsonObject { ["name"] = "Motor" });
        string hash = (string)original["data"]!["items"]![0]!["result"]!["data"]!["planHash"]!;
        foreach (var (field, value, code) in new[] {
            ("expectedPlanHash", new string('0', 64), "PLAN_STALE"),
            ("expectedProjectFile", "C:/Projects/other.ap21", "IDENTITY_MISMATCH")
        })
        {
            var arguments = new JsonObject {
                ["name"] = "Motor", ["expectedPlanHash"] = hash, ["expectedProjectFile"] = "C:/Projects/original.ap21", [field] = value
            };
            var preview = await fixture.Preview(arguments,
                new JsonObject { ["name"] = "Pump" });
            var applied = fixture.Apply(preview);
            Assert.False((bool?)applied["ok"]);
            Assert.Equal(code, (string?)applied["data"]!["items"]![0]!["result"]!["error"]!["code"]);
            Assert.Equal("NOT_EXECUTED", (string?)applied["data"]!["items"]![1]!["result"]!["error"]!["code"]);
        }
        Assert.Empty(fixture.Executions);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Apply_batch_revalidates_changed_planned_arguments_before_any_write(string release)
    {
        using var fixture = new BatchPreviewFixture(release);
        var preview = await fixture.Preview(new JsonObject { ["name"] = "Motor" }, new JsonObject { ["name"] = "Pump" });
        fixture.Revision++;
        var applied = fixture.Apply(preview);
        Assert.False((bool?)applied["ok"]);
        Assert.Equal("PLAN_STALE", (string?)applied["error"]!["code"]);
        Assert.Equal("preview", (string?)applied["error"]!["details"]!["reason"]);
        Assert.Empty(fixture.Executions);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Apply_batch_keeps_stored_hash_when_an_earlier_item_changes_the_next_plan(string release)
    {
        using var fixture = new BatchPreviewFixture(release);
        var preview = await fixture.Preview(new JsonObject { ["name"] = "Motor" }, new JsonObject { ["name"] = "Pump" });
        fixture.AfterExecute = name => { if (name == "Motor") fixture.Revision++; };
        var applied = fixture.Apply(preview);
        Assert.False((bool?)applied["ok"]);
        Assert.Equal("partial", (string?)applied["meta"]!["outcome"]);
        var items = applied["data"]!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.True((bool?)items[0]!["result"]!["ok"]);
        Assert.Equal("completed", (string?)items[0]!["result"]!["meta"]!["execution"]);
        Assert.Equal((string?)preview["data"]!["items"]![0]!["result"]!["data"]!["planHash"],
            (string?)items[0]!["result"]!["data"]!["planHash"]);
        Assert.Equal("PLAN_STALE", (string?)items[1]!["result"]!["error"]!["code"]);
        Assert.Equal((string?)preview["data"]!["items"]![1]!["result"]!["data"]!["planHash"],
            (string?)items[1]!["result"]!["error"]!["details"]!["planHash"]);
        Assert.Equal("not-started", (string?)items[1]!["result"]!["meta"]!["execution"]);
        Assert.Equal(1, fixture.Executions["Motor"]);
        Assert.False(fixture.Executions.ContainsKey("Pump"));
        Assert.Equal(1, fixture.Revision);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Apply_batch_refuses_invalid_stored_preview_without_dispatch(string release)
    {
        using var fixture = new BatchPreviewFixture(release) { InvalidPreviewIdentity = true };
        var preview = await fixture.Preview(new JsonObject { ["name"] = "Motor" });
        var applied = fixture.Apply(preview);
        Assert.False((bool?)applied["ok"]);
        var result = applied["data"]!["items"]![0]!["result"]!;
        Assert.Equal("PRECONDITION_FAILED", (string?)result["error"]!["code"]);
        Assert.Equal("batch-preview", (string?)result["error"]!["details"]!["condition"]);
        Assert.Equal("Stored batch preview or plan-binding arguments are invalid.", (string?)result["error"]!["message"]);
        Assert.Equal("not-started", (string?)result["meta"]!["execution"]);
        Assert.Empty(fixture.Executions);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private sealed class BatchPreviewFixture : IDisposable
    {
        internal readonly BatchPreviewWorker Worker = new();
        internal readonly Dictionary<string, int> Executions = new(StringComparer.Ordinal);
        internal int Revision, NativePreviews;
        internal bool InvalidPreviewIdentity;
        internal Action<string>? AfterExecute;
        private readonly toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline pipeline;
        private readonly IDisposable session;

        internal BatchPreviewFixture(string release, bool native = false)
        {
            var tool = McpServerTool.Create((string name, bool dryRun = true, string expectedPlanHash = "",
                string expectedProjectFile = "", bool confirm = false) => {
                var arguments = new { name, revision = Revision, confirm };
                CallToolResult Execute()
                {
                    Executions[name] = Executions.GetValueOrDefault(name) + 1;
                    AfterExecute?.Invoke(name);
                    return Host.V4Result("DeletePlcBlock", new JsonObject { ["confirm"] = confirm }, completed: true);
                }
                CallToolResult Preview()
                {
                    NativePreviews++;
                    return Host.V4Result("DeletePlcBlock", new JsonObject { ["name"] = name });
                }
                var result = native
                    ? Host.RunNativeWritePreview("DeletePlcBlock", dryRun, arguments, Preview, Execute, expectedPlanHash, expectedProjectFile)
                    : Host.RunWritePreview("DeletePlcBlock", dryRun, arguments, Execute, expectedPlanHash, expectedProjectFile);
                if (dryRun && InvalidPreviewIdentity)
                {
                    result.StructuredContent!["data"]!["targetIdentity"] = 42;
                    result.Content = new[] { new TextContentBlock { Text = result.StructuredContent.ToJsonString() } };
                }
                return result;
            }, new() { Name = "DeletePlcBlock" });
            pipeline = new(release, Worker, new[] { tool });
            session = pipeline.EnterSession();
        }

        internal async Task<JsonObject> Preview(params JsonObject[] arguments)
        {
            var operations = arguments.Select(args => new { name = "DeletePlcBlock", arguments = args }).ToArray();
            var request = Request("PreviewToolBatch", new { expectedProject = "Fixture", operations });
            var result = await pipeline.AllTools.Single(tool => tool.ProtocolTool.Name == "PreviewToolBatch").InvokeAsync(request);
            Assert.True((bool?)result.StructuredContent!["ok"], result.StructuredContent.ToJsonString());
            return result.StructuredContent.AsObject();
        }

        internal JsonObject Apply(JsonObject preview)
        {
            string token = (string)preview["data"]!["previewToken"]!;
            using var request = Host.UseProgressRequest(Request("ApplyToolBatch", new { previewToken = token }), CancellationToken.None);
            return Host.ApplyToolBatch(token).StructuredContent!.AsObject();
        }

        private static RequestContext<CallToolRequestParams> Request(string name, object arguments)
            => new(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments)) }
            };

        public void Dispose() { session.Dispose(); pipeline.Dispose(); Worker.Dispose(); }
    }

    private sealed class BatchPreviewWorker : toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        internal readonly PlanWorker Plan = new();
        internal int Calls => Plan.Calls;
        internal BatchPreviewWorker()
        {
            var identity = Plan.Identity["identity"]!.AsObject();
            identity["tiaMajorVersion"] = 21;
            identity["processStartUtc"] = "2026-11-17T00:00:00Z";
            identity["projectName"] = "Fixture";
            identity["generation"] = 1;
        }
        public bool Faulted => false;
        public JsonNode? Binding => Plan.Binding;
        public object SessionKey => Plan.SessionKey;
        public JsonObject Snapshot() => new() {
            ["readiness"] = new JsonObject { ["ready"] = true },
            ["session"] = new JsonObject { ["isConnected"] = true, ["project"] = "Fixture" }, ["binding"] = Binding!.DeepClone()
        };
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => Task.FromResult<IDisposable>(new Lease());
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public Task<toolpipeline::TiaMcp.FoundationHost.EngineReply> Invoke(
            string id, string name, JsonObject arguments, bool preview, CancellationToken token) => Plan.Invoke(id, name, arguments, preview, token);
        public void Dispose() => Plan.Dispose();
    }

    public static IEnumerable<object[]> EmptyNativePreviewDataCases() => from release in Releases()
        from omitData in new[] { false, true }
        select new object[] { release[0], omitData };

    [Theory, MemberData(nameof(EmptyNativePreviewDataCases))]
    public void Native_write_preview_creates_plan_data_when_native_data_is_null_or_missing(string release, bool omitData)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var args = new { action = "delete", masterCopyPath = "Motor" };
        var native = Host.V4Result("ManageLibraryMasterCopy", null);
        var body = native.StructuredContent!.AsObject();
        if (omitData) body.Remove("data");
        else body["data"] = null;
        native.Content = new[] { new TextContentBlock { Text = body.ToJsonString() } };
        int previews = 0;
        var result = Host.RunNativeWritePreview("ManageLibraryMasterCopy", true, args,
            () => { previews++; return native; }, () => throw new InvalidOperationException("Preview must not execute."));
        Assert.True((bool?)result.StructuredContent!["ok"]);
        var data = Assert.IsType<JsonObject>(result.StructuredContent["data"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)data["planHash"]!);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(args), data["plannedChanges"]));
        Assert.True(JsonNode.DeepEquals(worker.Identity, data["targetIdentity"]));
        Assert.True((bool?)data["targetBound"]);
        Assert.True((bool?)data["dryRun"]);
        Assert.False((bool?)data["mayHaveChanged"]);
        Assert.Null(data["targetVerified"]);
        Assert.Equal(1, previews);
        Assert.Equal(0, worker.Calls);
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_write_preview_discloses_unbound_scope_and_keeps_bound_native_scope(string release)
    {
        foreach (bool bound in new[] { false, true })
        {
            using var worker = new PlanWorker();
            if (!bound) worker.Identity = new JsonObject();
            using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
            using var session = pipeline.EnterSession();
            var result = Host.RunNativeWritePreview("ManageLibraryMasterCopy", true, new { action = "delete", masterCopyPath = "Motor" },
                () => Host.V4Result("ManageLibraryMasterCopy", new JsonObject {
                    ["scope"] = "Native master copy resolution", ["resolvedPath"] = "Library/Motor"
                }), () => throw new InvalidOperationException("Preview must not execute."));
            Assert.True((bool?)result.StructuredContent!["ok"]);
            var data = result.StructuredContent["data"]!;
            Assert.Equal(bound, (bool?)data["targetBound"]);
            Assert.Equal(bound ? "Native master copy resolution"
                : "No project is bound, so this preview covers only the arguments and input files and names no target. "
                    + "The target is not verified. Applying it refuses with PROJECT_NOT_BOUND; bind a project and preview again.",
                (string?)data["scope"]);
            Assert.Equal("Library/Motor", (string?)data["resolvedPath"]);
            Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
            Assert.Equal(0, worker.Calls);
        }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_write_preview_keeps_native_scope_and_target_evidence(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var args = new { action = "delete", masterCopyPath = "Motor" };
        int previews = 0, executions = 0;
        var native = Host.V4Result("ManageLibraryMasterCopy", new JsonObject {
            ["scope"] = "Native master copy resolution", ["resolvedPath"] = "Library/Motor", ["found"] = true,
            ["dryRun"] = false, ["mayHaveChanged"] = true
        });
        var result = Host.RunNativeWritePreview("ManageLibraryMasterCopy", true, args,
            () => { previews++; return native; },
            () => { executions++; return Host.V4Result("ManageLibraryMasterCopy", new JsonObject(), completed: true); });
        var data = result.StructuredContent!["data"]!;
        Assert.True((bool?)result.StructuredContent["ok"]);
        Assert.Equal(1, previews);
        Assert.Equal(0, executions);
        Assert.Equal("Native master copy resolution", (string?)data["scope"]);
        Assert.Equal("Library/Motor", (string?)data["resolvedPath"]);
        Assert.True((bool?)data["found"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)data["planHash"]!);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(args), data["plannedChanges"]));
        Assert.True(JsonNode.DeepEquals(worker.Identity, data["targetIdentity"]));
        Assert.True((bool?)data["targetBound"]);
        Assert.True((bool?)data["dryRun"]);
        Assert.False((bool?)data["mayHaveChanged"]);
        Assert.Null(data["targetVerified"]);
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_write_execution_refuses_missing_or_stale_confirmation_before_either_delegate(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var args = new { action = "delete", masterCopyPath = "Motor" };
        int previews = 0, executions = 0;
        CallToolResult Preview() { previews++; return Host.V4Result("ManageLibraryMasterCopy", new JsonObject()); }
        CallToolResult Execute() { executions++; return Host.V4Result("ManageLibraryMasterCopy", new JsonObject(), completed: true); }
        var plan = Host.RunWritePreview("ManageLibraryMasterCopy", true, args, Execute);
        string hash = (string)plan.StructuredContent!["data"]!["planHash"]!;
        foreach (var (expectedHash, project, code) in new[] {
            ("", "C:/Projects/original.ap21", "INVALID_ARGUMENT"),
            (new string('0', 64), "C:/Projects/original.ap21", "PLAN_STALE"),
            (hash, "C:/Projects/other.ap21", "IDENTITY_MISMATCH")
        })
        {
            var result = Host.RunNativeWritePreview("ManageLibraryMasterCopy", false, args, Preview, Execute, expectedHash, project);
            Assert.False((bool?)result.StructuredContent!["ok"]);
            Assert.Equal(code, (string?)result.StructuredContent["error"]!["code"]);
            Assert.Equal("rejected-before-operation", (string?)result.StructuredContent["meta"]!["outcome"]);
            Assert.Equal("not-started", (string?)result.StructuredContent["meta"]!["execution"]);
        }
        Assert.Equal(0, previews);
        Assert.Equal(0, executions);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_write_execution_uses_the_matching_plan_without_calling_preview(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var args = new { action = "delete", masterCopyPath = "Motor" };
        var preview = Host.RunNativeWritePreview("ManageLibraryMasterCopy", true, args,
            () => Host.V4Result("ManageLibraryMasterCopy", new JsonObject()), () => throw new InvalidOperationException());
        var plan = preview.StructuredContent!["data"]!;
        int previews = 0, executions = 0;
        var result = Host.RunNativeWritePreview("ManageLibraryMasterCopy", false, args,
            () => { previews++; return Host.V4Result("ManageLibraryMasterCopy", new JsonObject()); },
            () => { executions++; return Host.V4Result("ManageLibraryMasterCopy",
                new JsonObject { ["scope"] = "Native deletion readback", ["deleted"] = true }, completed: true); },
            (string)plan["planHash"]!, "C:/Projects/original.ap21");
        var data = result.StructuredContent!["data"]!;
        Assert.True((bool?)result.StructuredContent["ok"]);
        Assert.Equal(0, previews);
        Assert.Equal(1, executions);
        foreach (string field in new[] { "planHash", "plannedChanges", "targetIdentity", "targetBound" })
            Assert.True(JsonNode.DeepEquals(plan[field], data[field]), field);
        Assert.False((bool?)data["dryRun"]);
        Assert.True((bool?)data["deleted"]);
        Assert.Equal("Native deletion readback", (string?)data["scope"]);
        Assert.Null(data["targetVerified"]);
        Assert.Equal("completed", (string?)result.StructuredContent["meta"]!["execution"]);
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_write_preview_returns_native_errors_unchanged_without_a_plan(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var native = Host.V4Reject("ManageLibraryMasterCopy", Host.InvalidInput("masterCopyPath"),
            new JsonObject { ["nativeFailure"] = true });
        string original = native.StructuredContent!.ToJsonString();
        int previews = 0, executions = 0;
        var result = Host.RunNativeWritePreview("ManageLibraryMasterCopy", true, new { action = "delete", masterCopyPath = "Motor" },
            () => { previews++; return native; },
            () => { executions++; return Host.V4Result("ManageLibraryMasterCopy", new JsonObject(), completed: true); });
        Assert.Same(native, result);
        Assert.Equal(original, result.StructuredContent!.ToJsonString());
        Assert.Equal(original, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)!.ToJsonString());
        Assert.Null(result.StructuredContent["data"]?["planHash"]);
        Assert.Equal(1, previews);
        Assert.Equal(0, executions);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_and_argument_only_previews_share_one_plan_contract(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var args = new { action = "delete", masterCopyPath = "Motor" };
        CallToolResult Execute() => throw new InvalidOperationException("Preview must not execute.");
        var native = Host.RunNativeWritePreview("ManageLibraryMasterCopy", true, args,
            () => Host.V4Result("ManageLibraryMasterCopy", new JsonObject()), Execute);
        var generic = Host.RunWritePreview("ManageLibraryMasterCopy", true, args, Execute);
        foreach (string field in new[] { "planHash", "plannedChanges", "targetIdentity", "targetBound" })
            Assert.True(JsonNode.DeepEquals(generic.StructuredContent!["data"]![field], native.StructuredContent!["data"]![field]), field);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_write_preview_applies_argument_refusals_before_either_delegate(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Native() { calls++; return Host.V4Result("EnsureSubnet", new JsonObject()); }
        var result = Host.RunNativeWritePreview("EnsureSubnet", true,
            new { anchorDeviceItemPath = "PLC/CPU", subnetType = "PROFIBUS", subnetName = "PN" }, Native, Native);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]!["code"]);
        Assert.Equal("/subnetType", (string?)result.StructuredContent["error"]!["details"]!["parameter"]);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent["meta"]!["outcome"]);
        Assert.Null(result.StructuredContent["data"]?["planHash"]);
        Assert.Equal(0, calls);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Native_write_plan_preserves_input_failure_semantics_before_either_delegate(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "native-preview-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "Screen.xml");
            File.Copy(Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
                "templates", "mcp-full-e2e-verify", "import", "MCP_E2E_Tags.xml"), file);
            var args = new { importPath = file };
            CallToolResult Unexpected() => throw new InvalidOperationException("No native operation may run.");
            var preview = Host.RunWritePreview("ImportHmiScreen", true, args, Unexpected);
            string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
            File.Delete(file);
            var missing = Host.RunNativeWritePreview("ImportHmiScreen", true, args, Unexpected, Unexpected);
            Assert.Equal("INVALID_ARGUMENT", (string?)missing.StructuredContent!["error"]!["code"]);
            Assert.Null(missing.StructuredContent["data"]?["planHash"]);
            var stale = Host.RunNativeWritePreview("ImportHmiScreen", false, args, Unexpected, Unexpected,
                hash, "C:/Projects/original.ap21");
            Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent!["error"]!["code"]);
            Assert.Equal("input-changed", (string?)stale.StructuredContent["error"]!["details"]!["reason"]);
            Assert.Equal("rejected-before-operation", (string?)stale.StructuredContent["meta"]!["outcome"]);
            Assert.Equal(0, worker.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Stale_write_plan_names_the_reconnect_cause(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("EnsureSubnet", new JsonObject(), completed: true); }
        var args = new { subnetName = "PN", anchorDeviceItemPath = "PLC/CPU" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, Execute);
        Assert.True((bool?)preview.StructuredContent!["ok"]);
        var stale = Host.RunWritePreview("EnsureSubnet", false, args, Execute, new string('0', 64), "C:/Projects/original.ap21");
        Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent!["error"]!["code"]);
        Assert.Equal("arguments-or-target", (string?)stale.StructuredContent["error"]!["details"]!["reason"]);
        Assert.Equal("This plan no longer matches the preview: arguments, target, input files or the TIA binding differ. "
            + "A reconnect or a host restart also changes the binding. Nothing ran; preview again.",
            (string?)stale.StructuredContent["error"]!["message"]);
        Assert.Equal(0, calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Unbound_preview_says_it_names_no_target(string release)
    {
        using var worker = new PlanWorker { Identity = new JsonObject() };
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        CallToolResult Execute() => throw new InvalidOperationException("Unbound preview must not execute.");
        var args = new { subnetName = "PN", anchorDeviceItemPath = "PLC/CPU" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, Execute);
        Assert.True((bool?)preview.StructuredContent!["ok"]);
        var data = preview.StructuredContent["data"]!;
        Assert.False((bool?)data["targetBound"]);
        Assert.False((bool?)data["targetVerified"]);
        Assert.Equal("No project is bound, so this preview covers only the arguments and input files and names no target. "
            + "The target is not verified. Applying it refuses with PROJECT_NOT_BOUND; bind a project and preview again.", (string?)data["scope"]);
        var refused = Host.RunWritePreview("EnsureSubnet", false, args, Execute, (string)data["planHash"]!, "C:/Projects/original.ap21");
        Assert.Equal("PROJECT_NOT_BOUND", (string?)refused.StructuredContent!["error"]!["code"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Bound_preview_keeps_its_scope_and_hash(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var args = new { subnetName = "PN", anchorDeviceItemPath = "PLC/CPU" };
        CallToolResult Execute() => throw new InvalidOperationException("Preview must not execute.");
        var preview = await Host.RunWritePreviewAsync("EnsureSubnet", true, args, () => Task.FromResult(Execute()));
        var data = preview.StructuredContent!["data"]!;
        Assert.True((bool?)data["targetBound"]);
        Assert.False((bool?)data["targetVerified"]);
        Assert.Equal("Normalized arguments, input file contents and cached target identity; the target is not verified. Native validation/readback occurs during execution.",
            (string?)data["scope"]);
        var hashed = new JsonObject { ["releaseKey"] = release, ["tool"] = "EnsureSubnet",
            ["arguments"] = JsonSerializer.SerializeToNode(args), ["inputFiles"] = new JsonArray(),
            ["targetIdentity"] = worker.Identity.DeepClone(), ["hostLifetime"] = (string?)typeof(Host).Assembly
                .GetType("TiaOpenness.Shared.CallJournalPayload")!
                .GetProperty("PlanHostLifetime", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) };
        Assert.Equal(TiaOpenness.Shared.PendingApproval.Hash(TiaOpenness.Shared.PendingApproval.Canonical(hashed)), (string?)data["planHash"]);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Executed_read_omits_preview_scope_and_target_verification(string release)
    {
        using var worker = new PlanWorker { Identity = new JsonObject() };
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int calls = 0;
        var preview = Host.RunWritePreview("InvokeObject", true, new { methodName = "GetAttributeInfos" },
            () => { calls++; return Host.V4Result("InvokeObject", new JsonObject()); }, writing: false);
        Assert.True((bool?)preview.StructuredContent!["ok"]);
        Assert.False(preview.StructuredContent["data"]!.AsObject().ContainsKey("targetBound"));
        Assert.True((bool?)preview.StructuredContent["data"]!["dryRun"]);
        Assert.True((bool?)preview.StructuredContent["data"]!["readOnlyInvocation"]);
        Assert.Null(preview.StructuredContent["data"]!["scope"]);
        Assert.Null(preview.StructuredContent["data"]!["targetVerified"]);
        Assert.Equal(1, calls);
        Assert.Equal(0, worker.Calls);
    }

    public static IEnumerable<object[]> ReadOnlyInvocations() => from release in Releases()
        from tool in new[] { "InvokeObject", "InvokeService" }
        from dryRun in new[] { true, false }
        select new object[] { release[0], tool, dryRun };

    [Theory, MemberData(nameof(ReadOnlyInvocations))]
    public void Read_only_invoke_keeps_the_requested_dryRun(string release, string tool, bool dryRun)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.ReflectionPortService((_, arguments) => {
            calls++;
            Assert.True((bool?)arguments["dryRun"]);
            Assert.False((bool?)arguments["request"]?["AllowWrite"]);
            Assert.Equal("GetAttribute", (string?)arguments["request"]?["MethodName"]);
            Assert.False(arguments.ContainsKey("confirm"));
            return new JsonObject { ["Observation"] = new JsonObject { ["Message"] = "read",
                ["Meta"] = new JsonObject { ["success"] = true, ["invocationAttempted"] = true } } };
        }, () => throw new InvalidOperationException("A read must not request a write identity."));
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.ReflectionTools(service);
        var args = new[] { new TiaMcp.Logic.V4.Inputs.NativeValue(JsonSerializer.SerializeToElement("Name")) };
        var result = tool == "InvokeObject"
            ? tools.InvokeObjectV4("Project", "", "GetAttribute", args, dryRun: dryRun, allowWrite: false)
            : tools.InvokeServiceV4("Project", "", "PlcChecksumProvider", "GetAttribute", args, dryRun: dryRun, allowWrite: false);
        var body = result.StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.Equal(dryRun, (bool?)body["data"]!["dryRun"]);
        Assert.True((bool?)body["data"]!["readOnlyInvocation"]);
        Assert.True((bool?)body["data"]!["invocationAttempted"]);
        Assert.Null(body["data"]!["targetVerified"]);
        Assert.DoesNotContain("target is not verified", body["data"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("read-only", (string?)body["meta"]!["execution"]);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        Assert.Equal(1, calls);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(ReadOnlyInvocations))]
    public async Task Async_read_only_invoke_keeps_the_requested_dryRun(string release, string tool, bool dryRun)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int calls = 0;
        var result = await Host.RunWritePreviewAsync(tool, dryRun, new { methodName = "GetAttribute" }, () => {
            calls++;
            return Task.FromResult(Host.V4Result(tool, new JsonObject { ["invocationAttempted"] = true }));
        }, writing: false);
        var body = result.StructuredContent!;
        Assert.True((bool?)body["ok"]);
        Assert.Equal(dryRun, (bool?)body["data"]!["dryRun"]);
        Assert.True((bool?)body["data"]!["readOnlyInvocation"]);
        Assert.True((bool?)body["data"]!["invocationAttempted"]);
        Assert.Null(body["data"]!["targetVerified"]);
        Assert.DoesNotContain("target is not verified", body["data"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("read-only", (string?)body["meta"]!["execution"]);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        Assert.Equal(1, calls);
        Assert.Equal(0, worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task EnsureSubnet_preview_refuses_an_unknown_type_without_a_plan(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools(null!);
        var rejected = tools.EnsureSubnet("PLC/CPU", "PROFIBUS", "PN");
        var asyncRejected = await Host.RunWritePreviewAsync("EnsureSubnet", true,
            new { anchorDeviceItemPath = "PLC/CPU", subnetType = "PROFIBUS", subnetName = "PN" },
            () => throw new InvalidOperationException("Invalid preview must not execute."));
        foreach (var result in new[] { rejected, asyncRejected })
        {
            var body = result.StructuredContent!;
            Assert.False((bool?)body["ok"]);
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
            Assert.Equal("/subnetType", (string?)body["error"]!["details"]!["parameter"]);
            Assert.Null(body["data"]?["planHash"]);
            Assert.Equal("not-started", (string?)body["meta"]!["execution"]);
            Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        }
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PROFINET")]
    [InlineData("PN")]
    [InlineData("IndustrialEthernet")]
    [InlineData("Industrial Ethernet")]
    [InlineData("PN/IE")]
    [InlineData(" profinet ")]
    public void EnsureSubnet_preview_accepts_the_existing_native_type_values(string subnetType)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var result = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareNetworkTools(null!).EnsureSubnet("PLC/CPU", subnetType, "PN");
        Assert.True((bool?)result.StructuredContent!["ok"]);
        Assert.False((bool?)result.StructuredContent["data"]!["targetVerified"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)result.StructuredContent["data"]!["planHash"]!);
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public async Task InputContentsAreBoundAcrossPreviewAndApply(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "preview-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "Screen.xml");
            File.WriteAllText(file, "<Document>A</Document>");
            var arguments = new JsonObject { ["importPath"] = file };
            int calls = 0;
            CallToolResult Execute() { calls++; return Host.V4Result("ImportHmiScreen", new JsonObject(), completed: true); }
            string Hash() => (string)Host.RunWritePreview("ImportHmiScreen", true, arguments, Execute).StructuredContent!["data"]!["planHash"]!;
            string hash = Hash();
            DateTime timestamp = File.GetLastWriteTimeUtc(file);
            File.WriteAllText(file, "<Document>B</Document>");
            File.SetLastWriteTimeUtc(file, timestamp);
            Assert.NotEqual(hash, Hash());
            var stale = await Host.RunWritePreviewAsync("ImportHmiScreen", false, arguments, () => Task.FromResult(Execute()), hash, "C:/Projects/original.ap21");
            Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent!["error"]!["code"]);
            Assert.Equal("This plan no longer matches the preview: arguments, target, input files or the TIA binding differ. "
                + "A reconnect or a host restart also changes the binding. Nothing ran; preview again.",
                (string?)stale.StructuredContent["error"]!["message"]);
            Assert.Equal(0, calls);
            string fresh = Hash();
            Assert.True((bool?)Host.RunWritePreview("ImportHmiScreen", false, arguments, Execute, fresh, "C:/Projects/original.ap21").StructuredContent!["ok"]);
            Assert.Equal(1, calls);
            File.Delete(file);
            var missing = Host.RunWritePreview("ImportHmiScreen", true, arguments, Execute);
            Assert.Equal("INVALID_ARGUMENT", (string?)missing.StructuredContent!["error"]!["code"]);
            var deleted = Host.RunWritePreview("ImportHmiScreen", false, arguments, Execute, fresh, "C:/Projects/original.ap21");
            Assert.Equal("PLAN_STALE", (string?)deleted.StructuredContent!["error"]!["code"]);
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DirectoryPlansBindNestedContentsNamesAndMembershipInSortedOrder()
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "preview-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            string first = Path.Combine(root, "A.xml"), nested = Path.Combine(root, "nested", "B.xml");
            File.WriteAllText(nested, "<Document>B</Document>"); File.WriteAllText(first, "<Document>A</Document>");
            int calls = 0;
            CallToolResult Execute() { calls++; return Host.V4Result("ImportHmiScreensFromDirectory", new JsonObject(), completed: true); }
            var args = new { dir = root };
            string Hash() => (string)Host.RunWritePreview("ImportHmiScreensFromDirectory", true, args, Execute).StructuredContent!["data"]!["planHash"]!;
            string original = Hash();
            File.Delete(first); File.WriteAllText(first, "<Document>A</Document>");
            Assert.Equal(original, Hash());
            File.WriteAllText(nested, "<Document>C</Document>"); Assert.NotEqual(original, Hash());
            File.WriteAllText(nested, "<Document>B</Document>"); Assert.Equal(original, Hash());
            string renamed = Path.Combine(root, "nested", "C.xml");
            File.Move(nested, renamed); Assert.NotEqual(original, Hash());
            File.Move(renamed, nested); Assert.Equal(original, Hash());
            File.WriteAllText(renamed, "<Document>C</Document>"); Assert.NotEqual(original, Hash());
            File.Delete(nested); Assert.NotEqual(original, Hash());
            var stale = Host.RunWritePreview("ImportHmiScreensFromDirectory", false, args, Execute, original, "C:/Projects/original.ap21");
            Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent!["error"]!["code"]);
            Assert.Equal(0, calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("password", "weak-secret")]
    [InlineData("nested", "{\"name\":\"password\",\"value\":\"weak-secret\"}")]
    [InlineData("embedded", "{\"token\":\"weak-secret\"}")]
    [InlineData("text", "Bearer weak-secret")]
    [InlineData("text", "{password=weak-secret")]
    [InlineData("text", "password=weak-secret")]
    [InlineData("text", "https://user:weak-secret@example.invalid/")]
    public void SecretPlanHashesAreStableKeyedAndRejectChangedSecrets(string field, string value)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var arguments = new JsonObject { [field] = field == "nested" ? JsonNode.Parse(value) : JsonValue.Create(value) };
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("DownloadPlc", new JsonObject(), completed: true); }
        JsonObject Preview() => Host.RunWritePreview("DownloadPlc", true, arguments, Execute).StructuredContent!["data"]!.AsObject();
        var preview = Preview(); string hash = (string)preview["planHash"]!;
        Assert.Equal(hash, (string?)Preview()["planHash"]);
        Assert.DoesNotContain("weak-secret", preview.ToJsonString());
        var unkeyed = new JsonObject { ["releaseKey"] = "21", ["tool"] = "DownloadPlc", ["arguments"] = arguments.DeepClone(),
            ["inputFiles"] = new JsonArray(), ["targetIdentity"] = worker.Identity.DeepClone() };
        unkeyed["hostLifetime"] = (string?)typeof(Host).Assembly.GetType("TiaOpenness.Shared.CallJournalPayload")!
            .GetProperty("PlanHostLifetime", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        Assert.NotEqual(TiaOpenness.Shared.PendingApproval.Hash(TiaOpenness.Shared.PendingApproval.Canonical(unkeyed)), hash);
        unkeyed.Remove("hostLifetime");
        unkeyed.Remove("inputFiles");
        Assert.NotEqual(TiaOpenness.Shared.PendingApproval.Hash(TiaOpenness.Shared.PendingApproval.Canonical(unkeyed)), hash);
        if (field == "nested") arguments[field]!["value"] = "another-secret";
        else arguments[field] = value.Replace("weak-secret", "another-secret", StringComparison.Ordinal);
        Assert.NotEqual(hash, (string?)Preview()["planHash"]);
        var stale = Host.RunWritePreview("DownloadPlc", false, arguments, Execute, hash, "C:/Projects/original.ap21");
        Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent!["error"]!["code"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FileListsAndDirectoryFileNamesUseTheSameInputSelectionAsValidation()
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build", "preview-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "A.xml"), second = Path.Combine(root, "B.xml");
            File.WriteAllText(first, "<Document>A</Document>"); File.WriteAllText(second, "<Document>B</Document>");
            CallToolResult Execute() => throw new InvalidOperationException("A preview must not execute.");
            string Hash(object args) => (string)Host.RunWritePreview("ImportUnifiedHmiTags", true, args, Execute).StructuredContent!["data"]!["planHash"]!;
            var selected = new { directory = root, fileName = "A.xml" };
            string original = Hash(selected);
            File.WriteAllText(second, "<Document>C</Document>"); Assert.Equal(original, Hash(selected));
            var list = new { files = new[] { first, second } };
            string listed = Hash(list);
            File.WriteAllText(second, "<Document>D</Document>"); Assert.NotEqual(listed, Hash(list));
            File.WriteAllText(first, "<Document>E</Document>"); Assert.NotEqual(original, Hash(selected));
            var ordered = new JsonObject { ["inputPath"] = first, ["templateFile"] = second };
            var reordered = new JsonObject { ["templateFile"] = second, ["inputPath"] = first };
            Assert.Equal(Hash(ordered), Hash(reordered));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SecretDigestsChangeWithTheHostLifetime()
    {
        JsonNode? Digest()
        {
            var context = new System.Runtime.Loader.AssemblyLoadContext("preview-key-" + Guid.NewGuid().ToString("N"), isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(typeof(Host).Assembly.Location);
                var payload = assembly.GetType("TiaOpenness.Shared.CallJournalPayload", throwOnError: true)!;
                var protectedArguments = (JsonNode?)payload.GetMethod("ProtectPlanArguments", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object[] { new JsonObject { ["password"] = "weak-secret" } });
                var lifetime = (string)payload.GetProperty("PlanHostLifetime", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                return new JsonObject { ["arguments"] = protectedArguments, ["lifetime"] = lifetime };
            }
            finally { context.Unload(); }
        }
        var first = Digest()!; var second = Digest()!;
        Assert.False(JsonNode.DeepEquals(first["arguments"], second["arguments"]));
        Assert.NotEqual((string?)first["lifetime"], (string?)second["lifetime"]);
    }

    [Fact]
    public void StructuredSecretsRetainCanonicalArgumentOrdering()
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        string Hash(JsonObject args) => (string)Host.RunWritePreview("DownloadPlc", true, args,
            () => throw new InvalidOperationException("Preview must not execute.")).StructuredContent!["data"]!["planHash"]!;
        var first = JsonNode.Parse("{\"credential\":{\"user\":\"sample\",\"password\":\"weak\"}}")!.AsObject();
        var reordered = JsonNode.Parse("{\"credential\":{\"password\":\"weak\",\"user\":\"sample\"}}")!.AsObject();
        Assert.Equal(Hash(first), Hash(reordered));
    }

    [Fact]
    public void InvokeServiceReadDispatchesWithoutApprovalInputs()
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.ReflectionPortService((_, arguments) => {
            calls++;
            Assert.True((bool?)arguments["dryRun"]);
            Assert.False((bool?)arguments["request"]?["AllowWrite"]);
            Assert.Equal("GetAttributeInfos", (string?)arguments["request"]?["MethodName"]);
            Assert.False(arguments.ContainsKey("confirm"));
            return new JsonObject { ["Observation"] = new JsonObject { ["Message"] = "read", ["Meta"] = new JsonObject { ["success"] = true } } };
        }, () => throw new InvalidOperationException("A read must not request a write identity."));
        var result = new toolpipeline::TiaMcpServer.ModelContextProtocol.ReflectionTools(service)
            .InvokeServiceV4("Project", "", "PlcChecksumProvider", "GetAttributeInfos");
        Assert.True((bool?)result.StructuredContent!["ok"]);
        Assert.Equal(1, calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Drive_read_keeps_the_default_dryRun(string release)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int calls = 0;
        var service = new toolpipeline::TiaMcpServer.Siemens.Services.StartdriveService((_, arguments) => {
            calls++;
            Assert.True((bool?)arguments["dryRun"]);
            Assert.False((bool?)arguments["request"]?["IsWrite"]);
            Assert.False(arguments.ContainsKey("confirm"));
            return new JsonObject { ["Message"] = "read", ["Meta"] = new JsonObject { ["success"] = true } };
        }, () => throw new InvalidOperationException("A read must not request the bound write identity."));
        var result = new toolpipeline::TiaMcpServer.ModelContextProtocol.StartdriveTools(service)
            .ManageOnlineDriveFunctions(new[] { "Drive" }, new[] { "Axis" });
        Assert.True((bool?)result.StructuredContent!["ok"]);
        Assert.True((bool?)result.StructuredContent["data"]!["dryRun"]);
        Assert.True((bool?)result.StructuredContent["data"]!["readOnlyInvocation"]);
        Assert.Equal("read-only", (string?)result.StructuredContent["meta"]!["execution"]);
        Assert.Equal(1, calls);
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public void ReadExamplesDispatchReadsWithDefaultPreviewFlagAndNoApprovalInputs()
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        int reflectionCalls = 0, driveCalls = 0;
        var reflection = new toolpipeline::TiaMcpServer.Siemens.Services.ReflectionPortService((_, arguments) => {
            reflectionCalls++;
            Assert.True((bool?)arguments["dryRun"]);
            Assert.False((bool?)arguments["request"]?["AllowWrite"]);
            Assert.False(arguments.ContainsKey("confirm"));
            Assert.Equal("GetAttributeInfos", (string?)arguments["request"]?["MethodName"]);
            return new JsonObject { ["Observation"] = new JsonObject { ["Message"] = "read", ["Meta"] = new JsonObject { ["success"] = true } } };
        }, () => throw new InvalidOperationException("A read must not request the bound write identity."));
        var drives = new toolpipeline::TiaMcpServer.Siemens.Services.StartdriveService((_, arguments) => {
            driveCalls++;
            Assert.True((bool?)arguments["dryRun"]);
            Assert.False((bool?)arguments["request"]?["IsWrite"]);
            Assert.False(arguments.ContainsKey("confirm"));
            return new JsonObject { ["Message"] = "read", ["Meta"] = new JsonObject { ["success"] = true } };
        }, () => throw new InvalidOperationException("A read must not request the bound write identity."));
        var reflected = new toolpipeline::TiaMcpServer.ModelContextProtocol.ReflectionTools(reflection).InvokeObjectV4("Project", "", "GetAttributeInfos");
        var online = new toolpipeline::TiaMcpServer.ModelContextProtocol.StartdriveTools(drives).ManageOnlineDriveFunctions(new[] { "Drive" }, new[] { "Axis" });
        Assert.True((bool?)reflected.StructuredContent!["ok"]);
        Assert.True((bool?)online.StructuredContent!["ok"]);
        Assert.Equal(1, reflectionCalls);
        Assert.Equal(1, driveCalls);
    }

    [Fact]
    public void Plan_hash_matches_normalized_arguments_and_rejects_changed_arguments_or_target_before_execution()
    {
        using var worker = new PlanWorker();
        using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("EnsureSubnet", new JsonObject(), completed: true); }
        var args = new JsonObject { ["subnetName"] = "PN", ["anchorDeviceItemPath"] = "PLC/CPU" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, Execute);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        var reordered = new JsonObject { ["anchorDeviceItemPath"] = "PLC/CPU", ["subnetName"] = "PN" };
        Assert.Equal(hash, (string?)Host.RunWritePreview("EnsureSubnet", true, reordered, Execute).StructuredContent!["data"]!["planHash"]);
        args["subnetName"] = "changed";
        Assert
            .Equal("PLAN_STALE", (string?)Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "C:/Projects/original.ap21")
                .StructuredContent!["error"]!["code"]);
        Assert.Equal(0, calls);
        args["subnetName"] = "PN"; worker.Identity["identity"]!["projectPath"] = "C:/Projects/other.ap21";
        Assert
            .Equal("PLAN_STALE", (string?)Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "C:/Projects/original.ap21")
                .StructuredContent!["error"]!["code"]);
        Assert.Equal(0, calls);
        worker.Identity["identity"]!["projectPath"] = "C:/Projects/original.ap21";
        Assert.True((bool?)Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "C:/Projects/original.ap21").StructuredContent!["ok"]);
        Assert.Equal(1, calls);
        output.WriteLine("Generic preview uses the existing canonical SHA-256 plan hash and cached target identity.");
    }

    [Fact]
    public async Task Async_plan_hash_rejects_a_changed_target_before_dispatch()
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        Task<CallToolResult> Execute() { calls++; return Task.FromResult(Host.V4Result("DownloadPlc", new JsonObject(), completed: true)); }
        var preview = await Host.RunWritePreviewAsync("DownloadPlc", true, new { softwarePath = "PLC/CPU" }, Execute);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        worker.Identity["identity"]!["processId"] = 456;
        var rejected = await Host.RunWritePreviewAsync("DownloadPlc", false, new { softwarePath = "PLC/CPU" }, Execute, hash, "C:/Projects/original.ap21");
        Assert.Equal("PLAN_STALE", (string?)rejected.StructuredContent!["error"]!["code"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Published_write_wrapper_forwards_the_expected_plan_hash_before_service_access()
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.HardwareDevicesTools(null!);
        var preview = tools.SetDeviceItemAttributeV4("PLC/CPU", "Name", "CPU_2");
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        var stale = tools.SetDeviceItemAttributeV4("PLC/CPU", "Name", "CPU_3", false, hash, "C:/Projects/original.ap21");
        Assert.Equal("PLAN_STALE", (string?)stale.StructuredContent!["error"]!["code"]);
        Assert.Equal("not-started", (string?)stale.StructuredContent["meta"]!["execution"]);
        var wrongProject = tools.SetDeviceItemAttributeV4("PLC/CPU", "Name", "CPU_2", false, hash, "C:/Projects/other.ap21");
        Assert.Equal("IDENTITY_MISMATCH", (string?)wrongProject.StructuredContent!["error"]!["code"]);
    }

    [Theory]
    [InlineData("C:/Projects/other.ap21", "IDENTITY_MISMATCH")]
    [InlineData("relative.ap21", "INVALID_ARGUMENT")]
    [InlineData("C:", "INVALID_ARGUMENT")]
    [InlineData("", "INVALID_ARGUMENT")]
    public async Task Execution_requires_the_absolute_bound_project_before_sync_or_async_dispatch(string project, string code)
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("EnsureSubnet", new JsonObject(), completed: true); }
        var args = new { subnetName = "PN" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, Execute);
        Assert.True((bool?)preview.StructuredContent!["ok"]);
        string hash = (string)preview.StructuredContent["data"]!["planHash"]!;
        var sync = Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, project);
        var asyncResult = await Host.RunWritePreviewAsync("EnsureSubnet", false, args, () => Task.FromResult(Execute()), hash, project);
        Assert.Equal(code, (string?)sync.StructuredContent!["error"]!["code"]);
        Assert.Equal(code, (string?)asyncResult.StructuredContent!["error"]!["code"]);
        Assert.Equal("not-started", (string?)sync.StructuredContent["meta"]!["execution"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Execution_requires_a_reviewed_hash_and_accepts_the_canonical_bound_project()
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        CallToolResult Execute() { calls++; return Host.V4Result("EnsureSubnet", new JsonObject(), completed: true); }
        var args = new { subnetName = "PN" };
        var preview = Host.RunWritePreview("EnsureSubnet", true, args, Execute);
        string hash = (string)preview.StructuredContent!["data"]!["planHash"]!;
        var rejected = Host.RunWritePreview("EnsureSubnet", false, args, Execute, "", "C:/Projects/original.ap21");
        Assert.Equal("INVALID_ARGUMENT", (string?)rejected.StructuredContent!["error"]!["code"]);
        Assert.Equal("/expectedPlanHash", (string?)rejected.StructuredContent["error"]!["details"]!["parameter"]);
        Assert.Equal(0, calls);
        Assert.True((bool?)Host.RunWritePreview("EnsureSubnet", false, args, Execute, hash, "c:/PROJECTS/original.ap21").StructuredContent!["ok"]);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("InvokeObject")]
    [InlineData("InvokeService")]
    [InlineData("ManageOnlineDriveFunctions")]
    public void Read_modes_keep_their_existing_execution_without_project_or_hash_inputs(string tool)
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        var result = Host.RunWritePreview(tool, false, new { action = "read" }, () => {
            calls++; return Host.V4Result(tool, new JsonObject());
        }, writing: false);
        Assert.True((bool?)result.StructuredContent!["ok"]);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void In_memory_export_handle_operations_execute_directly_without_a_preview()
    {
        using var worker = new PlanWorker();
        using var session = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker));
        var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.ExportTools();
        string id = toolpipeline::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "A", "retained", 4).id;
        Assert.True((bool?)tools.DeleteExport(id).StructuredContent!["ok"]);
        Assert.Null(toolpipeline::TiaMcp.FoundationHost.SessionExportStore.Get(id));
        toolpipeline::TiaMcp.FoundationHost.SessionExportStore.PutAndSlice("fixture", "B", "retained", 4);
        Assert.Equal(1, (int?)tools.ClearExports(0).StructuredContent!["data"]!["deleted"]);
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Clearing_parked_responses_only_deletes_the_calling_sessions_handles(string release)
    {
        using var worker = new PlanWorker();
        var catalog = new HmiTagPathContractTests.DeleteCatalog(release);
        var current = new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog };
        var other = new toolpipeline::TiaMcp.FoundationHost.EngineSessionContext(worker) { Catalog = catalog };
        string previous = toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey;
        string currentId = "", otherId = "";
        try
        {
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = release;
            string Park(string target)
            {
                var response = Host.V4Result("GetSessionState", new JsonObject { ["value"] = target });
                var parked = toolpipeline::TiaMcpServer.ModelContextProtocol.ResponseGuardTool.Shrink(
                    response, "GetSessionState", target, force: true);
                return (string)parked.StructuredContent!["data"]!["export"]!["id"]!;
            }
            using (toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(other)) otherId = Park("other");
            using (toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(current))
            {
                currentId = Park("current");
                var tools = new toolpipeline::TiaMcpServer.ModelContextProtocol.ExportTools();
                Assert.Equal(1, (int?)tools.ClearExports(0).StructuredContent!["data"]!["deleted"]);
                Assert.Equal(0, (int?)tools.ClearExports(0).StructuredContent!["data"]!["deleted"]);
                Assert.Null(toolpipeline::TiaMcp.FoundationHost.SessionExportStore.Get(currentId));
                Assert.True(tools.GetExport(otherId).IsError);
            }
            using (toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.Enter(other))
            {
                Assert.NotNull(toolpipeline::TiaMcp.FoundationHost.SessionExportStore.Get(otherId));
                Assert.False(new toolpipeline::TiaMcpServer.ModelContextProtocol.ExportTools().GetExport(otherId).IsError ?? false);
            }
        }
        finally
        {
            current.Exports.Dispose(); other.Exports.Dispose();
            TiaMcpServer.ModelContextProtocol.ExportStore.Delete(currentId);
            TiaMcpServer.ModelContextProtocol.ExportStore.Delete(otherId);
            toolpipeline::TiaMcp.FoundationHost.EngineHostConfiguration.ReleaseKey = previous;
        }
    }

    [Theory]
    [InlineData("14sp1")]
    [InlineData("15.1")]
    [InlineData("16")]
    [InlineData("17")]
    [InlineData("18")]
    [InlineData("19")]
    [InlineData("20")]
    [InlineData("21")]
    public void Every_registered_write_has_a_default_preview_or_an_explicit_plan(string release)
    {
        using var worker = new FakeWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        var writes = tools.Where(t => Metadata.Find(t.ProtocolTool.Name)?.Operation is "WRITE" or "ONLINE-WRITE").ToArray();
        Assert.NotEmpty(writes);
        foreach (var implementation in writes)
        {
            var tool = implementation.ProtocolTool;
            var properties = tool.InputSchema.GetProperty("properties");
            bool preview = properties.TryGetProperty("dryRun", out var dryRun)
                && dryRun.GetProperty("type").GetString() == "boolean"
                && dryRun.GetProperty("default").GetBoolean();
            bool planned = properties.TryGetProperty("mode", out var mode)
                && mode.TryGetProperty("default", out var value) && value.GetString() == "preview";
            bool previewedBatch = tool.Name == "ApplyToolBatch" && properties.TryGetProperty("previewToken", out _);
            bool restartPreview = tool.Name == "RestartOpennessWorker"
                && !properties.GetProperty("confirmRestart").GetProperty("default").GetBoolean();
            bool memoryOnly = tool.Name is "DeleteExportHandle" or "ClearExportHandles"; // in-memory handle only, no project change
            Assert.True(preview || planned || previewedBatch || restartPreview || memoryOnly, release + ": " + tool.Name);
        }
    }

    [Fact]
    public void Planned_arguments_are_identical_on_preview_and_execution_and_secrets_are_redacted()
    {
        using var worker = new PlanWorker(); using var foundationWorker = new FakeWorker();
        var shared = LegacyHostToolRegistry.Create(foundationWorker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        using var session = pipeline.EnterSession();
        int calls = 0;
        var arguments = new { devicePath = "PLC/CPU", attributeName = "Name", value = "CPU_2", password = "private-value" };
        CallToolResult Execute() { calls++; return Host.V4Result("SetDeviceItemAttribute", new JsonObject(), completed: true); }
        var preview = Host.RunWritePreview("SetDeviceItemAttribute", true, arguments, Execute);
        Assert.Equal(0, calls);
        var applied = Host
            .RunWritePreview("SetDeviceItemAttribute", false, arguments, Execute, (string)preview.StructuredContent!["data"]!["planHash"]!,
                "C:/Projects/original.ap21");
        Assert.Equal(1, calls);
        Assert.True(JsonNode.DeepEquals(preview.StructuredContent!["data"]!["plannedChanges"], applied.StructuredContent!["data"]!["plannedChanges"]));
        Assert.DoesNotContain("private-value", preview.StructuredContent.ToJsonString());
        Assert.Equal("read-only", (string?)preview.StructuredContent["meta"]?["execution"]);
        Assert.Equal("completed", (string?)applied.StructuredContent["meta"]?["execution"]);
        Assert.Equal(applied.StructuredContent.ToJsonString(), JsonNode.Parse(((TextContentBlock)applied.Content.Single()).Text)!.ToJsonString());
    }

    [Theory]
    [InlineData("SetDeviceItemAttribute", "{\"deviceItemPath\":\"PLC/CPU\",\"attributeName\":\"Name\",\"value\":\"CPU_2\"}")]
    [InlineData("SetPlcCpuSettings", "{\"cpuPath\":\"PLC/CPU\",\"settings\":{\"exactAttributes\":{\"Name\":\"CPU_2\"}}}")]
    [InlineData("DownloadPlc", "{\"softwarePath\":\"PLC\",\"targetIpAddress\":\"192.168.1.10\",\"password\":\"private-value\"}")]
    [InlineData("SetUnifiedHmiButtonEventScriptCode",
                "{\"hmiSoftwarePath\":\"HMI\",\"screenName\":\"/Overview\",\"buttonName\":\"Start\"," +
                "\"eventType\":\"Tapped\",\"scriptCode\":\"HMIRuntime.Tags(\\\"Start\\\").Write(true);\"}")]
    public async Task Default_preview_does_not_dispatch_a_native_operation(string name, string json)
    {
        using var worker = new HardwareAddressingAdmissionTests.UnboundWorker();
        var shared = LegacyHostToolRegistry.Create(worker, "21", false, hostPorts: false);
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline("21", worker, shared);
        var tool = pipeline.AllTools.Single(t => t.ProtocolTool.Name == name);
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }
        });
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.True((bool?)result.StructuredContent?["data"]?["dryRun"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)result.StructuredContent!["data"]!["planHash"]!);
        Assert.DoesNotContain("private-value", result.StructuredContent!.ToJsonString());
        Assert.InRange(worker.Calls, 0, 1); // The fixture refuses every operation except managed ReadState.
    }

    [Theory]
    [InlineData("AttachDeviceNodeToSubnet")]
    [InlineData("ConnectDeviceNodesToProfinetSubnet")]
    [InlineData("CreateGsdDevice")]
    [InlineData("CompileDevice")]
    [InlineData("CreateVersionControlWorkspace")]
    [InlineData("DownloadPlc")]
    [InlineData("EnsureSubnet")]
    [InlineData("ImportAlarmClasses")]
    [InlineData("ImportAlarmTextLists")]
    [InlineData("ImportHmiConnection")]
    [InlineData("ImportHmiScreen")]
    [InlineData("ImportHmiScreensFromDirectory")]
    [InlineData("ImportHmiTagTable")]
    [InlineData("ImportHmiTagTablesFromDirectory")]
    [InlineData("ImportMasterCopyFromGlobalLibrary")]
    [InlineData("ImportOpcUaInterface")]
    [InlineData("ImportTechnologyObject")]
    [InlineData("ImportTechnologyObjectsFromDirectory")]
    [InlineData("InvokeObject")]
    [InlineData("InvokeService")]
    [InlineData("ManageOnlineDriveFunctions")]
    [InlineData("SeedProjectFromReference")]
    [InlineData("SetDeviceItemAttribute")]
    [InlineData("SetOpcUaInterfaceEnabled")]
    [InlineData("SetPlcCpuSettings")]
    [InlineData("SetPlcWatchTableModifyValue")]
    [InlineData("SetUnifiedHmiButtonEventScriptCode")]
    [InlineData("SetUnifiedHmiRuntimeState")]
    public async Task Maintained_write_examples_preview_without_native_dispatch(string name)
        => await AssertMaintainedWritePreview("21", name);

    [Theory, MemberData(nameof(Releases))]
    public async Task Generic_preview_says_it_did_not_verify_the_target(string release)
    {
        using var worker = new FakeWorker();
        var registered = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false)
            .Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        var names = new[] { "AttachDeviceNodeToSubnet", "ConnectDeviceNodesToProfinetSubnet", "CreateDevice", "CreateGsdDevice",
            "CompileDevice", "CreateVersionControlWorkspace", "DownloadPlc", "EnsureSubnet", "ImportAlarmClasses", "ImportAlarmTextLists",
            "ImportHmiConnection", "ImportHmiScreen", "ImportHmiScreensFromDirectory", "ImportHmiTagTable", "ImportHmiTagTablesFromDirectory",
            "ImportMasterCopyFromGlobalLibrary", "ImportOpcUaInterface", "ImportTechnologyObject", "ImportTechnologyObjectsFromDirectory",
            "InvokeObject", "InvokeService", "ManageOnlineDriveFunctions", "SeedProjectFromReference", "SetDeviceItemAttribute",
            "SetOpcUaInterfaceEnabled", "SetPlcCpuSettings", "SetPlcWatchTableModifyValue", "SetUnifiedHmiButtonEventScriptCode",
            "SetUnifiedHmiRuntimeState" };
        Assert.Contains("EnsureSubnet", registered);
        foreach (string name in names.Where(registered.Contains)) await AssertMaintainedWritePreview(release, name);
    }

    private static async Task AssertMaintainedWritePreview(string release, string name)
    {
        using var worker = new PlanWorker();
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, worker, Array.Empty<McpServerTool>());
        using var session = pipeline.EnterSession();
        var method = typeof(Host).Assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Single(candidate => candidate.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == "McpServerToolAttribute"
                && attribute.NamedArguments.Any(argument => argument.MemberName == "Name" && (string?)argument.TypedValue.Value == name)));
        // Unconfigured services make any accidental service access fail; published admission is covered separately.
        var constructor = method.DeclaringType!.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var target = constructor.Invoke(new object?[constructor.GetParameters().Length]);
        var tool = new toolpipeline::TiaMcp.FoundationHost.CanonicalParameterTool(McpServerTool.Create(method, target), release);
        var path = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "reference", "tool-examples", "calls.json");
        var arguments = JsonNode.Parse(File.ReadAllText(path))!["profiles"]!["plc-foundation"]![name]!["arguments"]!.DeepClone().AsObject();
        arguments["dryRun"] = true;
        arguments.Remove("expectedPlanHash");
        arguments.Remove("expectedProjectFile");
        using var inputs = new PreviewInputFixture(name, arguments);
        if (name is "InvokeObject" or "InvokeService") arguments["allowWrite"] = true;
        if (name == "ManageOnlineDriveFunctions") arguments["action"] = "performRamToRomCopy";
        foreach (string field in new[] { "dryRun", "expectedPlanHash", "expectedProjectFile" })
            Assert.Equal(CompatibilityTools.PreviewParameterDescriptions[field],
                tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty(field).GetProperty("description").GetString());
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(
            DispatchProxy.Create<IMcpServer, ApprovalPrecheckTests.ServerProxy>()) {
                Params = new CallToolRequestParams { Name = name,
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) }
            });
        Assert.True((bool?)result.StructuredContent?["ok"], result.StructuredContent?.ToJsonString());
        Assert.True((bool?)result.StructuredContent?["data"]?["dryRun"]);
        Assert.False((bool?)result.StructuredContent?["data"]?["targetVerified"]);
        Assert.Contains("target is not verified", (string?)result.StructuredContent?["data"]?["scope"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("read-only", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)result.StructuredContent!["data"]!["planHash"]!);
        Assert.Equal(0, worker.Calls);
    }

    private sealed class PreviewInputFixture : IDisposable
    {
        private readonly string root = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
            "bin-build", "preview-example-" + Guid.NewGuid().ToString("N"));
        internal PreviewInputFixture(string tool, JsonObject arguments)
        {
            Directory.CreateDirectory(root);
            // These previews read caller bytes but never invoke the native format parser.
            // Use the maintained SIMATIC XML fixture for the file contents.
            string xml = Path.Combine(TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory),
                "templates", "mcp-full-e2e-verify", "import", "MCP_E2E_Tags.xml");
            if (arguments.ContainsKey("importPath")) arguments["importPath"] = xml;
            var inputDirectories = new[] { "dir", "referenceDir", "libraryPath" }.Concat(
                TiaMcp.Logic.V4.CanonicalToolParameters.Aliases(tool)
                    .Where(alias => alias.Canonical == "sourceDirectory")
                    .SelectMany(alias => new[] { alias.Legacy, alias.Canonical })).Distinct();
            foreach (string parameter in inputDirectories)
                if (arguments.ContainsKey(parameter))
                {
                    File.Copy(xml, Path.Combine(root, "MCP_E2E_Tags.xml"), overwrite: true);
                    arguments[parameter] = root;
                }
        }
        public void Dispose() => Directory.Delete(root, true);
    }

    internal sealed class PlanWorker : toolpipeline::TiaMcp.FoundationHost.IEngineWorker
    {
        internal int Calls;
        internal JsonObject Identity = new() { ["identity"] = new JsonObject { ["processId"] = 123, ["projectPath"] = "C:/Projects/original.ap21" } };
        public bool Faulted => false;
        public JsonNode? Binding => Identity;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<toolpipeline::TiaMcp.FoundationHost
            .EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        { Calls++; throw new NotSupportedException(); }
        public void Dispose() { }
    }

}
