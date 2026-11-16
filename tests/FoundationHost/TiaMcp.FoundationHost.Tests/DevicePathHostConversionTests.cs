extern alias toolpipeline;
extern alias adaptertests;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Logic.V4;
using HostParameters = toolpipeline::TiaMcp.FoundationHost.CanonicalParameterTool;
using NativeDevice = adaptertests::Siemens.Engineering.HW.Device;
using NativeItem = adaptertests::Siemens.Engineering.HW.DeviceItem;
using NativeGroup = adaptertests::Siemens.Engineering.HW.DeviceUserGroup;
using NativeUngrouped = adaptertests::Siemens.Engineering.HW.DeviceGroup;
using Xunit;

public sealed class DevicePathHostConversionTests
{
    public static IEnumerable<object[]> Entries =>
        from release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        from tool in new[] { "GetDeviceInfo", "GetDeviceAttributes", "GetDeviceIpAddress", "ExportDeviceAml",
            "GetPlcLiveValuesS7", "GetPlcPutGetAccess", "SetPlcPutGetAccess" }
        select new object[] { release, tool };

    [Theory, MemberData(nameof(Entries))]
    public async Task Array_cpu_names_do_not_use_the_string_alias(string release, string tool)
    {
        var station = new NativeDevice { Name = "S7-1500/ET200MP station_1" };
        station.DeviceItems.Add(new NativeItem { Name = "PLC_1" });
        var fixture = new LookupTool(tool, release) { Roots = new[] { station } };
        var array = await Call(fixture, release, new JsonArray("PLC_1"));
        Assert.True((bool?)array["ok"], array.ToJsonString());
        Assert.Equal("devices/PLC_1", fixture.LastPath);
        Assert.Null(fixture.Selected);
        Assert.Empty(array["meta"]!["warnings"]!.AsArray());

        var text = await Call(fixture, release, JsonValue.Create("PLC_1")!);
        Assert.True((bool?)text["ok"], text.ToJsonString());
        Assert.Equal("PLC_1", fixture.LastPath);
        Assert.Same(station, fixture.Selected);
        Assert.Equal("DEPRECATED_PARAMETER", (string?)Assert.Single(text["meta"]!["warnings"]!.AsArray())!["code"]);
        Assert.Equal(2, fixture.Calls);
    }

    [Theory, MemberData(nameof(Entries))]
    public async Task Canonical_words_inside_arrays_are_exact_group_names(string release, string tool)
    {
        var root = new NativeDevice { Name = "X" };
        var loose = new NativeDevice { Name = "X" };
        var fixture = new LookupTool(tool, release) { Roots = new[] { root } };
        fixture.Ungrouped.Devices.Add(loose);
        foreach (string groupName in new[] { "devices", "ungrouped" })
        {
            var result = await Call(fixture, release, new JsonArray(groupName, "X"));
            Assert.True((bool?)result["ok"], result.ToJsonString());
            Assert.Equal("device-groups/" + groupName + "/devices/X", fixture.LastPath);
            Assert.Null(fixture.Selected);
        }
        await Call(fixture, release, new JsonArray("X"));
        Assert.Same(root, fixture.Selected);
        await Call(fixture, release, new JsonArray("$ungrouped", "X"));
        Assert.Equal("ungrouped/X", fixture.LastPath);
        Assert.Same(loose, fixture.Selected);
        Assert.Equal(4, fixture.Calls);
    }

    [Theory, MemberData(nameof(Entries))]
    public async Task Slash_names_preserve_their_array_boundaries(string release, string tool)
    {
        var first = new NativeDevice { Name = "B" };
        var second = new NativeDevice { Name = "A/B" };
        var slashGroup = new NativeGroup { Name = "G/A" }; slashGroup.Devices.Add(first);
        var plainGroup = new NativeGroup { Name = "G" }; plainGroup.Devices.Add(second);
        var fixture = new LookupTool(tool, release) { Groups = new[] { slashGroup, plainGroup } };
        var left = await Call(fixture, release, new JsonArray("G/A", "B"));
        Assert.True((bool?)left["ok"], left.ToJsonString());
        Assert.Equal("device-groups/G%2FA/devices/B", fixture.LastPath);
        Assert.Same(first, fixture.Selected);
        var right = await Call(fixture, release, new JsonArray("G", "A/B"));
        Assert.True((bool?)right["ok"], right.ToJsonString());
        Assert.Equal("device-groups/G/devices/A%2FB", fixture.LastPath);
        Assert.Same(second, fixture.Selected);
        Assert.Equal(2, fixture.Calls);
    }

