extern alias toolpipeline;
using System.Reflection;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using Xunit;
using Profile = toolpipeline::TiaMcp.FoundationHost.ReleaseCatalogData;

[Collection(BundleSelectionCollection.Name)]
public sealed class UnifiedSessionPolicyTests
{
    public static IEnumerable<object[]> Releases() => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(Releases))]
    public void Session_status_and_observation_policy_is_identical_on_every_release(string release)
    {
        var profile = Profile.Releases[release];
        using var worker = new WorkerClient(release, "unused", "unused", false);
        Assert.True(worker.SharedSession);
        foreach (string name in new[] { "SharedSession", "ExtendedWorkerStatus", "NativeObservations", "StartSupervisorOnRead", "ObservationSet" })
        {
            var property = typeof(Profile).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            object? value = property.GetValue(profile);
            if (name == "ObservationSet")
                Assert.Equal(new[] { "environment", "group", "group.fix", "assemblies", "tool-names", "reflection", "reflection-deny",
                    "session.GetState", "session.ListPortalProcessProjects", "session.GetProjectTree", "session.ConnectPortal",
                    "session.ValidateAutomationContext" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(value));
            else Assert.Equal(name != "StartSupervisorOnRead", Assert.IsType<bool>(value));
        }
        foreach (string name in new[] { "StatusOutcomeIsFault", "RevealSessionEndedDiagnostic", "ObservationToolNames" })
            Assert.Null(typeof(Profile).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Theory, MemberData(nameof(Releases))]
    public async Task Observations_use_native_facts_and_the_actual_roster(string release)
    {
        using var worker = new ObservationWorker();
        var services = FoundationHostPorts.CreateServices(worker, release, () => new JsonObject { ["ready"] = true }, null);
        foreach (string operation in new[] { "environment", "group", "reflection", "reflection-deny", "session.GetState",
            "session.GetProjectTree", "session.ConnectPortal", "session.ValidateAutomationContext" })
        {
            var result = await services.Observe(operation, new(), CancellationToken.None);
            Assert.Equal(operation, (string?)result?["nativeOperation"]);
        }
        await services.Observe("group.fix", new(), CancellationToken.None);
        Assert.Equal("project-lifecycle.EnsureOpennessUserGroup", worker.Calls.Last());
        await services.Observe("session.ListPortalProcessProjects", new(), CancellationToken.None);
        Assert.Equal("ReadPortalProcessProjects", worker.Calls.Last());
        Assert.Equal(10, worker.Calls.Count);
        Assert.Empty((await services.Observe("tool-names", new(), CancellationToken.None))!.AsArray());
        using var pipeline = new toolpipeline::TiaMcp.FoundationHost.EngineHostPipeline(release, services,
            Array.Empty<ModelContextProtocol.Server.McpServerTool>());
        var tools = pipeline.AllTools;
        services.Roster = () => tools;
        Assert.Equal(tools.Select(tool => tool.ProtocolTool.Name),
            (await services.Observe("tool-names", new(), CancellationToken.None))!.AsArray().Select(name => (string)name!));
    }

    private sealed class ObservationWorker : IFoundationWorker
    {
        internal readonly List<string> Calls = new();
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken cancellationToken)
        {
            Calls.Add(operation);
            return Task.FromResult<JsonNode?>(operation == "ReadPortalProcessProjects" ? new JsonObject { ["Processes"] = new JsonArray() }
                : new JsonObject { ["nativeOperation"] = arguments["operation"]?.DeepClone() });
        }
        public void Dispose() { }
    }
}
