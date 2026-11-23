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
public sealed partial class WorkbenchRenderFeaturePagesTests(WpfContext wpf)
{
    internal static void AssertCenteredColumn(FrameworkElement column, FrameworkElement viewport, double maximum)
    {
        double left = column.TranslatePoint(new Point(), viewport).X;
        double right = viewport.ActualWidth - left - column.ActualWidth;
        Assert.Equal(Visibility.Visible, column.Visibility);
        Assert.True(column.ActualHeight > 0);
        Assert.True(viewport.ActualWidth > 48);
        Assert.Equal(Math.Min(maximum, viewport.ActualWidth - 48), column.ActualWidth);
        Assert.InRange(Math.Abs(left - right), 0, 1);
    }

    internal static ScrollContentPresenter Viewport(ScrollViewer scroll) =>
        (ScrollContentPresenter)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);

    [Theory]
    [InlineData(AppLanguage.Chinese)]
    [InlineData(AppLanguage.English)]
    public void Doctor_renders_failures_and_repairs_and_logs_owns_the_export_access_key(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var environment = new FeaturePageFixtures.Environment();
            var failed = Core.Environment.EnvironmentFindingStatus.Fail;
            var rows = new Core.Environment.EnvironmentFinding[]
            {
                new("runtime", Core.Environment.EnvironmentFindingStatus.Pass, "Pass"),
                new("membership", failed, "Fail"),
                new("proxy-remote", failed, "Fail"),
                new("firewall", failed, "Fail", "http://127.0.0.1:8765/"),
                new("port", failed, "PortUsed", "http://127.0.0.1:8765/"),
                new("framework", failed, "Fail"),
            }.Select(Services.EnvironmentCheckCatalogue.Row).ToArray();
            environment.Replace([new(LocalizedText.Literal("TIA Portal"), rows)]);
            var diagnostics = new FeaturePageFixtures.Diagnostics();
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(false), diagnostics);
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), environment);
                window.Navigate("Environment"); WpfContext.ShowWindow(window); WpfContext.Drain(); window.UpdateLayout();
                var doctor = window.EnvironmentContent;
                Assert.Equal(FeatureTone.Error, window.Features.EnvironmentTone);
                var neutralTag = window.FindResource("Primer.Tag");
                Assert.Same(neutralTag, doctor.SummaryChip.Style);
                Assert.Equal("✕", doctor.SummaryGlyph.Text);
                Assert.Same(window.FindResource("Ant.colorError"), doctor.SummaryGlyph.Foreground);
                var tag = Assert.Single(Descendants<Border>(doctor.PassedList), border => border.IsVisible && border.Name == "PassedTag");
                Assert.Same(neutralTag, tag.Style);
                Assert.Collection(Assert.IsType<StackPanel>(tag.Child).Children.Cast<TextBlock>(),
                    glyph => { Assert.Equal("✓", glyph.Text); Assert.Same(window.FindResource("Primer.ok"), glyph.Foreground); },
                    label => Assert.Equal(Loc.Current["Env.Passed"], label.Text));
                Assert.Single(Descendants<TextBlock>(doctor.PassedList), text => text.IsVisible && text.Text == Loc.Current["Env.Passed"]);
                Assert.DoesNotContain(Descendants<Button>(doctor.PassedList), button => button.IsVisible);
                var buttons = Descendants<Button>(doctor.CheckList).Where(button => button.IsVisible).ToArray();
                Assert.Equal(
                    new[] { "membership", "proxy-remote", "firewall", "port" },
                    buttons.Select(button => ((EnvironmentRow)button.DataContext).Check.Id));
                Assert.All(buttons, button => Assert.DoesNotContain("_", Assert.IsType<string>(button.Content)));
                var firewall = buttons.Single(button => ((EnvironmentRow)button.DataContext).Check.Id == "firewall");
                Assert.Equal(language == AppLanguage.English ? "Configure network permission" : "配置网络权限", firewall.Content);
                var port = buttons.Single(button => ((EnvironmentRow)button.DataContext).Check.Id == "port");
                Assert.Equal(Loc.Current["Shell.ChangePort"], port.Content);
                Assert.Equal(language == AppLanguage.English ? "Change port in MCP" : "前往 MCP 更改端口",
                    new System.Windows.Automation.Peers.ButtonAutomationPeer(port).GetName());
                foreach (var row in window.Features.FailedChecks)
                {
                    var name = Assert.Single(Descendants<TextBlock>(doctor.CheckList), text => text.IsVisible && text.Text == row.Name);
                    Assert.Equal(row.Name, name.ToolTip);
                    Assert.NotEqual(Loc.Current["Env.Failed"], row.Result);
                    Assert.Contains(Descendants<TextBlock>(doctor.CheckList), text => text.IsVisible && text.Text == row.Result);
                }
                Assert.Contains("8765", window.Features.FailedChecks.Single(row => row.Check.Id == "firewall").Result);
                Assert.DoesNotContain(Descendants<Button>(doctor), button => Equals(button.Content, Loc.Current["Env.Generate"]));
                environment.Replace([new(LocalizedText.Key("Env.Runtime"), [Services.EnvironmentCheckCatalogue.Checking("runtime")])]);
                WpfContext.Drain();
                Assert.Equal("•", doctor.SummaryGlyph.Text);
                Assert.Same(window.FindResource("Primer.accent"), doctor.SummaryGlyph.Foreground);
                window.Navigate("Log"); WpfContext.Drain(); window.UpdateLayout();
                var export = window.LogContent.GenerateButton;
                Assert.True(export.IsVisible && export.IsEnabled);
                Assert.Equal('G', char.ToUpperInvariant(Assert.Single(Descendants<AccessText>(export)).AccessKey));
                System.Windows.Input.AccessKeyManager.ProcessKey(PresentationSource.FromVisual(export), "g", false);
                Assert.True(window.Features.DiagnosticRunning);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Environment_check_waits_for_a_visible_page_with_a_feature_model()
    {
        wpf.Run(() =>
        {
            var environment = new FeaturePageFixtures.Environment();
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(false), new FeaturePageFixtures.Diagnostics());
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), environment);
                window.OpenEnvironmentCheck();
                Assert.False(window.EnvironmentContent.IsVisible);
                Assert.Equal(0, environment.RecheckCount);
                var model = window.EnvironmentContent.DataContext;
                window.EnvironmentContent.DataContext = null;
                WpfContext.ShowWindow(window);
                window.EnvironmentContent.Recheck();
                Assert.False(window.EnvironmentContent.HasChecked);
                window.EnvironmentContent.DataContext = model;
                window.OpenEnvironmentCheck();
                Assert.True(window.EnvironmentContent.IsVisible);
                Assert.Equal(1, environment.RecheckCount);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Advanced_project_options_open_environment_and_run_one_check_per_click()
    {
        wpf.Run(() =>
        {
            var environment = new FeaturePageFixtures.Environment();
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(false), new FeaturePageFixtures.Diagnostics());
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), environment);
                WpfContext.ShowWindow(window);
                for (int visit = 1; visit <= 2; visit++)
                {
                    window.Navigate("Engineering");
                    window.OperationsContent.ShowProjectOptions();
                    var options = window.OperationsContent.Options;
                    options.AdvancedProjectTools.IsExpanded = true;
                    WpfContext.Drain();
                    var check = Assert.Single(Descendants<Button>(options.AdvancedProjectTools),
                        button => Equals(button.Content, Loc.Current["Toolbar.Doctor"]));
                    UnifiedDesktopTests.ClickControl(check);
                    WpfContext.Drain();
                    Assert.Equal("Environment", window.CurrentPage);
                    Assert.False(options.IsOpen);
                    Assert.Equal(visit, environment.RecheckCount);
                }
                Assert.Null(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(window.EnvironmentContent.SummaryGlyph));
            }
            finally { window.Close(); }
        });
    }

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
                    if (page == "toast") window.Features.Export();
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
                    if (state.StartsWith("calls", StringComparison.Ordinal)) Assert.Equal(190, window.CallsContent.SearchBox.ActualWidth);
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
                        Assert.Equal(320, inspector.ActualWidth);
                        Assert.True(inspector.TranslatePoint(new Point(), list).X >= list.ActualWidth);
                    }
                    else
                    {
                        Assert.Equal(280, inspector.ActualWidth);
                        Assert.True(inspector.TranslatePoint(new Point(), list).X >= list.ActualWidth);
                    }
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
                    Assert.Equal(1, Grid.GetColumn(inspector));
                    Assert.Equal(0, Grid.GetRow(inspector));
                    Assert.Equal(width == 1260 ? 320 : 280, inspector.ActualWidth);
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
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.English)]
    [InlineData(1360, 812, AppTheme.Dark, AppLanguage.Chinese)]
    [InlineData(1920, 1080, AppTheme.Light, AppLanguage.Chinese)]
    public void Final_feature_views_use_the_design_sizes_and_actual_request_fields(int width, int height, AppTheme theme, AppLanguage language)
    {
        Render(false);
        if (width == 1360 && theme == AppTheme.Light && language == AppLanguage.Chinese) Render(true);

        void Render(bool hc) => wpf.RunWithLanguage(language, () =>
        {
            var themes = ThemeManager.Current;
            var previous = (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen);
            themes.Theme = theme; themes.Accent = AppAccent.Cobalt; themes.HighContrastOverride = hc; App.Settings.LeftPaneOpen = true;
            var approvals = FeaturePageFixtures.Approvals(false);
            string planHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("fixture plan"u8));
            var import = FeaturePageFixtures.Request() with
            {
                PlanHash = planHash,
                Operations = [new(LocalizedText.Literal("ImportPlcBlock"), "Conveyor / Conveyor_Ctrl_FB", "ImportPlcBlock")],
                Risks = [LocalizedText.Key("Approval.OverwritesExisting")],
                ParametersJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    plc = "PLC_1", blockName = "Conveyor_Ctrl_FB", overwrite = true,
                    before = "BEGIN\n    IF #Enable THEN\n        #Status := 1;\n    ELSE\n        #Status := 0;\n    END_IF;\nEND_FUNCTION_BLOCK",
                    after = "BEGIN\n    IF #Enable AND #Permissive THEN\n        #Status := 1;\n    ELSE\n        #Status := 0;\n"
                        + "        #Ready := FALSE;\n    END_IF;\nEND_FUNCTION_BLOCK"
                })
            };
            approvals.Receive(FeaturePageFixtures.Request("previous-import") with { PlanHash = planHash });
            Assert.True(approvals.Approve("previous-import"));
            approvals.Transition("previous-import", Services.ApprovalState.Executing);
            approvals.Transition("previous-import", Services.ApprovalState.Completed);
            foreach (var (id, tool, target, state) in new[]
            {
                ("declined", "DeletePlcBlock", "ObsoleteValve", Services.ApprovalState.Rejected),
                ("reviewed-write", "ImportBlock", "MotionPermit", Services.ApprovalState.Completed),
                ("uncertain", "CompileProject", "AssemblyCell", Services.ApprovalState.Unknown),
                ("disconnected", "SaveProject", "AssemblyCell", Services.ApprovalState.Disconnected)
            })
            {
                approvals.Receive(import with { Id = id, Operations = [new(LocalizedText.Literal(tool), target, tool)] });
                if (state is Services.ApprovalState.Completed or Services.ApprovalState.Unknown)
                {
                    Assert.True(approvals.Approve(id));
                    Assert.True(approvals.Transition(id, Services.ApprovalState.Executing));
                }
                Assert.True(approvals.Transition(id, state));
            }
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
                settings.Audit.Approval("declined", "engine", "21", "DeletePlcBlock", "denied", planHash);
                settings.Audit.Approval("reviewed-write", "engine", "21", "ImportBlock", "granted", planHash);
                settings.Audit.Approval("previous-import", "engine", "21", "ImportBlock", "granted", planHash);
                settings.Audit.Append("request", import.Id, "engine", "21", "ImportPlcBlock");
                settings.Audit.Approval(import.Id, "engine", "21", "ImportPlcBlock", "granted", planHash);
                settings.Audit.Append("start", import.Id, "engine", "21", "ImportPlcBlock");
                settings.Audit.Append("end", import.Id, "engine", "21", "ImportPlcBlock", "succeeded");
                string journalDirectory = WriteRetentionFixture(settings.Root);
                using var audit = new Services.AuditLogService(Path.Combine(settings.Root, "audit"), journalDirectory,
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
                var clock = FeaturePageFixtures.Now;
                window.ApprovalsContent.DecisionGuard = new(() => clock);
                var environment = new FeaturePageFixtures.Environment();
                window.ConfigureFeaturePages(journal, audit, environment, new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                window.Content = host;
                WpfContext.ShowWindow(window);
                string suffix = (hc ? "hc" : theme.ToString().ToLowerInvariant()) + (Loc.Current.IsChinese ? "-zh" : "-en") + $"-{width}x{height}";
                string? output = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
                string? directory = output == null ? null : Directory.CreateDirectory(Path.Combine(output, "b2")).FullName;
                void Capture(string view)
                {
                    while (window.Toast.IsVisible) UnifiedDesktopTests.ClickControl(window.ToastClose);
                    WpfContext.Drain(); host.UpdateLayout();
                    if (directory != null) WorkbenchRenderTests.SaveRender(host, Path.Combine(directory, view + "-" + suffix + ".png"), width, height);
                }

                void CheckSheet(FrameworkElement column, double left, double expectedWidth)
                {
                    Assert.Equal(expectedWidth, column.ActualWidth);
                    Assert.InRange(column.TranslatePoint(new Point(), host).X, left - 1, left + 1);
                }

                void CheckJournalSelection(ListBox list)
                {
                    var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem));
                    Assert.Equal(new Thickness(3, 0, 0, 0), item.BorderThickness);
                    Assert.Equal(((SolidColorBrush)window.FindResource("Primer.accentSoft")).Color, ((SolidColorBrush)item.Background).Color);
                    Assert.Equal(((SolidColorBrush)window.FindResource("Primer.text")).Color, ((SolidColorBrush)item.Foreground).Color);
                }

                window.Navigate("Approvals");
                Capture("agent-approvals");
                Assert.Contains(window.Features.SelectedRequest!.SourceChanges, line => line.Sign == "+");
                var selected = window.Features.SelectedRequest!;
                Assert.Equal(Loc.Current.T("Approval.SourceChangeSummary", "+2", "−1"), selected.FeedSummary);
                Assert.Contains(Descendants<TextBlock>(window.ApprovalsContent.RequestList), text => text.IsVisible && text.Text == selected.FeedSummary);
                Assert.Equal(Loc.Current["Approval.WriteHint"], window.ApprovalsContent.DecisionHint.Text);
                Assert.True(window.ApprovalsContent.DecisionHint.IsVisible);
                Assert.True(window.ApprovalsContent.DecisionHint.TranslatePoint(new Point(), host).X
                    < window.ApprovalsContent.DenyRequest.TranslatePoint(new Point(), host).X);
                Assert.DoesNotContain(Descendants<Expander>(window.ApprovalsContent), item => Equals(item.Header, Loc.Current["Approval.HistoryTool"]));
                var arguments = Descendants<Expander>(window.ApprovalsContent).Single(item => Equals(item.Header, Loc.Current["Approval.Parameters"]));
                arguments.IsExpanded = true;
                WpfContext.Drain(); host.UpdateLayout();
                Assert.True(window.ApprovalsContent.ToolOperations.IsVisible);
                arguments.IsExpanded = false;
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.HistoryTab);
                Capture("agent-approvals-history");
                AssertCenteredColumn(window.ApprovalsContent.HistoryColumn, window.ApprovalsContent.PageBody, 960);
                var history = window.ApprovalsContent.HistoryList.Items.Cast<ApprovalRow>().ToArray();
                Assert.Equal(5, history.Length);
                var handled = Assert.Single(history, row => row.Request.Id == "previous-import");
                Assert.Contains(history, row => !row.HasAuditDecision);
                Assert.Equal("previous-import", handled.AuditDecision?.RequestId);
                var auditLink = Descendants<Button>(window.ApprovalsContent.HistoryList)
                    .Single(b => Equals(b.CommandParameter, "Audit") && b.DataContext == handled);
                UnifiedDesktopTests.ClickControl(auditLink);
                Assert.Equal("Audit", window.CurrentPage);
                Assert.Equal(handled.AuditDecision, window.Features.SelectedAudit?.Record);
                window.Navigate("Approvals");
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.HistoryTab);
                WpfContext.Drain(); host.UpdateLayout();
                var review = Descendants<Button>(window.ApprovalsContent.HistoryList)
                    .Single(button => button.DataContext == handled && Equals(button.Content, handled.HistoryTools));
                UnifiedDesktopTests.ClickControl(review);
                Assert.True(window.Features.HistoryDetailOpen);
                host.UpdateLayout();
                AssertCenteredColumn(window.ApprovalsContent.DetailColumn, window.ApprovalsContent.DetailViewport, 780);
                Assert.False(window.ApprovalsContent.DecisionArea.IsVisible);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.HistoryTab);
                Assert.True(window.Features.ShowHistoryTable);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.PendingTab);

                host.UpdateLayout();
                AssertCenteredColumn(window.ApprovalsContent.DetailScroll, window.ApprovalsContent.DetailViewport, 780);
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
                if (width == 1366) Assert.Equal(260, window.ApprovalsContent.RequestListPane.ActualWidth);
                Assert.True(approvals.Deny(import.Id));
                WpfContext.Drain();
                var destructive = window.Features.SelectedRequest!;
                Assert.Empty(destructive.SourceChanges);
                Assert.Equal("PLC_1 → 192.0.2.10", destructive.FeedSummary);
                Assert.Equal(Loc.Current["Approval.OnlineHint"], destructive.DecisionHint);
                Assert.Equal(Loc.Current["Approval.Operations"], destructive.ComparisonTitle);
                Assert.Equal(4, destructive.DestructiveFields.Count);
                clock = clock.AddMilliseconds(500);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.ApproveRequest);
                Capture("danger-sheet");
                var sheet = Descendants<Border>(window.ApprovalsContent.ConfirmationSheet).Single(b => b.Width == 420);
                CheckSheet(sheet, (width - 420) / 2d, 420);
                double middle = sheet.TranslatePoint(new Point(0, sheet.ActualHeight / 2), host).Y;
                Assert.InRange(middle, height / 2d - 1, height / 2d + 1);
                Assert.True(window.ApprovalsContent.ConfirmationSheet.IsVisible);
                CheckButton(window.ApprovalsContent.CancelDestructive, 36);
                CheckButton(window.ApprovalsContent.ConfirmDestructive, 36);
                Assert.Equal("workbench-modal-open", window.ApplyControl(WorkbenchControlTests.Request()).Refusal?.Condition);
                Assert.Equal(1, approvals.PendingCount);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.CancelDestructive);

                window.Navigate("Calls");
                window.Features.OpenCall(window.Features.Calls.Single(row => row.Tool == "ExportBlock"));
                Capture("agent-calls");
                CheckJournalSelection(window.CallsContent.CallList);
                if (width < 1260)
                {
                    Assert.True(window.DrawerOverlay.IsVisible);
                    var close = Descendants<Button>(window.DrawerPanel).Single(button =>
                        System.Windows.Automation.AutomationProperties.GetName(button) == Loc.Current["Access.CloseDrawer"]);
                    UnifiedDesktopTests.ClickControl(close);
                    Assert.False(window.DrawerOverlay.IsVisible);
                }
                CheckButton(window.CallsContent.AllFilter, 26);
                Assert.Equal(28, window.CallsContent.SearchBox.ActualHeight);
                var header = Descendants<Border>(window.CallsContent).Single(border =>
                    ReferenceEquals(border.Style, window.FindResource("Primer.TableHeaderBand")));
                Assert.Equal(34, header.ActualHeight);
                window.Navigate("Environment"); Capture("doctor-issues");
                Assert.NotEmpty(window.EnvironmentContent.CheckList.Items);
                var disclosures = Descendants<Expander>(window.EnvironmentContent.CheckList).ToArray();
                Assert.Equal(window.EnvironmentContent.CheckList.Items.Count, disclosures.Length);
                foreach (var disclosure in disclosures)
                {
                    var problem = Assert.IsType<EnvironmentRow>(disclosure.DataContext);
                    var toggle = Descendants<ToggleButton>(disclosure).Single();
                    Assert.Equal(Loc.Current["Env.ManualSteps"], disclosure.Header);
                    Assert.Contains(Descendants<TextBlock>(toggle), text => text.Text == "▸" && text.IsVisible);
                    UnifiedDesktopTests.ClickControl(toggle); WpfContext.Drain(); host.UpdateLayout();
                    Assert.True(disclosure.IsExpanded);
                    Assert.Contains(Descendants<TextBlock>(disclosure), text => text.Text == problem.Detail && text.IsVisible);
                    Assert.Contains(Descendants<TextBlock>(toggle), text => text.Text == "▾" && text.IsVisible);
                    UnifiedDesktopTests.ClickControl(toggle);
                    Assert.False(disclosure.IsExpanded);
                }
                environment.Replace(environment.Groups.Select(g => g with
                    { Rows = g.Rows.Where(row => row.Status == Services.CheckStatus.Pass).ToArray() }).ToArray());
                Capture("doctor-pass");
                Assert.Empty(window.EnvironmentContent.CheckList.Items);
                Assert.NotEmpty(window.EnvironmentContent.PassedList.Items);
                AssertCenteredColumn(window.EnvironmentContent.EnvironmentColumn, Viewport(window.EnvironmentContent.DoctorScroll), 860);
                window.Navigate("Audit");
                Assert.Equal(64, window.Features.SelectedAudit!.Hash.Length);
                Capture("audit");
                CheckJournalSelection(window.AuditContent.AuditList);
                model.Activity.ClearLog();
                model.Activity.Append("ImportPlcBlock · Conveyor_Ctrl_FB", Services.WorkbenchActivity.Severity.Info);
                model.Activity.Append("DownloadPlc · " + Loc.Current["Approval.Pending"], Services.WorkbenchActivity.Severity.Warning);
                model.Activity.Append("ExportBlock · KNOW_HOW_PROTECTED", Services.WorkbenchActivity.Severity.Error);
                window.Navigate("Log"); Capture("logs");
                window.Navigate("Settings"); window.SettingsContent.ShowSection("AI");
                WpfContext.Complete(window.SettingsContent.LoadSafetyAsync());
                Capture("settings-safety");
                var settingsView = window.SettingsContent;
                AssertCenteredColumn(settingsView.SettingsColumn, Viewport(settingsView.DetailScroll), 740);
                if (width == 1366)
                {
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
                if (width == 1366) Assert.Equal(228, ((Border)window.SettingsContent.SectionList.Parent).ActualWidth);
                CaptureConfirmation(window, host, settingsView.ReviewSafety, "confirm-safety", directory, suffix);
                settingsView.ShowSection("Appearance"); Capture("settings-appearance");
                AssertCenteredColumn(settingsView.SettingsColumn, Viewport(settingsView.DetailScroll), 740);
                settingsView.ShowSection("Service");
                string bundleRoot = MainWindow.FindBundleRoot(AppContext.BaseDirectory);
                bool hasVersion = TiaMcpConfigurator.UpdateCheck.Installed(bundleRoot) != null;
                bool sourceCheckout = TiaMcpConfigurator.UpdateCheck.IsSourceRepository(bundleRoot);
                Assert.Equal(sourceCheckout, settingsView.SourceCheckoutHint.IsVisible);
                Assert.Equal(Loc.Current["Settings.SourceCheckoutHint"], settingsView.SourceCheckoutHint.Text);
                Assert.Equal(Loc.Current["Config.SourceRepositoryUpdate"], settingsView.SourceCheckoutHint.ToolTip);
                Assert.Equal(hasVersion && !sourceCheckout, settingsView.CheckUpdate.IsEnabled);
                if (sourceCheckout) Capture("settings-service-checkout");
                // Both deployment layouts use the same installed manifest and measured journal data.
                settingsView.SetUpdateAvailability(hasVersion, sourceCheckout: false);
                Assert.Equal(hasVersion, settingsView.CheckUpdate.IsEnabled);
                Assert.False(settingsView.SourceCheckoutHint.IsVisible);
                Capture("settings-service");
                AssertCenteredColumn(settingsView.SettingsColumn, Viewport(settingsView.DetailScroll), 740);
                settingsView.SetUpdateAvailability(hasVersion, sourceCheckout);
                Assert.Equal(Path.Combine(settingsView.DataPath.Text, "reports"), settingsView.ExportsPath.Text);
                long bytes = new DirectoryInfo(journalDirectory).GetFiles("calls-*.jsonl*").Sum(file => file.Length);
                Assert.Equal(Loc.Current.T("Settings.JournalUsage", bytes / 1048576d, 15, 2, 3), settingsView.RetentionUsageText.Text);
                Assert.Equal(bytes * 100d / (15 * 1048576d), settingsView.RetentionUsageBar.Value, 6);
                Assert.True(settingsView.Size5.IsChecked);
                Assert.True(settingsView.Copies3.IsChecked);
                Assert.Equal(Loc.Current["Settings.GenerateZip"], settingsView.SettingsGenerateButton.Content);
                foreach (var folder in new[] { settingsView.OpenConfig, settingsView.OpenLog, settingsView.OpenData, settingsView.OpenExports })
                    Assert.Equal(Loc.Current["Settings.StorageOpenFolder"], folder.Content);
                Assert.Contains(Descendants<TextBlock>(settingsView.ServicePanel), text => text.Text == Loc.Current["Settings.ServiceTrademark"]);
                var retentionToggle = Descendants<ToggleButton>(settingsView.RetentionDetails).Single();
                UnifiedDesktopTests.ClickControl(retentionToggle); WpfContext.Drain(); host.UpdateLayout();
                Assert.Contains(Descendants<TextBlock>(settingsView.RetentionDetails), text => text.IsVisible && text.Text == window.Features.Coverage);
                UnifiedDesktopTests.ClickControl(retentionToggle);
                window.Navigate("Log");
                int logCount = model.Activity.Entries.Count;
                CaptureConfirmation(window, host, window.LogContent.ClearLogButton, "confirm-clear-log", directory, suffix);
                Assert.Equal(logCount, model.Activity.Entries.Count);
                CaptureConfirmation(window, host, window.LogContent.ClearLogButton, "confirm-clear-log", null, suffix, apply: true);
                Assert.Empty(model.Activity.Entries);
                Capture("logs-empty");
                Assert.True(window.LogContent.EmptyLog.IsVisible);
                Assert.False(window.LogContent.ClearLogButton.IsEnabled);
                UnifiedDesktopTests.ClickControl(Descendants<Button>(window.LogContent.EmptyLog).Single());
                Assert.Equal("Settings", window.CurrentPage);
                Assert.True(settingsView.ServicePanel.IsVisible);

                Assert.True(approvals.Deny(destructive.Request.Id));
                window.Navigate("Approvals"); Capture("agent-approvals-empty");
                Assert.True(window.ApprovalsContent.EmptyRequests.IsVisible);
                var historyAction = Descendants<Button>(window.ApprovalsContent.EmptyRequests).Single();
                Assert.True(historyAction.IsEnabled);
                UnifiedDesktopTests.ClickControl(historyAction);
                Assert.True(window.Features.HandledRequests);
                journal.Replace([]);
                window.Navigate("Calls"); Capture("agent-calls-empty");
                Assert.True(window.CallsContent.EmptyCalls.IsVisible);
                UnifiedDesktopTests.ClickControl(Descendants<Button>(window.CallsContent.EmptyCalls).Single());
                Assert.Equal("Clients", window.CurrentPage);
                CaptureAuditVerification(window, journal, environment, Capture);
                window.Navigate("Settings"); settingsView.ShowSection("Service");
                UnifiedDesktopTests.ClickControl(settingsView.SettingsGenerateButton); WpfContext.Drain();
                Assert.True(window.Features.DiagnosticRunning);
                Assert.False(settingsView.SettingsGenerateButton.IsEnabled);
                Assert.IsType<FeaturePageFixtures.Diagnostics>(window.Features.Diagnostics).Complete(); WpfContext.Drain();
                Assert.True(window.Features.DiagnosticDone);
                Assert.Contains(Descendants<TextBlock>(settingsView.StorageTools), text => text.IsVisible && text.Text == window.Features.DiagnosticPath);
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
                Assert.Equal(request.Id, row.HistoryTools);
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