    [Theory, MemberData(nameof(Entries))]
    public async Task Nested_slash_and_percent_names_keep_their_exact_spelling(string release, string tool)
    {
        var device = new NativeDevice { Name = "D/%2F" };
        var nested = new NativeGroup { Name = "Sub/B%" }; nested.Devices.Add(device);
        var group = new NativeGroup { Name = "G/A%" }; group.Groups.Add(nested);
        var fixture = new LookupTool(tool, release) { Groups = new[] { group } };
        var result = await Call(fixture, release, new JsonArray("G/A%", "Sub/B%", "D/%2F"));
        Assert.True((bool?)result["ok"], result.ToJsonString());
        Assert.Equal("device-groups/G%2FA%25/groups/Sub%2FB%25/devices/D%2F%252F", fixture.LastPath);
        Assert.Same(device, fixture.Selected);
        Assert.Equal(1, fixture.Calls);
    }

    private static async Task<JsonNode> Call(LookupTool inner, string release, JsonNode path)
    {
        var tool = new HostParameters(inner, release);
        var args = new JsonObject { ["devicePath"] = path };
        var result = await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(
            DispatchProxy.Create<IMcpServer, WorkerRobustnessTests.ServerProxy>()) {
                Params = new() { Name = tool.ProtocolTool.Name, Arguments = args.Deserialize<Dictionary<string, JsonElement>>() } });
        return result.StructuredContent!;
    }

    private sealed class LookupTool : McpServerTool
    {
        // The adapter contract project links the production resolver against its managed Siemens stand-ins.
        private static readonly Func<IEnumerable<NativeDevice>?, IEnumerable<NativeGroup>?, NativeUngrouped?, string, NativeDevice?> FindDevice =
            typeof(adaptertests::TiaMcp.Adapters.PlcFoundationEngine).Assembly
                .GetType("TiaMcp.Adapters.Native.Hardware.ProjectDeviceWalk")!
                .GetMethod("FindDevice", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<IEnumerable<NativeDevice>?, IEnumerable<NativeGroup>?, NativeUngrouped?, string, NativeDevice?>>();
        private readonly string release;
        private readonly Tool protocol;
        internal NativeDevice[] Roots = Array.Empty<NativeDevice>();
        internal NativeGroup[] Groups = Array.Empty<NativeGroup>();
        internal NativeUngrouped Ungrouped = new();
        internal NativeDevice? Selected;
        internal string? LastPath;
        internal int Calls;
        internal LookupTool(string name, string release)
        {
            this.release = release;
            protocol = new Tool { Name = name, InputSchema = JsonSerializer.SerializeToElement(new JsonObject {
                ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("devicePath"),
                ["properties"] = new JsonObject { ["devicePath"] = new JsonObject { ["type"] = "string" } } }) };
        }
        public override Tool ProtocolTool => protocol;
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPath = request.Params!.Arguments!["devicePath"].GetString()!;
            Selected = FindDevice(Roots, Groups, Ungrouped, LastPath);
            var meta =
                new Meta(DateTimeOffset.UtcNow, release, protocol.Name, "fixture", Outcome.Succeeded, Execution.ReadOnly, BehaviorPolicy.NotApplicable,
                    Completeness.Complete, null, Array.Empty<Warning>());
            var body = JsonNode.Parse(V4Json.Serialize(Envelope.Create(new { found = Selected != null }, null, meta)))!;
            return ValueTask.FromResult(new CallToolResult { IsError = false, StructuredContent = body,
                Content = new[] { new TextContentBlock { Text = body.ToJsonString() } } });
        }
    }
}
