extern alias enginehost;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.WorkerChannel;
using Host = enginehost::TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class SessionRebindTests
{
    public static IEnumerable<object[]> ReleasesAndSessions()
    {
        foreach (string release in new[] { "14sp1", "15.1", "16", "17", "18", "19", "20", "21" })
        foreach (bool shared in new[] { false, true })
            yield return new object[] { release, shared };
    }

    [Theory]
    [InlineData("project-lifecycle.ArchiveSaveProjectCopy", "{\"dryRun\":false}", true)]
    [InlineData("project-lifecycle.ArchiveSaveProjectCopy", "{\"dryRun\":true}", false)]
    [InlineData("project-lifecycle.ConnectIsolatedPortal", "{}", true)]
    [InlineData("project-lifecycle.RetrieveProjectArchive", "{\"request\":{\"DryRun\":false}}", true)]
    [InlineData("project-lifecycle.RetrieveProjectArchive", "{\"request\":{\"DryRun\":true}}", false)]
    [InlineData("project-lifecycle.ExecuteMultiuserSession", "{\"request\":{\"Action\":\"commit\",\"DryRun\":false}}", true)]
    [InlineData("project-lifecycle.ExecuteMultiuserSession", "{\"request\":{\"Action\":\"commit\",\"DryRun\":true}}", false)]
    [InlineData("project-lifecycle.ExecuteMultiuserSession", "{\"request\":{\"Action\":\"open\",\"DryRun\":false}}", false)]
    [InlineData("project-lifecycle.ExecuteProjectScaffoldPrimitive", "{\"request\":{\"Kind\":\"connect\"}}", true)]
    [InlineData("project-lifecycle.ExecuteProjectScaffoldPrimitive", "{\"request\":{\"Kind\":\"createProject\"}}", true)]
    [InlineData("project-lifecycle.ExecuteProjectScaffoldPrimitive", "{\"request\":{\"Kind\":\"compile\"}}", false)]
    public void Binding_change_table_declares_every_rebinding_operation(string operation, string json, bool rebind)
        => Assert.Equal(rebind ? BindingChange.MayAdvance : BindingChange.None,
            WorkerClient.BindingChangeFor(operation, JsonNode.Parse(json)!.AsObject()));

    [Theory, MemberData(nameof(ReleasesAndSessions))]
    public async Task Retrieve_project_archive_execute_rebinds_and_reports_succeeded(string release, bool shared)
    {
        using var worker = Worker(release, shared: shared);
        var result = await worker.Call("project-lifecycle.RetrieveProjectArchive", Retrieve("fixture"), CancellationToken.None);
        Assert.True((bool?)result?["Meta"]?["operationSuccess"]);
        Assert.Equal(1, (long?)worker.OperatorSnapshot()["binding"]?["bindingEpoch"]);
        Assert.False(worker.Faulted);
    }

    [Theory, MemberData(nameof(ReleasesAndSessions))]
    public async Task Retrieve_project_archive_documented_refusal_keeps_the_session(string release, bool shared)
    {
        using var worker = Worker(release, shared: shared);
        var result = await worker.Call("project-lifecycle.RetrieveProjectArchive", Retrieve("refused"), CancellationToken.None);
        Assert.Equal("PreconditionFailed", (string?)result?["Meta"]?["status"]);
        Assert.False(worker.Faulted);
        await worker.Call("ReadState", new(), CancellationToken.None);
    }

    [Theory, MemberData(nameof(ReleasesAndSessions))]
    public async Task Connect_isolated_portal_on_a_fresh_worker_rebinds_and_records_the_process(string release, bool shared)
    {
        using var worker = Worker(release, true, shared);
        await worker.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None);
        Assert.Equal(123, (int?)worker.OperatorSnapshot()["binding"]?["processId"]);
        await worker.Call("ReadBlocks", new(), CancellationToken.None);
        Assert.False(worker.Faulted);
    }

    [Theory, MemberData(nameof(ReleasesAndSessions))]
    public async Task Connect_isolated_portal_on_a_connected_worker_is_refused_not_unknown(string release, bool shared)
    {
        using var worker = Worker(release, shared: shared);
        await worker.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<WorkerOperationException>(() =>
            worker.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None));
        Assert.Equal("rejected-before-operation", error.Outcome);
        Assert.False(worker.Faulted);
    }

    [Theory, MemberData(nameof(ReleasesAndSessions))]
    public async Task Explicit_worker_restart_permits_an_isolated_connection(string release, bool shared)
    {
        using var worker = Worker(release, shared: shared);
        await worker.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None);
        var restart = await worker.RestartHostWorker(true, CancellationToken.None);
        Assert.True((bool?)restart["restarted"]);
        await worker.Call("project-lifecycle.ConnectIsolatedPortal", new(), CancellationToken.None);
        Assert.Equal(123, (int?)worker.OperatorSnapshot()["binding"]?["processId"]);
        Assert.False(worker.Faulted);
    }

    [Theory, MemberData(nameof(ReleasesAndSessions))]
    public async Task Lost_retrieve_reply_is_outcome_unknown_not_not_started(string release, bool shared)
    {
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT");
        Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", "crash");
        try
        {
            using var worker = Worker(release, shared: shared);
            using var request = FoundationWorkerRequest.Use(Guid.NewGuid().ToString("N"), "RetrieveProjectArchive");
            await worker.Call("FaultFixtureArm", new() { ["method"] = "adapter.project-lifecycle.RetrieveProjectArchive" }, CancellationToken.None);
            var cause = await Assert.ThrowsAsync<ChannelFaultException>(() => worker.Call(
                "project-lifecycle.RetrieveProjectArchive", Retrieve("fixture"), CancellationToken.None));
            var body = Host.PlcExchangeContract.Run("RetrieveProjectArchive", () => throw new Exception("Wrapped.", cause),
                true, true).StructuredContent!;
            Assert.Equal("OUTCOME_UNKNOWN", (string?)body["error"]?["code"]);
            Assert.Equal("unknown", (string?)body["meta"]?["execution"]);
            Assert.True(worker.Faulted);
            Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", null);
            var next = await worker.Call("ReadBlocks", new(), CancellationToken.None);
            Assert.Equal("fixture", (string?)next?[0]?["Name"]);
            Assert.False(worker.Faulted);
            Assert.Equal(2, (long?)worker.OperatorSnapshot()["generation"]);
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_PLC_WORKER_FAULT", previous); }
    }

    [Theory, MemberData(nameof(ReleasesAndSessions))]
    public async Task Save_as_refreshes_approval_project_and_os_process_start(string release, bool shared)
    {
        using var worker = Worker(release, shared: shared);
        await worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        string target = Path.Combine(AppContext.BaseDirectory, "renamed.ap" + release);
        await worker.Call("project-lifecycle.ArchiveSaveProjectCopy", new() { ["path"] = target, ["dryRun"] = false }, CancellationToken.None);
        var approval = JsonNode.Parse(worker.ApprovalIdentity)!;
        Assert.Equal(target, (string?)(approval["ProjectFile"] ?? approval["projectPath"]));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            (string?)worker.HostSnapshot()["binding"]?["identity"]?["processStartUtc"]);
        Assert.Equal(target, (string?)worker.HostSnapshot()["binding"]?["identity"]?["projectPath"]);
    }

    [Theory, MemberData(nameof(ReleasesAndSessions))]
    public async Task Commit_closing_the_project_clears_approval_project(string release, bool shared)
    {
        using var worker = Worker(release, shared: shared);
        await worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        string target = Path.Combine(AppContext.BaseDirectory, "committed.ap" + release);
        await worker.Call("project-lifecycle.ArchiveSaveProjectCopy", new() { ["path"] = target, ["dryRun"] = false }, CancellationToken.None);
        var before = JsonNode.Parse(worker.ApprovalIdentity)!;
        Assert.Equal(target, (string?)(before["ProjectFile"] ?? before["projectPath"]));
        await worker.Call("project-lifecycle.ExecuteMultiuserSession", new() { ["request"] = new JsonObject {
            ["Action"] = "commit", ["DryRun"] = false } }, CancellationToken.None);
        var approval = JsonNode.Parse(worker.ApprovalIdentity)!.AsObject();
        string projectKey = shared ? "projectPath" : "ProjectFile";
        Assert.True(approval.ContainsKey(projectKey));
        Assert.Null(approval[projectKey]);
        Assert.Null(worker.HostSnapshot()["binding"]?["identity"]?["projectPath"]);
        Assert.Equal(Environment.ProcessId, (int?)worker.HostSnapshot()["binding"]?["identity"]?["processId"]);
        string afterCommit = worker.ApprovalIdentity;
        await worker.Call("project-lifecycle.ReadProjectLifecycleBinding", new(), CancellationToken.None);
        Assert.Equal(afterCommit, worker.ApprovalIdentity);
        Assert.False(worker.Faulted);
    }

    [Fact]
    public void Typed_refusal_of_a_session_write_stays_rejected_before_operation()
    {
        var cause = new WorkerOperationException("Save As needs a single-user project.", -32603, "rejected-before-operation");
        cause.Data["foundationKnownNoMutation"] = true;
        var body = Host.SessionToolContract.Failure("SaveProjectCopy", new Exception("Wrapped.", cause), true, true).StructuredContent!;
        Assert.Equal("PRECONDITION_FAILED", (string?)body["error"]?["code"]);
        Assert.Equal("rejected-before-operation", (string?)body["meta"]?["outcome"]);
        Assert.False((bool?)body["meta"]?["requiresSessionReset"]);
        Assert.Contains("single-user", (string?)body["error"]?["message"]);
    }

    private static JsonObject Retrieve(string path) => new() { ["request"] = new JsonObject {
        ["ArchivePath"] = path, ["DestinationDirectory"] = "fixture", ["DryRun"] = false } };

    private static WorkerClient Worker(string release, bool bundled = false, bool shared = true)
    {
        string fixture = TiaMcp.Engine.Tests.Shared.TestPaths.WorkerFixture(
            "TiaMcp.PlcWorker.FaultFixture", "TiaMcp.PlcWorker.FaultFixture.exe", "net48");
        TiaMcp.Engine.Tests.Shared.TestPaths.WriteFixtureFile(
            Path.Combine(Path.GetDirectoryName(fixture)!, "TiaMcp.Adapter." + release + ".dll"), "offline fixture identity");
        return new WorkerClient(release, fixture, Path.GetDirectoryName(fixture)!, true) { Bundled = bundled, SharedSession = shared };
    }
}
