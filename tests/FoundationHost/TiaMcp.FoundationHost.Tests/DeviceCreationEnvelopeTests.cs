extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Adapters;
using TiaMcp.Adapters.Contracts;
using TiaMcp.FoundationHost;
using TiaMcp.PlcWorker;
using Xunit;
using Engine = toolpipeline::TiaMcp.FoundationHost;

public sealed class DeviceCreationEnvelopeTests
{
    private const string Tp700 = "6AV2 124-0GC01-0AX0";
    private const string Unified = "6AV2 128-3QB06-0AX1";
    private const string Project = "C:/Fixture/P.ap21";
    private const string Item = "MCP_RC_PLC/MCP_RC_PLC";

    public static IEnumerable<object[]> CatalogCases =>
        from release in new[] { "18", "19", "20", "21" }
        from tool in new[] { "CreateDevice", "CreateHardwareDevice" }
        from viaBridge in new[] { false, true }
        select new object[] { release, tool, viaBridge };

    [Theory, MemberData(nameof(CatalogCases))]
    public async Task Exact_catalog_refusals_identify_the_called_tools_fields(string release, string tool, bool viaBridge)
    {
        using var fixture = new Fixture(release);
        string article = tool == "CreateDevice" ? "orderNumber" : "preferredMlfb";
        string version = tool == "CreateDevice" ? "version" : "preferredVersion";
        bool tp700 = release == "18" && viaBridge;
        var args = new JsonObject { [article] = tp700 ? Tp700 : "6ES7 513-9XX99-0XX0", [version] = tp700 ? "17.0.0.0" : "V9.9",
            ["deviceName"] = tp700 ? "MCP_RC_HMI2" : "PLC_2" };
        var preview = await fixture.Call(tool, args, viaBridge);
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        foreach (int count in new[] { 0, 2 })
        {
            fixture.Worker.MatchingRows = count;
            foreach (bool apply in new[] { false, true })
            {
                var input = args.DeepClone().AsObject();
                if (apply) Confirm(input, preview);
                var body = await fixture.Call(tool, input, viaBridge);
                Refused(body, "INVALID_ARGUMENT");
                Assert.Equal("/" + article, (string?)body["error"]?["details"]?["parameter"]);
                Assert.Equal(new[] { "/" + article, "/" + version },
                    body["error"]!["details"]!["parameters"]!.AsArray().Select(p => (string)p!));
                string message = (string)body["error"]!["message"]!;
                Assert.Contains(count == 0 ? "No catalog row" : "Several catalog rows", message);
                Assert.Contains("SearchHardwareCatalog", message);
                Assert.Contains("same row", message);
                Assert.DoesNotContain("preferredMlfb/preferredVersion", message);
            }
        }
    }

    [Theory]
    [InlineData("14sp1", 14), InlineData("15.1", 15), InlineData("16", 16), InlineData("17", 17)]
    [InlineData("18", 18), InlineData("19", 19), InlineData("20", 20), InlineData("21", 21)]
    public async Task Panel_version_refusal_reaches_the_client(string release, int major)
    {
        using var fixture = new Fixture(release);
        foreach (string tool in major >= 18 ? new[] { "CreateDevice", "CreateHardwareDevice" } : new[] { "CreateDevice" })
        {
            var args = new JsonObject { [tool == "CreateDevice" ? "orderNumber" : "preferredMlfb"] = Unified,
                [tool == "CreateDevice" ? "version" : "preferredVersion"] = "13.0.0.0", ["deviceName"] = "Panel" };
            var preview = await fixture.Call(tool, args, true);
            if (major < 18)
            {
                Assert.True((bool?)preview["ok"], preview.ToJsonString());
                Confirm(args, preview);
            }
            else
            {
                PanelRefusal(preview);
                args["dryRun"] = false;
                args["confirm"] = true;
                args["expectedProjectFile"] = Project;
                args["expectedPlanHash"] = new string('a', 64);
            }
            PanelRefusal(await fixture.Call(tool, args, true));
        }
    }

    [Theory]
    [InlineData("14sp1", "PositionNumber", "1", ""), InlineData("15.1", "OrderNumber", "6ES7 521-1BH00-0AB0", "")]
    [InlineData("15.1", "PositionNumber", "2", ""), InlineData("16", "PositionNumber", "1", ""), InlineData("17", "PositionNumber", "1", "")]
    [InlineData("18", "PositionNumber", "1", "System.Int32"), InlineData("19", "PositionNumber", "1", "System.Int32")]
    [InlineData("20", "PositionNumber", "1", "System.Int32"), InlineData("21", "PositionNumber", "1", "System.Int32")]
    public async Task Read_only_attribute_reason_reaches_the_client_with_the_recorded_evidence(string release, string name, string oldValue, string type)
    {
        using var fixture = new Fixture(release);
        string reason = $"Attribute '{name}' is not writable (AccessMode=Read); nothing was written.";
        fixture.Worker.AttributeEvidence = new JsonObject {
            ["success"] = false, ["deviceItemPath"] = Item, ["attributeName"] = name, ["mayHaveChanged"] = false,
            ["oldValue"] = oldValue, ["attributeDataType"] = type, ["attributeAccessMode"] = "Read",
            ["attributeWritable"] = false, ["refusalReason"] = reason };
        var attr = new JsonObject { ["deviceItemPath"] = Item, ["attributeName"] = name, ["value"] = "99999" };
        var plan = await fixture.Call("SetDeviceItemAttribute", attr, true);
        Assert.True((bool?)plan["ok"], plan.ToJsonString());
        Confirm(attr, plan, confirm: false);
        var refused = await fixture.Call("SetDeviceItemAttribute", attr, true);
        Refused(refused, "PRECONDITION_FAILED");
        Assert.Equal(reason, (string?)refused["error"]?["message"]);
        Assert.Equal("hardware-admission", (string?)refused["error"]?["details"]?["condition"]);
        Assert.Equal(Item, (string?)refused["error"]?["details"]?["target"]);
        Assert.Null(refused["error"]?["details"]?["parameter"]);
        Assert.True(JsonNode.DeepEquals(fixture.Worker.AttributeEvidence, refused["data"]?["evidence"]));
        Assert.Equal("Hardware operation did not establish a complete successful result.", (string?)refused["data"]?["summary"]);
        foreach (string field in new[] { "plannedChanges", "planHash", "targetIdentity", "targetBound" })
            Assert.True(JsonNode.DeepEquals(plan["data"]![field], refused["data"]![field]), field);
        Assert.False((bool?)refused["data"]?["dryRun"]);
    }

