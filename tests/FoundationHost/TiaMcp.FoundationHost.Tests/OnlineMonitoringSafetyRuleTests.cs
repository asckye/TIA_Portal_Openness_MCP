extern alias enginehost;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcp.Engine.Tests.Shared;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Metadata = TiaMcpServer.ModelContextProtocol.ToolMetadata;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class OnlineMonitoringSafetyRuleTests
{
    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();

    [Fact]
    public void Force_rule_allows_the_read_tool_and_the_reviewed_write()
    {
        Assert.Empty(Forbidden(new[] { "ListPlcForceTables", "ManageWatchForceTableWebAccess" }, Metadata.Find));
    }

    [Theory]
    [InlineData("ForcePlcValue", null, false)]
    [InlineData("GetForceValue", null, false)]
    [InlineData("ListPlcForceValues", "READ", false)]
    [InlineData("ImportPlcForceTable", "WRITE", false)]
    [InlineData("StartPlcForce", "ONLINE-WRITE", false)]
    public void Force_rule_refuses_everything_else(string name, string? operation, bool readOnly)
    {
        Metadata.Classification? Find(string _) => operation == null ? null
            : new Metadata.Classification("L1", "PLC-Online", operation, false, false, false, readOnlyHint: readOnly);
        Assert.Equal(new[] { name }, Forbidden(new[] { name }, Find));
    }

    [Theory, MemberData(nameof(Releases))]
    public void Self_test_passes_on_the_registered_roster(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var prior = Host.HostToolServices.Override;
        try
        {
            Host.HostToolServices.Override = (operation, arguments) => Observe(fixture, operation, arguments);
            var response = new Host.HostMetaTools().RunOnlineMonitoringSafetySelfTest();
            Assert.Equal("pass", Assert.Single(response.Items!, item => item.Id == "safety.no-force-tools").Status);
            Assert.Equal("pass", Assert.Single(response.Items!, item => item.Id == "safety.required-readonly-tools").Status);
            Assert.True(response.Ok);
            Assert.Equal(fixture.Pipeline.AllTools.Count, (int?)response.Meta!["checkedTools"]);
        }
        finally { Host.HostToolServices.Override = prior; }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Acceptance_report_is_ok_when_the_checks_pass(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        string parent = Path.Combine(TestPaths.RepositoryRoot(AppContext.BaseDirectory), "bin-build");
        string directory = Path.Combine(parent, "safety-report-" + Guid.NewGuid().ToString("N"));
        var prior = Host.HostToolServices.Override;
        try
        {
            Host.HostToolServices.Override = (operation, arguments) => Observe(fixture, operation, arguments);
            var response = await new Host.HostMetaTools().GenerateAcceptanceReport(directory);
            Assert.True(response.Ok);
            Assert.True(File.Exists(response.MarkdownPath));
            Assert.True(File.Exists(response.JsonPath));
            Assert.True((bool?)JsonNode.Parse(File.ReadAllText(response.JsonPath))!["Ok"]);
        }
        finally
        {
            Host.HostToolServices.Override = prior;
            Assert.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, Path.GetFullPath(directory));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, InlineData("20"), InlineData("21")]
    public async Task Tool_names_observation_returns_the_registered_roster(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        var services = FoundationHostPorts.CreateServices(fixture.Worker, release, () => new JsonObject { ["ready"] = true }, null);
        services.Roster = () => fixture.Pipeline.AllTools;
        var observed = await services.Observe("tool-names", new JsonObject(), CancellationToken.None);
        Assert.Equal(fixture.Pipeline.AllTools.Select(tool => tool.ProtocolTool.Name),
            observed!.AsArray().Select(name => (string)name!));
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Capability_self_test_reports_a_bound_session_without_a_worker_call(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var prior = Host.HostToolServices.Override;
        try
        {
            Host.HostToolServices.Override = (operation, arguments) => Observe(fixture, operation, arguments);
            var response = await new Host.HostMetaTools().RunCapabilitySelfTest(connectIfNeeded: false, includeProjectTree: true);
            var connection = Assert.Single(response.Items!, item => item.Id == "tia.connection");
            Assert.Equal("pass", connection.Status);
            Assert.Contains("Project=Fixture", connection.Detail);
            var project = Assert.Single(response.Items!, item => item.Id == "project.open");
            Assert.Equal("pass", project.Status);
            Assert.Equal("Project 'Fixture' is bound. Project-specific checks run only with connectIfNeeded=true.", project.Detail);
            Assert.Null(response.ProjectTree);
            Assert.DoesNotContain(response.Items!, item => item.Id is "project.tree" or "project.automation-context");
        }
        finally { Host.HostToolServices.Override = prior; }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Capability_self_test_with_no_binding_still_warns(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        fixture.Worker.Bound = false;
        using var session = fixture.Pipeline.EnterSession();
        var prior = Host.HostToolServices.Override;
        try
        {
            Host.HostToolServices.Override = (operation, arguments) => Observe(fixture, operation, arguments);
            var response = await new Host.HostMetaTools().RunCapabilitySelfTest();
            Assert.Equal("warn", Assert.Single(response.Items!, item => item.Id == "tia.connection").Status);
            Assert.Equal("warn", Assert.Single(response.Items!, item => item.Id == "project.open").Status);
        }
        finally { Host.HostToolServices.Override = prior; }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Capability_self_test_with_connect_enabled_keeps_project_checks(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        fixture.Worker.Bound = false;
        using var session = fixture.Pipeline.EnterSession();
        var prior = Host.HostToolServices.Override;
        var operations = new List<string>();
        bool connected = false;
        try
        {
            Host.HostToolServices.Override = (operation, _) =>
            {
                operations.Add(operation);
                return operation switch
                {
                    "group" => JsonValue.Create(true),
                    "session.GetState" => new JsonObject { ["IsConnected"] = connected, ["Project"] = connected ? "Fixture" : "-",
                        ["Session"] = "-" },
                    "session.ConnectPortal" => JsonValue.Create(connected = true),
                    "session.ValidateAutomationContext" => new JsonObject { ["Message"] = "Fixture validated.",
                        ["Meta"] = new JsonObject { ["success"] = true } },
                    "session.GetProjectTree" => JsonValue.Create("Fixture\n  PLC_1"),
                    _ => throw new InvalidOperationException("Unexpected diagnostic operation: " + operation)
                };
            };
            var response = await new Host.HostMetaTools().RunCapabilitySelfTest(connectIfNeeded: true, includeProjectTree: true);
            Assert.True(response.Ok);
            Assert.Equal("pass", Assert.Single(response.Items!, item => item.Id == "project.automation-context").Status);
            Assert.Equal("pass", Assert.Single(response.Items!, item => item.Id == "project.tree").Status);
            Assert.Equal("Fixture\n  PLC_1", response.ProjectTree);
            Assert.Equal(new[] { "group", "session.GetState", "session.ConnectPortal", "session.GetState",
                "session.ValidateAutomationContext", "session.GetProjectTree" }, operations);
        }
        finally { Host.HostToolServices.Override = prior; }
        Assert.Equal(0, fixture.Worker.Calls);
    }

    private static IReadOnlyList<string> Forbidden(IEnumerable<string> names, Func<string, Metadata.Classification?> find)
    {
        var method = typeof(Host.OnlineMonitoringPolicy).GetMethod("ForbiddenForceTools", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(method.Invoke(null, new object[] { names, find }));
    }

    private static JsonNode? Observe(PreviewHostFixture fixture, string operation, JsonObject arguments) => operation switch
    {
        "tool-names" => new JsonArray(fixture.Pipeline.AllTools.Select(tool => (JsonNode)JsonValue.Create(tool.ProtocolTool.Name)!).ToArray()),
        "group" => JsonValue.Create(true),
        "reflection" => new JsonObject { ["guard"] = true, ["DescribeService"] = true, ["InvokeService"] = true,
            ["InvokeObject"] = true, ["probe"] = true },
        "reflection-deny" => JsonValue.Create((string?)arguments["method"] == "GetAttribute" ? null : "Operation denied."),
        _ => throw new InvalidOperationException("Unexpected diagnostic operation: " + operation)
    };
}
