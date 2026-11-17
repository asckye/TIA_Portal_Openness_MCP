extern alias toolpipeline;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Services = toolpipeline::TiaMcpServer.Siemens.Services;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class HardwareManagementValidationTests
{
    private const string Project = "C:/Projects/Fixture.ap21";
    private const string Type = "OrderNumber:6ES7 513-1AM03-0AB0/V3.0";

    [Theory]
    [InlineData("other", "PLC", "", "", "", "action")]
    [InlineData("deleteDevice", "", "", "", "", "devicePath")]
    [InlineData("deleteItem", "", "CPU", "", "", "devicePath")]
    [InlineData("moveItem", "", "CPU", "", "", "devicePath")]
    [InlineData("copyItem", "", "CPU", "", "", "devicePath")]
    [InlineData("changeType", "", "CPU", Type, "", "devicePath")]
    [InlineData("deleteDevice", "PLC", "CPU", "", "", "itemPath")]
    [InlineData("deleteItem", "PLC", "", "", "", "itemPath")]
    [InlineData("moveItem", "PLC", "", "", "", "itemPath")]
    [InlineData("copyItem", "PLC", "", "", "", "itemPath")]
    [InlineData("changeType", "PLC", "", Type, "", "itemPath")]
    [InlineData("changeType", "PLC", "CPU", "", "", "typeIdentifier")]
    [InlineData("changeType", "PLC", "CPU", " ", "", "typeIdentifier")]
    [InlineData("createSubnet", "PLC", "", "System:Subnet.Ethernet", "PN_IE_1", "devicePath")]
    [InlineData("deleteSubnet", "PLC", "", "", "PN_IE_1", "devicePath")]
    [InlineData("createSubnet", "", "CPU", "System:Subnet.Ethernet", "PN_IE_1", "itemPath")]
    [InlineData("deleteSubnet", "", "CPU", "", "PN_IE_1", "itemPath")]
    [InlineData("createSubnet", "", "", "", "PN_IE_1", "typeIdentifier")]
    [InlineData("createSubnet", "", "", "System:Subnet.Ethernet", "", "subnetName")]
    [InlineData("deleteSubnet", "", "", "", " ", "subnetName")]
    public void Invalid_combinations_refuse_before_project_or_native_access(string action, string device, string item,
        string type, string subnet, string parameter)
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        int calls = 0;
        var service = new Services.HardwareManagementService((_, _) => {
            calls++; throw new InvalidOperationException("Invalid input must not dispatch.");
        }, () => { calls++; return true; }, () => Project);
        var result = new Host.HardwareManagementTools(service).ManageHardwareObjectV4(action,
            device.Length == 0 ? null : new[] { device }, item.Length == 0 ? null : new[] { item },
            typeIdentifier: type, subnetName: subnet).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        Assert.Equal("/" + parameter, (string?)result["error"]?["details"]?["parameter"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("moveItem", false, 1)]
    [InlineData("copyItem", false, 1)]
    [InlineData("moveItem", true, -1)]
    [InlineData("copyItem", true, -1)]
    public void Move_and_copy_still_require_a_destination_and_nonnegative_position(string action, bool destination, int position)
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var service = new Services.HardwareManagementService((_, _) => throw new InvalidOperationException("No dispatch expected."),
            () => throw new InvalidOperationException("No project lookup expected."), () => Project);
        var result = new Host.HardwareManagementTools(service).ManageHardwareObjectV4(action, new[] { "PLC" }, new[] { "CPU" },
            destination ? new[] { "Target" } : null, position: position).StructuredContent!;
        Assert.Equal("INVALID_ARGUMENT", (string?)result["error"]?["code"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
    }

    public static IEnumerable<object[]> SupportedActions()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
            foreach (string action in new[] { "deleteDevice", "deleteItem", "moveItem", "copyItem", "changeType", "createSubnet", "deleteSubnet" })
                if (action != "changeType" || release is "18" or "19" or "20" or "21") yield return new object[] { release, action };
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
    public async Task Served_usage_returns_complete_action_examples_and_execution_bindings_on_each_release(string release)
    {
        using var fixture = new DiscoveryFixture(release);
        foreach (string action in new[] { "changeType", "createSubnet", "deleteSubnet" })
        {
            var body = await fixture.Call("GetToolUsage", new { toolName = "ManageHardwareObject", operation = action });
            Assert.True((bool?)body["ok"], body.ToJsonString());
            var data = body["data"]!;
            Assert.Equal(release, (string?)data["releaseKey"]);
            var example = data["example"]!;
            var args = example["request"]!["params"]!["arguments"]!.AsObject();
            Assert.Equal(action, (string?)args["action"]);
            Assert.True((bool?)args["dryRun"]);
            var execution = example["execution"]!["arguments"]!;
            Assert.False((bool?)execution["dryRun"]);
            Assert.Contains("data.planHash", (string?)execution["expectedPlanHash"]);
            Assert.Contains("targetIdentity", (string?)execution["expectedProjectFile"]);
            if (action == "changeType")
            {
                Assert.StartsWith("OrderNumber:6ES7 ", (string?)args["typeIdentifier"]);
                Assert.True((bool?)data["parameterSources"]?["typeIdentifier"]?["requiresBinding"]);
                Assert.Contains("SearchHardwareCatalog", data["parameterSources"]!["typeIdentifier"]!["meaning"]!.ToString());
                Assert.NotEmpty(args["devicePath"]!.AsArray());
            }
            else
            {
                Assert.False(args.ContainsKey("devicePath"));
                Assert.False(args.ContainsKey("itemPath"));
                Assert.True((bool?)data["parameterSources"]?["subnetName"]?["requiresBinding"]);
                if (action == "createSubnet") Assert.Equal("System:Subnet.Ethernet", (string?)args["typeIdentifier"]);
            }
        }
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("project")]
    public void A_changed_plan_or_project_refuses_before_native_dispatch(string changed)
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        int calls = 0;
        var service = new Services.HardwareManagementService((_, _) => {
            calls++;
            return new JsonObject { ["Message"] = "Subnet preview", ["Meta"] = new JsonObject { ["operationSuccess"] = true } };
        }, () => true, () => Project);
        var tool = new Host.HardwareManagementTools(service);
        var preview = tool.ManageHardwareObjectV4("createSubnet", typeIdentifier: "System:Subnet.Ethernet", subnetName: "New").StructuredContent!;
        var result = tool.ManageHardwareObjectV4("createSubnet", dryRun: false, typeIdentifier: "System:Subnet.Ethernet",
            subnetName: changed == "plan" ? "Changed" : "New", expectedPlanHash: (string)preview["data"]!["planHash"]!,
            expectedProjectFile: changed == "project" ? "C:/Projects/Other.ap21" : Project).StructuredContent!;
        Assert.Equal(changed == "plan" ? "PLAN_STALE" : "IDENTITY_MISMATCH", (string?)result["error"]?["code"]);
        Assert.Equal("not-started", (string?)result["meta"]?["execution"]);
        Assert.Equal(1, calls);
    }

    [Theory, MemberData(nameof(SupportedActions))]
    public void Preview_and_reviewed_execution_preserve_exact_arguments(string release, string action)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var calls = new List<JsonObject>();
        var service = new Services.HardwareManagementService((operation, arguments) => {
            Assert.Equal("hardware-devices.HardwareManageHardwareObject", operation);
            calls.Add((JsonObject)arguments.DeepClone());
            return new JsonObject { ["Message"] = "Hardware fixture readback", ["Meta"] = new JsonObject {
                ["operationSuccess"] = true, ["dryRun"] = arguments["dryRun"]!.DeepClone(),
                ["mayHaveChanged"] = !(bool)arguments["dryRun"]! } };
        }, () => true, () => Project);
        var tool = new Host.HardwareManagementTools(service);
        bool subnet = action is "createSubnet" or "deleteSubnet";
        string[]? device = subnet ? null : new[] { "Group/one", "PLC,exact" };
        string[]? item = subnet || action == "deleteDevice" ? null : new[] { "CPU%one", "接口/一" };
        var preview = tool.ManageHardwareObjectV4(action, device, item, new[] { "Destination" }, new[] { "Rack" }, 2,
            typeIdentifier: Type, subnetName: "PN/IE %1").StructuredContent!;
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        Assert.Matches("^[0-9a-f]{64}$", (string)preview["data"]!["planHash"]!);
        Assert.False((bool?)preview["data"]?["mayHaveChanged"]);
        var applied = tool.ManageHardwareObjectV4(action, device, item, new[] { "Destination" }, new[] { "Rack" }, 2, false,
            Type, "PN/IE %1", (string)preview["data"]!["planHash"]!, Project).StructuredContent!;
        Assert.True((bool?)applied["ok"], applied.ToJsonString());
        Assert.Equal("completed", (string?)applied["meta"]?["execution"]);
        Assert.Equal(2, calls.Count);
        foreach (var call in calls)
        {
            Assert.Equal(action, (string?)call["action"]);
            Assert.Equal(Type, (string?)call["typeIdentifier"]);
            Assert.Equal("PN/IE %1", (string?)call["subnetName"]);
            Assert.Equal(device ?? Array.Empty<string>(), JsonSerializer.Deserialize<string[]>(call["devicePathJson"]!.ToJsonString()));
            Assert.Equal(item ?? Array.Empty<string>(), JsonSerializer.Deserialize<string[]>(call["itemPathJson"]!.ToJsonString()));
            Assert.Equal(new[] { "Destination" }, JsonSerializer.Deserialize<string[]>(call["destinationDevicePathJson"]!.ToJsonString()));
            Assert.Equal(new[] { "Rack" }, JsonSerializer.Deserialize<string[]>(call["destinationItemPathJson"]!.ToJsonString()));
            Assert.Equal(2, (int?)call["position"]);
        }
        Assert.True((bool?)calls[0]["dryRun"]);
        Assert.False((bool?)calls[1]["dryRun"]);
        Assert.True((bool?)calls[1]["confirm"]);
        Assert.Equal(Project, (string?)calls[1]["expectedProjectFile"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_known_type_mismatch_is_failed_and_an_unreadable_write_stays_unknown(bool known)
    {
        using var fixture = new PreviewHostFixture("21");
        using var session = fixture.Pipeline.EnterSession();
        var service = new Services.HardwareManagementService((_, arguments) => new JsonObject {
            ["Message"] = "Fixture result", ["Meta"] = new JsonObject {
                ["operationSuccess"] = (bool)arguments["dryRun"]!, ["mayHaveChanged"] = !(bool)arguments["dryRun"]!,
                ["verified"] = (bool)arguments["dryRun"]!, ["postStateKnown"] = known,
                ["readbackTypeIdentifier"] = "Different" } }, () => true, () => Project);
        var tool = new Host.HardwareManagementTools(service);
        var preview = tool.ManageHardwareObjectV4("changeType", new[] { "PLC" }, new[] { "CPU" }, typeIdentifier: Type).StructuredContent!;
        var result = tool.ManageHardwareObjectV4("changeType", new[] { "PLC" }, new[] { "CPU" }, dryRun: false, typeIdentifier: Type,
            expectedPlanHash: (string)preview["data"]!["planHash"]!, expectedProjectFile: Project).StructuredContent!;
        Assert.False((bool?)result["ok"]);
        Assert.Equal(known ? "failed" : "unknown", (string?)result["meta"]?["outcome"]);
        Assert.True((bool?)result["data"]?["evidence"]?["mayHaveChanged"]);
        Assert.Equal("Different", (string?)result["data"]?["evidence"]?["readbackTypeIdentifier"]);
    }
}
