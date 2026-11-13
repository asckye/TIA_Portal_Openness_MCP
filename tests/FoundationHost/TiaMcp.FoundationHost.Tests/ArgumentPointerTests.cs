extern alias enginehost;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using Xunit;
using Engine = enginehost::TiaMcp.FoundationHost;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;

public sealed class ArgumentPointerTests
{
    [Theory]
    [InlineData("20", "token=sk-live")]
    [InlineData("21", "token=sk-live")]
    [InlineData("20", "123456")]
    [InlineData("21", "123456")]
    public async Task Production_private_field_evidence_hides_unquoted_data(string release, string key)
    {
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var worker = new PrivateFieldEvidenceWorker(key);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        using var pipeline = new Engine.EngineHostPipeline(release, contextWorker, tools);
        using var session = pipeline.EnterSession();
        var tool = tools.Single(t => t.ProtocolTool.Name == "SetUnifiedObjectProperties");
        var arguments = new JsonObject {
            ["softwarePath"] = "HMI",
            ["objectPath"] = new JsonArray(new JsonObject { ["property"] = "Screens", ["name"] = "Main" }),
            ["properties"] = new JsonObject { [key] = true }, ["dryRun"] = true };
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = tool.ProtocolTool.Name,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } });
        var body = result.StructuredContent!;
        string expectedEvidence = key == "token=sk-live" ? "Field token=•••• was refused." : "Input does not satisfy the declared contract.";
        Assert.Equal(1, worker.Calls);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
        Assert.Equal("The request could not be admitted; inspect the retained evidence.", (string?)body["error"]!["message"]);
        Assert.DoesNotContain(key, body.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, ((TextContentBlock)result.Content.Single()).Text, StringComparison.Ordinal);
        Assert.Equal(expectedEvidence, (string?)body["data"]!["evidence"]!["message"]);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
    }

    private sealed class PrivateFieldEvidenceWorker(string key) : IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap21" });
            Calls++;
            string message = "Field " + key + " was refused.";
            return Task.FromResult<JsonNode?>(new JsonObject { ["Value"] = new JsonObject {
                ["Message"] = message, ["Meta"] = new JsonObject {
                    ["v4Rejection"] = "INVALID_ARGUMENT", ["rejectedParameter"] = key,
                    ["evidence"] = new JsonObject { ["message"] = message } } } });
        }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Production_property_error_preserves_ordinary_data_words(string release)
    {
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var worker = new VisiblePropertyWorker();
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        using var pipeline = new Engine.EngineHostPipeline(release, contextWorker, tools);
        using var session = pipeline.EnterSession();
        var tool = tools.Single(t => t.ProtocolTool.Name == "SetUnifiedObjectProperties");
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = tool.ProtocolTool.Name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                "{\"softwarePath\":\"HMI\",\"objectPath\":[{\"property\":\"Screens\",\"name\":\"Main\"}],"
                    + "\"properties\":{\"Visible\":false},\"dryRun\":true}") } });
        var body = result.StructuredContent!;
        Assert.Equal(1, worker.Calls);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
        Assert.Equal("The Visible property is read-only.", (string?)body["data"]!["evidence"]!["message"]);
        Assert.DoesNotContain("token=sk-live", body.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token=sk-live", ((TextContentBlock)result.Content.Single()).Text, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
    }

    private sealed class VisiblePropertyWorker : IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap21" });
            Calls++;
            return Task.FromResult<JsonNode?>(new JsonObject { ["Value"] = new JsonObject {
                ["Message"] = "The Visible property is read-only.", ["Meta"] = new JsonObject {
                    ["v4Rejection"] = "INVALID_ARGUMENT", ["parameter"] = "properties",
                    ["evidence"] = new JsonObject { ["message"] = "The Visible property is read-only." },
                    ["notice"] = "Rejected 'token=sk-live'.", ["rejectedParameter"] = "token=sk-live" } } });
        }
        public void Dispose() { }
    }

    public static IEnumerable<object[]> ProductionPortedCases() =>
        new[] { "20", "21" }.SelectMany(release => new[] { "token=sk-live", "123456" }.SelectMany(key =>
            new[] { "ExportUnifiedEngineeringList", "SetUnifiedObjectProperties", "ManageUnifiedDynamization" }
                .Select(name => new object[] { release, key, name })));

    [Theory, MemberData(nameof(ProductionPortedCases))]
    public async Task Production_ported_wrappers_restrict_worker_field_names(string release, string key, string name)
    {
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var worker = new PortedRefusingWorker(key);
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        using var pipeline = new Engine.EngineHostPipeline(release, contextWorker, tools);
        using var session = pipeline.EnterSession();
        var cases = new Dictionary<string, string> {
            ["ExportUnifiedEngineeringList"] = "{\"softwarePath\":\"HMI\",\"category\":\"textLists\",\"name\":\"List\","
                + "\"destinationDirectory\":\"C:/fixture\"}",
            ["SetUnifiedObjectProperties"] = "{\"softwarePath\":\"HMI\",\"objectPath\":[{\"property\":\"Screens\",\"name\":\"Main\"}],"
                + "\"properties\":{\"Width\":800}}",
            ["ManageUnifiedDynamization"] = "{\"softwarePath\":\"HMI\",\"objectPath\":[{\"property\":\"Screens\",\"name\":\"Main\"}],"
                + "\"propertyName\":\"Visible\",\"action\":\"read\"}" };
        var tool = tools.Single(t => t.ProtocolTool.Name == name);
        int before = worker.Refusals;
        var arguments = JsonNode.Parse(cases[name])!.AsObject();
        arguments["dryRun"] = true;
        if (name == "ExportUnifiedEngineeringList")
            arguments["destinationDirectory"] = TiaOpenness.Shared.DataLocations.Current.OutputRoot;
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = name,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } });
        var body = result.StructuredContent!;
        Assert.True(worker.Refusals == before + 1, body.ToJsonString());
        Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
        Assert.Equal("", (string?)body["error"]!["details"]!["parameter"]);
        Assert.Equal(new[] { "" }, body["error"]!["details"]!["parameters"]!.AsArray().Select(v => (string?)v));
        Assert.True(body["error"]!["details"]!.AsObject().ContainsKey("expected"));
        Assert.DoesNotContain(key, body.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(key, ((TextContentBlock)result.Content.Single()).Text, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
    }


    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    public async Task Production_ported_wrappers_declare_and_enforce_fixed_enums(string release)
    {
        using var contextWorker = new HardwareAddressingAdmissionTests.UnboundWorker();
        using var worker = new PortedRefusingWorker("token=sk-live");
        var tools = LegacyHostToolRegistry.Create(worker, release, false);
        using var pipeline = new Engine.EngineHostPipeline(release, contextWorker, tools);
        using var session = pipeline.EnterSession();
        foreach (string name in new[] { "CompareProjects", "ExportUnifiedEngineeringList" })
        {
            var tool = tools.Single(t => t.ProtocolTool.Name == name);
            string field = name == "CompareProjects" ? "kind" : "category";
            string[] allowed = field == "kind" ? new[] { "software", "softwareToLibrary", "hardware" }
                : new[] { "textLists", "graphicLists", "systemTextLists" };
            Assert.Equal(allowed, tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty(field)
                .GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
            var arguments = new JsonObject { [field] = "bad" };
            if (field == "category")
            {
                arguments["softwarePath"] = "HMI";
                arguments["name"] = "List";
                arguments["destinationDirectory"] = TiaOpenness.Shared.DataLocations.Current.OutputRoot;
            }
            var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new() { Name = name,
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } });
            var details = result.StructuredContent!["error"]!["details"]!;
            Assert.Equal("/" + field, (string?)details["parameter"]);
            Assert.Equal("one of the allowed values", (string?)details["expected"]);
            Assert.Equal(allowed, details["allowedValues"]!.AsArray().Select(v => (string?)v));
            Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        }
        Assert.Equal(0, worker.Refusals);
    }

    private sealed class PortedRefusingWorker(string parameter) : IFoundationWorker
    {
        internal int Refusals;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProcessId"] = 123, ["IsAttached"] = true, ["ProjectFile"] = "C:/Fixture.ap21" });
            Refusals++;
            var argumentError = new TiaMcp.Logic.V4.Error("Invalid field " + parameter + ".",
                new TiaMcp.Logic.V4.InvalidArgumentDetails(parameter, Array.Empty<string>(), expected: parameter));
            return Task.FromResult<JsonNode?>(new JsonObject { ["Value"] = new JsonObject {
                ["Message"] = argumentError.Message, ["Meta"] = new JsonObject {
                    ["v4Rejection"] = "INVALID_ARGUMENT", ["parameter"] = parameter,
                    ["v4ArgumentError"] = JsonNode.Parse(TiaMcp.Logic.V4.V4Json.Serialize(argumentError)) } } });
        }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("softwarePath", "/softwarePath")]
    [InlineData("token=sk-live", "")]
    [InlineData("123456", "")]
    public async Task Escaped_argument_exceptions_keep_declared_field_identity(string parameter, string pointer)
    {
        var tool = new FoundationV4Tool(new ThrowingArgumentTool(parameter), "21",
            readinessForTest: () => new JsonObject { ["ready"] = true });
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
            Params = new() { Name = "GetPlcBlockInfo",
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"softwarePath\":\"PLC\"}") } });
        Assert.True((string?)result.StructuredContent!["error"]!["code"] == "INVALID_ARGUMENT", result.StructuredContent.ToJsonString());
        Assert.Equal(pointer, (string?)result.StructuredContent["error"]!["details"]!["parameter"]);
        Assert.Equal("Invalid Foundation argument. Nothing was executed.", (string?)result.StructuredContent["error"]!["message"]);
        Assert.DoesNotContain("Private input value.", result.StructuredContent.ToJsonString());
        Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
    }

    private sealed class ThrowingArgumentTool(string parameter) : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new() { Name = "GetPlcBlockInfo", InputSchema = JsonSerializer.Deserialize<JsonElement>(
            "{\"type\":\"object\",\"properties\":{\"softwarePath\":{\"type\":\"string\"}},\"required\":[\"softwarePath\"]}") };
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default) => throw new ArgumentException("Private input value.", parameter);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("library")]
    [InlineData("drive")]
    public void Legacy_aggregate_checks_keep_parameter_metadata_when_supplied(string contract)
    {
        void Reject(string softwarePath) => throw new ArgumentException("Private input value.", nameof(softwarePath));
        CallToolResult result;
        if (contract == "network")
            result = Host.HardwareToolContract.Invoke("Fixture", true, false, () => {
                Host.HardwareToolContract.Check(() => Reject("PLC"));
                return new Host.ResponseMessage();
            });
        else if (contract == "library")
            result = Host.LibraryToolContract.Run("Fixture", false, false, () => {
                Host.LibraryToolContract.Check(() => Reject("PLC"));
                return new Host.ResponseMessage();
            });
        else
            result = Host.DriveToolContract.Run("Fixture", false, false, () => {
                Host.DriveToolContract.Check(() => Reject("PLC"));
                return new Host.ResponseMessage();
            });
        Assert.Equal("/softwarePath", (string?)result.StructuredContent!["error"]!["details"]!["parameter"]);
        Assert.Equal("Input does not satisfy the declared contract.", (string?)result.StructuredContent["error"]!["message"]);
        Assert.DoesNotContain("Private input value.", result.StructuredContent.ToJsonString());
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
    public async Task Fixed_enums_are_rejected_before_dispatch_on_every_registered_release(string release)
    {
        using var worker = new Worker();
        var shared = LegacyHostToolRegistry.Create(worker, release, false, hostPorts: false);
        using var engine = new EngineWorker();
        using var pipeline = new Engine.EngineHostPipeline(release, engine, shared);
        var tools = pipeline.AllTools;
        foreach (string name in new[] { "CompareProjects", "ExportUnifiedEngineeringList" })
        {
            var tool = tools.SingleOrDefault(t => t.ProtocolTool.Name == name);
            Assert.Equal(PortedFamilies.Available(release, name), tool != null);
            if (tool == null) continue;
            string parameter = name == "CompareProjects" ? "kind" : "category";
            var allowed = name == "CompareProjects" ? new[] { "software", "softwareToLibrary", "hardware" }
                : new[] { "textLists", "graphicLists", "systemTextLists" };
            Assert.Equal(allowed, tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty(parameter)
                .GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
            var arguments = new JsonObject { [parameter] = "bad" };
            if (name == "ExportUnifiedEngineeringList")
            {
                arguments["softwarePath"] = "HMI";
                arguments["name"] = "List";
                arguments["destinationDirectory"] = "C:/fixture";
            }
            var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new CallToolRequestParams { Name = name,
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()) } });
            Assert.Equal("INVALID_ARGUMENT", (string?)result.StructuredContent!["error"]!["code"]);
            var details = result.StructuredContent["error"]!["details"]!;
            Assert.Equal("/" + parameter, (string?)details["parameter"]);
            Assert.Equal("one of the allowed values", (string?)details["expected"]);
            Assert.Equal(allowed, details["allowedValues"]!.AsArray().Select(v => (string?)v));
            Assert.True(JsonNode.DeepEquals(result.StructuredContent, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        }
        Assert.Equal(0, worker.Calls);
    }

    public class ServerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }

    [Theory]
    [InlineData("token=sk-live")]
    [InlineData("123456")]
    [InlineData("softwarePath")]
    public async Task Worker_parameter_evidence_is_projected_through_the_declared_schema(string parameter)
    {
        foreach (string release in ReleaseRange.All)
        {
            using var worker = new RefusingWorker(parameter);
            var definition = FoundationTools.Definitions.Single(d => d.Name == "GetPlcBlockInfo");
            var tool = new FoundationV4Tool(new FoundationTool(definition, worker), release,
                readinessForTest: () => new JsonObject { ["ready"] = true });
            var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(DispatchProxy.Create<IMcpServer, ServerProxy>()) {
                Params = new CallToolRequestParams { Name = definition.Name,
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"softwarePath\":\"PLC\",\"blockPath\":\"Main\"}") } });
            var body = result.StructuredContent!;
            Assert.Equal("INVALID_ARGUMENT", (string?)body["error"]!["code"]);
            Assert.Equal(parameter == "softwarePath" ? "/softwarePath" : "", (string?)body["error"]!["details"]!["parameter"]);
            Assert.True(body["error"]!["details"]!.AsObject().ContainsKey("expected"));
            if (parameter == "softwarePath") Assert.Equal("Invalid field softwarePath.", (string?)body["error"]!["message"]);
            else
            {
                Assert.DoesNotContain(parameter, body.ToJsonString(), StringComparison.Ordinal);
                Assert.DoesNotContain(parameter, ((TextContentBlock)result.Content.Single()).Text, StringComparison.Ordinal);
            }
            Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(((TextContentBlock)result.Content.Single()).Text)));
        }
    }

    private sealed class RefusingWorker(string parameter) : IFoundationWorker
    {
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token) =>
            throw new WorkerOperationException("Invalid field " + parameter + ".", -32602, "rejected-before-operation",
                JsonSerializer.Serialize(new { parameter, exceptionType = nameof(AdapterPreconditionException) }));
        public void Dispose() { }
    }

    private sealed class Worker : IFoundationWorker
    {
        internal int Calls;
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        { Calls++; throw new InvalidOperationException("An invalid argument reached the worker."); }
        public void Dispose() { }
    }

    private sealed class EngineWorker : Engine.IEngineWorker
    {
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => new();
        public Task<JsonObject> Status(CancellationToken token) => Task.FromResult(Snapshot());
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new InvalidOperationException();
        public Task<Engine.EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("An invalid argument reached the engine worker.");
        public void Dispose() { }
    }
}
