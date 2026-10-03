using System;
using System.Collections.Generic;
using System.Linq;
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
    private const int Width = 1400;
    private const int Height = 880;

    private sealed class Rendered : IDisposable
    {
        public required MainWindow Window { get; init; }
        public required Border Host { get; init; }
        public required RenderTargetBitmap Bitmap { get; init; }

        public void Dispose() => Window.Close();
    }

    private static Rendered Render()
    {
        var window = new MainWindow();

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
    public void Renders_in_chinese_as_well_as_english()
    {
        wpf.RunWithLanguage(AppLanguage.Chinese, () =>
        {
            using var rendered = Render();

            var labels = Descendants<TextBlock>(rendered.Host).Select(t => t.Text).ToList();
            Assert.Contains("设备", labels);
            Assert.Contains("尚未打开项目", labels);
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

                var expected = ((SolidColorBrush)Application.Current.FindResource("Ui.HeaderBackground")).Color;
                var actual = PixelAt(rendered.Bitmap, Width / 2, 6);

                Assert.Equal(expected.R, actual.R);
                Assert.Equal(expected.G, actual.G);
                Assert.Equal(expected.B, actual.B);
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
    [InlineData("View", 3)]
    [InlineData("Appearance", 3)]
    [InlineData("Language", 2)]
    public void Every_picker_group_has_one_and_only_one_choice(string group, int expectedSegments)
    {
        wpf.Run(() =>
        {
            using var rendered = Render();
            var segments = Group(rendered.Host, group);

            Assert.Equal(expectedSegments, segments.Count);
            Assert.Single(segments, r => r.IsChecked == true);
        });
    }

    [Fact]
    public void The_view_switch_starts_on_blocks_and_swaps_the_visible_card()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();
            var model = (ViewModels.MainViewModel)rendered.Host.DataContext;

            Assert.True(model.IsBlocksTab);

            model.IsVcTab = true;
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

            Assert.False(model.HasDevices);
            Assert.False(model.HasBlocks);

            var visible = Descendants<TextBlock>(rendered.Host)
                .Where(IsShown)
                .Select(t => t.Text)
                .ToList();

            Assert.Contains(Loc.Current["Blocks.Empty.Title"], visible);
            Assert.Contains(Loc.Current["Sidebar.Empty"], visible);

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

            Assert.Equal(Loc.Current["Badge.NoVersion"], model.OpennessBadge);
            Assert.Equal(string.Empty, model.ModeLabel);

            model.UseMock = true;
            Assert.Equal(Loc.Current["Status.MockMode"], model.ModeLabel);
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
    /// Every button is drawn by one shared template. If a style ever loses it, the button falls
    /// back to the stock WPF chrome and quietly stops matching the rest of the window, so the
    /// check is that each one really is using the theme's template.
    /// </summary>
    [Fact]
    public void Every_button_uses_the_themed_template_and_has_a_size()
    {
        wpf.Run(() =>
        {
            using var rendered = Render();
            var themed = (ControlTemplate)Application.Current.FindResource("Ui.ButtonTemplate");

            var buttons = Descendants<ButtonBase>(rendered.Host)
                .Where(IsShown)
                .Where(b => b is Button or RadioButton)
                // The language switch is text-only and has a template of its own.
                .Where(b => (b as RadioButton)?.GroupName != "Language")
                .ToList();

            Assert.True(buttons.Count >= 12, "only found " + buttons.Count + " buttons");

            foreach (var button in buttons)
            {
                Assert.Same(themed, button.Template);
                Assert.True(button.ActualHeight >= 22, button.Content + " has no height");
            }
        });
    }
}
