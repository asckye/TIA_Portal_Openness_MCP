extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Adapters.Contracts;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Services = enginehost::TiaMcpServer.Siemens.Services;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class CompileDevicePreviewTests
{
    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();
    private const string Project = "C:/Projects/Fixture.ap21";

    [Theory, MemberData(nameof(Releases))]
    public void Compile_device_defaults_to_a_preview(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        fixture.Worker.Bound = false;
        using var session = fixture.Pipeline.EnterSession();
        int calls = 0;
        var service = new Services.PlcOrganisationPortService((_, _) => {
            calls++; throw new InvalidOperationException("Preview must not dispatch a compile.");
        }, () => throw new InvalidOperationException("Preview must use only the cached binding."), () => Project);
        var body = new Host.PlcCompilePortTools(service).CompileDevice(new[] { "PLC_1" }).StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.True((bool?)body["data"]?["dryRun"]);
        Assert.Matches("^[0-9a-f]{64}$", (string)body["data"]!["planHash"]!);
        Assert.False((bool?)body["data"]?["mayHaveChanged"]);
        Assert.False((bool?)body["data"]?["targetBound"]);
        Assert.Contains("PROJECT_NOT_BOUND", (string?)body["data"]?["scope"]);
        Assert.Equal("read-only", (string?)body["meta"]?["execution"]);
        Assert.Equal(0, calls);
        Assert.Equal(0, fixture.Worker.Calls);
        Assert.Equal(0, fixture.Worker.NativeCalls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Compile_device_apply_needs_the_reviewed_project_and_plan(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        int calls = 0;
        var service = new Services.PlcOrganisationPortService((_, _) => {
            calls++; throw new InvalidOperationException("Unreviewed compile must not dispatch.");
        }, () => true, () => Project);
        var tools = new Host.PlcCompilePortTools(service);
        var missing = tools.CompileDevice(new[] { "PLC_1" }, dryRun: false).StructuredContent!;
        Assert.False((bool?)missing["ok"]);
        Assert.Equal("INVALID_ARGUMENT", (string?)missing["error"]?["code"]);
        Assert.Equal("/expectedProjectFile", (string?)missing["error"]?["details"]?["parameter"]);
        Assert.Equal("not-started", (string?)missing["meta"]?["execution"]);
        var stale = tools.CompileDevice(new[] { "PLC_1" }, dryRun: false,
            expectedPlanHash: new string('0', 64), expectedProjectFile: Project).StructuredContent!;
        Assert.False((bool?)stale["ok"]);
        Assert.Equal("PLAN_STALE", (string?)stale["error"]?["code"]);
        Assert.Equal("not-started", (string?)stale["meta"]?["execution"]);
        Assert.Equal(0, calls);
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Compile_device_reviewed_apply_dispatches_the_same_request(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var requests = new List<PlcSoftwareRequest>();
        var service = new Services.PlcOrganisationPortService((_, arguments) => {
            requests.Add(JsonSerializer.Deserialize<PlcSoftwareRequest>(arguments["request"]!.ToJsonString())!);
            return new JsonObject { ["Message"] = "Compiled", ["Meta"] = new JsonObject {
                ["operationSuccess"] = true, ["effectiveState"] = "Success", ["nativeCompleted"] = true,
                ["errorCount"] = 0, ["warningCount"] = 0 } };
        }, () => true, () => Project);
        var tools = new Host.PlcCompilePortTools(service);
        string[] device = { "PLC/one,exact" }, item = { "CPU", "Module" };
        var preview = tools.CompileDevice(device, item).StructuredContent!;
        Assert.True((bool?)preview["ok"], preview.ToJsonString());
        Assert.True((bool?)preview["data"]?["targetBound"]);
        Assert.Empty(requests);
        var body = tools.CompileDevice(device, item, false, (string)preview["data"]!["planHash"]!, Project).StructuredContent!;
        Assert.True((bool?)body["ok"], body.ToJsonString());
        Assert.Equal((string?)preview["data"]?["planHash"], (string?)body["data"]?["planHash"]);
        var request = Assert.Single(requests);
        Assert.Equal("CompileDevice", request.Operation);
        Assert.Equal(device, request.DevicePath);
        Assert.Equal(item, request.ItemPath);
        Assert.False(request.DryRun);
        Assert.Equal(0, fixture.Worker.Calls);
    }
}
