using System.Text.Json.Nodes;
using TiaOpenness.Shared;
using Xunit;

public sealed partial class PipelineTests
{
    [Theory, InlineData("20"), InlineData("21")]
    public async Task Pending_approval_and_audit_share_the_foundation_host_label(string release)
    {
        using var fixture = new Fixture(release);
        string root = Path.Combine(AppContext.BaseDirectory, "foundation-label", Guid.NewGuid().ToString("N"));
        var log = new AuditLog(root);
        using var audit = AuditInvocation.UseLog(log);
        PendingApproval? approval = null;
        fixture.Wait = (pending, _, _) =>
        {
            approval = pending;
            return Task.FromResult(new ApprovalOutcome(pending, false, "denied"));
        };
        var result = await fixture.Call("SaveProject", "{}");
        Assert.NotNull(approval);
        Assert.Equal("foundation", approval.Host);
        Assert.True(log.Verify().Passed);
        Assert.NotEmpty(log.Read());
        Assert.All(log.Read(), row => Assert.Equal("foundation", row.Host));
        Assert.DoesNotContain("SaveProject", fixture.Worker.Calls);
        Assert.Equal("not-started", (string?)result.StructuredContent?["meta"]?["execution"]);
    }
}
