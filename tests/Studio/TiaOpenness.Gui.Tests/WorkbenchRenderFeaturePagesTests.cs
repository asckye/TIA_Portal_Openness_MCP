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
