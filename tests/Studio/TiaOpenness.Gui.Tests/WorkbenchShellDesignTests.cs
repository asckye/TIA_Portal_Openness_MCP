using System;
using System.IO;
using System.Collections.Generic;
using System.Windows.Automation;
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
public sealed partial class WorkbenchShellDesignTests(WpfContext wpf)
{
    internal static void AssertSettingsBounds(MainWindow window, double width, double height)
    {
        var card = window.SettingsCard;
        Assert.Equal(width, card.Width);
        Assert.Equal(height, card.Height);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(card);
        double Rendered(double value, double scale) => card.UseLayoutRounding ? Math.Round(value * scale) / scale : value;
        Assert.Equal(Rendered(width, dpi.DpiScaleX), card.ActualWidth, 5);
        Assert.Equal(Rendered(height, dpi.DpiScaleY), card.ActualHeight, 5);
        var corner = card.TranslatePoint(new Point(), window.Root);
        Assert.InRange(Math.Abs(corner.X - (window.Root.ActualWidth - card.ActualWidth) / 2), 0, 1 / dpi.DpiScaleX);
        Assert.InRange(Math.Abs(corner.Y - (window.Root.ActualHeight - card.ActualHeight) / 2), 0, 1 / dpi.DpiScaleY);
    }

