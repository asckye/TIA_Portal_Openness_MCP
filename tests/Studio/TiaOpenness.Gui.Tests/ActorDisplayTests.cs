using System;
using System.Linq;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class ActorDisplayTests(WpfContext wpf)
{
    [Theory]
    [InlineData(AppLanguage.English, "Human", "Workbench (human)", "Unrecorded")]
    [InlineData(AppLanguage.Chinese, "人", "工作台（人）", "未记录")]
    public void Calls_approvals_and_audit_show_localized_actor_and_filters_preserve_pending_calls(
        AppLanguage language, string human, string client, string unrecorded)
    {
        wpf.Run(() =>
        {
            var previous = Loc.Current.Language; Loc.Current.Language = language;
            try
            {
                var approvals = FeaturePageFixtures.Approvals(false);
                var journal = new FeaturePageFixtures.Journal();
                var template = journal.Calls[0];
                journal.Replace([template with { RequestId = "ai", JournalKey = "ai", Actor = "mcp" },
                    template with { RequestId = "human", JournalKey = "human", Actor = "workbench", McpSession = new string('a', 64) },
                    template with { RequestId = "legacy", JournalKey = "legacy", Actor = null }]);
                var audit = new FeaturePageFixtures.Audit { Events = [new AuditEvent(1, FeaturePageFixtures.Now, AuditEventType.End, "SaveProject", "succeeded", "hash") { Actor = "workbench" }] };
                using var model = new FeaturePagesViewModel(approvals, journal, audit, new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics(), () => FeaturePageFixtures.Now);
                Assert.Equal(human, model.Calls.Single(r => r.Record.RequestId == "human").Actor);
                Assert.Equal("AI", model.Calls.Single(r => r.Record.RequestId == "ai").Actor);
                Assert.Equal(unrecorded, model.Calls.Single(r => r.Record.RequestId == "legacy").Actor);
                Assert.Equal(human, Assert.Single(model.AuditRows).Actor);
                approvals.Receive(FeaturePageFixtures.Request("panel") with { Actor = "workbench" }); WpfContext.Drain();
                var approval = Assert.Single(model.Requests); Assert.Equal(human, approval.Actor); Assert.Equal(client, approval.Client);
                Assert.Contains(client, model.PendingSummary);
                model.ToggleFollow(); model.HumanOnly = true; Assert.Equal(2, model.Calls.Count); Assert.True(model.Calls[0].Pending);
                model.AiOnly = true; Assert.False(model.HumanOnly); Assert.Equal("ai", Assert.Single(model.Calls).Record.RequestId);
                model.AiOnly = false; Assert.Equal(4, model.Calls.Count);
                var previousAuditRow = Assert.Single(model.AuditRows);
                Loc.Current.Language = language == AppLanguage.English ? AppLanguage.Chinese : AppLanguage.English; WpfContext.Drain();
                Assert.NotEqual(human, approval.Actor); Assert.NotEqual(client, approval.Client);
                Assert.NotEqual(human, Assert.Single(model.AuditRows).Actor);
                Assert.NotSame(previousAuditRow, Assert.Single(model.AuditRows));
            }
            finally { Loc.Current.Language = previous; }
        });
    }
}
