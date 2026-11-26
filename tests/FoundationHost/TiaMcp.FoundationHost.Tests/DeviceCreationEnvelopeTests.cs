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
    private const string Tp700Version = "17.0.0.0";
    private const string Landscape = "OrderNumber:" + Tp700 + "/" + Tp700Version;
    private const string Portrait = Landscape + "/Portrait";
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
        foreach (int count in new[] { 0, 2 })
        {
            bool tp700 = count == 2 || release == "18" && viaBridge;
            string number = tp700 ? Tp700 : "6ES7 513-9XX99-0XX0";
            string firmware = tp700 ? Tp700Version : "V9.9";
            var args = new JsonObject { [article] = number, [version] = firmware, ["deviceName"] = tp700 ? "MCP_RC_HMI2" : "PLC_2" };
            fixture.Worker.Rows = [Row(number, firmware)];
            var preview = await fixture.Call(tool, args, viaBridge);
            Assert.True((bool?)preview["ok"], preview.ToJsonString());
            fixture.Worker.Rows = count == 0 ? [] : RecordedTp700Rows();
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
                if (count == 2)
                {
                    Assert.Contains("exact typeIdentifier", message);
                    Assert.Contains(article, message);
                    Assert.Contains("empty " + version, message);
                    Assert.DoesNotContain("copy the article number and version", message);
                }
                else Assert.Equal("No catalog row matches the selection. Run SearchHardwareCatalog and copy the article number and version "
                    + "from the same row, preserving spaces.", message);
            }
        }
    }

    public static IEnumerable<object[]> ExactIdentifierCases =>
        from release in new[] { "18", "19", "20", "21" }
        from tool in new[] { "CreateDevice", "CreateHardwareDevice", "CreateHardwareCatalogDevice" }
        from viaBridge in new[] { false, true }
        select new object[] { release, tool, viaBridge };

    [Theory, MemberData(nameof(ExactIdentifierCases))]
    public async Task Full_identifier_selects_landscape_and_explains_an_unsupported_variant(string release, string tool, bool viaBridge)
    {
        using var fixture = new Fixture(release);
        fixture.Worker.Rows = RecordedTp700Rows();
        string field = tool switch { "CreateDevice" => "orderNumber", "CreateHardwareDevice" => "preferredMlfb", _ => "keyword" };
        var args = new JsonObject { [field] = Landscape, ["deviceName"] = "MCP_RC_HMI2" };
        if (tool != "CreateHardwareCatalogDevice") args[tool == "CreateDevice" ? "version" : "preferredVersion"] = "";
        var plan = await fixture.Call(tool, args, viaBridge);
        Assert.True((bool?)plan["ok"], plan.ToJsonString());
        Assert.Equal(Landscape, (string?)plan["data"]?["typeIdentifier"]);

        args[field] = Portrait;
        var body = await fixture.Call(tool, args, viaBridge);
        Refused(body, "INVALID_ARGUMENT");
        string message = (string)body["error"]!["message"]!;
        Assert.Contains("Exact creation does not support this catalog variant", message);
        Assert.DoesNotContain("SearchHardwareCatalog", message);
        Assert.Equal("/" + field, (string?)body["error"]?["details"]?["parameter"]);
        if (tool == "CreateHardwareCatalogDevice")
            Assert.Equal(new[] { "/keyword" }, body["error"]!["details"]!["parameters"]!.AsArray().Select(p => (string)p!));
    }

    [Theory]
    [InlineData("18", false), InlineData("18", true), InlineData("19", false), InlineData("19", true)]
    [InlineData("20", false), InlineData("20", true), InlineData("21", false), InlineData("21", true)]
    public async Task Catalog_keyword_refusal_names_only_the_declared_selection_field(string release, bool viaBridge)
    {
        const string tool = "CreateHardwareCatalogDevice";
        using var fixture = new Fixture(release);
        fixture.Worker.Rows = RecordedTp700Rows();
        var args = new JsonObject { ["keyword"] = Landscape, ["deviceName"] = "MCP_RC_HMI2" };
        var plan = await fixture.Call(tool, args, viaBridge);
        Assert.True((bool?)plan["ok"], plan.ToJsonString());
        fixture.Worker.Rows = [];
        foreach (bool apply in new[] { false, true })
        {
            if (apply) Confirm(args, plan);
            var missing = await fixture.Call(tool, args, viaBridge);
            Refused(missing, "INVALID_ARGUMENT");
            Assert.Equal("/keyword", (string?)missing["error"]?["details"]?["parameter"]);
            Assert.Equal(new[] { "/keyword" }, missing["error"]!["details"]!["parameters"]!.AsArray().Select(p => (string)p!));
            Assert.Contains("SearchHardwareCatalog", (string?)missing["error"]?["message"]);
            Assert.Contains("exact typeIdentifier", (string?)missing["error"]?["message"]);
            Assert.Contains("keyword", (string?)missing["error"]?["message"]);
        }
    }

    private static PlcHardwareCatalogCandidate Row(string number, string version, string suffix = "") =>
        new() { ArticleNumber = number, Version = version, TypeIdentifier = "OrderNumber:" + number + "/" + version + suffix };

    // V18 rc-full raw-A2/119_SearchHardwareCatalog.json, TP700 Comfort and TP700 Comfort Portrait.
    private static PlcHardwareCatalogCandidate[] RecordedTp700Rows() => [Row(Tp700, Tp700Version), Row(Tp700, Tp700Version, "/Portrait")];

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
                Assert.Null(preview["data"]!["plannedChanges"]!["family"]);
                Confirm(args, preview);
            }
            else
            {
                PanelRefusal(preview, major);
                args["dryRun"] = false;
                args["confirm"] = true;
                args["expectedProjectFile"] = Project;
                args["expectedPlanHash"] = new string('a', 64);
            }
            PanelRefusal(await fixture.Call(tool, args, true), major);
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task Device_preview_preserves_explicit_family_including_the_default_label(bool viaBridge)
    {
        using var fixture = new Fixture("17");
        foreach (string? family in new string?[] { null, "S7-1500", "HMI" })
        {
            var args = new JsonObject { ["orderNumber"] = Tp700, ["version"] = Tp700Version, ["deviceName"] = "MCP_RC_HMI2" };
            if (family != null) args["family"] = family;
            var preview = await fixture.Call("CreateDevice", args, viaBridge);
            Assert.True((bool?)preview["ok"], preview.ToJsonString());
            var changes = preview["data"]!["plannedChanges"]!.AsObject();
            Assert.Equal(family != null, changes.ContainsKey("family"));
            Assert.Equal(family, (string?)changes["family"]);
        }
    }

    [Theory]
    [InlineData("The name must contain a character.")]
    [InlineData("TIA refused the name without a reason. An empty name is not allowed.")]
    public async Task V14_empty_device_name_exposes_the_native_reason_in_the_public_error(string reason)
    {
        using var fixture = new Fixture("14sp1");
        var message = "Error when calling method 'set_Name' of type 'Siemens.Engineering.HW.DeviceItemImpl'. " + reason;
        fixture.Worker.AttributeEvidence = new JsonObject {
            ["success"] = false, ["deviceItemPath"] = Item, ["attributeName"] = "Name", ["mayHaveChanged"] = false,
            ["oldValue"] = "MCP_RC_PLC", ["requestedValue"] = "", ["attributeDataType"] = "", ["attributeAccessMode"] = "ReadWrite",
            ["attributeWritable"] = true, ["writeOutcomeUnknown"] = false, ["apiCallSuccess"] = false, ["nativeMessage"] = message };
        var args = new JsonObject { ["deviceItemPath"] = Item, ["attributeName"] = "Name", ["value"] = "" };
        var plan = await fixture.Call("SetDeviceItemAttribute", args, true);
        Assert.True((bool?)plan["ok"], plan.ToJsonString());
        Confirm(args, plan, confirm: false);
        var refused = await fixture.Call("SetDeviceItemAttribute", args, true);
        Assert.Equal("NATIVE_OPERATION_FAILED", (string?)refused["error"]?["code"]);
        Assert.Equal(message, (string?)refused["error"]?["message"]);
        Assert.Equal("failed", (string?)refused["meta"]?["outcome"]);
    }

    [Theory, InlineData("PositionNumber", false, true), InlineData("positionnumber", false, false), InlineData("PositionNumber", true, false)]
    public async Task Attribute_preview_warns_only_for_a_readable_exact_match(string name, bool failedRead, bool blocked)
    {
        using var fixture = new Fixture("16");
        fixture.Worker.FailAttributeRead = failedRead;
        var plan = await fixture.Call("SetDeviceItemAttribute", new JsonObject { ["deviceItemPath"] = Item, ["attributeName"] = name, ["value"] = "2" }, true);
        Assert.True((bool?)plan["ok"], plan.ToJsonString());
        Assert.Equal(blocked, (bool?)plan["data"]!["applyBlocked"] == true);
        Assert.Equal(blocked, plan["meta"]!["warnings"]!.AsArray().Any(warning => ((string?)warning!["message"])?.Contains("read-only") == true));
        if (blocked) Assert.Contains("read-only", (string?)plan["data"]!["applyBlockedReason"]);
        else Assert.Null(plan["data"]!["applyBlockedReason"]);
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

    private static void PanelRefusal(JsonNode body, int major)
    {
        Refused(body, "PRECONDITION_FAILED");
        Assert.Contains("panel version", (string?)body["error"]?["message"]);
        Assert.Contains("release", (string?)body["error"]?["message"]);
        Assert.Contains("13.0.0.0", (string?)body["error"]?["message"]);
        Assert.Contains(major + ".0.0.0", (string?)body["error"]?["message"]);
    }

    [Fact]
    public void Panel_refusal_only_discloses_the_selected_numeric_version()
    {
        var refusal = Assert.Throws<AdapterPreconditionException>(() =>
            DeviceCreationRules.RequireUnifiedPanelVersion(Unified, "V17.2.3.4/private-marker", 18));
        Assert.False(refusal.IsArgument);
        Assert.Contains("17.2.3.4", refusal.Message);
        Assert.Contains("18.0.0.0", refusal.Message);
        Assert.DoesNotContain("private-marker", refusal.Message);
        Assert.DoesNotContain(Unified, refusal.Message);
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
        internal PlcHardwareCatalogCandidate[] Rows = [Row(Unified, "13.0.0.0")];
        internal JsonObject? AttributeEvidence;
        internal bool FailAttributeRead;

        public Task<JsonNode?> Call(string operation, JsonObject args, CancellationToken token)
        {
            if (operation == "ReadState") return Task.FromResult<JsonNode?>(new JsonObject {
                ["ProjectFile"] = Project, ["ProcessId"] = 42, ["IsAttached"] = true });
            if (operation == "hardware-devices.HardwareDescribeItemAt")
            {
                if (FailAttributeRead) throw new IOException("Attribute metadata unavailable.");
                return Task.FromResult(JsonSerializer.SerializeToNode(new { Name = "CPU",
                    Attributes = new[] { new { Name = "PositionNumber", AccessMode = "Read" } } }));
            }
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
                var result = PlcDeviceAddPolicy.Run(request, () => Rows, () => Array.Empty<PlcDeviceAddItem>(),
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
