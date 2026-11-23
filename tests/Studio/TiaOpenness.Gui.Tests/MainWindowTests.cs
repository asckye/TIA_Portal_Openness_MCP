using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// Builds the real main window and pushes it through layout and a render pass.
///
/// This is the test the theme most needs, because the failures it catches are the ones the
/// compiler cannot: a StaticResource that does not exist throws when the XAML is parsed, a
/// resource key that resolves to the wrong type throws inside OnRender, and a template that
/// quietly binds to nothing shows up as a control that is simply not there. All three happened
/// while the theme was being written and none of them were visible before the window opened.
///
/// The window's content is re-hosted in a plain Border for rendering: a Window that has never
/// been shown has no HWND and no visual tree of its own, so it cannot be rendered directly, but
/// everything the user actually sees is under Content and renders fine.
/// </summary>
[Collection(WpfCollection.Name)]
public class MainWindowTests(WpfContext wpf)
{
    private const int Width = 1200;
    private const int Height = 780;

    private sealed class Rendered : IDisposable
    {
        public required MainWindow Window { get; init; }
        public required Border Host { get; init; }
        public required RenderTargetBitmap Bitmap { get; init; }

        public void Dispose() => Window.Close();
    }

    private static Rendered Render(bool connected = false)
    {
        var window = new MainWindow(ProjectPageTestSupport.Model(connected, release: "21"), false);

        var content = (UIElement)window.Content;
        window.Content = null;

        var host = new Border
        {
            Child = content,
            DataContext = window.DataContext,
            Width = Width,
            Height = Height,
        };

        Layout(host);

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        return new Rendered { Window = window, Host = host, Bitmap = bitmap };
    }

