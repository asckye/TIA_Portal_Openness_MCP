using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class WorkbenchShellDesignTests(WpfContext wpf)
{
    [Fact]
    public void Toast_uses_the_palette_for_the_event_that_opened_it()
    {
        wpf.Run(() =>
        {
            var service = FeaturePageFixtures.Approvals(false);
            var model = new MainViewModel(new FakeProjectClient(), new FakeDialogService());
            var window = new MainWindow(model, false, service, new FeaturePageFixtures.Diagnostics());
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(),
                    new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                WpfContext.ShowWindow(window);
                void AssertDot(string key)
                {
                    Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                    Assert.Same(window.ToastDot.FindResource(key), window.ToastDot.Fill);
                }

                window.Features.Export();
                AssertDot("Primer.ok");
                service.Receive(FeaturePageFixtures.Request());
                AssertDot("Primer.accent");
                window.Navigate("Approvals");
                var request = Assert.Single(window.Features.Requests);
                window.Features.Decide(request, true);
                Assert.Equal(Loc.Current["Approval.ApprovedToast"], window.ToastMessage.Text);
                AssertDot("Primer.ok");
                window.Features.Decide(request, true);
                Assert.Equal(Loc.Current["Approval.NoLongerPending"], window.ToastMessage.Text);
                AssertDot("Primer.warn");

                window.Navigate("VersionControl");
                Assert.Equal(Loc.Current["Shell.PageUnavailable"], window.ToastTitle.Text);
                AssertDot("Primer.warn");
                window.Features.Run(() => throw new InvalidOperationException("fixture failure"));
                Assert.Equal("fixture failure", window.ToastMessage.Text);
                AssertDot("Primer.warn");

                window.Features.Export();
                AssertDot("Primer.ok");
                var done = new TaskCompletionSource();
                var busy = model.Activity.Guarded("Env.Checking", () => done.Task);
                try
                {
                    window.Close();
                    Assert.True(window.IsVisible);
                    Assert.Equal(Loc.Current["Shell.BusyTitle"], window.ToastTitle.Text);
                    AssertDot("Primer.warn");
                }
                finally { done.SetResult(); WpfContext.Complete(busy); }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Pending_badge_draws_the_system_outline_in_high_contrast()
    {
        wpf.Run(() =>
        {
            var themes = ThemeManager.Current;
            var previous = themes.HighContrastOverride;
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals());
            try
            {
                WpfContext.ShowWindow(window);
                themes.HighContrastOverride = false;
                window.UpdateLayout();
                var badge = DesktopCapture.Descendants<Border>(window.PendingBadge).Single();
                Assert.Equal(new Thickness(0), badge.BorderThickness);
                themes.HighContrastOverride = true;
                window.UpdateLayout();
                Assert.Equal(new Thickness(1), badge.BorderThickness);
                Assert.Same(badge.FindResource("Primer.hcOutline"), badge.BorderBrush);
            }
            finally { themes.HighContrastOverride = previous; window.Close(); }
        });
    }

    [Fact]
    public void Shared_primitives_measure_at_the_handoff_sizes_and_expose_native_patterns()
    {
        wpf.Run(() =>
        {
            static T Layout<T>(T element, string key) where T : FrameworkElement
            {
                element.Style = (Style)Application.Current.FindResource("Primer." + key);
                element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                element.Arrange(new Rect(element.DesiredSize));
                element.UpdateLayout();
                return element;
            }

            foreach (var family in new[] { "Primary", "Secondary", "Ghost", "Danger", "DangerSolid" })
                foreach (var (size, height) in new[] { ("Small", 28), ("Medium", 32), ("Large", 36) })
                {
                    var button = Layout(new Button { Content = "OK" }, $"Button.{family}.{size}");
                    Assert.Equal(height, button.ActualHeight);
                    Assert.NotNull(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
                }
            foreach (var (key, height) in new[] { ("IdeBar", 44), ("IdeSubBar", 38), ("IdeChip", 26), ("Tag", 22), ("TableHeaderBand", 34) })
                Assert.Equal(height, Layout(new Border(), key).ActualHeight);
            var cell = Layout(new Border(), "TableCell");
            Assert.Equal(new Thickness(14, 10, 14, 10), cell.Padding);
            var badge = Layout(new Border(), "BlkBadge");
            Assert.Equal(new Size(22, 18), badge.RenderSize);
            var search = Layout(new TextBox(), "IdeSearch");
            Assert.Equal(28, search.ActualHeight);
            Assert.Equal(28, Layout(new Button { Content = "OK" }, "IdeToolButton").ActualHeight);
            Assert.Equal(28, Layout(new RadioButton { Content = "OK" }, "IdePill").ActualHeight);
            Assert.Equal(16, Layout(new Border(), "IdePillCount").ActualHeight);
            foreach (var (key, height) in new[] { ("IdeChipButton", 26), ("IdeToolToggle", 28), ("SelRow", 28) })
            {
                var toggle = Layout(new ToggleButton { Content = "OK" }, key);
                Assert.Equal(height, toggle.ActualHeight);
                Assert.NotNull(new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle));
                toggle.IsChecked = true;
                Assert.Equal(FontWeights.SemiBold, toggle.FontWeight);
            }
            var switchButton = Layout(new ToggleButton(), "Switch");
            var track = (Border)switchButton.Template.FindName("Track", switchButton);
            Assert.Equal(new Size(36, 20), track.RenderSize);
            var thumb = (FrameworkElement)track.Child;
            Assert.Equal(new Size(14, 14), thumb.RenderSize);
            switchButton.IsChecked = true;
            Assert.Equal(HorizontalAlignment.Right, thumb.HorizontalAlignment);

            var tree = new TreeViewItem { Header = "PLC" };
            tree.Measure(new Size(240, 100));
            tree.Arrange(new Rect(tree.DesiredSize));
            Assert.Equal(28, ((Border)tree.Template.FindName("Node", tree)).ActualHeight);
        });
    }

    [Theory]
    [InlineData(1366, 860, AppTheme.Light, AppLanguage.Chinese)]
    [InlineData(1366, 860, AppTheme.Light, AppLanguage.English)]
    [InlineData(1366, 860, AppTheme.Dark, AppLanguage.Chinese)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.Chinese)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.English)]
    [InlineData(1024, 640, AppTheme.Dark, AppLanguage.Chinese)]
    public void Chrome_frames_each_page_and_overlay(int width, int height, AppTheme theme, AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var themes = ThemeManager.Current;
            var saved = (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen);
            themes.Theme = theme; themes.Accent = AppAccent.Cobalt; themes.HighContrastOverride = false; App.Settings.LeftPaneOpen = true;
            var model = ProjectPageTestSupport.Model(results: true, release: "21");
            var service = FeaturePageFixtures.Approvals();
            var preview = new TiaMcpConfigurator.ConfigurationPreview("21", @"C:\fixture\Portal V21", "127.0.0.1", true, false, false, "", "",
                [new("fixture", "Codex", @"C:\fixture\config.toml", "Offline fixture") { Detected = true }]);
            var window = new MainWindow(model, false, service, new FeaturePageFixtures.Diagnostics(), preview);
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(),
                    new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                model.Engineering.Compile.Execute(null);
                WpfContext.Complete(window.Features.VerifyAsync());
                window.BlocksContent.LocateControlBlock(model.Engineering.Blocks[1]);
                var folder = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
                string suffix = $"{theme.ToString().ToLowerInvariant()}-{(Loc.Current.IsChinese ? "zh" : "en")}-{width}x{height}";
                void Capture(string view)
                {
                    WpfContext.Drain(); host.UpdateLayout();
                    Assert.Equal(38, ((Border)window.CaptionBar.Parent).ActualHeight);
                    Assert.Equal(60, window.Sider.ActualWidth);
                    Assert.Equal(26, window.IdeStatusBar.ActualHeight);
                    Assert.Equal("Ctrl+K", new ButtonAutomationPeer(window.CommandPaletteButton).GetAcceleratorKey());
                    if (folder == null) return;
                    var directory = Directory.CreateDirectory(Path.Combine(folder, "design"));
                    WorkbenchRenderTests.SaveRender(host, Path.Combine(directory.FullName, $"{view}-{suffix}.png"), width, height);
                }
                window.Navigate("Engineering"); Capture("project-code");
                var file = Path.GetFileName(model.Session.OpenProjectFile);
                Assert.NotEmpty(file);
                model.Session.ProjectPath = @"C:\fixture\unopened.ap21";
                Assert.EndsWith(file, window.CurrentPageTitle.Text);
                Assert.EndsWith(file, window.SessionStatus.Text);
                Assert.Equal(width == 1366, window.ProjectTree.Visibility == Visibility.Visible);
                if (width == 1366) Assert.Equal(262, window.ProjectTree.ActualWidth);
                // The current Project view has no Interface tab. Capture the real Project host for the shell comparison.
                Capture("project-interface");
                window.BlocksContent.ShowCompileTab(); Capture("project-compile");
                UnifiedDesktopTests.ClickControl((RadioButton)ProjectPageTestSupport.Find(window, "TagsTab")); Capture("project-tags");
                window.Navigate("VersionControl"); Capture("project-vci");
                window.Navigate("Approvals"); Capture("agent-approvals");
                Assert.True(window.LeftPaneToggle.IsEnabled);
                if (width == 1366) Assert.Equal(262, window.ApprovalsContent.RequestListPane.ActualWidth);
                window.Navigate("Calls"); Capture("agent-calls");
                Assert.False(window.LeftPaneToggle.IsEnabled); Assert.Equal(0.3, window.LeftPaneToggle.Opacity);
                foreach (var (page, name) in new[] { ("Mcp", "mcp"), ("Clients", "clients"), ("Environment", "doctor"),
                    ("Audit", "audit"), ("Log", "logs"), ("Settings", "settings") })
                {
                    window.Navigate(page); Capture(name);
                    if (page == "Settings" && width == 1366)
                        Assert.Equal(236, ((Border)window.SettingsContent.SectionList.Parent).ActualWidth);
                }
                window.Navigate("Engineering");
                WorkbenchCommands.CommandPalette.Execute(null, host);
                Assert.Equal(Visibility.Visible, window.OverlayHost.Visibility);
                Capture("palette");
                UnifiedDesktopTests.ClickControl(((CommandPalette)window.OverlayHost.Children[0]).Dismiss);
                window.Navigate("Approvals");
                Assert.True(service.Deny(service.Requests.Single(row => row.State == ApprovalState.Pending).Id));
                var request = FeaturePageFixtures.Request("shell-delete") with { Risk = ApprovalRisk.Destructive };
                service.Receive(request); WpfContext.Drain();
                window.Features.SelectedRequest = window.Features.Requests.Single(row => row.Request.Id == request.Id);
                host.UpdateLayout();
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Capture("toast");
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.ApproveRequest);
                Assert.Equal(Visibility.Visible, window.ApprovalsContent.ConfirmationSheet.Visibility);
                Capture("danger-sheet");
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.CancelDestructive);
            }
            finally
            {
                window.Close();
                (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen) = saved;
            }
        });
    }
}
