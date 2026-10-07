using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
                var window = new MainWindow(new MainViewModel(new FakeStudioClient(), new FakeDialogService()), false, approvals, diagnostics, preview);
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
                        UnifiedDesktopTests.ClickControl((RadioButton)((Views.SettingsView)window.FindName("SettingsContent")).FindName("ApprovalOff"));
                        Assert.Equal(Visibility.Visible, ((Grid)window.FindName("ConfirmOverlay")).Visibility);
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
                    if (state.StartsWith("calls", StringComparison.Ordinal)) Assert.Equal(170, ((FrameworkElement)((Views.AiCallsView)window.FindName("CallsContent")).FindName("SearchBox")).ActualWidth);
                    var bitmap = new RenderTargetBitmap(1200, 780, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, state + ".png")); encoder.Save(stream);
                }
                finally { window.Close(); ThemeManager.Current.Theme = previousTheme; }
            });
        }
    }

    internal static Border Detach(MainWindow window)
    {
        var content = (FrameworkElement)window.Content; window.Content = null;
        var host = new Border { Child = content, DataContext = window.DataContext, Width = 1200, Height = 780 };
        NameScope.SetNameScope(host, NameScope.GetNameScope(window));
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