    private static void PanelRefusal(JsonNode body)
    {
        Refused(body, "PRECONDITION_FAILED");
        Assert.Contains("panel version", (string?)body["error"]?["message"]);
        Assert.Contains("release", (string?)body["error"]?["message"]);
    }

    private static void Refused(JsonNode body, string code)
    {
        Assert.Equal(code, (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.Equal("not-started", (string?)body["meta"]?["execution"]);
    }

    private static void Confirm(JsonObject args, JsonNode preview, bool confirm = true)
    {
        args["dryRun"] = false;
        if (confirm) args["confirm"] = true;
        args["expectedPlanHash"] = preview["data"]!["planHash"]!.DeepClone();
        args["expectedProjectFile"] = Project;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly CatalogWorker Worker;
        private readonly PortedPortalAdmissionTests.DisabledApproval approval = new();
        private readonly OnlinePortAdmissionTests.OnlineWorker engine = new();
        private readonly Engine.EngineHostPipeline pipeline;
        private readonly IDisposable session;
        private readonly string previousRelease = Engine.EngineHostConfiguration.ReleaseKey;
        internal Fixture(string release)
        {
            Worker = new CatalogWorker(release);
            pipeline = new(release, engine, LegacyHostToolRegistry.Create(Worker, release, false, hostPorts: false));
            session = pipeline.EnterSession();
        }

        internal async Task<JsonNode> Call(string name, JsonObject args, bool viaBridge)
        {
            string entry = viaBridge ? "CallTool" : name;
            var input = viaBridge ? new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() } : args;
            var tool = pipeline.AllTools.Single(t => t.ProtocolTool.Name == entry);
            var result = await tool.InvokeAsync(SharedPipelineTests.Scope.Request(entry, input));
            var body = result.StructuredContent!;
            Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text!)));
            return body;
        }

        public void Dispose()
        {
            session.Dispose();
            pipeline.Dispose();
            engine.Dispose();
            Worker.Dispose();
            approval.Dispose();
            Engine.EngineHostConfiguration.ReleaseKey = previousRelease;
        }
    }

    private sealed class CatalogWorker(string release) : IFoundationWorker
    {
        internal int MatchingRows = 1;
        internal JsonObject? AttributeEvidence;

        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProjectFile"] = Project, ["ProcessId"] = 42, ["IsAttached"] = true });
            if (operation == "hardware-devices.HardwareLegacySetDeviceItemAttribute")
            {
                var reply = new JsonObject { ["Message"] = $"Device item attribute '{args["attributeName"]}' is not writable",
                    ["Meta"] = AttributeEvidence!.DeepClone(), ["OutcomeUnknown"] = false, ["MayHaveChanged"] = false };
                WorkerProtocol.ValidateExchangeResult(operation, args, reply);
                return Task.FromResult<JsonNode?>(reply);
            }
            try
            {
                if (operation == "hardware-devices.HardwareCreateDevice")
                {
                    DeviceCreationRules.RequireUnifiedPanelVersion((string)args["orderNumber"]!, (string)args["version"]!,
                        release == "14sp1" ? 14 : release == "15.1" ? 15 : int.Parse(release));
                    throw new InvalidOperationException("Expected the native panel admission to refuse.");
                }
                Assert.Equal("AddDeviceWithFallback", operation);
                var request = new PlcDeviceAddRequest { Release = release, Project = Project, ProcessId = 42, RootIdentity = "root",
                    PreferredMlfb = (string)args["preferredMlfb"]!, PreferredVersion = (string)args["preferredVersion"]!,
                    Name = (string)args["deviceName"]!, DryRun = (bool)args["dryRun"]!, Confirm = (bool?)args["confirm"] == true,
                    ExpectedProject = (string?)args["expectedProjectFile"] ?? "", ExpectedHash = (string?)args["expectedPlanHash"] ?? "" };
                var row = new PlcHardwareCatalogCandidate { ArticleNumber = request.PreferredMlfb, Version = request.PreferredVersion,
                    TypeIdentifier = "OrderNumber:" + request.PreferredMlfb + "/" + request.PreferredVersion };
                var result = PlcDeviceAddPolicy.Run(request, () => Enumerable.Repeat(row, MatchingRows), () => Array.Empty<PlcDeviceAddItem>(),
                    () => { }, (_, _) => throw new InvalidOperationException("Unexpected native create."));
                return Task.FromResult(JsonSerializer.SerializeToNode(result));
            }
            catch (AdapterPreconditionException error)
            {
                var classification = WorkerFailurePolicy.Classify(error, true, false);
                throw WorkerOperationException.FromChannelFailure(new TiaMcp.WorkerChannel.ChannelFailureException(
                    error.Message, classification.Code, classification.Outcome, WorkerJson.Evidence(error)));
            }
        }
        public void Dispose() { }
    }
}