    [Fact]
    public void Default_frame_and_notification_stack_match_the_workbench_dimensions()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, new Services.Stubs.ApprovalServiceStub());
            try
            {
                Assert.Equal(1360, window.Width);
                Assert.Equal(812, window.Height);
                Assert.Equal(HorizontalAlignment.Right, window.ToastStack.HorizontalAlignment);
                Assert.Equal(VerticalAlignment.Bottom, window.ToastStack.VerticalAlignment);
                Assert.Equal(16, window.ToastStack.Margin.Right);
                Assert.Equal(36, window.ToastStack.Margin.Bottom);
            }
            finally { window.Close(); }
        });
    }

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
                AssertDot("Primer.accent");
                UnifiedDesktopTests.ClickControl(window.ToastClose);
                service.Receive(FeaturePageFixtures.Request());
                WpfContext.Drain();
                Assert.Equal(Visibility.Visible, window.PendingBadge.Visibility);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                window.Navigate("Approvals");
                var request = Assert.Single(window.Features.Requests);
                window.Features.Decide(request, true);
                WpfContext.Drain();
                void AssertRowStatus(ApprovalRow decided, string expected)
                {
                    var list = window.ApprovalsContent.RequestList;
                    list.ScrollIntoView(decided);
                    window.UpdateLayout();
                    var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(decided));
                    Assert.Equal(expected, System.Windows.Automation.AutomationProperties.GetItemStatus(item));
                    Assert.Contains(DesktopCapture.Descendants<TextBlock>(item), text => text.IsVisible && text.Text == expected);
                }
                var approved = Loc.Current.T("Approval.ApprovedRequest", request.Request.Id);
                AssertRowStatus(request, approved);
                Assert.Equal(approved, System.Windows.Automation.AutomationProperties.GetItemStatus(window.ApprovalsContent));
                Assert.True(service.Transition(request.Request.Id, ApprovalState.Executing));
                WpfContext.Drain();
                AssertRowStatus(request, approved);
                Assert.Equal(Loc.Current["Approval.Executing"], request.FeedKind);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                window.Features.Decide(request, true);
                WpfContext.Drain();
                Assert.Equal(Loc.Current["Approval.NoLongerPending"], System.Windows.Automation.AutomationProperties.GetItemStatus(window.ApprovalsContent));
                AssertRowStatus(request, Loc.Current["Approval.NoLongerPending"]);
                var language = Loc.Current.Language;
                try
                {
                    Loc.Current.Language = language == AppLanguage.English ? AppLanguage.Chinese : AppLanguage.English;
                    Assert.Equal(Loc.Current["Approval.NoLongerPending"],
                        System.Windows.Automation.AutomationProperties.GetItemStatus(window.ApprovalsContent));
                }
                finally { Loc.Current.Language = language; }
                Assert.True(service.Transition(request.Request.Id, ApprovalState.Completed));
                service.Receive(FeaturePageFixtures.Request("next")); WpfContext.Drain();
                var next = window.Features.SelectedRequest!;
                Assert.Equal("next", next.Request.Id);
                AssertRowStatus(request, Loc.Current["Approval.NoLongerPending"]);
                window.Features.Decide(next, false); WpfContext.Drain();
                AssertRowStatus(next, Loc.Current.T("Approval.RejectedRequest", next.Request.Id));
                window.ApprovalsContent.RequestList.SelectedItem = request; WpfContext.Drain();
                Assert.Equal("", next.DecisionMessage);
                Assert.Equal("", System.Windows.Automation.AutomationProperties.GetItemStatus(window.ApprovalsContent));
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);

                UnifiedDesktopTests.ClickControl(window.ToastClose);
                window.Navigate("VersionControl");
                Assert.Equal(Loc.Current["Shell.PageUnavailable"], window.ToastTitle.Text);
                AssertDot("Primer.warn");
                UnifiedDesktopTests.ClickControl(window.ToastClose);
                window.Features.Run(() => throw new InvalidOperationException("fixture failure"));
                Assert.Equal("fixture failure", window.ToastMessage.Text);
                AssertDot("Ant.colorError");
                UnifiedDesktopTests.ClickControl(window.ToastClose);

                window.Features.Export();
                AssertDot("Primer.accent");
                UnifiedDesktopTests.ClickControl(window.ToastClose);
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
    [InlineData(5)]
    [InlineData(2)]
    public void Scrim_blurs_the_live_backdrop_and_keeps_only_the_tint_in_reduced_modes(int radius)
    {
        wpf.Run(() =>
        {
            var themes = ThemeManager.Current;
            var saved = (themes.LowEffects, themes.HighContrastOverride);
            try
            {
                themes.LowEffects = LowEffectsMode.Off;
                themes.HighContrastOverride = false;
                var source = new Grid { Background = System.Windows.Media.Brushes.White };
                var stripe = new Border { Width = 64, HorizontalAlignment = HorizontalAlignment.Left, Background = System.Windows.Media.Brushes.Black };
                source.Children.Add(stripe);
                var scrim = new WorkbenchScrim { Source = source, BlurRadius = radius };
                var host = new Grid { Width = 128, Height = 48, Children = { source, scrim } };
                byte Pixel(int x)
                {
                    host.Measure(new Size(128, 48)); host.Arrange(new Rect(0, 0, 128, 48)); host.UpdateLayout();
                    var frame = DesktopCapture.Render(host);
                    byte[] pixel = new byte[4];
                    frame.CopyPixels(new Int32Rect(x, 24, 1, 1), pixel, 4, 0);
                    return pixel[0];
                }
                Assert.True(Pixel(63) > Pixel(40));
                Assert.True(Pixel(64) < Pixel(90));
                Assert.Equal(radius, Assert.IsType<System.Windows.Media.Effects.BlurEffect>(scrim.Backdrop.Effect).Radius);
                stripe.HorizontalAlignment = HorizontalAlignment.Right;
                Assert.True(Pixel(63) < Pixel(40));
                themes.LowEffects = LowEffectsMode.On;
                Assert.Null(scrim.Backdrop.Effect); Assert.Equal(Pixel(40), Pixel(63));
                Assert.NotEqual((byte)255, Pixel(40));
                themes.LowEffects = LowEffectsMode.Off; themes.HighContrastOverride = true;
                Assert.Null(scrim.Backdrop.Effect); Assert.Equal(Pixel(40), Pixel(63));
            }
            finally { themes.HighContrastOverride = saved.HighContrastOverride; themes.LowEffects = saved.LowEffects; }
        });
    }

    [Fact]
    public void Environment_actions_use_the_checked_finding_and_dismissal_survives_an_in_progress_recheck()
    {
        wpf.Run(() =>
        {
            var environment = new FeaturePageFixtures.Environment();
            var preview = new TiaMcpConfigurator.ConfigurationPreview("21", @"C:\fixture", "127.0.0.1", true, false, false, "", "", []);
            var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics(), preview);
            try
            {
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), environment);
                WpfContext.ShowWindow(window);
                const string evidence = "http://127.0.0.1:8123/mcp/\nfixture.json";
                var membership = EnvironmentCheckCatalogue.Row(new("membership", Core.Environment.EnvironmentFindingStatus.Fail, "NewLogon", ""));
                var port = EnvironmentCheckCatalogue.Row(
                    new("port", Core.Environment.EnvironmentFindingStatus.Fail, "PortUsed", evidence + "\nfixture (PID 17)"));
                var firewall = EnvironmentCheckCatalogue.Row(new("firewall", Core.Environment.EnvironmentFindingStatus.Fail, "Fail", evidence));
                var unknown = EnvironmentCheckCatalogue.Row(new("firewall", Core.Environment.EnvironmentFindingStatus.Unknown, "Unknown", evidence));
                void Replace(params EnvironmentCheck[] rows)
                {
                    environment.Replace([new(LocalizedText.Key("Shell.Environment"), rows)]);
                    WpfContext.Drain();
                }
                Replace(membership, port, firewall);
                var announcements = new List<string>();
                void Heard(FrameworkElement source) => announcements.Add(AutomationProperties.GetName(source));
                AccessibleStatus.Announced += Heard;
                try
                {
                    var refreshed = EnvironmentCheckCatalogue.Row(new("membership", Core.Environment.EnvironmentFindingStatus.Fail, "NewLogon", ""));
                    Replace(refreshed, port, firewall);
                    Assert.Empty(announcements);
                    foreach (var key in new[] { "sticky-1", "sticky-2", "sticky-3" })
                        window.ShowToast(ToastKind.Warning, LocalizedText.Literal(key), key, sticky: true);
                    Replace(membership, port, firewall);
                    Assert.Contains(announcements, title => title.Contains(Loc.Current["Shell.GroupLogon"]));
                }
                finally { AccessibleStatus.Announced -= Heard; }
                Assert.Equal(Loc.Current["Shell.GroupLogon"], window.ToastTitle.Text);
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.True(window.RailEnvironment.IsChecked);
                Assert.Same(membership, window.Features.SelectedEnvironment!.Check);
                Replace(membership with { }, port, firewall);
                Assert.Equal(Loc.Current["Shell.GroupLogon"], window.ToastTitle.Text);
                UnifiedDesktopTests.ClickControl(window.ToastClose);
                Assert.Contains("8123", window.ToastTitle.Text);
                Assert.Contains("PID 17", window.ToastMessage.Text);
                Assert.Equal(Loc.Current["Shell.ChangePort"], window.ToastAction.Content);
                Assert.Equal(Loc.Current.IsChinese ? "前往 MCP 更改端口" : "Change port in MCP",
                    new System.Windows.Automation.Peers.ButtonAutomationPeer(window.ToastAction).GetName());
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.True(window.RailMcp.IsChecked);
                Assert.True(window.Configuration!.RemoteNav.IsChecked);
                UnifiedDesktopTests.ClickControl(window.ToastClose);
                Assert.Same(window.FindResource("Primer.warn"), window.Toast.BorderBrush);
                Assert.Equal(Loc.Current["Env.ConfigureNetworkPermission"], window.ToastAction.Content);
                UnifiedDesktopTests.ClickControl(window.ToastAction);
                Assert.True(window.Configuration.RemoteNav.IsChecked);
                Assert.NotEqual("8123", window.Configuration.ServerPort.Text);
                UnifiedDesktopTests.ClickControl(window.ToastClose);
                while (window.Toast.IsVisible && window.ToastMessage.Text.StartsWith("sticky-", StringComparison.Ordinal))
                    UnifiedDesktopTests.ClickControl(window.ToastClose);
                Replace(EnvironmentCheckCatalogue.Checking("port"));
                Replace(membership, port, firewall);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Replace(unknown);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Replace(port);
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                UnifiedDesktopTests.ClickControl(window.ToastClose);
                Replace(EnvironmentCheckCatalogue.Checking("port"));
                Replace(port);
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                Replace(unknown);
                Replace(port);
                window.Configuration.LocalNav.IsChecked = true;
                RoutedEventHandler fail = (_, _) => throw new InvalidOperationException("fixture network review failed");
                window.Configuration.RemoteNav.Checked += fail;
                try
                {
                    UnifiedDesktopTests.ClickControl(window.ToastAction);
                    WpfContext.Drain();
                    Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                    Assert.Equal("fixture network review failed", window.ToastMessage.Text);
                    Assert.Same(window.FindResource("Ant.colorError"), window.ToastDot.Fill);
                }
                finally { window.Configuration.RemoteNav.Checked -= fail; }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.English)]
    [InlineData(1360, 812, AppTheme.Dark, AppLanguage.Chinese)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.Chinese)]
    [InlineData(1024, 640, AppTheme.Light, AppLanguage.English)]
    [InlineData(1024, 640, AppTheme.Dark, AppLanguage.Chinese)]
    [InlineData(1360, 812, AppTheme.Light, AppLanguage.Chinese, true)]
    public void Chrome_frames_each_page_and_overlay(int width, int height, AppTheme theme, AppLanguage language, bool hc = false)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var themes = ThemeManager.Current;
            var saved = (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen, themes.LowEffects);
            themes.LowEffects = LowEffectsMode.Off;
            themes.Theme = theme; themes.Accent = AppAccent.Cobalt; themes.HighContrastOverride = hc; App.Settings.LeftPaneOpen = true;
            FakeProjectClient client = null!;
            var model = ProjectPageTestSupport.Model(results: true, release: "21", configure: fake => client = fake);
            var service = FeaturePageFixtures.Approvals();
            service.Receive(FeaturePageFixtures.Request("shell-second"));
            var journal = new FeaturePageFixtures.Journal();
            var audit = new FeaturePageFixtures.Audit();
            var preview = new TiaMcpConfigurator.ConfigurationPreview("21", @"C:\fixture\Portal V21", "127.0.0.1", true, false, false, "", "",
                [new("fixture", "Codex", @"C:\fixture\config.toml", "Offline fixture") { Detected = true }]);
            var environment = new FeaturePageFixtures.Environment();
            var observedIssues = environment.Groups;
            environment.Replace([new(LocalizedText.Key("Env.Network"),
                [EnvironmentCheckCatalogue.Row(new("port", Core.Environment.EnvironmentFindingStatus.Pass, "Pass", "http://127.0.0.1:8123/mcp/"))])]);
            var window = new MainWindow(model, false, service, new FeaturePageFixtures.Diagnostics(), preview);
            try
            {
                window.ConfigureFeaturePages(journal, audit, environment,
                    new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                var host = WorkbenchRenderTests.CreateHost(window, width, height);
                client.CompileResult = new() { State = "Warning", WarningCount = 1 };
                model.Engineering.Compile.Execute(null);
                WpfContext.Complete(window.Features.VerifyAsync());
                window.BlocksContent.LocateControlBlock(model.Engineering.Blocks[1]);
                client.LogicHandler = (_, _) => Task.FromResult("VAR_INPUT\n    Start : Bool;\nEND_VAR\nBEGIN\n    #Running := #Start;\nEND_FUNCTION_BLOCK");
                WpfContext.Complete(model.Engineering.ReadFocusedBlockAsync());
                var folder = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
                string suffix = $"{(hc ? "hc" : theme.ToString().ToLowerInvariant())}-{(Loc.Current.IsChinese ? "zh" : "en")}-{width}x{height}";
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
                void AssertSharpPanel(WorkbenchScrim scrim, Border panel)
                {
                    Assert.Equal(panel == window.ApprovalsContent.ConfirmationCard ? 5 : 2, scrim.BlurRadius);
                    Assert.Equal(hc, scrim.Backdrop.Effect == null);
                    var bounds = panel.TransformToVisual(scrim).TransformBounds(new Rect(panel.RenderSize));
                    Assert.True(bounds.Width > 100 && bounds.Height > 40);
                    Assert.True(scrim.Clip.FillContains(new Point(8, scrim.ActualHeight / 2)));
                    Assert.False(scrim.Clip.FillContains(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)));
                    var point = panel.TranslatePoint(new Point(10, 16), host);
                    byte[] Pixel()
                    {
                        byte[] pixel = new byte[4];
                        DesktopCapture.Render(host).CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                        return pixel;
                    }
                    var painted = Pixel();
                    scrim.Opacity = 0;
                    try { Assert.Equal(Pixel(), painted); }
                    finally { scrim.Opacity = 1; }
                }
                void ClearFeedback()
                {
                    while (window.Toast.Visibility == Visibility.Visible) UnifiedDesktopTests.ClickControl(window.ToastClose);
                }
                window.Navigate("Engineering"); ClearFeedback(); Capture("project-code");
                Assert.Equal(width == 1024 ? 240 : 350, window.CommandPaletteButton.ActualWidth);
                var normal = environment.Groups;
                foreach (var (id, result, evidence) in new[]
                {
                    ("membership", "Fail", "Group membership fixture."),
                    ("port", "PortUsed", "http://127.0.0.1:8123/\nfixture.json\nfixture (PID 17)"),
                    ("firewall", "Fail", "http://127.0.0.1:8123/\nfixture.json")
                })
                {
                    var check = EnvironmentCheckCatalogue.Row(new(id, Core.Environment.EnvironmentFindingStatus.Fail, result, evidence));
                    environment.Replace([new(LocalizedText.Key("Shell.Environment"), [check])]);
                    WpfContext.Drain(); host.UpdateLayout();
                    Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                    if (id != "membership") Assert.Contains("8123", window.ToastTitle.Text);
                    Capture("env-alert-" + (id == "membership" ? "group" : id));
                    var dismiss = window.ToastClose;
                    UnifiedDesktopTests.ClickControl(dismiss);
                    Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                }
                environment.Replace(normal);
                WpfContext.Drain();
                var gate = new TaskCompletionSource();
                var busy = model.Activity.Guarded("Status.ReadingBlocks", () => gate.Task, model.Engineering.SelectedDevice!.Name);
                try
                {
                    client.EmitProgress(new Contracts.Rpc.ProgressPayload { Operation = "ListBlocks", Current = 1, Total = 2, Message = "Reading" });
                    WpfContext.Drain(); host.UpdateLayout();
                    Assert.Equal(Visibility.Visible, window.BusyHairline.Visibility);
                    Assert.Equal(340, window.BusyCard.ActualWidth);
                    Capture("state-busy");
                }
                finally { gate.SetResult(); WpfContext.Complete(busy); }
                Assert.Equal(Visibility.Collapsed, window.BusyCard.Visibility);
                window.BlocksContent.LocateControlBlock(model.Engineering.Blocks[3]);
                client.LogicHandler = (_, _) => Task.FromException<string>(new InvalidOperationException("Read timed out."));
                WpfContext.Complete(model.Engineering.ReadFocusedBlockAsync());
                ClearFeedback();
                Assert.Equal(Visibility.Visible, window.BlocksContent.SourceError.Visibility);
                Capture("state-error");
                client.LogicHandler = (_, _) => Task.FromResult("END_FUNCTION_BLOCK");
                model.Engineering.RetrySource.Execute(null);
                WpfContext.Drain();
                Assert.Equal(Visibility.Collapsed, window.BlocksContent.SourceError.Visibility);
                Assert.Equal("END_FUNCTION_BLOCK", Assert.Single(model.Engineering.SourceLines));
                UnifiedDesktopTests.ClickControl(window.SessionStatusButton); Capture("session-popover");
                UnifiedDesktopTests.ClickControl(window.SessionStatusButton);
                var file = Path.GetFileName(model.Session.OpenProjectFile);
                Assert.NotEmpty(file);
                model.Session.ProjectPath = @"C:\fixture\unopened.ap21";
                Assert.EndsWith(file, window.CurrentPageTitle.Text);
                Assert.EndsWith(file, window.SessionStatus.Text);
                Assert.Equal(Visibility.Visible, window.ProjectTree.Visibility);
                Assert.Equal(width == 1024 ? 214 : 262, window.ProjectTree.ActualWidth);
                UnifiedDesktopTests.ClickControl(window.OperationsContent.InterfaceTab); Capture("project-interface");
                window.OperationsContent.ShowProjectOptions(); Capture("drawer-project");
                AssertSharpPanel(window.ProjectOptionsRailScrim, window.OperationsContent.Options.OptionsPanel);
                window.OperationsContent.HideOptions();
                window.OperationsContent.ShowTransferOptions(); Capture("drawer-export"); window.OperationsContent.HideOptions();
                window.OperationsContent.ShowControlInspectionRules(); Capture("drawer-rules"); window.OperationsContent.HideOptions();
                window.BlocksContent.ShowCompileTab(); Capture("project-compile");
                UnifiedDesktopTests.ClickControl((RadioButton)ProjectPageTestSupport.Find(window, "TagsTab")); Capture("project-tags");
                window.Navigate("VersionControl"); Capture("project-vci");
                window.Navigate("Approvals"); Capture("agent-approvals");
                window.Features.HandledRequests = true; Capture("agent-approvals-history"); window.Features.HandledRequests = false;
                Assert.True(window.LeftPaneToggle.IsEnabled);
                Assert.Equal(width == 1024 ? 214 : 260, window.ApprovalsContent.RequestListPane.ActualWidth);
                window.Navigate("Calls"); Capture("agent-calls");
                Assert.False(window.LeftPaneToggle.IsEnabled); Assert.Equal(0.3, window.LeftPaneToggle.Opacity);
                foreach (var (page, name) in new[] { ("Mcp", "mcp"), ("Clients", "clients"), ("Environment", "doctor"),
                    ("Audit", "audit"), ("Log", "logs"), ("Settings", "settings") })
                {
                    window.Navigate(page); Capture(name);
                    if (page == "Audit")
                    {
                        using var finish = new System.Threading.ManualResetEventSlim();
                        audit.BeforeVerify = finish.Wait;
                        var verifying = window.Features.VerifyAsync();
                        try { Assert.True(window.Features.Verifying); Capture("audit-verifying"); }
                        finally { finish.Set(); WpfContext.Complete(verifying); audit.BeforeVerify = null; }
                        ClearFeedback(); Capture("audit-ok");
                        audit.Broken = true; WpfContext.Complete(window.Features.VerifyAsync()); ClearFeedback(); Capture("audit-broken");
                        audit.Broken = false; WpfContext.Complete(window.Features.VerifyAsync()); ClearFeedback();
                    }
                    if (page == "Environment")
                    {
                        environment.Replace(observedIssues); Capture("doctor-issues");
                        environment.Replace(normal);
                    }
                    if (page == "Mcp")
                    {
                        window.Configuration!.RemoteNav.IsChecked = true;
                        Capture("mcp-remote");
                    }
                    if (page == "Settings")
                    {
                        window.SettingsContent.ShowSection("Appearance"); Capture("settings-appearance");
                        window.SettingsContent.ShowSection("AI"); Capture("settings-safety");
                        window.SettingsContent.ShowSection("Service"); Capture("settings-service");
                        Assert.Equal(204, ((Border)window.SettingsContent.SectionList.Parent).ActualWidth);
                        AssertSettingsBounds(window, width == 1024 ? width * 0.94 : 840, width == 1024 ? height * 0.88 : 584);
                    }
                }
                window.Navigate("Engineering");
                WorkbenchCommands.CommandPalette.Execute(null, host);
                Assert.Equal(Visibility.Visible, window.OverlayHost.Visibility);
                Capture("palette");
                UnifiedDesktopTests.ClickControl(((CommandPalette)window.OverlayHost.Children[0]).Dismiss);
                window.Navigate("Approvals");
                foreach (var pending in service.Requests.Where(row => row.State == ApprovalState.Pending).ToArray())
                    Assert.True(service.Deny(pending.Id));
                WpfContext.Drain();
                Assert.Equal(Visibility.Collapsed, window.PendingBadge.Visibility);
                Capture("agent-approvals-empty");
                service.Receive(FeaturePageFixtures.Request("review-first"));
                service.Receive(FeaturePageFixtures.Request("review-next")); WpfContext.Drain();
                var decided = window.Features.Requests.Single(row => row.Request.Id == "review-first");
                var following = window.Features.Requests.Single(row => row.Request.Id == "review-next");
                window.Features.Decide(decided, true); WpfContext.Drain(); Capture("approval-approved");
                window.Features.Decide(decided, true); WpfContext.Drain(); Capture("approval-warning");
                Assert.True(service.Transition(decided.Request.Id, ApprovalState.Executing));
                Assert.True(service.Transition(decided.Request.Id, ApprovalState.Completed));
                window.Features.Decide(following, false); WpfContext.Drain(); Capture("approval-denied");
                var calls = journal.Calls;
                journal.Replace([]); window.Navigate("Calls"); Capture("agent-calls-empty"); journal.Replace(calls);
                var events = audit.Events;
                audit.Events = []; window.Navigate("Audit"); Capture("audit-empty"); audit.Events = events;
                WpfContext.Complete(window.Features.VerifyAsync()); ClearFeedback();
                model.Activity.ClearLog(); window.Navigate("Log"); Capture("logs-empty");
                window.Navigate("Approvals");
                var request = FeaturePageFixtures.Request("shell-delete") with { Risk = ApprovalRisk.Destructive };
                service.Receive(request); WpfContext.Drain();
                window.Features.SelectedRequest = window.Features.Requests.Single(row => row.Request.Id == request.Id);
                host.UpdateLayout();
                ClearFeedback();
                Assert.Equal(Visibility.Collapsed, window.Toast.Visibility);
                window.Features.Export();
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Capture("toast");
                UnifiedDesktopTests.ClickControl(window.ToastClose);
                WpfContext.WaitForDecisionGuard(window.ApprovalsContent);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.ApproveRequest);
                Assert.Equal(Visibility.Visible, window.ApprovalsContent.ConfirmationSheet.Visibility);
                Capture("danger-sheet");
                AssertSharpPanel(window.ConfirmationFrameScrim, window.ApprovalsContent.ConfirmationCard);
                UnifiedDesktopTests.ClickControl(window.ApprovalsContent.CancelDestructive);
                window.Navigate("Engineering");
                model.Engineering.Document = ProjectDocument.Render;
                window.Width = width; window.Height = height; window.Content = host;
                WpfContext.ShowWindow(window);
                CaptureNativeModals(window, host, folder, suffix);
                window.Navigate("Engineering");
                Assert.True(service.Deny(request.Id));
                var populatedBlocks = client.Blocks.ToArray();
                client.Blocks.Clear();
                model.Engineering.Refresh.Execute(null); WpfContext.Drain();
                Assert.Equal(Visibility.Visible, window.BlocksContent.EmptyBlocks.Visibility);
                Capture("state-empty");
                client.Blocks.AddRange(populatedBlocks);
                model.Engineering.Refresh.Execute(null); WpfContext.Drain();
                window.BlocksContent.LocateControlBlock(model.Engineering.Blocks[1]);
                WpfContext.Complete(model.Engineering.ReadFocusedBlockAsync());
                service.Receive(FeaturePageFixtures.Request("after-empty"));
                ClearFeedback();
                client.Exit(); WpfContext.Drain();
                Assert.Equal(Visibility.Visible, window.Toast.Visibility);
                Assert.Equal(Loc.Current["Project.ViewLogs"], window.ToastSecondAction.Content);
                Assert.Equal("✕", window.SessionGlyph.Text);
                Capture("state-crashed");
                model.Session.Connect.Execute(null); WpfContext.Drain();
                model.Session.Disconnect.Execute(null); WpfContext.Drain(); ClearFeedback();
                Assert.EndsWith(Loc.Current["Shell.NoProjectFile"], window.SessionStatus.Text);
                Capture("state-disconnected");
            }
            finally
            {
                window.Close();
                (themes.Theme, themes.Accent, themes.HighContrastOverride, App.Settings.LeftPaneOpen, themes.LowEffects) = saved;
            }
        });
    }
}
