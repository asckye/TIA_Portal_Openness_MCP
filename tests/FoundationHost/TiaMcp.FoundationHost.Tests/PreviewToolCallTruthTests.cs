extern alias enginehost;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.Logic.V4.Inputs;
using Xunit;
using Host = enginehost::TiaMcpServer.ModelContextProtocol.McpServer;
using Context = enginehost::TiaMcp.FoundationHost.EngineHostConfiguration;

namespace TiaMcp.FoundationHost.Tests;

[Collection(BundleSelectionCollection.Name)]
public sealed class PreviewToolCallTruthTests
{
    public static IEnumerable<object[]> Releases() => CanonicalParameterRound2Tests.Releases();
    public static IEnumerable<object[]> ProjectCopyAndDownloadReleases() => new[] { "15.1", "16", "17", "18", "19", "20", "21" }
        .Select(release => new object[] { release });

    [Theory, MemberData(nameof(ProjectCopyAndDownloadReleases))]
    public void SaveProjectCopy_runs_at_once_and_changes_state(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("SaveProjectCopy", new { newProjectPath = "C:/Projects/Copy" });
        Assert.False((bool?)data["dryRun"]!["supported"]);
        Assert.False((bool?)data["dryRun"]!["effective"]);
        Assert.True((bool?)data["changesState"]);
        Assert.DoesNotContain("does not change the project", data["precautions"]!.ToJsonString());
        Assert.True((bool?)data["prerequisites"]!["needsProject"]);
        Assert.Contains("EXECUTES on this call", (string?)data["summary"]);
        Assert.Contains("dryRun: not available; this call runs for real.", data.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void ConnectOnlinePlc_is_not_called_read_only(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("ConnectOnlinePlc", new { softwarePath = "PLC_1" });
        Assert.True((bool?)data["changesState"]);
        Assert.DoesNotContain("read-only", data["precautions"]!.ToJsonString());
        Assert.Contains("Changes the online connection state", data["precautions"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(ProjectCopyAndDownloadReleases))]
    public void DownloadPlc_default_is_a_preview(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("DownloadPlc", new { softwarePath = "PLC_1" });
        Assert.True((bool?)data["dryRun"]!["supported"]);
        Assert.Null(data["dryRun"]!["given"]);
        Assert.True((bool?)data["dryRun"]!["effective"]);
        Assert.False((bool?)data["changesState"]);
        string precautions = data["precautions"]!.ToJsonString();
        Assert.DoesNotContain("maintainer", precautions);
        Assert.DoesNotContain("confirm flag", precautions);
        Assert.Contains("expectedPlanHash", precautions);
        Assert.Contains("expectedProjectFile", precautions);
        Assert.Contains("it is a preview (dryRun)", (string?)data["summary"]);
    }

    [Theory, MemberData(nameof(ProjectCopyAndDownloadReleases))]
    public void DownloadPlc_with_dryRun_false_executes(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("DownloadPlc", new { softwarePath = "PLC_1", dryRun = false });
        Assert.False((bool?)data["dryRun"]!["effective"]);
        Assert.True((bool?)data["changesState"]);
        Assert.Contains("EXECUTES (dryRun=false)", (string?)data["summary"]);
    }

    [Theory, MemberData(nameof(Releases))]
    public void Write_tool_with_a_confirm_flag_names_it(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("CreatePlcTagTable", new { plc = "PLC_1", group = "", name = "Tags" });
        Assert.Contains("confirm=true", data["precautions"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void GetSessionState_changes_nothing(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("GetSessionState", new { });
        Assert.False((bool?)data["dryRun"]!["supported"]);
        Assert.False((bool?)data["dryRun"]!["effective"]);
        Assert.False((bool?)data["changesState"]);
        Assert.Contains("does not change the project", data["precautions"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Prefill_refused_fields_are_not_ready(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("PrefillWorkbenchForm", new { form = "blockFilter", mode = "set", fields = new { namePattern = "^X_" } });
        Assert.False((bool?)data["ok"]);
        Assert.Contains("NOT READY", (string?)data["summary"]);
        Assert.Contains("Set for blockFilter requires exactly these fields: filter.", data["typeProblems"]!.ToJsonString());
    }

    [Theory, MemberData(nameof(Releases))]
    public void Prefill_right_fields_are_ready(string release)
    {
        using var fixture = new PreviewHostFixture(release);
        using var session = fixture.Pipeline.EnterSession();
        var data = Preview("PrefillWorkbenchForm", new { form = "blockFilter", mode = "set", fields = new { filter = "^X_" } });
        Assert.True((bool?)data["ok"]);
        Assert.StartsWith("READY", (string?)data["summary"]);
    }

    private static JsonObject Preview(string name, object arguments)
    {
        Assert.NotNull(Host.CatalogView.Find(name));
        var result = Host.PreviewToolCall(name, new ToolArguments(JsonSerializer.SerializeToElement(arguments)));
        return Assert.IsType<JsonObject>(result.StructuredContent!["data"]);
    }
}

internal sealed class PreviewHostFixture : IDisposable
{
    internal readonly CachedWorker Worker = new();
    internal readonly enginehost::TiaMcp.FoundationHost.EngineHostPipeline Pipeline;
    private readonly string release = Context.ReleaseKey;
    private readonly string profile = Host.ResolvedProfile();

    internal PreviewHostFixture(string key)
    {
        Context.ReleaseKey = key;
        Host.SetProfileOverride("full");
        Pipeline = new(key, Worker, LegacyHostToolRegistry.Create(Worker, key, false, hostPorts: false));
    }

    public void Dispose()
    {
        Pipeline.Dispose();
        Worker.Dispose();
        Context.ReleaseKey = release;
        Host.SetProfileOverride(profile);
    }

    internal sealed class CachedWorker : enginehost::TiaMcp.FoundationHost.IEngineWorker, IFoundationWorker
    {
        internal int Calls;
        internal bool Bound = true;
        public bool Faulted => false;
        public object SessionKey { get; } = new();
        public JsonNode? Binding => Bound ? new JsonObject { ["identity"] = new JsonObject {
            ["projectPath"] = "C:/Projects/Fixture.ap21", ["projectName"] = "Fixture", ["processId"] = 42 } } : null;
        public JsonObject Snapshot() => new() { ["session"] = new JsonObject {
            ["isConnected"] = Bound, ["project"] = Bound ? "Fixture" : "-", ["session"] = "-" } };
        public Task<JsonObject> Status(CancellationToken token)
        {
            Calls++;
            return Task.FromResult(Snapshot());
        }
        public Task<JsonObject> Restart(bool confirmed, CancellationToken token) => throw new NotSupportedException();
        public Task<IDisposable> Acquire(CancellationToken token) => throw new NotSupportedException();
        public Task<JsonNode?> Call(string operation, JsonObject arguments, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Diagnostics must use cached session state.");
        }
        public Task<enginehost::TiaMcp.FoundationHost.EngineReply> Invoke(
            string id, string name, JsonObject arguments, bool preview, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("Diagnostics must not dispatch a tool.");
        }
        public void Dispose() { }
    }
}
