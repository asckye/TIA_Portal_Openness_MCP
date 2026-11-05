extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

public sealed class RoundTwoRegressionTests
{
    public static IEnumerable<object[]> ToolFailures()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (string tool in new[] { "BuildProjectScaffold", "SaveProjectCopy" })
        foreach (bool timeout in new[] { false, true })
            yield return new object[] { release, tool, timeout };
    }

    [Theory, MemberData(nameof(ToolFailures))]
    public async Task Earlier_mutation_keeps_later_refusal_or_read_timeout_unknown(string release, string name, bool timeout)
    {
        using var worker = new FlowWorker(name, timeout, false, release);
        var inner = Tool(worker, release, name);
        var tool = new FoundationV4Tool(inner, release, () => new(false, 1));
        var result = await tool.InvokeAsync(Request(name, Arguments(name, tool.ProtocolTool.InputSchema)));
        bool supported = release != "14sp1" || name != "SaveProjectCopy";
        Assert.Equal(supported ? name == "SaveProjectCopy" ? 1 : 2 : 0, worker.Mutations);
        Assert.Equal(supported ? "unknown" : "rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal(supported ? "unknown" : "not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.Equal(supported ? "OUTCOME_UNKNOWN" : "UNSUPPORTED_CAPABILITY", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal(supported, (bool?)result.StructuredContent?["data"]?["earlierMutationMayHaveChanged"] ?? false);
        Assert.Equal(supported, (bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.Equal(supported, inner.SessionRequiresReset);
    }

    public static IEnumerable<object[]> FirstRefusals()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (string tool in new[] { "BuildProjectScaffold", "SaveProjectCopy" })
            yield return new object[] { release, tool };
    }

    [Theory, MemberData(nameof(FirstRefusals))]
    public async Task First_mutation_refusal_keeps_the_tool_unstarted_and_unlocked(string release, string name)
    {
        using var worker = new FlowWorker(name, false, true, release);
        var inner = Tool(worker, release, name);
        var tool = new FoundationV4Tool(inner, release, () => new(false, 1));
        var result = await tool.InvokeAsync(Request(name, Arguments(name, tool.ProtocolTool.InputSchema)));
        Assert.Equal(0, worker.Mutations);
        Assert.Equal("rejected-before-operation", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
        Assert.False((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.False(inner.SessionRequiresReset);
    }

    public static IEnumerable<object[]> CollectedSourceFailures()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (bool continueAfterRefusal in new[] { false, true })
            yield return new object[] { release, continueAfterRefusal };
    }

    [Theory, MemberData(nameof(CollectedSourceFailures))]
    public async Task Scaffold_collected_source_refusal_keeps_its_step_report(string release, bool continueAfterRefusal)
    {
        using var worker = new FlowWorker("BuildProjectScaffold", false, false, release, collectedSourceRefusal: true);
        var inner = Tool(worker, release, "BuildProjectScaffold");
        var tool = new FoundationV4Tool(inner, release, () => new(false, 1));
        var args = Arguments("BuildProjectScaffold", tool.ProtocolTool.InputSchema);
        var spec = JsonNode.Parse(args["spec"]!.ToString())!.AsObject();
        spec["sclSourceFiles"] = continueAfterRefusal ? new JsonArray("bad.scl", "good.scl") : new JsonArray("bad.scl");
        args["spec"] = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("spec").GetProperty("type").GetString() == "string"
            ? JsonValue.Create(spec.ToJsonString()) : spec;
        var result = await tool.InvokeAsync(Request("BuildProjectScaffold", args));
        Assert.Equal(continueAfterRefusal ? 5 : 3, worker.Mutations);
        Assert.False((bool?)result.StructuredContent?["data"]?["ok"]);
        var steps = Assert.IsType<JsonArray>(result.StructuredContent?["data"]?["steps"]);
        Assert.Contains(steps, step => (string?)step?["step"] == "createProject" && (string?)step?["status"] == "ok");
        Assert.Contains(steps, step => (string?)step?["step"] == "scl" && (string?)step?["status"] == "failed");
        Assert.Equal(continueAfterRefusal, steps.Any(step => (string?)step?["step"] == "scl" && (string?)step?["status"] == "ok"));
        Assert.Equal("unknown", (string?)result.StructuredContent?["meta"]?["outcome"]);
        Assert.True((bool?)result.StructuredContent?["meta"]?["requiresSessionReset"]);
        Assert.True(inner.SessionRequiresReset);
    }

    [Theory, InlineData("not-a-timestamp"), InlineData("2026-11-04T12:00:00")]
    public void Malformed_or_offset_free_since_is_an_argument_error(string value)
    {
        using var session = EngineHostParity.EnterScope();
        var result = new Host.EngineeringDiagnosticsTools().GetNativeInvocationLogV4(sinceUtc: value);
        Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent?["error"]?["code"]);
        Assert.Equal("sinceUtc", (string?)result.StructuredContent?["error"]?["details"]?["parameter"]);
    }

    [Theory, InlineData("2026-11-04T12:00:00Z"), InlineData("2026-11-04T13:00:00+01:00")]
    public void Explicit_offsets_are_accepted(string value)
        => Assert.True((bool?)new Host.EngineeringDiagnosticsTools().GetNativeInvocationLogV4(take: 1, sinceUtc: value).StructuredContent?["ok"]);

    [Theory, InlineData(false), InlineData(true)]
    public void Lost_read_reply_stays_a_read_failure(bool timeout)
    {
        var cause = new IOException("Fixture lost read reply.");
        cause.Data["foundationRequestSent"] = true;
        cause.Data["workerOutcomeUnknown"] = true;
        if (timeout) cause.Data["workerReadTimeout"] = true;
        var body = Host.PlcExchangeContract.Run("ManagePlcTagDefinition", () => throw cause, false, true).StructuredContent!;
        Assert.Equal("read-failed", (string?)body["meta"]?["outcome"]);
        Assert.Equal("read-only", (string?)body["meta"]?["execution"]);
        Assert.Equal(timeout ? "TIMEOUT" : "NATIVE_OPERATION_FAILED", (string?)body["error"]?["code"]);
    }

    private static FoundationTool Tool(IFoundationWorker worker, string release, string name)
    {
        var registered = PortedToolContract.Create(worker, release).OfType<FoundationTool>().SingleOrDefault(t => t.ProtocolTool.Name == name);
        bool supported = release != "14sp1" || name != "SaveProjectCopy";
        Assert.Equal(supported, registered != null);
        // V14 SP1 has no Save As API; exercise the real capability refusal without publishing that tool.
        return registered ?? new FoundationTool(worker, release,
            typeof(Host.ProjectLifecycleProjectTools).GetMethod("SaveProjectCopyV4")!, name);
    }

    private static JsonObject Arguments(string name, JsonElement schema)
    {
        if (name == "SaveProjectCopy") return new() { ["newProjectPath"] = Path.Combine(AppContext.BaseDirectory, "copy") };
        var spec = new JsonObject { ["projectName"] = "Fixture", ["directoryPath"] = AppContext.BaseDirectory,
            ["plcMlfb"] = "6ES7 516-3AN02-0AB0", ["plcVersion"] = "V2.9", ["compile"] = false, ["save"] = false };
        return new() { ["spec"] = schema.GetProperty("properties").GetProperty("spec").GetProperty("type").GetString() == "string"
            ? JsonValue.Create(spec.ToJsonString()) : spec, ["dryRun"] = false };
    }

    private static RequestContext<CallToolRequestParams> Request(string name, JsonObject args) => new(
        DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
            Params = new() { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.ToJsonString()) } };

    private sealed class FlowWorker(string tool, bool timeout, bool firstRefusal, string release, bool collectedSourceRefusal = false)
        : IFoundationWorker
    {
        internal int Mutations;
        private int bindingReads, sourceImports;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["IsAttached"] = tool == "SaveProjectCopy", ["ProcessId"] = 123, ["ProjectFile"] = "C:/fixture.ap" + release });
            if (operation == "project-lifecycle.ReadProjectLifecycleBinding")
            {
                if (++bindingReads > 1) throw Failure();
                return Task.FromResult<JsonNode?>(new JsonObject { ["identity"] = new JsonObject { ["projectPath"] = "C:/fixture.ap" + release } });
            }
            if (operation == "project-lifecycle.ArchiveSaveProjectCopy")
            {
                if (firstRefusal) throw Failure();
                Mutations++;
                return Task.FromResult<JsonNode?>(JsonValue.Create(true));
            }
            if (operation == "project-lifecycle.ExecuteProjectScaffoldPrimitive")
            {
                string? kind = (string?)arguments["request"]?["Kind"];
                if (firstRefusal || !collectedSourceRefusal && kind == "addDevice") throw Failure();
                if (collectedSourceRefusal && kind == "sclImport" && ++sourceImports == 1) throw Failure();
                Mutations++;
                if (kind == "addDevice") return Task.FromResult<JsonNode?>(new JsonObject {
                    ["Ok"] = true, ["DeviceName"] = "PLC_1", ["Meta"] = new JsonObject { ["success"] = true } });
                return Task.FromResult<JsonNode?>(new JsonObject { ["Message"] = "Fixture completed.",
                    ["Meta"] = new JsonObject { ["success"] = true } });
            }
            throw new InvalidOperationException("Unexpected fixture operation: " + operation);
        }
        private WorkerOperationException Failure()
        {
            var failure = new WorkerOperationException("Fixture refusal.", -32603,
                timeout && Mutations > 0 ? "read-failed" : "rejected-before-operation");
            if (timeout && Mutations > 0) failure.Data["workerReadTimeout"] = true;
            return failure;
        }
        public void Dispose() { }
    }
}
