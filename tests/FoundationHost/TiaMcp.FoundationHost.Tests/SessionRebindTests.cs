extern alias toolpipeline;
using System.Text.Json.Nodes;
using TiaMcp.FoundationHost;
using TiaMcp.WorkerChannel;
using Host = toolpipeline::TiaMcpServer.ModelContextProtocol;
using Xunit;

namespace TiaMcp.FoundationHost.Tests;

[Collection(WorkerDataDirectoryCollection.Name)]
public sealed class SessionRebindTests
{
    public static IEnumerable<object[]> ReleasesAndSessions()
    {
        // Keep explicit non-shared protocol coverage; the production session policy is shared on every release.
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

    [Theory]
    [InlineData("OpenProject", "{\"dryRun\":false,\"openMode\":\"secondary\"}", false)]
    [InlineData("OpenProject", "{\"dryRun\":true,\"openMode\":\"secondary\"}", false)]
    [InlineData("CloseProject", "{\"dryRun\":false,\"secondaryProjectFile\":\"C:/Reference.ap21\"}", false)]
    [InlineData("CloseProject", "{\"dryRun\":true,\"secondaryProjectFile\":\"C:/Reference.ap21\"}", false)]
    [InlineData("OpenProject", "{\"dryRun\":false,\"openMode\":\"primary\"}", true)]
    [InlineData("OpenProject", "{\"dryRun\":false}", true)]
    [InlineData("CloseProject", "{\"dryRun\":false,\"secondaryProjectFile\":\"\"}", true)]
    public void Secondary_operations_preserve_the_primary_binding_epoch(string operation, string json, bool advances)
        => Assert.Equal(advances ? BindingChange.Advance : BindingChange.None,
            WorkerClient.BindingChangeFor(operation, JsonNode.Parse(json)!.AsObject()));

    public static IEnumerable<object[]> SecondaryReleasesAndSessions()
        => ReleasesAndSessions().Where(row => (string)row[0] != "14sp1");

    [Theory, MemberData(nameof(SecondaryReleasesAndSessions))]
    public async Task Secondary_round_trip_preserves_host_identity_and_publishes_only_the_reference_file(string release, bool shared)
    {
        using var worker = Worker(release, shared: shared);
        await worker.Call("Attach", new() { ["processId"] = Environment.ProcessId }, CancellationToken.None);
        string suffix = TiaMcp.Adapters.PlcLifecyclePolicy.FileSuffix(release);
        string primary = Path.Combine(AppContext.BaseDirectory, "Primary.ap" + suffix);
        await worker.Call("BindProject", new() { ["projectName"] = "Primary", ["expectedProjectFile"] = primary }, CancellationToken.None);
        string identity = worker.ApprovalIdentity;
        string reference = Path.Combine(AppContext.BaseDirectory, "Reference.ap" + suffix);
        var open = new JsonObject { ["path"] = reference, ["openMode"] = "secondary", ["dryRun"] = true };
        await worker.Call("OpenProject", open, CancellationToken.None);
        Assert.False(SharedSessionState.For(worker).Snapshot(release).ContainsKey("secondaryProjectFile"));
        open["dryRun"] = false; open["confirm"] = true; open["expectedProjectFile"] = reference;
        await worker.Call("OpenProject", open, CancellationToken.None);
        Assert.Equal(identity, worker.ApprovalIdentity);
        var state = SharedSessionState.For(worker).Snapshot(release);
        Assert.Equal(reference, (string?)state["secondaryProjectFile"]);
        Assert.Equal(primary, (string?)state["binding"]?["identity"]?["projectPath"]);
        string savedPrimary = Path.Combine(AppContext.BaseDirectory, "SavedPrimary.ap" + suffix);
        await worker.Call("project-lifecycle.ArchiveSaveProjectCopy", new() { ["path"] = savedPrimary, ["dryRun"] = false }, CancellationToken.None);
        state = SharedSessionState.For(worker).Snapshot(release);
        Assert.Equal(savedPrimary, (string?)state["binding"]?["identity"]?["projectPath"]);
        Assert.Equal(reference, (string?)state["secondaryProjectFile"]);
        identity = worker.ApprovalIdentity;
        var close = new JsonObject { ["secondaryProjectFile"] = reference, ["dryRun"] = true };
        await worker.Call("CloseProject", close, CancellationToken.None);
        Assert.Equal(reference, (string?)SharedSessionState.For(worker).Snapshot(release)["secondaryProjectFile"]);
        close["dryRun"] = false; close["confirm"] = true; close["expectedProjectFile"] = reference;
        await worker.Call("CloseProject", close, CancellationToken.None);
        Assert.Equal(identity, worker.ApprovalIdentity);
        Assert.False(SharedSessionState.For(worker).Snapshot(release).ContainsKey("secondaryProjectFile"));
        Assert.False(worker.Faulted);
    }

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