    private static void Layout(FrameworkElement host)
    {
        host.Measure(new Size(Width, Height));
        host.Arrange(new Rect(0, 0, Width, Height));
        host.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    /// <summary>
    /// Whether an element actually made it onto the surface.
    ///
    /// Not <c>IsVisible</c>: that is false for the whole tree here, because it also requires a
    /// live PresentationSource and these tests deliberately never open a window. Size after
    /// layout is the honest test - a collapsed element, or one inside a collapsed parent,
    /// measures to nothing.
    /// </summary>
    private static bool IsShown(FrameworkElement element)
        => element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0;

    private static Color PixelAt(RenderTargetBitmap bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static IReadOnlyList<RadioButton> Group(DependencyObject root, string name)
        => Descendants<RadioButton>(root).Where(r => r.GroupName == name).ToList();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetSystemMetricsForDpi(int metric, uint dpi);

    [Theory]
    [InlineData(AppLanguage.Chinese)]
    [InlineData(AppLanguage.English)]
    public void Maximizing_insets_the_frame_and_offers_restore(AppLanguage language) => wpf.RunWithLanguage(language, () =>
    {
        var window = new MainWindow(ProjectPageTestSupport.Model(), false);
        try
        {
            WpfContext.ShowWindow(window);
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(window.MaximizeWindowButton);
            var normal = (window.Root.Margin, window.ShellFrame.CornerRadius, window.ShellFrame.BorderThickness, window.MaximizeGlyph.Data);
            window.WindowState = WindowState.Maximized;
            WpfContext.Drain();
            var scale = VisualTreeHelper.GetDpi(window);
            uint dpi = (uint)Math.Round(scale.PixelsPerInchX);
            double x = (GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi)) / scale.DpiScaleX;
            double y = (GetSystemMetricsForDpi(33, dpi) + GetSystemMetricsForDpi(92, dpi)) / scale.DpiScaleY;
            Assert.Equal(new Thickness(x, y, x, y), window.Root.Margin);
            Assert.Equal(new CornerRadius(0), window.ShellFrame.CornerRadius);
            Assert.Equal(new Thickness(0), window.ShellFrame.BorderThickness);
            Assert.Equal(Loc.Current["Access.Restore"], peer.GetName());
            Assert.NotSame(normal.Data, window.MaximizeGlyph.Data);
            window.WindowState = WindowState.Normal;
            WpfContext.Drain();
            Assert.Equal(normal, (window.Root.Margin, window.ShellFrame.CornerRadius, window.ShellFrame.BorderThickness, window.MaximizeGlyph.Data));
            Assert.Equal(Loc.Current["Access.Maximize"], peer.GetName());
        }
        finally { window.Close(); }
    });

    [Fact]
    public void An_attached_session_without_participant_text_hides_its_separator() => wpf.Run(() =>
    {
        var session = new ProjectPageInteractionTests.DocumentSession((_, _) => "{}");
        var model = new ViewModels.MainViewModel(new Services.McpProjectClient(session), new FakeDialogService());
        var window = new MainWindow(model, false);
        try
        {
            WpfContext.Complete(model.Session.ApplySharedStateAsync(null));
            WorkbenchRenderTests.CreateHost(window, 1366, 860);
            Assert.True(model.Panel!.Attached);
            Assert.Empty(window.ClientStatus.Text);
            Assert.Equal(Visibility.Collapsed, window.TiaProcessStatus.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ParticipantStatus.Visibility);
            Assert.Equal("·", window.ParticipantStatus.Children.OfType<TextBlock>().First().Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Window_chrome_returns_the_native_maximize_hit_for_Snap_Layouts()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            try
            {
                WpfContext.ShowWindow(window); window.UpdateLayout();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                int Hit(FrameworkElement element)
                {
                    var screen = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
                    return (int)SendMessage(hwnd, 0x0084, IntPtr.Zero, ((int)screen.Y << 16) | (ushort)(int)screen.X);
                }
                Assert.Equal(9, Hit(window.MaximizeWindowButton));
                Assert.NotEqual(9, Hit(window.MinimizeWindowButton));
                Assert.NotEqual(9, Hit(window.CloseWindowButton));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_caption_click_paints_hover_and_press_and_toggles_maximize_once_on_release(bool highContrast)
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(ProjectPageTestSupport.Model(), false);
            var themes = ThemeManager.Current;
            var previous = themes.HighContrastOverride;
            try
            {
                themes.HighContrastOverride = highContrast;
                WpfContext.ShowWindow(window);
                var button = window.MaximizeWindowButton;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                int clicks = 0;
                button.Click += (_, _) => clicks++;
                void Send(int message, int hit)
                {
                    window.UpdateLayout();
                    var screen = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                    SendMessage(hwnd, message, hit, ((int)screen.Y << 16) | (ushort)(int)screen.X);
                }

                var surface = (Border)button.Template.FindName("Surface", button);
                var normalFill = button.Background;
                Send(0x00A0, 9);
                Assert.Same(button.FindResource("Primer.pillHover"), button.Background);
                Assert.Same(button.Background, surface.Background);
                if (highContrast)
                {
                    Assert.Same(button.FindResource("Primer.hcOutline"), surface.BorderBrush);
                    Assert.Equal(new Thickness(2), surface.BorderThickness);
                }
                Send(0x00A1, 9);
                Assert.Same(button.FindResource(highContrast ? "Primer.primaryBg" : "Primer.cardSel"), button.Background);
                Assert.Same(button.Background, surface.Background);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Send(0x00A2, 9);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.Equal(1, clicks);

                Send(0x00A1, 9);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Send(0x00A2, 9);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(2, clicks);

                Send(0x00A1, 9);
                Send(0x02A2, 0);
                Assert.Same(normalFill, button.Background);
                Send(0x00A2, 9);
                Assert.Same(normalFill, button.Background);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(2, clicks);
            }
            finally { themes.HighContrastOverride = previous; window.Close(); }
        });
    }

    [Fact]
    public void Loads_lays_out_and_renders_without_throwing()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();

            Assert.True(rendered.Host.ActualWidth > 0);
            Assert.Equal(Width, rendered.Bitmap.PixelWidth);
        });
    }

