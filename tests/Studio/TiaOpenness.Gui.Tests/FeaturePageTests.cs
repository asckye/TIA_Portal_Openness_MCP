using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchRenderFeaturePagesTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class FeaturePageTests(WpfContext wpf)
{
    [Fact]
    public void Product_stubs_do_not_fabricate_history_checks_or_exports()
    {
        var approval = new ApprovalServiceStub();
        var journal = new CallJournalServiceStub();
        var audit = new AuditLogServiceStub();
        var environment = new EnvironmentCheckService();
        var diagnostics = new DiagnosticBundleService();
        Assert.Empty(approval.Requests); Assert.Empty(journal.Calls); Assert.Empty(audit.Events);
        Assert.Equal(0, approval.PendingCount); Assert.False(approval.Approve("unknown"));
        Assert.Null(audit.Verify().Passed); Assert.Empty(journal.Connection.Address);
        Assert.All(environment.Groups.SelectMany(g => g.Rows), r => Assert.Equal(CheckStatus.Unchecked, r.Status));
        Assert.IsAssignableFrom<EnvironmentCheckService>(environment);
        Assert.IsAssignableFrom<Services.DiagnosticBundleService>(diagnostics);
        Assert.Equal(DiagnosticState.Idle, diagnostics.Progress.State);
        Assert.Throws<ArgumentOutOfRangeException>(() => approval.TimeoutSeconds = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => audit.FileSizeMb = 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => audit.Copies = 20);
    }

    [Fact]
    public void Approval_queue_serializes_decisions_and_never_executes_on_approval()
    {
        var now = FeaturePageFixtures.Now;
        var service = new ApprovalServiceStub(() => now);
        int received = 0; service.NewRequest += (_, _) => received++;
        service.Receive(FeaturePageFixtures.Request("one")); service.Receive(FeaturePageFixtures.Request("two"));
        Assert.Equal(2, received); Assert.Equal(2, service.PendingCount);
        Assert.False(service.Approve("two")); Assert.True(service.Approve("one"));
        Assert.Equal(ApprovalState.Approved, service.Requests[0].State);
        Assert.False(service.Approve("one")); Assert.False(service.Approve("two"));
        Assert.False(service.Transition("one", ApprovalState.Completed));
        Assert.True(service.Transition("one", ApprovalState.Executing));
        Assert.True(service.Transition("one", ApprovalState.Partial));
        Assert.True(service.Deny("two")); Assert.Equal(0, service.PendingCount);
        Assert.False(service.Transition("two", ApprovalState.Executing));
        Assert.Throws<ArgumentException>(() => service.Receive(FeaturePageFixtures.Request("one")));
    }

    [Fact]
    public void Deadline_wins_a_late_approval_and_disconnect_distinguishes_unstarted_work()
    {
        var now = FeaturePageFixtures.Now;
        var service = new ApprovalServiceStub(() => now);
        service.Receive(FeaturePageFixtures.Request("expired")); now = now.AddSeconds(118);
        Assert.False(service.Approve("expired")); Assert.Equal(ApprovalState.TimedOut, service.Requests[0].State);
        now = FeaturePageFixtures.Now;
        service.Receive(FeaturePageFixtures.Request("active")); service.Approve("active");
        service.Receive(FeaturePageFixtures.Request("waiting")); service.Disconnect();
        Assert.Equal(ApprovalState.Unknown, service.Requests[1].State);
        Assert.Equal(ApprovalState.Disconnected, service.Requests[2].State);
    }

    [Theory]
    [InlineData(ApprovalState.Rejected, "Approval.NoneExecuted")]
    [InlineData(ApprovalState.TimedOut, "Approval.NoneExecuted")]
    [InlineData(ApprovalState.Disconnected, "Approval.NoneExecuted")]
    [InlineData(ApprovalState.Unknown, "Approval.VerifyProject")]
    public void Terminal_request_notes_preserve_execution_uncertainty(ApprovalState state, string key)
    {
        wpf.Run(() =>
        {
            using var model = Model();
            var row = new ApprovalRow(FeaturePageFixtures.Request() with { State = state }, model);
            Assert.False(row.Pending); Assert.False(row.CanDecide); Assert.Equal(Loc.Current[key], row.Note);
        });
    }

    [Fact]
    public void Calls_filter_combine_and_pause_keeps_a_snapshot_until_resumed()
    {
        wpf.Run(() =>
        {
            var journal = new FeaturePageFixtures.Journal();
            using var model = Model(journal: journal);
            Assert.True(model.Calls[0].Pending);
            model.WriteOnly = true; Assert.Single(model.Calls);
            model.FailOnly = true; Assert.Empty(model.Calls);
            model.WriteOnly = false; Assert.Equal("ExportBlock", Assert.Single(model.Calls).Tool);
            model.FailOnly = false; model.Search = "listplc"; Assert.Equal("ListPlcBlocks", Assert.Single(model.Calls).Tool);
            model.Search = ""; model.ReleaseOnly = true; model.Release = "21"; Assert.Empty(model.Calls);
            model.Release = "14sp1"; Assert.Equal(4, model.Calls.Count);
            model.ToggleFollow(); journal.Replace([]); Assert.Equal(4, model.Calls.Count);
            Assert.False(model.FollowLatest); model.ToggleFollow(); Assert.True(Assert.Single(model.Calls).Pending);
        });
    }

    [Fact]
    public void Display_and_copy_redact_nested_secrets_and_malformed_json()
    {
        string input = "{\"api_key\":\"private-one\",\"nested\":[{\"accessToken\":\"private-two\",\"password\":\"private-three\"}],\"headers\":{\"Authorization\":\"Bearer private-four\"},\"value\":\"Bearer private-five\",\"block\":\"Main\"}";
        string redacted = FeatureJson.Redact(input);
        Assert.DoesNotContain("private-", redacted); Assert.Contains("Main", redacted); Assert.Contains("Bearer ••••", redacted);
        Assert.Equal("••••", FeatureJson.Redact("{broken secret"));
        wpf.Run(() =>
        {
            using var model = Model(); string? copied = null; model.CopyText = text => copied = text;
            model.Copy(model.ConnectionJson); Assert.DoesNotContain("fixture-private-key", copied!);
            model.OpenCall(model.Calls.Single(c => c.Tool == "ExportBlock"));
            model.Copy(model.SelectedCall!.Parameters); Assert.Contains("••••", copied!); Assert.DoesNotContain("fixture-private-key", copied!);
        });
    }

    [Fact]
    public void Audit_verification_retention_and_jump_use_service_records()
    {
        wpf.Run(() =>
        {
            var audit = new FeaturePageFixtures.Audit();
            using var model = Model(audit: audit);
            model.Verify(); Assert.False(model.VerificationBroken); Assert.Equal(FeatureTone.Success, model.VerificationTone);
            audit.Broken = true; model.Verify(); Assert.True(model.VerificationBroken);
            Assert.Equal(532, model.JumpToBreak()!.Record.Index); Assert.Single(model.AuditRows, r => r.Selected);
            model.SetRetention(50, true); model.SetRetention(10, false); Assert.Equal(50, audit.FileSizeMb); Assert.Equal(10, audit.Copies);
            model.Run(audit.OpenFolder); Assert.Equal(1, audit.OpenCount);
        });
    }

    [Fact]
    public void Environment_summary_and_bundle_states_follow_service_changes()
    {
        wpf.RunWithLanguage(AppLanguage.Chinese, () =>
        {
            var environment = new FeaturePageFixtures.Environment(); var diagnostics = new FeaturePageFixtures.Diagnostics();
            using var model = Model(environment: environment, diagnostics: diagnostics);
            Assert.Equal("3 项需要处理", model.EnvironmentSummary);
            model.Run(environment.Recheck); model.Run(() => environment.Fix("membership")); Assert.Equal(1, environment.RecheckCount); Assert.Equal("membership", environment.FixedId);
            Assert.True(model.DiagnosticIdle); model.Export(); Assert.True(model.DiagnosticRunning); Assert.False(model.DiagnosticCanStart); Assert.Equal(35, model.DiagnosticPercent);
            diagnostics.Complete(); Assert.True(model.DiagnosticDone); Assert.EndsWith(".zip", model.DiagnosticPath);
            environment.Replace([new(LocalizedText.Literal("TIA Portal"), [new("x", CheckStatus.Unchecked, LocalizedText.Literal("TIA"), LocalizedText.Empty, LocalizedText.Empty, LocalizedText.Empty)])]);
            Assert.Equal("未检查", model.EnvironmentSummary);
            Loc.Current.Language = AppLanguage.English; Assert.Equal("Not checked", model.EnvironmentSummary);
        });
    }

    [Fact]
    public void Real_controls_open_drawers_copy_toggle_parameters_and_deny()
    {
        wpf.Run(() =>
        {
            using var trace = new BindingPathTests.BindingTrace();
            var approvals = FeaturePageFixtures.Approvals();
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false, approvals);
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(), now: () => FeaturePageFixtures.Now);
                string? copied = null; window.Features.CopyText = text => copied = text;
                window.Navigate("Calls"); var host = Detach(window); Layout(host);
                var calls = (Views.AiCallsView)window.FindName("CallsContent");
                UnifiedDesktopTests.ClickControl(Descendants<Button>(calls).Single(b => b.DataContext is CallRow c && c.Tool == "ExportBlock")); Layout(host);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("CallDetailContent")).Visibility);
                UnifiedDesktopTests.ClickControl(Descendants<Button>((DependencyObject)window.FindName("CallDetailContent")).Single()); Assert.Contains("••••", copied!);
                window.Features.OpenApprovals(); Layout(host);
                var drawer = (Views.ApprovalsDrawer)window.FindName("ApprovalsContent");
                var parameters = Descendants<Button>(drawer).First(b => Equals(b.Content, Loc.Current["Approval.Parameters"]));
                UnifiedDesktopTests.ClickControl(parameters); Layout(host); Assert.True(window.Features.Requests[0].JsonOpen);
                UnifiedDesktopTests.ClickControl(Descendants<Button>(drawer).First(b => Equals(b.Content, Loc.Current["Approval.Deny"]))); Layout(host);
                Assert.Equal(0, approvals.PendingCount); Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void New_request_shows_badge_toast_and_minimized_notification_then_unsubscribes()
    {
        wpf.Run(() =>
        {
            var approvals = FeaturePageFixtures.Approvals(false); var notification = new FeaturePageFixtures.Notification();
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false, approvals);
            window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(), notification, () => FeaturePageFixtures.Now);
            try
            {
                approvals.Receive(FeaturePageFixtures.Request("normal"));
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("PendingBadge")).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("Toast")).Visibility); Assert.Equal(0, notification.Count);
                window.WindowState = WindowState.Minimized;
                approvals.Receive(FeaturePageFixtures.Request("minimized")); Assert.Equal(1, notification.Count);
                notification.Activate!(); Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ApprovalsContent")).Visibility);
            }
            finally { window.Close(); }
            approvals.Receive(FeaturePageFixtures.Request("after-close")); Assert.Equal(1, notification.Count);
        });
    }

    [Fact]
    public async Task Approval_events_from_a_transport_thread_are_marshaled_to_the_window()
    {
        MainWindow? window = null;
        var approvals = FeaturePageFixtures.Approvals(false);
        wpf.Run(() => window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false, approvals));
        try
        {
            await Task.Run(() => approvals.Receive(FeaturePageFixtures.Request()));
            wpf.Run(() =>
            {
                window!.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("PendingBadge")).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("Toast")).Visibility);
            });
        }
        finally { wpf.Run(() => window!.Close()); }
    }

    [Fact]
    public void Audit_jump_scrolls_to_the_broken_record_and_environment_detail_survives_progress()
    {
        wpf.Run(() =>
        {
            var diagnostics = new FeaturePageFixtures.Diagnostics();
            var audit = new FeaturePageFixtures.Audit { Broken = true };
            audit.Events = Enumerable.Range(0, 70).Select(i => new AuditEvent(600 - i, FeaturePageFixtures.Now,
                AuditEventType.End, "record-" + i, "done", "abc123")).ToArray();
            var environment = new FeaturePageFixtures.Environment();
            var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false, diagnostics: diagnostics);
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), audit, environment);
                window.Navigate("Audit"); var host = Detach(window); Layout(host);
                var view = (Views.AuditLogView)window.FindName("AuditContent");
                UnifiedDesktopTests.ClickControl((Button)view.FindName("VerifyButton")); Layout(host);
                UnifiedDesktopTests.ClickControl((Button)view.FindName("JumpButton")); Layout(host);
                Assert.True(((ScrollViewer)view.FindName("AuditScroll")).VerticalOffset > 0);
                Assert.Equal(532, Assert.Single(window.Features.AuditRows, r => r.Selected).Record.Index);
                window.Navigate("Environment"); Layout(host);
                var envView = (Views.EnvironmentView)window.FindName("EnvironmentContent");
                var expander = Descendants<Expander>(envView).First(); expander.IsExpanded = true;
                diagnostics.Export(); Layout(host); Assert.True(expander.IsExpanded); Assert.Contains(expander, Descendants<Expander>(envView));
                UnifiedDesktopTests.ClickControl(Descendants<Button>(envView).Single(b => b.DataContext is EnvironmentRow row && row.Check.Id == "membership"));
                Assert.Equal("membership", environment.FixedId);
            }
            finally { window.Close(); }
        });
    }

    private static FeaturePagesViewModel Model(FeaturePageFixtures.Journal? journal = null, FeaturePageFixtures.Audit? audit = null,
        FeaturePageFixtures.Environment? environment = null, FeaturePageFixtures.Diagnostics? diagnostics = null)
        => new(FeaturePageFixtures.Approvals(), journal ?? new(), audit ?? new(), environment ?? new(), diagnostics ?? new(), () => FeaturePageFixtures.Now);
}
