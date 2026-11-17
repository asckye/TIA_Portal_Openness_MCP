using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using Xunit;

public sealed class ScopedCompileContractTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public ScopedCompileContractTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Report_default_compile_binding_before_and_after_scope_arguments()
    {
        using var worker = new Worker();
        var definition = FoundationTools.Definitions.Single(d => d.Name == "CompilePlcSoftware");
        var before = new FoundationTool(definition with { Arguments = definition.Arguments.Take(3).ToArray(), ResponseMember = "Mutation" }, worker);
        var after = new FoundationTool(definition, worker);
        var args = new JsonObject { ["softwarePath"] = "CPU" }.Deserialize<Dictionary<string, JsonElement>>()!;
        double Measure(FoundationTool tool)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int index = 0; index < 10000; index++) tool.ValidateApplyArguments(args);
            return watch.Elapsed.TotalMilliseconds / 10;
        }
        Measure(before); Measure(after);
        var original = new List<double>(); var scoped = new List<double>();
        for (int pass = 0; pass < 7; pass++) { original.Add(Measure(before)); scoped.Add(Measure(after)); }
        double oldMedian = original.OrderBy(t => t).ElementAt(3), newMedian = scoped.OrderBy(t => t).ElementAt(3);
        output.WriteLine($"Foundation argument binding, software preview, median of 7 x 10000: before={oldMedian:F3} us; after={newMedian:F3} us.");
        Assert.Equal(0, worker.Calls);
    }

    private const string Project = "C:/Projects/Scoped.ap21";
    private sealed class Worker : IFoundationWorker
    {
        internal JsonObject? Arguments;
        internal int Calls;
        internal bool ServiceAvailable = true;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Assert.Equal("CompileSoftware", operation); Calls++; Arguments = arguments.DeepClone().AsObject();
            bool dry = (bool?)arguments["dryRun"] ?? true;
            if (!dry && !ServiceAvailable)
            {
                var cause = PlcCompilePolicy.ScopeUnavailable("Exact owner has no ICompilable.");
                var classified = WorkerFailurePolicy.Classify(cause, true, false);
                throw WorkerOperationException.FromChannelFailure(new TiaMcp.WorkerChannel.ChannelFailureException(
                    cause.Message, classified.Code, classified.Outcome, WorkerJson.Evidence(cause)));
            }
            PlcCompileResult result = new PlcCompileResult();
            string scope = (string?)arguments["scope"] ?? "software";
            if (scope != "software") result = new PlcScopedCompileResult { Scope = scope,
                ScopePath = ObjectPathEncoding.NormalizePath((string)arguments["scopePath"]!), UnitName = (string?)arguments["unitName"] ?? "",
                ResolvedObject = "devices/D/CPU/blocks/Group%2Fone/Main", Compilable = ServiceAvailable };
            result.ProjectFile = Project; result.Executed = !dry;
            if (!dry) { result.State = "Success"; result.ErrorCount = 0; result.WarningCount = 0; }
            return Task.FromResult(JsonSerializer.SerializeToNode(result, result.GetType()));
        }
        public void Dispose() { }
    }
    private static FoundationTool Tool(string name, Worker worker) => new(FoundationTools.Definitions.Single(d => d.Name == name), worker);
    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject arguments) =>
        new(DispatchProxy.Create<IMcpServer, ServerProxy>()) { Params = new() {
            Name = name, Arguments = arguments.Deserialize<Dictionary<string, JsonElement>>() } };
    public class ServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_ServerOptions" ? new McpServerOptions() : null;
    }

    [Fact]
    public void Existing_compile_contract_and_diagnostics_remain_checked() => CompileContractTests.Run((valid, message) => Assert.True(valid, message));

    [Theory]
    [InlineData("CompilePlcSoftware")][InlineData("CompilePlcDiagnostics")]
    public void Both_schemas_and_registrations_expose_all_parameters_without_a_plan_hash(string name)
    {
        using var worker = new Worker(); var native = Tool(name, worker); var tool = new FoundationV4Tool(native, "21");
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert
            .Equal(new[] { "software", "block", "group", "unit" }, properties.GetProperty("scope").GetProperty("enum").EnumerateArray()
                .Select(x => x.GetString()));
        Assert.Equal("software", properties.GetProperty("scope").GetProperty("default").GetString());
        Assert.False(properties.TryGetProperty("expectedPlanHash", out _)); Assert.False(native.IsWrite);
        string root = TiaMcp.Engine.Tests.Shared.TestPaths.RepositoryRoot(AppContext.BaseDirectory);
        var registration = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "reference/tool-catalog/engine-source.json")))!["registrations"]![name]!;
        foreach (var property in properties.EnumerateObject())
        {
            Assert.NotNull(registration["parameters"]![property.Name]);
            Assert.Contains(property.Name, (string)registration["signature"]!);
        }
    }

    [Theory]
    [InlineData("CompilePlcSoftware", false)][InlineData("CompilePlcDiagnostics", false)]
    [InlineData("CompilePlcSoftware", true)][InlineData("CompilePlcDiagnostics", true)]
    public async Task Default_or_explicit_software_scope_keeps_original_wire_arguments(string name, bool explicitDefaults)
    {
        using var worker = new Worker(); var tool = Tool(name, worker);
        var arguments = new JsonObject { ["softwarePath"] = "CPU" };
        if (explicitDefaults) { arguments["scope"] = "software"; arguments["scopePath"] = ""; arguments["unitName"] = ""; }
        var response = await tool.InvokeV4Async(Request(name, arguments), "21", "test", CancellationToken.None);
        Assert.False(response.IsError, response.StructuredContent?.ToJsonString());
        Assert.Equal(8, worker.Arguments!.Count);
        Assert.Equal("software", (string?)worker.Arguments["scope"]);
        Assert.Equal("", (string?)worker.Arguments["scopePath"]); Assert.Equal("", (string?)worker.Arguments["unitName"]);
        // The dispatched request retains all eight fields. Check the original five-field values on a copy as well.
        worker.Arguments = worker.Arguments.DeepClone().AsObject();
        Assert.True(worker.Arguments.Remove("scope")); Assert.True(worker.Arguments.Remove("scopePath"));
        Assert.True(worker.Arguments.Remove("unitName"));
        Assert
            .Equal("{\"softwarePath\":\"CPU\",\"password\":\"\",\"dryRun\":true,\"confirm\":false,\"expectedProjectFile\":\"\"}", worker.Arguments!
                .ToJsonString());
        Assert.DoesNotContain("compilable", response.StructuredContent!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CompilePlcSoftware", true)][InlineData("CompilePlcSoftware", false)]
    [InlineData("CompilePlcDiagnostics", true)][InlineData("CompilePlcDiagnostics", false)]
    public void Software_reply_accepts_explicit_default_scope_fields_without_requiring_scoped_target_evidence(string name, bool dryRun)
    {
        var arguments = new JsonObject { ["softwarePath"] = "CPU", ["scope"] = "software", ["scopePath"] = "", ["unitName"] = "",
            ["dryRun"] = dryRun, ["confirm"] = !dryRun, ["expectedProjectFile"] = Project };
        var result = JsonSerializer.SerializeToNode(new PlcCompileResult { Executed = !dryRun, ProjectFile = Project,
            State = dryRun ? null : "Success", ErrorCount = dryRun ? null : 0, WarningCount = dryRun ? null : 0 })!.AsObject();
        var original = V17CompileEnvelope.Wrap(name, result, dryRun, arguments);
        result["Scope"] = "software"; result["ScopePath"] = ""; result["UnitName"] = "";
        WorkerProtocol.ValidateExchangeResult("CompileSoftware", arguments, result);
        var explicitDefaults = V17CompileEnvelope.Wrap(name, result, dryRun, arguments);
        string meta = McpJsonUtilities.DefaultOptions.PropertyNamingPolicy!.ConvertName("Meta");
        Assert.NotNull(original[meta]!["timestamp"]); Assert.NotNull(explicitDefaults[meta]!["timestamp"]);
        original[meta]!["timestamp"] = explicitDefaults[meta]!["timestamp"]!.DeepClone();
        Assert.True(JsonNode.DeepEquals(original, explicitDefaults));
    }

    [Theory]
    [InlineData("CompilePlcSoftware")][InlineData("CompilePlcDiagnostics")]
    public async Task Software_execution_forwards_declared_scope_defaults_with_existing_confirmation(string name)
    {
        using var worker = new Worker(); var tool = Tool(name, worker);
        var arguments = new JsonObject { ["softwarePath"] = "CPU", ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = Project };
        var response = await tool.InvokeV4Async(Request(name, arguments), "21", "test", CancellationToken.None);
        Assert.False(response.IsError, response.StructuredContent?.ToJsonString()); Assert.Equal(1, worker.Calls);
        Assert.Equal(8, worker.Arguments!.Count); Assert.Equal("software", (string?)worker.Arguments["scope"]);
        Assert.Equal("", (string?)worker.Arguments["scopePath"]); Assert.Equal("", (string?)worker.Arguments["unitName"]);
        Assert.True((bool?)worker.Arguments["confirm"]); Assert.Equal(Project, (string?)worker.Arguments["expectedProjectFile"]);
    }

    [Theory]
    [InlineData("Scope", "block")][InlineData("ScopePath", "Main")][InlineData("UnitName", "Unit")]
    public void Software_reply_refuses_a_nondefault_scope_identity(string field, string value)
    {
        var arguments = new JsonObject { ["scope"] = "software", ["scopePath"] = "", ["unitName"] = "" };
        var result = JsonSerializer.SerializeToNode(new PlcCompileResult { ProjectFile = Project })!.AsObject();
        result["Scope"] = "software"; result["ScopePath"] = ""; result["UnitName"] = ""; result[field] = value;
        Assert.Throws<IOException>(() => V17CompileEnvelope.Validate(result, true, arguments));
    }

    [Theory]
    [InlineData("CompilePlcSoftware", true)][InlineData("CompilePlcDiagnostics", true)]
    [InlineData("CompilePlcSoftware", false)][InlineData("CompilePlcDiagnostics", false)]
    public async Task Scoped_request_reaches_worker_and_returns_target_with_no_plan_hash(string name, bool dryRun)
    {
        using var worker = new Worker(); var tool = Tool(name, worker);
        var args = new JsonObject { ["softwarePath"] = "devices/D/CPU", ["scope"] = "block", ["scopePath"] = "Group%2fone/Main",
            ["dryRun"] = dryRun, ["confirm"] = !dryRun, ["expectedProjectFile"] = dryRun ? "" : Project };
        var response = await tool.InvokeV4Async(Request(name, args), "21", "test", CancellationToken.None);
        Assert.False(response.IsError, response.StructuredContent?.ToJsonString()); Assert.Equal(1, worker.Calls);
        Assert.Equal("block", (string?)worker.Arguments?["scope"]); Assert.False(worker.Arguments!.ContainsKey("expectedPlanHash"));
        string text = response.StructuredContent!.ToJsonString(); Assert.Contains("devices/D/CPU/blocks/Group%2Fone/Main", text);
        Assert.Contains("compilable", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("blocks", "Main", "")][InlineData("block", "", "")][InlineData("group", "/G", "")]
    [InlineData("block", "G/../Main", "")][InlineData("unit", "U/G", "")][InlineData("unit", "U", "U")]
    [InlineData("software", "Main", "")][InlineData("software", "", "U")][InlineData("block", "Main", " U")]
    public void Invalid_scope_is_refused_before_dispatch(string scope, string path, string unit)
    {
        using var worker = new Worker(); var tool = Tool("CompilePlcSoftware", worker);
        var args = new JsonObject { ["softwarePath"] = "CPU", ["scope"] = scope, ["scopePath"] = path, ["unitName"] = unit };
        Assert.ThrowsAny<ArgumentException>(() => tool.ValidateApplyArguments(args.Deserialize<Dictionary<string, JsonElement>>()!));
        Assert.Equal(0, worker.Calls);
    }

    [Theory]
    [InlineData(false, "")][InlineData(true, "")][InlineData(false, Project)]
    public void Execution_keeps_confirmation_and_project_requirements(bool confirm, string expectedProject)
    {
        using var worker = new Worker(); var tool = Tool("CompilePlcSoftware", worker);
        var args = new JsonObject { ["softwarePath"] = "CPU", ["scope"] = "block", ["scopePath"] = "Main", ["dryRun"] = false,
            ["confirm"] = confirm, ["expectedProjectFile"] = expectedProject };
        Assert.Throws<ArgumentException>(() => tool.ValidateApplyArguments(args.Deserialize<Dictionary<string, JsonElement>>()!)); Assert
            .Equal(0, worker.Calls);
    }

    [Fact]
    public async Task Missing_scoped_service_returns_unsupported_capability_without_plan_hash()
    {
        using var worker = new Worker { ServiceAvailable = false }; var tool = Tool("CompilePlcSoftware", worker);
        var args = new JsonObject { ["softwarePath"] = "CPU", ["scope"] = "block", ["scopePath"] = "Group%2Fone/Main",
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = Project };
        var response = await tool.InvokeV4Async(Request("CompilePlcSoftware", args), "21", "test", CancellationToken.None);
        Assert.True(response.IsError); Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)response.StructuredContent?["error"]?["code"]);
        Assert.Equal(1, worker.Calls); Assert.False(worker.Arguments!.ContainsKey("expectedPlanHash"));
    }

    [Fact]
    public void Unavailable_scope_is_an_authored_admission_refusal_and_untyped_native_failures_stay_unknown()
    {
        var cause = PlcCompilePolicy.ScopeUnavailable("Exact group has no ICompilable.");
        var classified = WorkerFailurePolicy.Classify(cause, true, false);
        Assert.Equal(TiaMcp.WorkerChannel.ChannelOutcome.RejectedBeforeNative, classified.Outcome);
        var refused = WorkerOperationException.FromChannelFailure(new TiaMcp.WorkerChannel.ChannelFailureException(
            cause.Message, classified.Code, classified.Outcome, WorkerJson.Evidence(cause)));
        Assert.Equal(nameof(NotSupportedException), refused.ExceptionType); Assert.True(refused.KnownNoMutation);
        Assert.Equal(PlcCompileScope.UnavailableCondition, refused.Condition);
        var native = new NotSupportedException("Compiler failed after entering native code.");
        Assert.Equal(TiaMcp.WorkerChannel.ChannelOutcome.Unknown, WorkerFailurePolicy.Classify(native, true, false).Outcome);
        var unknown = WorkerOperationException.FromChannelFailure(new TiaMcp.WorkerChannel.ChannelFailureException(
            cause.Message, -32603, TiaMcp.WorkerChannel.ChannelOutcome.Unknown, WorkerJson.Evidence(cause)));
        Assert.Equal(nameof(AdapterPreconditionException), unknown.ExceptionType); Assert.False(unknown.KnownNoMutation);
    }

    [Theory]
    [InlineData("Scope", "group")][InlineData("ScopePath", "Other")][InlineData("UnitName", "Other")]
    [InlineData("ResolvedObject", "devices/D/CPU/blocks/Other")][InlineData("Compilable", false)][InlineData("ApplyBlocked", true)]
    public void Worker_scope_or_capability_substitution_is_rejected(string field, object value)
    {
        var args = new JsonObject { ["scope"] = "block", ["scopePath"] = "Group%2fone/Main", ["unitName"] = "" };
        var result = JsonSerializer.SerializeToNode(new PlcScopedCompileResult { Scope = "block", ScopePath = "Group%2Fone/Main", UnitName = "",
            ResolvedObject = "devices/D/CPU/blocks/Group%2Fone/Main", Compilable = true })!.AsObject();
        result[field] = JsonSerializer.SerializeToNode(value);
        Assert.Throws<IOException>(() => V17CompileEnvelope.Validate(result, true, args));
    }

    [Fact]
    public void Unavailable_service_preview_reports_false_and_apply_is_refused()
    {
        var args = new JsonObject { ["scope"] = "unit", ["scopePath"] = "Unit%2fone", ["unitName"] = "" };
        var result = JsonSerializer.SerializeToNode(new PlcScopedCompileResult { Scope = "unit", ScopePath = "Unit%2Fone", UnitName = "", ProjectFile = Project,
            ResolvedObject = "devices/D/CPU/units/Unit%2Fone", Compilable = false })!.AsObject();
        var preview = V17CompileEnvelope.Wrap("CompilePlcSoftware", result, true, args);
        string Wire(string name) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy!.ConvertName(name);
        Assert.False((bool?)preview[Wire("Compilable")]); Assert.True((bool?)preview[Wire("ApplyBlocked")]);
        result["Executed"] = true; result["State"] = "Success"; result["ErrorCount"] = 0; result["WarningCount"] = 0;
        Assert.Throws<IOException>(() => V17CompileEnvelope.Validate(result, false, args));
    }
}
