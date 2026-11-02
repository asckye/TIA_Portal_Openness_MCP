using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;
using Xunit;

namespace TiaOpenness.Gui.Tests;

public sealed class ApprovalServiceTests
{
    private static string Scratch() => Path.GetFullPath(Path.Combine("bin-build", "P6-44", "gui-approval", Guid.NewGuid().ToString("N")));
    [Theory]
    [InlineData("DeletePlcBlock", "{}", ApprovalRisk.Destructive)]
    [InlineData("DownloadPlc", "{}", ApprovalRisk.Destructive)]
    [InlineData("ImportPlcBlock", "{\"overwrite\":true}", ApprovalRisk.Write)]
    public async Task Real_pipe_transfers_host_risk_and_expired_requests_cannot_be_approved(string tool, string arguments, ApprovalRisk expected)
    {
        string root = Scratch(), pipe = "tia-gui-approval-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        using var service = new ApprovalService(Path.Combine(root, "approval.settings"), pipe, now: () => now);
        var arrived = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NewRequest += (_, row) => arrived.TrySetResult(row);
        var request = PendingApproval.Create("foundation", "21", tool, arguments, null, 120);
        var call = ApprovalClient.Wait(request, new ApprovalSettings(true, 120), CancellationToken.None, pipe);
        var row = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, row.Risk);
        if (tool == "ImportPlcBlock") Assert.Single(row.Risks);
        now = request.Deadline; service.Refresh();
        Assert.Equal(ApprovalState.TimedOut, service.Requests.Single().State);
        Assert.False(service.Approve(row.Id)); Assert.Equal(0, service.PendingCount);
        service.Dispose();
        Assert.NotNull((await call.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
        Assert.Equal(ApprovalState.TimedOut, service.Requests.Single().State);
    }

    [Fact]
    public async Task Real_queue_allows_only_the_first_request_and_waits_for_its_execution_outcome()
    {
        string root = Scratch(), pipe = "tia-gui-approval-" + Guid.NewGuid().ToString("N");
        using var service = new ApprovalService(Path.Combine(root, "approval.settings"), pipe);
        var first = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NewRequest += (_, row) => { if (!first.TrySetResult(row)) second.TrySetResult(row); };
        var call1 = ApprovalClient.Wait(PendingApproval.Create("foundation", "21", "WriteFixture", "{}", null, 120), new ApprovalSettings(), CancellationToken.None, pipe);
        var row1 = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var call2 = ApprovalClient.Wait(PendingApproval.Create("foundation", "21", "WriteFixture", "{}", null, 120), new ApprovalSettings(), CancellationToken.None, pipe);
        var row2 = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.Approve(row2.Id)); Assert.False(service.Deny(row2.Id));
        Assert.True(service.Approve(row1.Id));
        var approved = await call1.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.CanDecide(row2.Id)); Assert.False(service.Deny(row2.Id));
        await ApprovalClient.Complete(approved, "succeeded", pipe);
        await Eventually(service, () => service.Requests.First().State == ApprovalState.Completed);
        Assert.True(service.Deny(row2.Id)); Assert.Equal("denied", (await call2.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
    }
    [Fact]
    public async Task Workbench_v2_write_stays_pending_until_the_same_approval_card_is_decided()
    {
        string root = Scratch(), pipe = "tia-gui-approval-" + Guid.NewGuid().ToString("N"), operatorId = Guid.NewGuid().ToString("N");
        var audit = new AuditLog(Path.Combine(root, "audit"));
        using var service = new ApprovalService(Path.Combine(root, "approval.settings"), pipe, audit);
        var arrived = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NewRequest += (_, row) => arrived.TrySetResult(row);
        PendingApproval request;
        using (ActorScope.EnterWorkbench("attached-session", operatorId))
            request = PendingApproval.Create("foundation", "19", "WriteFixture", "{}", null, 5);
        var call = ApprovalClient.Wait(request, new ApprovalSettings(true, 5), CancellationToken.None, pipe, audit);
        var row = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("workbench", row.Actor); Assert.Equal(operatorId, row.OperatorCallId);
        Assert.Equal(ApprovalState.Pending, row.State); Assert.False(call.IsCompleted);
        Assert.True(service.Approve(row.Id));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(result.Reason);
        await ApprovalClient.Complete(result, "succeeded", pipe);
        await Eventually(service, () => service.Requests.Single().State == ApprovalState.Completed);
        Assert.Equal("workbench", Assert.Single(audit.Read()).Actor); Assert.True(audit.Verify().Passed);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_queue_decides_each_request_and_receives_execution_outcome(bool approve)
    {
        string root = Scratch(), pipe = "tia-gui-approval-" + Guid.NewGuid().ToString("N");
        var audit = new AuditLog(Path.Combine(root, "audit"));
        using var service = new ApprovalService(Path.Combine(root, "approval.settings"), pipe, audit);
        var arrived = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NewRequest += (_, row) => arrived.TrySetResult(row);
        var request = PendingApproval.Create("engine", "21", "WriteFixture", "{\"blockPath\":\"PLC/DB\"}", "{\"projectFile\":\"project.ap21\"}", 5);
        var call = ApprovalClient.Wait(request, new ApprovalSettings(true, 5), CancellationToken.None, pipe, audit);
        var row = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(request.RequestId, row.Id); Assert.Equal(request.PlanHash, row.PlanHash);
        Assert.Contains("project.ap21", row.Project); Assert.Single(row.Operations); Assert.Equal(1, service.PendingCount);
        Assert.True(approve ? service.Approve(row.Id) : service.Deny(row.Id)); Assert.False(service.Approve(row.Id));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(approve ? null : "denied", result.Reason);
        if (approve)
        {
            await ApprovalClient.Complete(result, "partial", pipe);
            await Eventually(service, () => service.Requests.Single().State == ApprovalState.Partial);
        }
        else Assert.Equal(ApprovalState.Rejected, service.Requests.Single().State);
        Assert.True(audit.Verify().Passed);
    }
    [Fact]
    public async Task Mcp_side_cannot_decide_and_closing_workbench_refuses_pending_call()
    {
        string root = Scratch(), pipe = "tia-gui-approval-" + Guid.NewGuid().ToString("N");
        using var service = new ApprovalService(Path.Combine(root, "approval.settings"), pipe);
        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(1000);
            await ApprovalFrames.Write(client, new ApprovalDecision { RequestId = "self", Decision = "granted" }, CancellationToken.None);
        }
        Assert.Empty(service.Requests);
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NewRequest += (_, _) => arrived.TrySetResult(true);
        var request = PendingApproval.Create("foundation", "19", "WriteFixture", "{}", null, 5);
        var call = ApprovalClient.Wait(request, new ApprovalSettings(true, 5), CancellationToken.None, pipe);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); service.Dispose();
        Assert.Equal("workbench-unavailable", (await call.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
        Assert.All(service.Requests, r => Assert.Equal(ApprovalState.Disconnected, r.State));
    }
    [Fact]
    public void Real_settings_survive_reopen_and_log_switches()
    {
        string root = Scratch(), path = Path.Combine(root, "approval.settings"); var audit = new AuditLog(Path.Combine(root, "audit"));
        using (var service = new ApprovalService(path, "tia-gui-approval-" + Guid.NewGuid().ToString("N"), audit))
        { service.TimeoutSeconds = 300; service.Enabled = false; Assert.False(service.Enabled); }
        using (var service = new ApprovalService(path, "tia-gui-approval-" + Guid.NewGuid().ToString("N"), audit))
        { Assert.False(service.Enabled); Assert.Equal(300, service.TimeoutSeconds); service.Enabled = true; }
        Assert.Equal(2, audit.Read().Count(r => r.Event == "approval-switch"));
    }
    private static async Task Eventually(ApprovalService service, Func<bool> check)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (check()) ready.TrySetResult(); }
        service.PropertyChanged += Changed;
        try { if (check()) ready.TrySetResult(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(check()); }
        finally { service.PropertyChanged -= Changed; }
    }
}
