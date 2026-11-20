using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchRenderFeaturePagesTests(WpfContext wpf)
{
    [WorkbenchRenderFact]
    public void Render_feature_pages_and_drawers_without_services()
    {
        string output = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR")!), "feature-pages");
        Directory.CreateDirectory(output);
        foreach (var language in new[] { "zh", "en" })
        foreach (var theme in new[] { "light", "dark" })
        foreach (string page in new[] { "calls", "call-detail", "approvals", "audit-pass", "audit-break", "env", "env-done", "env-running", "calls-empty", "toast", "confirm" })
        {
            string state = page + "-" + language + "-" + theme;
            wpf.RunWithLanguage(state.Contains("-en-", StringComparison.Ordinal) ? AppLanguage.English : AppLanguage.Chinese, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previousTheme = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = state.EndsWith("dark", StringComparison.Ordinal) ? AppTheme.Dark : AppTheme.Light;
                var approvals = FeaturePageFixtures.Approvals(!state.Contains("empty", StringComparison.Ordinal) && !state.StartsWith("toast", StringComparison.Ordinal));
                var journal = new FeaturePageFixtures.Journal();
                var audit = new FeaturePageFixtures.Audit { Broken = state.Contains("break", StringComparison.Ordinal) };
                var diagnostics = new FeaturePageFixtures.Diagnostics();
                if (state.Contains("done", StringComparison.Ordinal)) diagnostics.Complete();
                if (state.Contains("running", StringComparison.Ordinal)) diagnostics.Export();
                if (state.StartsWith("calls-empty", StringComparison.Ordinal)) journal.Replace([]);
                if (state == "approvals-empty-zh-light") journal.Replace(journal.Calls.Where(c => c.Result != Services.CallResult.Pending).ToArray());
                var preview = new TiaMcpConfigurator.ConfigurationPreview("14sp1", @"C:\Program Files\Siemens\Automation\Portal V14",
                    "192.168.86.131", false, false, false, "", "", []);
                var window = new MainWindow(new MainViewModel(new FakeProjectClient(), new FakeDialogService()), false, approvals, diagnostics, preview);
                try
                {
                    window.ConfigureFeaturePages(journal, audit, new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                    window.ShowConfiguration(false);
                    if (page == "toast") approvals.Receive(FeaturePageFixtures.Request());
                    window.Navigate(state.StartsWith("audit", StringComparison.Ordinal) ? "Audit" : state.StartsWith("env", StringComparison.Ordinal) ? "Environment" : "Calls");
                    if (state.StartsWith("audit", StringComparison.Ordinal)) WpfContext.Complete(window.Features.VerifyAsync());
                    if (state.StartsWith("approvals", StringComparison.Ordinal)) window.Features.OpenApprovals();
                    if (state.StartsWith("call-detail", StringComparison.Ordinal)) window.Features.OpenCall(window.Features.Calls.Single(c => c.Tool == "ExportBlock"));
                    if (page == "confirm")
                    {
                        window.OpenSettings();
                        var settings = (Views.SettingsView)window.FindName("SettingsContent");
                        settings.ShowSection("AI");
                        Assert.Equal(Visibility.Visible, ((FrameworkElement)settings.FindName("AiPanel")).Visibility);
                    }
                    if (page == "toast") Assert.Equal(Visibility.Visible, ((Border)window.FindName("Toast")).Visibility);
                    var host = Detach(window);
                    Layout(host);
                    Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
                    Assert.DoesNotContain(Descendants<TextBlock>(host), t => t.Text.Contains("fixture-private-key", StringComparison.Ordinal));
                    if (state == "env-zh-dark")
                    {
                        var label = Descendants<TextBlock>(host).Single(t => t.Text == "TIA Portal V14 SP1");
                        Assert.Equal(((SolidColorBrush)Application.Current.FindResource("Primer.text")).Color, ((SolidColorBrush)label.Foreground).Color);
                    }
                    if (state.StartsWith("calls", StringComparison.Ordinal)) Assert.Equal(220, ((FrameworkElement)((Views.AiCallsView)window.FindName("CallsContent")).FindName("SearchBox")).ActualWidth);
                    var bitmap = new RenderTargetBitmap(1200, 780, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, state + ".png")); encoder.Save(stream);
                }
                finally { window.Close(); ThemeManager.Current.Theme = previousTheme; }
            });
        }
    }

    [Theory]
    [InlineData(1024, 640)]
    [InlineData(1366, 860)]
    public void Feature_pages_share_the_IDE_bars_and_keep_the_selected_audit_record_at_both_sizes(int width, int height)
    {
        string? renders = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
        string root = Path.GetFullPath(Path.Combine("bin-build", "audit-render", Guid.NewGuid().ToString("N")));
        string chain = Path.Combine(root, "audit");
        var log = new TiaOpenness.Shared.AuditLog(chain);
        log.Append("request", "compile-1", "foundation", "21", "CompilePlc", actor: "mcp");
        var start = log.Append("start", "compile-1", "foundation", "21", "CompilePlc", actor: "mcp");
        log.Append("end", "compile-1", "foundation", "21", "CompilePlc", "succeeded", actor: "mcp");
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
        foreach (string theme in new[] { "Light", "Dark", "HighContrast" })
            wpf.RunWithLanguage(language, () =>
            {
                var previousTheme = ThemeManager.Current.Theme;
                var contrast = ThemeManager.Current.HighContrastOverride;
                using var trace = new BindingPathTests.BindingTrace();
                ThemeManager.Current.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
                ThemeManager.Current.HighContrastOverride = theme == "HighContrast";
                var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
                try
                {
                    var service = new Services.AuditLogService(chain, root, Path.Combine(root, "retention"), _ => { });
                    WpfContext.Complete(service.RefreshAsync());
                    window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), service,
                        new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                    window.Navigate("Calls");
                    var host = WorkbenchRenderTests.CreateHost(window, width, height);
                    var calls = (Views.AiCallsView)window.FindName("CallsContent");
                    var approvals = (Views.ApprovalsDrawer)window.FindName("ApprovalsContent");
                    RadioButton Pill(DependencyObject view, string page) =>
                        Descendants<RadioButton>(view).Single(p => Equals(p.CommandParameter, page));
                    void CheckBar(DependencyObject view, string name)
                    {
                        host.UpdateLayout();
                        var bar = Descendants<Border>(view).Single(b => ReferenceEquals(b.Style, Application.Current.FindResource("Primer.IdeBar")));
                        Assert.True(bar.ActualHeight >= 44);
                        if (!string.IsNullOrEmpty(renders))
                        {
                            string directory = Path.Combine(renders, "feature-pages");
                            Directory.CreateDirectory(directory);
                            WorkbenchRenderTests.SaveRender(host, Path.Combine(directory, $"{name}-{language}-{theme}-{width}x{height}.png"), width, height);
                        }
                    }
                    Assert.True(Pill(calls, "Calls").IsChecked);
                    CheckBar(calls, "Calls");
                    UnifiedDesktopTests.ClickControl(Pill(calls, "Approvals"));
                    Assert.Equal("Approvals", window.CurrentPage);
                    Assert.True(Pill(approvals, "Approvals").IsChecked);
                    Assert.True(((RadioButton)window.FindName("RailAgent")).IsChecked);
                    Assert.Equal(1, window.Approvals.PendingCount);
                    CheckBar(approvals, "Approvals");
                    UnifiedDesktopTests.ClickControl(Pill(approvals, "Calls"));
                    Assert.Equal("Calls", window.CurrentPage);
                    Assert.False(Pill(calls, "Approvals").IsChecked);
                    window.Features.OpenCall(window.Features.Calls.Single(c => c.Tool == "ExportBlock"));
                    host.UpdateLayout();
                    CheckBar(calls, "CallDetail");

                    window.Navigate("Audit");
                    host.UpdateLayout();
                    var audit = (Views.AuditLogView)window.FindName("AuditContent");
                    var list = (ListBox)audit.FindName("AuditList");
                    list.SelectedIndex = 1;
                    var inspector = (Border)audit.FindName("AuditInspector");
                    Assert.Equal(Visibility.Visible, inspector.Visibility);
                    Assert.Equal(start.PreviousHash, ((TextBlock)audit.FindName("PreviousHashText")).Text);
                    Assert.Equal(TiaOpenness.Shared.AuditLog.Hash(start), ((TextBlock)audit.FindName("HashText")).Text);
                    if (width == 1366)
                    {
                        Assert.Equal(336, inspector.ActualWidth);
                        Assert.True(inspector.TranslatePoint(new Point(), list).X >= list.ActualWidth);
                    }
                    else Assert.True(inspector.TranslatePoint(new Point(), list).Y >= list.ActualHeight);
                    var toggle = (ToggleButton)audit.FindName("DetailsToggle");
                    UnifiedDesktopTests.ClickControl(toggle);
                    Assert.Equal(Visibility.Collapsed, inspector.Visibility);
                    UnifiedDesktopTests.ClickControl(toggle);
                    var search = (TextBox)audit.FindName("AuditSearchBox");
                    search.Text = "#2";
                    Assert.Single(list.Items);
                    Assert.Equal(start.PreviousHash, ((TextBlock)audit.FindName("PreviousHashText")).Text);
                    var hint = (Border)audit.FindName("ChainHint");
                    Assert.Equal(Visibility.Collapsed, hint.Visibility);
                    UnifiedDesktopTests.ClickControl((ToggleButton)audit.FindName("ChainToggle"));
                    Assert.Equal(Visibility.Visible, hint.Visibility);
                    CheckBar(audit, "Audit");

                    foreach (var (page, name) in new[] { ("Environment", "EnvironmentContent"), ("Log", "LogContent"), ("Settings", "SettingsContent") })
                    {
                        window.Navigate(page);
                        CheckBar((DependencyObject)window.FindName(name), page);
                    }
                    var settings = (Views.SettingsView)window.FindName("SettingsContent");
                    foreach (var section in new[] { "Service", "AI" })
                    {
                        settings.ShowSection(section);
                        CheckBar(settings, "Settings-" + section);
                    }
                    Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
                }
                finally { window.Close(); ThemeManager.Current.HighContrastOverride = contrast; ThemeManager.Current.Theme = previousTheme; }
            });
    }

    [Fact]
    public void Audit_inspector_moves_at_1200_content_width_without_changing_the_selected_record()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(),
                    new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                window.Navigate("Audit");
                var host = WorkbenchRenderTests.CreateHost(window, 1259, 780);
                var inspector = window.AuditContent.AuditInspector;
                window.AuditContent.AuditList.SelectedIndex = 2;
                var selected = window.Features.SelectedAudit;
                Assert.NotNull(selected);
                foreach (int width in new[] { 1259, 1260, 1259 })
                {
                    host.Width = width;
                    host.Measure(new Size(width, 780));
                    host.Arrange(new Rect(0, 0, width, 780));
                    host.UpdateLayout();
                    WpfContext.Drain();
                    host.UpdateLayout();
                    Assert.Same(selected, window.Features.SelectedAudit);
                    Assert.Equal(width == 1260 ? 1 : 0, Grid.GetColumn(inspector));
                    Assert.Equal(width == 1260 ? 0 : 1, Grid.GetRow(inspector));
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1366, 860, AppTheme.Light, AppLanguage.Chinese)]
    [InlineData(1366, 860, AppTheme.Light, AppLanguage.English)]
    [InlineData(1366, 860, AppTheme.Dark, AppLanguage.Chinese)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.Chinese)]
    public void Final_feature_views_use_the_design_sizes_and_actual_request_fields(int width, int height, AppTheme theme, AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var themes = ThemeManager.Current;
            var previous = (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen);
            themes.Theme = theme; themes.Accent = AppAccent.Cobalt; themes.HighContrastOverride = false; App.Settings.LeftPaneOpen = true;
            var approvals = FeaturePageFixtures.Approvals(false);
            string planHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("fixture plan"u8));
            var import = FeaturePageFixtures.Request() with
            {
                PlanHash = planHash,
                Operations = [new(LocalizedText.Literal("ImportPlcBlock"), "Conveyor / Conveyor_Ctrl_FB", "ImportPlcBlock")],
                Risks = [LocalizedText.Key("Approval.OverwritesExisting")],
                ParametersJson = """{"plc":"PLC_1","blockName":"Conveyor_Ctrl_FB","overwrite":true}"""
            };
            approvals.Receive(FeaturePageFixtures.Request("previous-import") with { PlanHash = planHash });
            Assert.True(approvals.Approve("previous-import"));
            approvals.Transition("previous-import", Services.ApprovalState.Executing);
            approvals.Transition("previous-import", Services.ApprovalState.Completed);
            approvals.Receive(import);
            approvals.Receive(import with
            {
                Id = "online-review", Risk = Services.ApprovalRisk.Destructive, RiskAction = "online",
                Operations = [new(LocalizedText.Literal("DownloadPlc"), "PLC_1", "DownloadPlc")], Risks = [],
                ParametersJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    onlineTarget = new { kind = "physical", targetIpAddress = "192.0.2.10", pgPcInterface = "Fixture adapter" },
                    expectedPlanHash = planHash,
                })
            });
            var model = ProjectPageTestSupport.Model(results: true);
            var window = new MainWindow(model, false, approvals, new FeaturePageFixtures.Diagnostics()) { Width = width, Height = height };
            try
            {
                using var trace = new BindingPathTests.BindingTrace();
                using var settings = new SafetySettingsServiceTests.Fixture();
                settings.Service.SaveTargets(["192.0.2.10", "198.51.100.0/24"]);
                window.SettingsContent.SetSafetyService(settings.Service);
                settings.Audit.Append("request", import.Id, "engine", "21", "ImportPlcBlock");
                settings.Audit.Approval(import.Id, "engine", "21", "ImportPlcBlock", "granted", planHash);
                settings.Audit.Append("start", import.Id, "engine", "21", "ImportPlcBlock");
                settings.Audit.Append("end", import.Id, "engine", "21", "ImportPlcBlock", "succeeded");
                using var audit = new Services.AuditLogService(Path.Combine(settings.Root, "audit"), Path.Combine(settings.Root, "diagnostics"),
                    Path.Combine(settings.Root, "retention"), _ => { });
                WpfContext.Complete(audit.RefreshAsync());
                var journal = new FeaturePageFixtures.Journal();
                journal.Replace(journal.Calls.Select(call => call.Tool == "ExportBlock" ? call with
                {
                    ResultJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        schemaVersion = 4, ok = false, data = (object?)null,
                        error = new { code = call.ErrorCode, message = call.ErrorMessage.Resolve(), details = new { } },
                        meta = new
                        {
                            timestamp = call.Time, releaseKey = call.Release, tool = call.Tool, requestId = call.RequestId,
                            outcome = "read-failed", execution = "read-only", behaviorPolicy = "current", completeness = "none",
                            paging = (object?)null, warnings = Array.Empty<object>(),
                        },
                    }),
                } : call).ToArray());
                window.ConfigureFeaturePages(journal, audit,
                    new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                window.Content = host;
                WpfContext.ShowWindow(window);
                void Capture(string view)
                {
                    WpfContext.Drain(); host.UpdateLayout();
                    if (Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR") is not { Length: > 0 } output) return;
                    var dir = Directory.CreateDirectory(Path.Combine(output, "b2"));
                    string suffix = $"{theme.ToString().ToLowerInvariant()}-{(Loc.Current.IsChinese ? "zh" : "en")}-{width}x{height}";
                    WorkbenchRenderTests.SaveRender(host, Path.Combine(dir.FullName, view + "-" + suffix + ".png"), width, height);
                }

                void CheckColumn(FrameworkElement column, double left, double expectedWidth)
                {
                    Assert.Equal(expectedWidth, column.ActualWidth);
                    Assert.InRange(column.TranslatePoint(new Point(), host).X, left - 1, left + 1);
                }

                window.Navigate("Approvals");
                Capture("agent-approvals");
                if (width == 1366) CheckColumn(window.ApprovalsContent.DetailScroll, 434, 820);
                Assert.Equal(Loc.Current["Approval.ApproveImport"], window.ApprovalsContent.ApproveRequest.Content);
                void CheckButton(System.Windows.Controls.Primitives.ButtonBase button, int height)
                {
                    var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(button);
                    Assert.NotNull(peer);
                    Assert.False(string.IsNullOrWhiteSpace(peer.GetName()));
                    Assert.Equal(height, button.ActualHeight);
                }
                CheckButton(window.ApprovalsContent.ApproveRequest, 32);
                CheckButton(window.ApprovalsContent.DenyRequest, 32);
                if (width == 1366) Assert.Equal(262, window.ApprovalsContent.RequestListPane.ActualWidth);
                Assert.True(approvals.Deny(import.Id));
                WpfContext.Drain();
                var destructive = window.Features.SelectedRequest!;
                Assert.Equal(4, destructive.DestructiveFields.Count);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.ApproveRequest);
                Capture("danger-sheet");
                var sheet = Descendants<Border>(window.ApprovalsContent.ConfirmationSheet).Single(b => b.Width == 420);
                CheckColumn(sheet, (width - 420) / 2d, 420);
                double middle = sheet.TranslatePoint(new Point(0, sheet.ActualHeight / 2), host).Y;
                Assert.InRange(middle, height / 2d - 1, height / 2d + 1);
                Assert.True(window.ApprovalsContent.ConfirmationSheet.IsVisible);
                CheckButton(window.ApprovalsContent.CancelDestructive, 36);
                CheckButton(window.ApprovalsContent.ConfirmDestructive, 36);
                Assert.Equal("workbench-modal-open", window.ApplyControl(WorkbenchControlTests.Request()).Refusal?.Condition);
                Assert.Equal(1, approvals.PendingCount);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.CancelDestructive);

                window.Navigate("Calls");
                if (width >= 1260) window.Features.OpenCall(window.Features.Calls.Single(row => row.Tool == "ExportBlock"));
                Capture("agent-calls");
                CheckButton(window.CallsContent.AllFilter, 26);
                Assert.Equal(28, window.CallsContent.SearchBox.ActualHeight);
                var header = Descendants<Border>(window.CallsContent).Single(border =>
                    ReferenceEquals(border.Style, window.FindResource("Primer.TableHeaderBand")));
                Assert.Equal(34, header.ActualHeight);
                window.Navigate("Environment"); Capture("doctor");
                if (width == 1366)
                {
                    var column = Descendants<StackPanel>(window.EnvironmentContent).Single(panel => panel.MaxWidth == 860);
                    CheckColumn(column, 283, 860);
                }
                window.Navigate("Audit");
                Assert.Equal(64, window.Features.SelectedAudit!.Hash.Length);
                Capture("audit");
                model.Activity.ClearLog();
                model.Activity.Append("ImportPlcBlock · Conveyor_Ctrl_FB", Services.WorkbenchActivity.Severity.Info);
                model.Activity.Append("DownloadPlc · " + Loc.Current["Approval.Pending"], Services.WorkbenchActivity.Severity.Warning);
                model.Activity.Append("ExportBlock · KNOW_HOW_PROTECTED", Services.WorkbenchActivity.Severity.Error);
                window.Navigate("Log"); Capture("logs");
                window.Navigate("Settings"); window.SettingsContent.ShowSection("AI");
                WpfContext.Complete(window.SettingsContent.LoadSafetyAsync());
                Capture("settings");
                var settingsView = window.SettingsContent;
                if (width == 1366)
                {
                    var column = Descendants<StackPanel>(settingsView).Single(panel => panel.MaxWidth == 740);
                    CheckColumn(column, 461, 740);
                    Assert.Contains(Descendants<TextBlock>(settingsView.SectionList),
                        text => text.Text == (Loc.Current.IsChinese ? "偏好与安全" : "Settings"));
                }
                var policy = Descendants<TextBlock>(settingsView.AiPanel).Single(text => text.Text == Loc.Current["Settings.AlwaysConfirmed"]);
                Assert.False(policy.IsVisible);
                UnifiedDesktopTests.ClickControl(settingsView.ShowPolicyDetails);
                WpfContext.Drain();
                Assert.True(policy.IsVisible);
                UnifiedDesktopTests.ClickControl(settingsView.ShowPolicyDetails);
                WpfContext.Drain();
                Assert.False(policy.IsVisible);
                Assert.Equal(2, window.SettingsContent.OnlineTargets.Items.Count);
                if (width == 1366) Assert.Equal(236, ((Border)window.SettingsContent.SectionList.Parent).ActualWidth);
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            }
            finally
            {
                window.Close();
                (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen) = previous;
            }
        });
    }

    [Fact]
    public void Approval_details_omit_missing_fields_and_never_infer_safeguards()
    {
        wpf.Run(() =>
        {
            var approvals = FeaturePageFixtures.Approvals(false);
            using var model = new FeaturePagesViewModel(approvals, new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(),
                new FeaturePageFixtures.Environment(), new FeaturePageFixtures.Diagnostics(), () => FeaturePageFixtures.Now);
            var request = FeaturePageFixtures.Request() with
            {
                Project = "", Plc = "", Risks = [], Operations = [], ParametersJson = "{}", Risk = Services.ApprovalRisk.Destructive,
            };
            foreach (string json in new[] { "{}", "invalid", "[]", """{"onlineTarget":null}""" })
            {
                var row = new ApprovalRow(request with { ParametersJson = json }, model);
                Assert.Empty(row.Target); Assert.Empty(row.Change); Assert.Empty(row.Safeguard);
                Assert.Empty(row.FirstObject); Assert.Empty(row.DestructiveFields);
            }
        });
    }

    [Fact]
    public void Journal_JSON_colouring_preserves_escaped_names_unicode_numbers_and_truncation()
    {
        wpf.Run(() =>
        {
            var text = new TextBlock();
            string json = """{"指令\"名称" : "S7", "amount": -1.25e3, "flags":[true,false,null]}""";
            Views.CallDetailDrawer.SetJsonPreview(text, json);
            Assert.Equal(json, text.Text);
            var runs = text.Inlines.OfType<System.Windows.Documents.Run>().ToList();
            Assert.Equal(json, string.Concat(runs.Select(run => run.Text)));
            Assert.Same(Application.Current.FindResource("Primer.syntaxKeyword"), runs[1].Foreground);
            Assert.Same(Application.Current.FindResource("Primer.syntaxNumber"), runs.Single(run => run.Text == "-1.25e3").Foreground);
            string truncated = json[..^1] + TiaOpenness.Shared.CallJournalPayload.Truncated;
            Views.CallDetailDrawer.SetJsonPreview(text, truncated);
            Assert.Equal(truncated, text.Text);
            Assert.Equal(truncated, Assert.Single(text.Inlines.OfType<System.Windows.Documents.Run>()).Text);
        });
    }

    [Fact]
    public void Product_markup_and_translations_contain_no_reference_project_data()
    {
        var sources = SourceScan.Markup.Concat(SourceScan.Code.Where(file => file.Name.Contains(".Localization.Strings", StringComparison.Ordinal)));
        foreach (var file in sources)
        foreach (string sample in new[] { "Line04_Packaging", "req-9041", "192.168.10.21", "Intel I225-V", "CPU 1515F", "FB_ConveyorCtrl" })
            Assert.DoesNotContain(sample, file.Text, StringComparison.Ordinal);
    }

    internal static Border Detach(MainWindow window)
    {
        var content = (FrameworkElement)window.Content; window.Content = null;
        var host = new Border { Child = content, DataContext = window.DataContext, Width = 1200, Height = 780 };
        NameScope.SetNameScope(host, NameScope.GetNameScope(window));
        foreach (System.Windows.Input.CommandBinding command in window.CommandBindings) host.CommandBindings.Add(command);
        return host;
    }
    internal static void Layout(FrameworkElement host)
    {
        WpfContext.Drain();
        host.Measure(new Size(1200, 780)); host.Arrange(new Rect(0, 0, 1200, 780)); host.UpdateLayout();
        WpfContext.Drain(); host.UpdateLayout(); WpfContext.Drain();
    }
    internal static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
