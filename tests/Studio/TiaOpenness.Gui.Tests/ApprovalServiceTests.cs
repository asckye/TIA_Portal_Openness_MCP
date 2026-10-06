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
            await Eventually(() => service.Requests.Single().State == ApprovalState.Partial);
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
    private static async Task Eventually(Func<bool> check)
    { for (int i = 0; i < 100 && !check(); i++) await Task.Delay(20); Assert.True(check()); }
}
