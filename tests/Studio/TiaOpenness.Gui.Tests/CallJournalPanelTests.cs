using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchRenderFeaturePagesTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class CallJournalPanelTests(WpfContext wpf)
{
    private static FeaturePagesViewModel Model(IApprovalService? approvals = null, ICallJournalService? journal = null)
        => new(approvals ?? FeaturePageFixtures.Approvals(), journal ?? new FeaturePageFixtures.Journal(),
            new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics(), () => FeaturePageFixtures.Now);

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Actor_and_release_filter_popup_announces_active_filters_and_cycles_focus(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var (window, _, _) = WorkbenchControlTests.Window();
            try
            {
                window.Navigate("Calls");
                var view = window.CallsContent;
                void CheckLabel(int active)
                {
                    string expected = active == 0 ? Loc.Current["Calls.Filters"] : Loc.Current.T("Calls.ActiveFilters", active);
                    Assert.Equal(expected, view.FilterToggle.Content);
                    Assert.Equal(expected, System.Windows.Automation.AutomationProperties.GetName(view.FilterToggle));
                }
                CheckLabel(0);
                UnifiedDesktopTests.ClickControl(view.FilterToggle);
                WpfContext.Drain();
                Assert.Same(view.HumanFilter, Keyboard.FocusedElement);
                UnifiedDesktopTests.ClickControl(view.HumanFilter);
                CheckLabel(1);
                Assert.True(view.HumanFilter.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(view.AiFilter, Keyboard.FocusedElement);
                UnifiedDesktopTests.ClickControl(view.AiFilter);
                CheckLabel(1);
                Assert.True(view.AiFilter.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(view.ReleaseFilter, Keyboard.FocusedElement);
                view.ReleaseFilter.SelectedIndex = 1;
                CheckLabel(2);
                Assert.True(view.ReleaseFilter.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(view.HumanFilter, Keyboard.FocusedElement);
                Assert.True(view.HumanFilter.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)));
                Assert.Same(view.ReleaseFilter, Keyboard.FocusedElement);
                Assert.True(WorkbenchToastTests.PressEscape(view.ReleaseFilter).Handled);
                WpfContext.Drain();
                Assert.False(view.FilterPopup.IsOpen);
                Assert.Same(view.FilterToggle, Keyboard.FocusedElement);
                CheckLabel(2);
                window.Features.ReleaseOnly = window.Features.AiOnly = false;
                CheckLabel(0);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Real_service_uses_saved_endpoint_and_ClientProfiles_without_decrypting_keys()
    {
        wpf.Run(() =>
        {
            string root = Path.Combine(AppContext.BaseDirectory, "panel-journal", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "http-v21.json"), JsonSerializer.Serialize(new
                { Address = "127.0.0.1", Port = 8765, ProtectedKey = "private-invalid-protected-key" }));
                using var service = new CallJournalService("21", root, root, false);
                Assert.Equal("http://127.0.0.1:8765/mcp", service.Connection.Address);
                Assert.Equal("HTTP", service.Connection.Transport);
                Assert.Equal(TiaMcpConfigurator.ClientProfiles.ConnectionSnippet("127.0.0.1", 8765), service.Connection.ConfigurationJson);
                Assert.Contains("--relay", service.Connection.ConfigurationJson); Assert.DoesNotContain("Authorization", service.Connection.ConfigurationJson); Assert.DoesNotContain("private-", service.Connection.ConfigurationJson);
                File.WriteAllText(Path.Combine(root, "http-v20.json"), "{broken private-key");
                service.SetRelease("20"); service.Refresh(); Assert.Empty(service.Connection.Address);
            }
            finally { Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void Pending_approvals_remain_live_on_top_of_a_paused_journal_and_filters_work()
    {
        wpf.Run(() =>
        {
            var approvals = FeaturePageFixtures.Approvals(false);
            var journal = new FeaturePageFixtures.Journal(); journal.Replace(journal.Calls.Where(c => c.Result != CallResult.Pending).ToArray());
            using var model = Model(approvals, journal);
            model.ToggleFollow(); approvals.Receive(FeaturePageFixtures.Request("fresh")); WpfContext.Drain();
            Assert.True(model.Calls[0].Pending); Assert.Equal("fresh", model.Calls[0].Record.RequestId);
            model.WriteOnly = true; Assert.Single(model.Calls);
            model.FailOnly = true; Assert.Empty(model.Calls);
            model.WriteOnly = false; Assert.Single(model.Calls);
            model.FailOnly = false; model.ReleaseOnly = true; model.Release = "21"; Assert.Empty(model.Calls);
            model.Release = "14sp1"; Assert.Equal(4, model.Calls.Count);
            approvals.Deny("fresh"); WpfContext.Drain(); Assert.Equal(3, model.Calls.Count);
        });
    }

    [Theory]
    [InlineData(1024, 640)]
    [InlineData(1366, 860)]
    public void Escape_rejects_a_destructive_request_only_after_its_confirmation_sheet_closes(int width, int height)
    {
        wpf.Run(() =>
        {
            var service = FeaturePageFixtures.Approvals(false);
            service.Receive(FeaturePageFixtures.Request() with { Risk = ApprovalRisk.Destructive });
            using var model = Model(service);
            var view = new Views.ApprovalsDrawer { DataContext = model };
            var heading = new TextBlock { Focusable = true };
            DockPanel.SetDock(heading, Dock.Top);
            var content = new DockPanel { Children = { heading, view } };
            var window = new Window { Width = width, Height = height, Content = content };
            try
            {
                WpfContext.ShowWindow(window);
                WpfContext.Drain();
                WpfContext.WaitForDecisionGuard(view);
                UnifiedDesktopTests.ClickControl(view.ApproveRequest);
                Assert.Equal(Visibility.Visible, view.ConfirmationSheet.Visibility);
                Escape(view.CancelDestructive);
                Assert.Equal(Visibility.Collapsed, view.ConfirmationSheet.Visibility);
                Assert.Equal(1, service.PendingCount);
                Escape(heading);
                Assert.Equal(ApprovalState.Rejected, Assert.Single(service.Requests).State);
            }
            finally { window.Close(); }

            static void Escape(UIElement target)
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                target.RaiseEvent(key);
                if (!key.Handled) { key.RoutedEvent = Keyboard.KeyDownEvent; target.RaiseEvent(key); }
                Assert.True(key.Handled);
                WpfContext.Drain();
            }
        });
    }

    [Fact]
    public void Refused_offline_approval_navigation_keeps_the_calls_pill_selected()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(new MainViewModel(true), false, diagnostics: new FeaturePageFixtures.Diagnostics());
            try
            {
                window.Navigate("Calls");
                var host = WorkbenchRenderTests.CreateHost(window);
                var pills = Descendants<RadioButton>(window.CallsContent).Where(p => p.Command != null).ToArray();
                UnifiedDesktopTests.ClickControl(pills.Single(p => Equals(p.CommandParameter, "Approvals")));
                host.UpdateLayout();
                Assert.Equal("Calls", window.CurrentPage);
                Assert.True(pills.Single(p => Equals(p.CommandParameter, "Calls")).IsChecked);
                Assert.False(pills.Single(p => Equals(p.CommandParameter, "Approvals")).IsChecked);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Agent_counts_follow_pending_and_journal_changes_while_approvals_are_open()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var approvals = FeaturePageFixtures.Approvals(false);
            var journal = new FeaturePageFixtures.Journal();
            journal.Replace([journal.Calls[0] with { RequestId = "existing" }]);
            using var model = Model(approvals, journal);
            model.SetVisibility(false, false, true);
            approvals.Receive(FeaturePageFixtures.Request("existing"));
            approvals.Receive(FeaturePageFixtures.Request("new"));
            WpfContext.Drain();
            Assert.Equal("Approvals (2)", model.ApprovalsTabLabel);
            Assert.Equal("AI calls (2)", model.CallsTabLabel);
            journal.Replace([journal.Calls[0], journal.Calls[0] with { RequestId = "third", JournalKey = "third" }]);
            WpfContext.Drain();
            Assert.Equal("AI calls (3)", model.CallsTabLabel);
            model.SetVisibility(true, false, false);
            model.Search = "Conveyor_Line_04";
            Assert.All(model.Calls, c => Assert.Contains("Conveyor_Line_04", c.Target, StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(model.Calls);
            Assert.Equal("Claude Code (unverified)", model.Requests[0].UnverifiedClient);
            model.Search = "no matches";
            Assert.Empty(model.Calls);
            Assert.Equal("AI calls (3)", model.CallsTabLabel);
        });
    }

    [Fact]
    public void Audit_search_resolves_full_predecessor_hashes_only_from_the_loaded_chain_and_page()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var root = Path.GetFullPath(Path.Combine("bin-build", "audit-inspector", Guid.NewGuid().ToString("N")));
            string first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
            var log = new TiaOpenness.Shared.AuditLog(first, maxBytes: 1);
            var previous = log.Append("request", "r1", "engine", "21", "CompileFixture");
            var current = log.Append("end", "r1", "engine", "21", "FinishFixture", "succeeded");
            new TiaOpenness.Shared.AuditLog(second).Append("request", "r2", "engine", "20", "OtherFixture");
            var rows = new AuditTailReader(first).Poll();
            var other = Assert.Single(new AuditTailReader(second).Poll());
            var audit = new FeaturePageFixtures.Audit { Events = [other, rows[1], rows[0]] };
            using var model = new FeaturePagesViewModel(FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Journal(),
                audit, new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics());
            Assert.Equal(2, model.AuditRows[0].Record.Index);
            string hash = TiaOpenness.Shared.AuditLog.Hash(current);
            foreach (string query in new[] { " #2 ", "finishfixture", hash[^16..], "OK" })
            {
                model.AuditSearch = query;
                var row = Assert.Single(model.AuditRows);
                Assert.Same(row, model.SelectedAudit);
                Assert.Equal(hash, row.Hash);
                Assert.Equal(TiaOpenness.Shared.AuditLog.Hash(previous), row.PreviousHash);
            }
            model.AuditSearch = "nothing matches";
            Assert.True(model.AuditEmpty);
            Assert.Null(model.SelectedAudit);
            Assert.Equal("No matching audit records.", model.AuditEmptyText);
            audit.Events = [other, rows[1]];
            model.AuditSearch = "#2";
            Assert.Equal("—", Assert.Single(model.AuditRows).PreviousHash);
        });
    }

    [Theory]
    [InlineData(AppLanguage.English, "PARTIAL_FAILURE", "execution", "Partial", "preview limited")]
    [InlineData(AppLanguage.Chinese, "OUTCOME_UNKNOWN", "执行", "未知", "预览最多")]
    public void Detail_and_error_text_follow_language_and_keep_sanitized_truncated_previews(AppLanguage language,
        string code, string label, string status, string note)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var original = new FeaturePageFixtures.Journal().Calls[0];
            var call = new CallRow(original with
            {
                Result = language == AppLanguage.English ? CallResult.Partial : CallResult.Unknown,
                Outcome = "partial", Execution = "partial", Completeness = "partial", ErrorCode = code,
                ResultJson = new string('x', 4050) + TiaOpenness.Shared.CallJournalPayload.Truncated, ResultTruncated = true
            });
            Assert.Contains(label, call.Summary); Assert.Contains(status, call.Result);
            Assert.Contains(EngineResultText.Error(code), call.Error); Assert.Contains(note, call.PreviewNote);
            Assert.Contains("truncated", call.ResultJson); Assert.True(call.ResultJson.Length <= 4096);
            Assert.Equal(FeatureTone.Warning, call.Tone);
        });
    }

    [Fact]
    public void Same_request_id_in_different_host_processes_opens_the_selected_record()
    {
        wpf.Run(() =>
        {
            var journal = new FeaturePageFixtures.Journal();
            var first = journal.Calls[0] with { RequestId = "same", JournalKey = "host-one" };
            var second = first with { Host = "other", JournalKey = "host-two", ResultJson = "{\"value\":42}" };
            journal.Replace([first, second]);
            using var model = Model(journal: journal);
            model.OpenCall(model.Calls.Single(c => c.Host.StartsWith("other", StringComparison.Ordinal)));
            Assert.Equal("host-two", model.SelectedCall!.Record.JournalKey); Assert.Contains("42", model.SelectedCall.ResultJson);
        });
    }
}