    [Fact]
    public void Connected_portal_without_an_open_project_keeps_the_session_and_project_states_distinct()
    {
        wpf.Run(() =>
        {
            var model = ProjectPageTestSupport.Model(connected: false, configure: client => client.AttachedProject = null);
            var window = new MainWindow(model, false);
            try
            {
                model.Session.Connect.Execute(null);
                WpfContext.Drain();
                Assert.True(model.Session.IsConnected);
                Assert.Equal(Loc.Current["Shell.Engineering"], window.CurrentPageTitle.Text);
                Assert.EndsWith(Loc.Current["Shell.NoProjectFile"], window.SessionStatus.Text);
                Assert.Equal("✓", window.SessionGlyph.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Ide_toolbar_controls_use_shared_sizes_and_selection_boundaries()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();
            var window = rendered.Window;
            Assert.Equal(38, ((Grid)window.FindName("CaptionBar")).ActualHeight);
            foreach (var name in new[] { "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
            {
                var button = (Button)window.FindName(name);
                Assert.Equal(24, button.ActualWidth); Assert.Equal(24, button.ActualHeight);
            }
            Assert.Equal(26, ((Border)window.FindName("IdeStatusBar")).MinHeight);
            foreach (string name in new[] { "SessionStatusButton", "CompileStatusButton", "ApprovalStatusButton" })
                Assert.Equal(24, ((Button)window.FindName(name)).Height);
            Assert.Equal(24, window.SessionStatusButton.ActualHeight);
            Assert.Equal(24, window.ApprovalStatusButton.ActualHeight);

            FrameworkElement Styled(FrameworkElement element, string style)
            {
                element.Style = (Style)Application.Current.FindResource("Primer." + style);
                return element;
            }
            void GrowsWithContent(FrameworkElement element, int minimum)
            {
                Assert.Equal(minimum, element.MinHeight);
                Assert.True(double.IsNaN(element.Height));
                element.Measure(new Size(320, double.PositiveInfinity));
                Assert.True(element.DesiredSize.Height > minimum);
            }
            GrowsWithContent(Styled(new Border { Child = new Border { Height = 64 } }, "IdeBar"), 44);
            GrowsWithContent(Styled(new Border { Child = new Border { Height = 64 } }, "IdeSubBar"), 38);
            GrowsWithContent(Styled(new Button { Content = new Border { Height = 64 } }, "IdeToolButton"), 28);
            var search = (TextBox)Styled(new TextBox { Text = "first\nsecond\nthird" }, "IdeSearch");
            GrowsWithContent(search, 28);
            var boundary = Application.Current.FindResource("Primer.inputBorder");
            Assert.Same(boundary, search.BorderBrush);
            Assert.Same(boundary, ((Border)Styled(new Border(), "IdeChip")).BorderBrush);
            var pill = (RadioButton)Styled(new RadioButton(), "IdePill"); pill.IsChecked = true;
            Assert.Equal(28, pill.Height); Assert.Equal(FontWeights.SemiBold, pill.FontWeight);
            Assert.Same(Application.Current.FindResource("Primer.accent"), pill.BorderBrush);
            foreach (string style in new[] { "SelRow", "Tile" })
            {
                var selection = (ToggleButton)Styled(new RadioButton(), style);
                Assert.Same(boundary, selection.BorderBrush);
                selection.IsChecked = true;
                Assert.Same(Application.Current.FindResource("Primer.accent"), selection.BorderBrush);
            }
        });
    }

    [Fact]
    public void Status_counts_are_bound_to_completed_compile_and_current_verified_head()
    {
        wpf.Run(() =>
        {
            FakeProjectClient client = null!;
            var model = ProjectPageTestSupport.Model(configure: fixture => client = fixture);
            var window = new MainWindow(model, false, FeaturePageFixtures.Approvals(false));
            try
            {
                var audit = new FeaturePageFixtures.Audit();
                window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), audit, new FeaturePageFixtures.Environment());
                var compile = (TextBlock)window.FindName("CompileStatus");
                Assert.Null(model.Engineering.LastCompile);
                Assert.Equal(Visibility.Collapsed, compile.Visibility);
                model.Engineering.Compile.Execute(null); WpfContext.Drain();
                Assert.NotNull(model.Engineering.LastCompile);
                Assert.Equal(Visibility.Visible, compile.Visibility);
                Assert.Equal("✓ 0 ! 0", compile.Text);
                Assert.Equal(Loc.Current.T("Shell.CompileCounts", 0, 0), compile.Tag);
                Assert.Equal(1, window.CompileOkGlyph.PositionCount);
                client.CompileResult = new() { State = "Error", ErrorCount = 12, WarningCount = 3 };
                model.Engineering.Compile.Execute(null); WpfContext.Drain();
                Assert.Equal("✕ 12 ! 3", compile.Text);
                Assert.Equal(Loc.Current.T("Shell.CompileCounts", 12, 3), compile.Tag);
                Assert.Equal(0, window.CompileOkGlyph.PositionCount);
                Assert.Equal(1, window.CompileErrorGlyph.PositionCount);
                Assert.Equal(5, window.CompileWarningGlyph.PositionStart);
                window.Navigate("Calls");
                UnifiedDesktopTests.ClickControl(window.CompileStatusButton);
                Assert.Equal("Engineering", window.CurrentPage);
                Assert.Equal(ViewModels.ProjectDocument.Compile, model.Engineering.Document);

                var head = (TextBlock)window.FindName("AuditHead");
                Assert.Equal("Audit #1239", head.Text);
                Assert.Equal(Loc.Current.T("Shell.AuditHead", 1239), head.Tag);
                UnifiedDesktopTests.ClickControl(window.AuditHeadButton);
                Assert.Equal("Audit", window.CurrentPage);
                WpfContext.Complete(window.Features.VerifyAsync());
                Assert.Equal("Audit #1239 ✓", head.Text);
                Assert.Equal(Loc.Current.T("Shell.AuditHead", 1239) + " ✓", head.Tag);
                audit.Events = audit.Events.Append(audit.Events[0] with { Index = 1240 }).ToArray();
                window.Navigate("Calls");
                Assert.Equal("Audit #1240", head.Text);
                Assert.Equal(Loc.Current.T("Shell.AuditHead", 1240), head.Tag);
                audit.Broken = true;
                WpfContext.Complete(window.Features.VerifyAsync());
                Assert.Equal("Audit #1240", head.Text);
                Assert.Equal(Loc.Current.T("Shell.AuditHead", 1240), head.Tag);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Renders_in_chinese_as_well_as_english()
    {
        wpf.RunWithLanguage(AppLanguage.Chinese, () =>
        {
            using var rendered = Render();

            var labels = Descendants<TextBlock>(rendered.Host).Select(t => t.Text).ToList();
            Assert.Contains("项目", labels);
            Assert.Contains("未连接", labels);
        });
    }

    /// <summary>
    /// The language picker is a pair of RadioButtons bound two-way. Building the window must not
    /// push a value back into the catalogue - if it did, --lang zh would open the app in English.
    /// </summary>
    [Fact]
    public void Building_the_window_does_not_change_the_language()
    {
        wpf.RunWithLanguage(AppLanguage.Chinese, () =>
        {
            using var rendered = Render();

            Assert.Equal(AppLanguage.Chinese, Loc.Current.Language);
            Assert.True(Loc.Current.IsChinese);
        });
    }

    /// <summary>The theme actually painted: the header's ground is the palette's header brush.</summary>
    [Fact]
    public void The_header_is_painted_with_the_palette_brush()
    {
        wpf.Run(() =>
        {
            var previous = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Current.Theme = AppTheme.Light;
                using var rendered = Render();

                var expected = ((SolidColorBrush)Application.Current.FindResource("Primer.titlebar")).Color;
                var search = (Button)rendered.Window.FindName("CommandPaletteButton");
                var bounds = search.TransformToAncestor(rendered.Host).TransformBounds(new Rect(search.RenderSize));
                foreach (int x in new[] { (int)bounds.Left - 4, (int)bounds.Right + 4 })
                {
                    var actual = PixelAt(rendered.Bitmap, x, 6);
                    Assert.Equal(255, actual.A);
                    Assert.Equal(expected, actual);
                }
            }
            finally
            {
                ThemeManager.Current.Theme = previous;
            }
        });
    }

    /// <summary>
    /// Each picker is a RadioButton group, and the whole point of using RadioButtons rather than
    /// ToggleButtons is that exactly one is always chosen. A group that renders with none checked
    /// means its binding did not resolve.
    /// </summary>
    [Theory]
    [InlineData("Pages", 8)]
    [InlineData("SyncDirection", 2)]
    public void Every_picker_group_has_one_and_only_one_choice(string group, int expectedSegments)
    {
        wpf.Run(() =>
        {
            using var rendered = Render(connected: group == "SyncDirection");
            if (group == "SyncDirection")
            {
                rendered.Window.Navigate("VersionControl");
                Layout(rendered.Host);
            }
            var segments = Group(rendered.Host, group);

            Assert.Equal(expectedSegments, segments.Count);
            Assert.Single(segments, r => r.IsChecked == true);
        });
    }

    [Fact]
    public void Shell_navigation_swaps_operations_blocks_and_workspace_views()
    {
        wpf.Run(() =>
        {
            using var rendered = Render(connected: true);
            var model = (ViewModels.MainViewModel)rendered.Host.DataContext;

            Assert.True(model.IsBlocksTab);

            rendered.Window.Navigate("VersionControl");
            Layout(rendered.Host);

            Assert.False(model.IsBlocksTab);

            // The version-control view brings its own workspace picker; the blocks view has none.
            Assert.Contains(Descendants<ComboBox>(rendered.Host), IsShown);
        });
    }

    /// <summary>
    /// With no session there is nothing to list, so both panes must show their explanation
    /// rather than an empty grid with no hint of what is missing.
    /// </summary>
    [Fact]
    public void Shows_its_empty_states_before_anything_is_connected()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            using var rendered = Render();
            var model = (ViewModels.MainViewModel)rendered.Host.DataContext;

            Assert.False(model.Engineering.HasDevices);
            Assert.False(model.Engineering.HasBlocks);

            var visible = Descendants<TextBlock>(rendered.Host)
                .Where(IsShown)
                .Select(t => t.Text)
                .ToList();

            Assert.Contains(Loc.Current["Project.Disconnected"], visible);
            Assert.Contains(Loc.Current["Shell.Engineering"], visible);

            // And the grids behind them are hidden rather than merely empty.
            Assert.DoesNotContain(Descendants<DataGrid>(rendered.Host), IsShown);
        });
    }

    /// <summary>
    /// The badge must not invent a version before a session has bound one, and the mode label
    /// must stay empty until one of the two session switches is actually on.
    /// </summary>
    [Fact]
    public void Reports_no_openness_version_and_no_mode_until_a_session_exists()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            using var rendered = Render();
            var model = (ViewModels.MainViewModel)rendered.Host.DataContext;

            Assert.Equal(Loc.Current["Badge.NoVersion"], model.Session.OpennessBadge);
            Assert.Equal(string.Empty, model.Session.ModeLabel);

            model.Session.UseMock = true;
            Assert.Equal(Loc.Current["Status.MockMode"], model.Session.ModeLabel);
        });
    }

    /// <summary>
    /// The three views are one choice held in one field. Bound as three independent bools they
    /// could all be false at once, and the pane would show nothing.
    /// </summary>
    [Fact]
    public void Exactly_one_view_is_selected_at_a_time()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();
            var model = (ViewModels.MainViewModel)rendered.Host.DataContext;

            Assert.True(model.IsBlocksTab);
            Assert.False(model.IsVcTab);
            Assert.False(model.IsLogTab);

            model.IsLogTab = true;
            Layout(rendered.Host);
            Assert.True(model.IsLogTab);
            Assert.False(model.IsBlocksTab);
            Assert.False(model.IsVcTab);

            model.IsVcTab = true;
            Layout(rendered.Host);
            Assert.True(model.IsVcTab);
            Assert.False(model.IsLogTab);
            Assert.False(model.IsBlocksTab);
        });
    }

    /// <summary>
    /// Every action uses shared button chrome, while navigation uses the underline template.
    /// If a style ever loses its template, the button falls
    /// back to the stock WPF chrome and quietly stops matching the rest of the window, so the
    /// check is that each one really is using the theme's template.
    /// </summary>
    [Fact]
    public void Every_button_uses_the_themed_template_and_has_a_size()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();
            var themed = (ControlTemplate)Application.Current.FindResource("Primer.ButtonTemplate");

            var buttons = Descendants<ButtonBase>(rendered.Host)
                .Where(IsShown)
                .Where(b => b is Button or RadioButton)
                // The language switch is text-only and has a template of its own.
                .Where(b => (b as RadioButton)?.GroupName is not ("Pages" or "ProjectPages"))
                .ToList();

            Assert.True(buttons.Count >= 12, "only found " + buttons.Count + " buttons");

            foreach (var button in buttons)
            {
                Assert.Same(themed, button.Template);
                Assert.True(button.ActualHeight >= 16, button.Content + " has no height");
            }
        });
    }
}
