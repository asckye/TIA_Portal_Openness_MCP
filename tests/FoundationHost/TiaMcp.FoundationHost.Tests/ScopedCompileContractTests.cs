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
using TiaMcp.WorkerChannel;
using TiaOpenness.Shared;
using Xunit;

public sealed class ScopedCompileContractTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public ScopedCompileContractTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData("CompilePlcSoftware", "software")]
    [InlineData("CompilePlcDiagnostics", "software")]
    [InlineData("CompilePlcSoftware", "unit")]
    [InlineData("CompilePlcDiagnostics", "unit")]
    public async Task Incomplete_diagnostics_are_visible_in_both_host_envelopes(string tool, string scope)
    {
        const string warning = "Compile returned its state and counts, but the diagnostic messages could not be read.";
        var args = new JsonObject { ["softwarePath"] = "devices/D/CPU", ["dryRun"] = false, ["confirm"] = true,
            ["expectedProjectFile"] = Project, ["scope"] = scope };
        if (scope == "unit") args["scopePath"] = "Line";
        using var worker = new Worker();
        var native = (await worker.Call("CompileSoftware", args, CancellationToken.None))!.AsObject();
        if (scope == "unit") native["ResolvedObject"] = "devices/D/CPU/units/Line";
        native["State"] = "Error"; native["ErrorCount"] = 3; native["WarningCount"] = 2;
        native["DiagnosticsIncomplete"] = true;
        native["Warnings"] = new JsonArray(warning);
        native["Messages"] = new JsonArray(warning);
        var legacy = V17CompileEnvelope.Wrap(tool, native, false, args);
        string Wire(string field) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy!.ConvertName(field);
        Assert.False((bool?)legacy[Wire("Meta")]?["dataComplete"]);
        Assert.Contains(warning, (string?)legacy[Wire("Message")]);
        var definition = FoundationTools.Definitions.Single(d => d.Name == tool);
        var body = FoundationV4Result.Worker("20", definition, "fixture", args, native, legacy).StructuredContent!;
        Assert.Equal("failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("partial", (string?)body["meta"]?["completeness"]);
        Assert.Contains("3 errors and 2 warnings", (string?)body["error"]?["message"]);
        Assert.Contains("individual error", (string?)body["error"]?["message"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(warning, body["data"]!.ToJsonString());
    }

    [Theory, InlineData("CompilePlcSoftware"), InlineData("CompilePlcDiagnostics")]
    public async Task Block_compile_reports_errors_from_other_blocks_in_the_envelope(string tool)
    {
        var args = new JsonObject { ["softwarePath"] = "devices/D/CPU", ["scope"] = "block", ["scopePath"] = "Main", ["dryRun"] = false };
        using var worker = new Worker();
        var native = (await worker.Call("CompileSoftware", args, CancellationToken.None))!.AsObject();
        native["ResolvedObject"] = "devices/D/CPU/blocks/Main";
        native["ErrorCount"] = 1;
        native["State"] = "Error";
        native["OtherErrors"] = new JsonArray("Helper: bad operand");
        var legacy = V17CompileEnvelope.Wrap(tool, native, false, args);
        Assert.Equal("Helper: bad operand", (string?)legacy["otherErrors"]?[0]);
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task Typed_diagnostic_status_takes_precedence_over_warning_text(bool incomplete)
    {
        using var worker = new Worker();
        var args = new JsonObject { ["softwarePath"] = "CPU", ["dryRun"] = false };
        var native = (await worker.Call("CompileSoftware", args, CancellationToken.None))!.AsObject();
        native["DiagnosticsIncomplete"] = incomplete;
        native["Warnings"] = new JsonArray(incomplete ? "Fixture diagnostic unavailable." : PlcCompileResult.IncompleteDiagnostics);
        var legacy = V17CompileEnvelope.Wrap("CompilePlcSoftware", native, false, args);
        Assert.Equal(incomplete, (bool?)legacy["meta"]?["dataComplete"] == false);
        var definition = FoundationTools.Definitions.Single(d => d.Name == "CompilePlcSoftware");
        var result = FoundationV4Result.Worker("20", definition, "fixture", args, native, legacy).StructuredContent!;
        Assert.Equal(incomplete ? "partial" : "complete", (string?)result["meta"]?["completeness"]);
    }

    [Fact]
    public void Unreadable_error_count_reports_the_returned_error_state()
    {
        var data = new JsonObject { ["errorCount"] = 0, ["state"] = "Error", ["complete"] = false };
        var error = TiaMcp.Logic.V4.CompileResultMapping.Errors(data)!;
        Assert.Contains("reported Error", error.Message);
        Assert.DoesNotContain("0 errors", error.Message);
    }

    [Theory, InlineData("unchanged", false), InlineData("changed", true)]
    public void Instance_db_refusal_retains_the_observed_mutation_through_the_worker(string scenario, bool changed)
    {
        var reply = SafetyLoginFrameworkTests.RunFixture("instance-db", scenario);
        Assert.Equal(1, (int?)reply["CreateCalls"]);
        var result = reply["result"]!;
        Assert.Contains("CreateInstanceDB refused", (string?)result["Failure"]?["Message"]);
        Assert.Equal(changed, (bool?)result["MayHaveChanged"]);
        Assert.Equal(changed, (bool?)result["OutcomeUnknown"]);
    }

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

    [Theory]
    [InlineData("CompilePlcSoftware")] [InlineData("CompilePlcDiagnostics")]
    public async Task Compile_approval_binds_the_password_only_for_execution(string name)
    {
        foreach (bool enabled in new[] { false, true })
            foreach (string? password in new[] { null, "", "safety-fixture" })
                foreach (bool preview in new[] { false, true })
                {
                    using var worker = new Worker();
                    var args = new JsonObject { ["softwarePath"] = "devices/D/CPU", ["dryRun"] = preview,
                        ["confirm"] = !preview, ["expectedProjectFile"] = Project };
                    if (password != null) args["password"] = password;
                    int decisions = 0;
                    var tool = new FoundationV4Tool(Tool(name, worker), "21", () => new ApprovalSettings(enabled, 2),
                        (pending, settings, _) =>
                        {
                            if (!settings.Enabled) return Task.FromResult(new ApprovalOutcome(pending, true, null));
                            decisions++;
                            Assert.Equal("••••", (string?)JsonNode.Parse(pending.ParametersJson)!["password"]);
                            var changed = args.DeepClone().AsObject();
                            changed["password"] = "another-fixture";
                            var other = PendingApproval.Create(pending.Host, pending.ReleaseKey, name, changed.ToJsonString(), null, 2);
                            var same = PendingApproval.Create(pending.Host, pending.ReleaseKey, name, args.ToJsonString(), null, 2);
                            Assert.Equal(same.ArgumentDigest, pending.ArgumentDigest);
                            Assert.NotEqual(other.ArgumentDigest, pending.ArgumentDigest);
                            return Task.FromResult(new ApprovalOutcome(pending, false, "denied"));
                        });
                    var body = (await tool.InvokeAsync(Request(name, args))).StructuredContent!;
                    bool denied = enabled && !preview && !string.IsNullOrEmpty(password);
                    Assert.Equal(denied ? 1 : 0, decisions);
                    Assert.Equal(!denied, (bool?)body["ok"]);
                    if (denied) Assert.Equal("CONFIRMATION_REQUIRED", (string?)body["error"]?["code"]);
                    Assert.Equal(1, worker.Calls);
                    Assert.Equal(preview || denied, (bool?)worker.Arguments!["dryRun"]);
                    Assert.Equal(password ?? "", (string?)worker.Arguments["password"]);
                }
    }

    [Fact]
    public async Task Changing_the_password_while_approval_is_pending_refuses_execution()
    {
        const string name = "CompilePlcSoftware";
        using var worker = new Worker();
        var request = Request(name, new JsonObject { ["softwarePath"] = "CPU", ["password"] = "first",
            ["dryRun"] = false, ["confirm"] = true, ["expectedProjectFile"] = Project });
        var arguments = Assert.IsType<Dictionary<string, JsonElement>>(request.Params!.Arguments);
        var tool = new FoundationV4Tool(Tool(name, worker), "21", () => new ApprovalSettings(true, 2), (pending, _, _) =>
        {
            arguments["password"] = JsonSerializer.SerializeToElement("second");
            return Task.FromResult(new ApprovalOutcome(pending, false, null));
        });
        var result = (await tool.InvokeAsync(request)).StructuredContent!;
        Assert.Equal("CONFIRMATION_REQUIRED", (string?)result["error"]?["code"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
        Assert.Equal(1, worker.Calls);
        Assert.True((bool?)worker.Arguments!["dryRun"]);
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")] [InlineData("17")]
    [InlineData("18")] [InlineData("19")] [InlineData("20")] [InlineData("21")]
    public async Task Granted_compile_uses_the_reviewed_password_on_supported_releases(string release)
    {
        foreach (string name in new[] { "CompilePlcSoftware", "CompilePlcDiagnostics" })
        {
            using var worker = new Worker();
            var definition = FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == name), release);
            var args = new JsonObject { ["softwarePath"] = "CPU", ["dryRun"] = false, ["confirm"] = true,
                ["expectedProjectFile"] = Project, ["password"] = "fixture" };
            var native = new FoundationTool(definition, worker);
            Assert.True(TiaMcp.Logic.V4.HostBehavior.ApprovalWrite(name, args, native.IsWrite, hasDryRun: true, defaultPreview: true));
            if (release is "14sp1" or "15.1" or "16")
            {
                Assert.Throws<NotSupportedException>(() => PlcCompilePolicy.RequirePasswordCapability(release, "fixture"));
                continue;
            }
            int grants = 0;
            var tool = new FoundationV4Tool(native, release, () => new ApprovalSettings(true, 2), (pending, settings, _) =>
            {
                Assert.True(settings.Enabled);
                Assert.True((bool?)worker.Arguments!["dryRun"]);
                grants++;
                return Task.FromResult(new ApprovalOutcome(pending, false, null));
            });
            var body = (await tool.InvokeAsync(Request(name, args))).StructuredContent!;
            Assert.True((bool?)body["ok"], body.ToJsonString());
            Assert.Equal(1, grants);
            Assert.Equal(2, worker.Calls);
            Assert.False((bool?)worker.Arguments!["dryRun"]);
            Assert.Equal("fixture", (string?)worker.Arguments["password"]);
        }
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")]
    public void Unsupported_compile_password_is_classified_as_a_capability_refusal(string release)
    {
        var cause = Assert.Throws<NotSupportedException>(() => PlcCompilePolicy.RequirePasswordCapability(release, "fixture"));
        Assert.Contains(TiaMcp.Versioning.TiaVersionCatalog.Get(release).DisplayName, cause.Message);
        Assert.Contains($"V{TiaMcp.Adapters.Contracts.OpennessApi.SafetyPasswordCompile.FirstMajor} or later", cause.Message);
        var failure = WorkerFailurePolicy.Classify(cause, true, false);
        Assert.Equal(TiaMcp.WorkerChannel.ChannelOutcome.RejectedBeforeNative, failure.Outcome);
        var reported = WorkerOperationException.FromChannelFailure(new TiaMcp.WorkerChannel.ChannelFailureException(
            cause.Message, failure.Code, failure.Outcome, WorkerJson.Evidence(cause)));
        Assert.True(reported.CapabilityRefused);
    }

    [Theory]
    [InlineData("14sp1")] [InlineData("15.1")] [InlineData("16")]
    public async Task Unsupported_compile_password_is_refused_before_approval_or_dispatch(string release)
    {
        foreach (string name in new[] { "CompilePlcSoftware", "CompilePlcDiagnostics" })
        foreach (bool preview in new[] { true, false })
        {
            using var worker = new Worker();
            var native = new FoundationTool(FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == name), release), worker);
            var tool = new FoundationV4Tool(native, release, () => new ApprovalSettings(true, 2),
                (_, _, _) => throw new InvalidOperationException("Unexpected approval request."));
            var args = new JsonObject { ["softwarePath"] = "CPU", ["dryRun"] = preview, ["confirm"] = true,
                ["expectedProjectFile"] = Project, ["password"] = "fixture" };
            var body = (await tool.InvokeAsync(Request(name, args))).StructuredContent!;
            Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)body["error"]?["code"]);
            Assert.Contains(release, (string?)body["error"]?["message"]);
            Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
            Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
            Assert.Empty(native.Unsettled.Snapshot());
            Assert.Equal(0, worker.Calls);
        }
    }

    [Fact]
    public void Block_scope_keeps_errors_from_other_blocks()
    {
        var result = new PlcScopedCompileResult { Scope = "block", ErrorCount = 2 };
        PlcCompilePolicy.Classify(result, new[] {
            new PlcDiagnostic { State = "Error", Formatted = "Main: tag missing", InRequestedBlock = true },
            new PlcDiagnostic { State = "Error", Formatted = "Helper: bad operand" }
        }, blockScope: true);
        Assert.Equal(new[] { "Main: tag missing" }, result.Errors);
        Assert.Equal(new[] { "Helper: bad operand" }, result.OtherErrors);
        Assert.Equal(2, result.ErrorCount);
    }

    [Theory]
    [InlineData("17")][InlineData("18")][InlineData("19")][InlineData("20")][InlineData("21")]
    public async Task Compile_login_failure_preserves_the_worker_verdict_and_protects_credentials(string release)
    {
        const string password = "fixture-login-password";
        const string nativeText = "private vendor login detail";
        string root = Path.Combine(AppContext.BaseDirectory, "compile-login-audit", Guid.NewGuid().ToString("N"));
        var log = new AuditLog(root);
        using var audit = AuditInvocation.UseLog(log);
        var rows = new List<string>();
        TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(rows.Add);
        try
        {
            foreach (string name in new[] { "CompilePlcSoftware", "CompilePlcDiagnostics" })
            foreach (bool? readback in new bool?[] { false, true, null })
            {
                using var worker = new Worker { ApplyScenario = readback switch { false => "off", true => "on", null => "unreadable" } };
                var native = new FoundationTool(FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == name), release), worker);
                var tool = new FoundationV4Tool(native, release, () => new ApprovalSettings(false, 2));
                var args = new JsonObject { ["softwarePath"] = "CPU", ["dryRun"] = false, ["confirm"] = true,
                    ["expectedProjectFile"] = Project, ["password"] = password };
                var body = (await tool.InvokeAsync(Request(name, args))).StructuredContent!;
                bool denied = readback == false;
                Assert.Equal(denied ? "ACCESS_DENIED" : "OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
                Assert.Equal(denied ? "rejected-before-operation" : "unknown", (string?)body["meta"]?["outcome"]);
                Assert.Equal(denied ? "not-started" : "unknown", (string?)body["meta"]?["execution"]);
                Assert.Equal(denied ? 0 : 1, native.Unsettled.Snapshot().Count);
                Assert.Equal(denied ? 0 : 1, worker.Dispatched!["calls"]!.AsArray().Count(call => (string?)call == "logoff"));
                Assert.Equal(0, (int?)worker.Dispatched["compileCalls"]);
                var evidence = body["data"]!["evidence"]!;
                Assert.False((bool?)evidence["compileStarted"]);
                Assert.Equal(typeof(UnauthorizedAccessException).FullName, (string?)evidence["exceptionType"]);
                Assert.Equal(readback, (bool?)evidence["loggedOnAfterFailure"]);
                if (denied) Assert.Equal("Safety offline login failed; nothing was compiled", (string?)body["error"]?["message"]);
                else Assert.Contains(readback == true ? "was logged on" : "could not be read", (string?)body["error"]?["message"]);
                Assert.DoesNotContain(password, body.ToJsonString());
                Assert.DoesNotContain(nativeText, body.ToJsonString());
            }
            Assert.NotEmpty(rows);
            Assert.NotEmpty(log.Read());
            string recorded = string.Join("\n", rows.Concat(Directory.GetFiles(root, "*.jsonl").Select(File.ReadAllText)));
            Assert.DoesNotContain(password, recorded);
            Assert.DoesNotContain(nativeText, recorded);
        }
        finally
        {
            TiaMcpServer.ModelContextProtocol.InvocationJournal.ConfigureOutput(null);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Unit_scope_begins_at_V16()
    {
        Assert.Throws<AdapterPreconditionException>(() => PlcCompilePolicy.RequireScope("15.1", "unit", "Unit", ""));
        Assert.Equal(new[] { "Unit" }, PlcCompilePolicy.RequireScope("16", "unit", "Unit", ""));
    }

    [Theory]
    [InlineData("CompilePlcSoftware")][InlineData("CompilePlcDiagnostics")]
    public async Task Compile_exception_after_login_keeps_the_original_unknown_outcome(string name)
    {
        foreach (string release in new[] { "17", "18", "19", "20", "21" })
        {
            using var worker = new Worker { ApplyScenario = "success" };
            var native = new FoundationTool(FoundationTools.ForRelease(FoundationTools.Definitions.Single(d => d.Name == name), release), worker);
            var tool = new FoundationV4Tool(native, release, () => new ApprovalSettings(false, 2));
            var args = new JsonObject { ["softwarePath"] = "CPU", ["password"] = "fixture-login-password", ["dryRun"] = false,
                ["confirm"] = true, ["expectedProjectFile"] = Project };
            var body = (await tool.InvokeAsync(Request(name, args))).StructuredContent!;
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Equal("tool-call", (string?)body["error"]?["details"]?["stage"]);
            Assert.Null(body["data"]?["evidence"]?["compileStarted"]);
            Assert.Equal(1, (int?)worker.Dispatched!["compileCalls"]);
            Assert.Single(native.Unsettled.Snapshot());
        }
    }

    [Theory]
    [InlineData("CompilePlcSoftware")][InlineData("CompilePlcDiagnostics")]
    public async Task Lost_connection_during_login_leaves_the_compile_unsettled(string name)
    {
        using var worker = new Worker { ApplyScenario = "login-com" };
        var native = Tool(name, worker);
        var tool = new FoundationV4Tool(native, "21", () => new ApprovalSettings(false, 2));
        var args = new JsonObject { ["softwarePath"] = "CPU", ["password"] = "fixture-login-password", ["dryRun"] = false,
            ["confirm"] = true, ["expectedProjectFile"] = Project };
        var body = (await tool.InvokeAsync(Request(name, args))).StructuredContent!;
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
        Assert.Contains("connection was lost", (string?)body["error"]?["message"]);
        Assert.False((bool?)body["data"]?["evidence"]?["compileStarted"]);
        Assert.True((bool?)body["data"]?["evidence"]?["connectionLost"]);
        Assert.Equal(0, (int?)worker.Dispatched!["compileCalls"]);
        Assert.Single(native.Unsettled.Snapshot());
    }

    private const string Project = "C:/Projects/Scoped.ap21";
    private sealed class Worker : IFoundationWorker
    {
        internal JsonObject? Arguments;
        internal int Calls;
        internal bool ServiceAvailable = true;
        internal bool ForeignRoots;
        internal string? ApplyScenario;
        internal JsonObject? Dispatched;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Assert.Equal("CompileSoftware", operation); Calls++; Arguments = arguments.DeepClone().AsObject();
            bool dry = (bool?)arguments["dryRun"] ?? true;
            if (!dry && (ApplyScenario != null || !ServiceAvailable))
            {
                Dispatched = SafetyLoginFrameworkTests.RunFixture("foundation", ApplyScenario ?? "scope-unavailable", arguments);
                throw WorkerOperationException.FromChannelFailure(new ChannelFailureException((string)Dispatched["message"]!, (int)Dispatched["code"]!,
                    Enum.Parse<ChannelOutcome>((string)Dispatched["outcome"]!), Dispatched["evidence"]!.ToJsonString()));
            }
            PlcCompileResult result = new PlcCompileResult();
            string scope = (string?)arguments["scope"] ?? "software";
            if (scope != "software") result = new PlcScopedCompileResult { Scope = scope,
                ScopePath = ObjectPathEncoding.NormalizePath((string)arguments["scopePath"]!), UnitName = (string?)arguments["unitName"] ?? "",
                ResolvedObject = "devices/D/CPU/blocks/" + ObjectPathEncoding.NormalizePath((string)arguments["scopePath"]!), Compilable = ServiceAvailable,
                CompiledRoots = dry ? Array.Empty<string>() : ForeignRoots ? new[] { "+S1", "+S1-K1" } : new[] { "D", "CPU" }, ScopeMismatch = ForeignRoots };
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
        Assert.Equal(dryRun ? Array.Empty<string>() : new[] { "D", "CPU" }, response.StructuredContent["data"]!["compiledRoots"]!.Deserialize<string[]>());
    }

    [Theory]
    [InlineData("CompilePlcSoftware", "block")][InlineData("CompilePlcSoftware", "group")]
    [InlineData("CompilePlcDiagnostics", "block")][InlineData("CompilePlcDiagnostics", "group")]
    public async Task Executed_compile_reports_foreign_roots_even_when_native_counts_are_clean(string name, string scope)
    {
        using var worker = new Worker { ForeignRoots = true };
        var args = new JsonObject { ["softwarePath"] = "devices/D/CPU", ["scope"] = scope,
            ["scopePath"] = scope == "group" ? "Group%2fone" : "Group%2fone/Main", ["dryRun"] = false,
            ["confirm"] = true, ["expectedProjectFile"] = Project };
        var reply = (await Tool(name, worker).InvokeV4Async(Request(name, args), "21", "test", CancellationToken.None)).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)reply["error"]?["code"]);
        Assert.Equal("compile-scope-mismatch", (string?)reply["error"]?["details"]?["condition"]);
        Assert.True((bool?)reply["data"]?["executed"]); Assert.Equal(0, (int?)reply["data"]?["errorCount"]);
        Assert.Equal(new[] { "+S1", "+S1-K1" }, reply["data"]!["compiledRoots"]!.Deserialize<string[]>());
        Assert.Equal("completed", (string?)reply["meta"]?["execution"]); Assert.Equal(1, worker.Calls);
        var raw = await worker.Call("CompileSoftware", args, CancellationToken.None);
        var legacy = V17CompileEnvelope.Wrap(name, raw, false, args);
        string Wire(string field) => McpJsonUtilities.DefaultOptions.PropertyNamingPolicy!.ConvertName(field);
        Assert.False((bool?)legacy[Wire("Meta")]?["success"]);
        Assert.Equal("compile-scope-mismatch", (string?)legacy[Wire("Meta")]?["condition"]);
        Assert.Equal(new[] { "+S1", "+S1-K1" }, legacy[Wire("CompiledRoots")]!.Deserialize<string[]>());
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

    [Theory]
    [InlineData("block", "Group%2Fone/Main")]
    [InlineData("group", "Group%2Fone")]
    public async Task Missing_compiled_roots_after_native_compile_remain_unknown(string scope, string path)
    {
        using var worker = new Worker { ApplyScenario = "scoped-messages-unreadable" };
        var args = new JsonObject { ["softwarePath"] = "CPU", ["scope"] = scope, ["scopePath"] = path, ["dryRun"] = false,
            ["confirm"] = true, ["expectedProjectFile"] = Project };
        const string tool = "CompilePlcDiagnostics";
        var body = (await Tool(tool, worker).InvokeV4Async(Request(tool, args), "21", "fixture", CancellationToken.None)).StructuredContent!;
        Assert.Equal(1, (int?)worker.Dispatched?["compileCalls"]);
        Assert.Equal("Unknown", (string?)worker.Dispatched?["outcome"]);
        Assert.Equal("unknown", (string?)body["meta"]?["outcome"]);
        Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
        Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
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
