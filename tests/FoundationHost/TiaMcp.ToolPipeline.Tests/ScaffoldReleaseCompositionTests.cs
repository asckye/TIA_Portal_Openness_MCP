using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TiaMcp.Adapters.Contracts;
using TiaMcp.Adapters.Contracts.Tests;
using TiaMcp.FoundationHost;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens.Services;
using Xunit;

namespace TiaMcp.EngineHost.Tests;

public sealed class ScaffoldReleaseCompositionTests
{
    public ScaffoldReleaseCompositionTests()
    {
        Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Path.Combine(AppContext.BaseDirectory, "fixture-data"));
    }

    public static IEnumerable<object[]> Releases()
        => new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" }.Select(key => new object[] { key });

    [Theory, InlineData("14sp1"), InlineData("17"), InlineData("19")]
    public void Supported_full_composition_runs_in_the_same_order_and_dry_run_is_offline(string release)
    {
        string directory = Path.Combine(Path.GetTempPath(), "scaffold-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "Main.scl");
            File.WriteAllText(source, "FUNCTION Main : Void\nEND_FUNCTION");
            var fake = new ScaffoldWorkerFake(release);
            var tools = Tools(fake);
            // Devices are created exactly as specified (decided 2026-10-19), so the spec names the PLC and HMI types.
            var spec = new JsonObject { ["projectName"] = "P", ["directoryPath"] = directory,
                ["plcMlfb"] = "6ES7 513-1AM03-0AB0", ["plcVersion"] = "V3.0",
                ["sclSourceFiles"] = new JsonArray(source) };
            if (release == "19")
            {
                spec["hmiName"] = "HMI_1";
                spec["hmiMlfb"] = "OrderNumber:6AV2 155-0AA00-0AA0/19.0.0.0";
                spec["hmiScreens"] = new JsonArray(new JsonObject { ["screenName"] = "Main", ["designJson"] = new JsonObject() });
                spec["hmiTags"] = new JsonArray(new JsonObject { ["tagName"] = "Start" });
            }
            Assert.True(tools.ScaffoldProject(spec.ToJsonString()).Ok);
            Assert.Empty(fake.Calls);
            Assert.Empty(fake.Primitives);
            var result = tools.ScaffoldProject(spec.ToJsonString(), false);
            Assert.True(result.Ok);
            var order = new List<string> { "connect", "createProject", "addDevice" };
            if (release == "19") order.Add("addDevice");
            order.AddRange(new[] { "sclImport", "sclGenerate", "compile" });
            if (release == "19") order.AddRange(new[] { "hmiInfo", "hmiConnect", "hmiScreen", "hmiDesign", "hmiTag" });
            order.Add("save");
            Assert.Equal(order, fake.Primitives);
            Assert.All(result.Steps, step => Assert.Equal("ok", step.Status));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory, MemberData(nameof(Releases))]
    public void Unsupported_plan_steps_are_reported_in_dry_run_and_refused_before_connect(string release)
    {
        string previousRelease = EngineHostConfiguration.ReleaseKey;
        EngineHostConfiguration.ReleaseKey = release;
        using var session = EngineHostConfiguration.Enter(new EngineSessionContext(new AdmissionWorker()) {
            Catalog = new FoundationCatalog(release, Array.Empty<McpServerTool>())
        });
        try
        {
            var fake = new ScaffoldWorkerFake(release);
            var tools = Tools(fake);
            var plan = new JsonObject { ["projectName"] = "P", ["hmiName"] = "HMI_1",
                ["ladDocs"] = new JsonArray(new JsonObject { ["importPath"] = "unused", ["name"] = "Main" }),
                ["hmiScreens"] = new JsonArray(new JsonObject { ["screenName"] = "Main", ["designJson"] = new JsonObject() }),
                ["hmiTags"] = new JsonArray(new JsonObject { ["tagName"] = "Start" }) };
            var dry = tools.ScaffoldProject(plan.ToJsonString());
            var refused = dry.Steps.Where(step => step.Detail?.StartsWith("UNSUPPORTED_CAPABILITY:", StringComparison.Ordinal) == true).ToArray();
            string[] expected = release is "20" or "21" ? Array.Empty<string>() : release == "19" ? new[] { "lad" }
                : new[] { "lad", "hmiConnection", "hmiScreen", "hmiDesign", "hmiTag" };
            Assert.Equal(expected, refused.Select(step => step.Step));
            Assert.Empty(fake.Calls);
            Assert.Empty(fake.Primitives);
            if (expected.Length == 0) return;
            Assert.False(dry.Ok);
            Assert.Throws<NotSupportedException>(() => tools.ScaffoldProject(plan.ToJsonString(), false));
            foreach (bool preview in new[] { true, false })
            {
                var envelope = tools.BuildProjectScaffoldV4(plan.ToJsonString(), preview).StructuredContent!;
                Assert.Equal("UNSUPPORTED_CAPABILITY", (string?)envelope["error"]?["code"]);
                Assert.Equal("rejected-before-operation", (string?)envelope["meta"]?["outcome"]);
                Assert.Equal("not-started", (string?)envelope["meta"]?["execution"]);
                Assert.Equal(release, (string?)envelope["meta"]?["releaseKey"]);
                if (preview)
                    Assert.Equal(expected, envelope["data"]!["steps"]!.AsArray()
                        .Where(step => ((string?)step?["detail"])?.StartsWith("UNSUPPORTED_CAPABILITY:", StringComparison.Ordinal) == true)
                        .Select(step => (string?)step?["step"]));
            }
            Assert.Empty(fake.Calls);
            Assert.Empty(fake.Primitives);
        }
        finally { EngineHostConfiguration.ReleaseKey = previousRelease; }
    }

    [Theory, InlineData("20", "{"), InlineData("21", "{"),
        InlineData("20", "{\"projectName\":\"P\",\"projectName\":\"Q\"}"),
        InlineData("21", "{\"projectName\":\"P\",\"projectName\":\"Q\"}")]
    public void Malformed_specs_keep_the_existing_session_error_envelope(string release, string spec)
    {
        string previousRelease = EngineHostConfiguration.ReleaseKey;
        EngineHostConfiguration.ReleaseKey = release;
        using var session = EngineHostConfiguration.Enter(new EngineSessionContext(new AdmissionWorker()) {
            Catalog = new FoundationCatalog(release, Array.Empty<McpServerTool>())
        });
        try
        {
            var fake = new ScaffoldWorkerFake(release);
            var tools = Tools(fake);
            var expected = SessionToolContract.Run("BuildProjectScaffold", false, true, () => tools.ScaffoldProject(spec)).StructuredContent!;
            var actual = tools.BuildProjectScaffoldV4(spec).StructuredContent!;
            Assert.NotNull(expected["error"]);
            Assert.True(JsonNode.DeepEquals(expected["error"], actual["error"]));
            foreach (string key in new[] { "outcome", "execution", "completeness" })
                Assert.Equal((string?)expected["meta"]?[key], (string?)actual["meta"]?[key]);
            Assert.Empty(fake.Calls);
            Assert.Empty(fake.Primitives);
        }
        finally { EngineHostConfiguration.ReleaseKey = previousRelease; }
    }

    private static ProjectLifecycleProjectTools Tools(ScaffoldWorkerFake fake)
    {
        JsonNode? Call(string operation, JsonObject arguments)
            => operation == "project-lifecycle.ExecuteProjectScaffoldPrimitive"
                ? JsonSerializer.SerializeToNode(fake.Execute(JsonSerializer.Deserialize<ProjectScaffoldPrimitive>(arguments["request"]!.ToJsonString())!))
                : fake.Call(operation, arguments);
        return new ProjectLifecycleProjectTools(new ProjectLifecyclePortService(Call,
            () => fake.ProjectFile.Length > 0, () => fake.ProjectFile, fake.Release));
    }

    private sealed class AdmissionWorker : IEngineWorker
    {
        public bool Faulted => false;
        public JsonNode? Binding => null;
        public object SessionKey { get; } = new();
        public JsonObject Snapshot() => throw new InvalidOperationException("Unexpected worker observation.");
        public Task<JsonObject> Status(CancellationToken token) => throw new InvalidOperationException("Unexpected worker observation.");
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new InvalidOperationException("Unexpected worker restart.");
        public Task<IDisposable> Acquire(CancellationToken token) => throw new InvalidOperationException("Unexpected worker acquisition.");
        public Task<EngineReply> Invoke(string id, string name, JsonObject arguments, bool preview, CancellationToken token)
            => throw new InvalidOperationException("Unexpected worker dispatch.");
        public void Dispose() { }
    }
}
