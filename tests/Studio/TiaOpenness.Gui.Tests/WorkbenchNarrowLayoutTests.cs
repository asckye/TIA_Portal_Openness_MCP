using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// CI runners open the 1200 px window at 1044 px. Below Ant's xl breakpoint every page stacks its context column
/// under the content, page-head actions wrap below the title when they do not fit beside it, and from the 1024x640
/// minimum to 1920x1080 no title, label, field or button is clipped, truncated or covered.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class WorkbenchNarrowLayoutTests(WpfContext wpf)
{
    private static readonly string[] Pages = ["Mcp", "Engineering", "VersionControl", "Calls", "Audit", "Environment", "Log"];
    // The screenshot set the maintainer reviews: MCP & clients, Project, Environment and AI calls.
    private static readonly string[] CapturedPages = ["Mcp", "Engineering", "Environment", "Calls"];

    [Theory]
    [InlineData(1024, 640)]
    [InlineData(1044, 780)]
    [InlineData(1200, 780)]
    [InlineData(1366, 860)]
    [InlineData(1920, 1080)]
    public void Pages_stack_the_context_column_below_1200_px_and_clip_nothing(int width, int height)
    {
        string? render = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
        string? output = string.IsNullOrWhiteSpace(render) ? null : Path.Combine(Path.GetFullPath(render), "narrow");
        if (output != null) Directory.CreateDirectory(output);
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            wpf.RunWithLanguage(language, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = theme;
                var window = new MainWindow(ProjectPageTestSupport.Model(results: true, release: "21"), false,
                    FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
                try
                {
                    window.ConfigureFeaturePages(new FeaturePageFixtures.Journal(), new FeaturePageFixtures.Audit(), new FeaturePageFixtures.Environment(),
                        new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                    window.ShowConfiguration(false);
                    var host = WorkbenchRenderTests.CreateHost(window, width, height);
                    Assert.True(string.IsNullOrWhiteSpace(trace.Text), "first layout: " + trace.Text);
                    string suffix = $"{width}x{height}-{(language == AppLanguage.Chinese ? "zh" : "en")}-{theme.ToString().ToLowerInvariant()}";
                    foreach (string page in Pages)
                    {
                        window.Navigate(page);
                        Settle(host, width, height);
                        string context = page + " " + suffix;
                        var root = PageRoot(window, page);
                        AssertContextColumn(root, host, width, context);
                        AssertHeadsKeepTheirTitle(root, host, context);
                        AssertNothingClipped(root, host, context);
                        if (page == "Mcp") AssertConfigurationCards(window, height, context);
                        string errors = Unexpected(trace.Text, page, height);
                        Assert.True(string.IsNullOrWhiteSpace(errors), context + ": " + errors);
                        if (output != null && CapturedPages.Contains(page))
                            WorkbenchRenderTests.SaveRender(host, Path.Combine(output, page.ToLowerInvariant() + "-" + suffix + ".png"), width, height);
                    }
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
                Assert.True(string.IsNullOrWhiteSpace(Unexpected(trace.Text, "Log", height)), trace.Text);
            });
    }

    // Known before this test (P8-80b): opening the Log page in a 640 px window drops one list row before it is
    // prepared, and its theme bindings report a missing ItemsControl; the earlier deferred layout does the same.
    // Only those two lines are tolerated, only there; every other binding error fails.
    private static string Unexpected(string trace, string page, int height)
    {
        var lines = trace.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(Environment.NewLine, lines.Where(line => !(page == "Log" && height < 700
            && line.Contains("AncestorType='System.Windows.Controls.ItemsControl'", StringComparison.Ordinal)
            && line.Contains("target element is 'ListBoxItem'", StringComparison.Ordinal)
            && (line.Contains("Path=HorizontalContentAlignment;", StringComparison.Ordinal) || line.Contains("Path=VerticalContentAlignment;", StringComparison.Ordinal)))));
    }

    private static void Settle(FrameworkElement host, int width, int height)
    {
        WpfContext.Drain();
        host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
        WpfContext.Drain(); host.UpdateLayout();
    }

    private static FrameworkElement PageRoot(MainWindow window, string page) => page switch
    {
        "Mcp" => window.Configuration!,
        "Engineering" => (FrameworkElement)window.FindName("EngineeringPage"),
        "VersionControl" => (FrameworkElement)window.FindName("VersionControlContent"),
        "Log" => (FrameworkElement)window.FindName("LogContent"),
        "Calls" => (FrameworkElement)window.FindName("CallsContent"),
        "Audit" => (FrameworkElement)window.FindName("AuditContent"),
        _ => (FrameworkElement)window.FindName("EnvironmentContent"),
    };

    // Below the xl breakpoint the context column sits under the content and the content takes the full width.
    private static void AssertContextColumn(FrameworkElement root, FrameworkElement host, int width, string context)
    {
        var grids = new[] { root }.Concat(Descendants<FrameworkElement>(root)).OfType<Grid>()
            .Where(grid => ResponsiveLayout.GetEnabled(grid) && Shown(grid, host)).ToList();
        if (grids.Count == 0) return;
        var grid = grids[0];
        bool narrow = width < 1200;
        Assert.True(ResponsiveLayout.IsStacked(grid) == narrow, $"{context}: the context column is {(narrow ? "not stacked" : "stacked")} at {grid.ActualWidth} px");
        var children = (ResponsiveLayout.IsStacked(grid)
            ? ((StackPanel)grid.Children.OfType<ScrollViewer>().Single().Content).Children.Cast<UIElement>()
            : grid.Children.Cast<UIElement>()).OfType<FrameworkElement>().Where(child => child is not ToggleButton && Shown(child, host) && child.ActualWidth > 0).ToList();
        var side = children.Where(child => Grid.GetColumn(child) == 2).Select(child => Bounds(child, host)).ToList();
        var main = children.Where(child => Grid.GetColumn(child) == 0 && Grid.GetColumnSpan(child) == 1).Select(child => Bounds(child, host)).ToList();
        foreach (var aside in side)
        foreach (var content in main)
        {
            if (narrow)
            {
                Assert.True(aside.Top >= content.Bottom - 1, $"{context}: the context column {aside} is not below the content {content}");
                Assert.True(Math.Abs(aside.Left - content.Left) < 1, $"{context}: the stacked columns {aside} and {content} do not align");
            }
            else Assert.True(aside.Left >= content.Right, $"{context}: the context column {aside} overlaps the content {content}");
        }
    }

    // A page head keeps the whole title visible; its actions sit beside it or wrap below it, never over it.
    private static void AssertHeadsKeepTheirTitle(FrameworkElement root, FrameworkElement host, string context)
    {
        foreach (var head in Descendants<PageHeadPanel>(root).Where(head => Shown(head, host) && head.ActualWidth > 0))
        {
            var parts = head.Children.OfType<FrameworkElement>().Where(part => part.Visibility == Visibility.Visible && part.ActualWidth > 0).ToList();
            if (parts.Count < 2) continue;
            var heading = Bounds(parts[0], host);
            var actions = Bounds(parts[1], host);
            // The title's own text (secondary lines with a tooltip may trim) ends before the actions begin.
            IEnumerable<TextBlock> texts = head.Title is TextBlock title ? [title]
                : Descendants<TextBlock>(parts[0]).Where(text => Shown(text, host) && text.ActualWidth > 0 && text.ToolTip == null).ToList();
            foreach (var text in texts)
            {
                double right = Bounds(text, host).Left + NaturalWidth(text);
                Assert.True(head.IsWrapped || right <= actions.Left + 0.5, $"{context}: the head text '{text.Text}' ends at {right}, under its actions {actions}");
            }
            Assert.True(head.IsWrapped ? actions.Top >= heading.Bottom : actions.Left >= heading.Right,
                $"{context}: the head actions {actions} overlap the heading {heading}");
        }
    }

    private static void AssertNothingClipped(FrameworkElement root, FrameworkElement host, string context)
    {
        foreach (var element in Descendants<FrameworkElement>(root))
        {
            if (!IsPrimary(element) || element.ActualWidth <= 0 || !Shown(element, host) || Inside<ScrollBar>(element, root)) continue;
            string name = Describe(element);
            var bounds = Bounds(element, host);
            Assert.True(bounds.Left >= -0.5 && bounds.Right <= host.ActualWidth + 0.5, $"{context}: {name} {bounds} leaves the window");
            for (DependencyObject? node = element; node != null && node != host; node = VisualTreeHelper.GetParent(node))
            {
                if (node is not FrameworkElement ancestor) continue;
                var clip = LayoutInformation.GetLayoutClip(ancestor)?.Bounds ?? (ancestor.ClipToBounds ? new Rect(ancestor.RenderSize) : Rect.Empty);
                if (clip.IsEmpty) continue;
                // Horizontally scrolling content is reachable by scrolling; the page and card scrollers are vertical only.
                if (ancestor is ScrollContentPresenter { ScrollOwner: { } owner } && owner.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                    && owner.ExtentWidth > owner.ViewportWidth + 0.5) continue;
                var visible = ancestor.TransformToAncestor(host).TransformBounds(clip);
                Assert.True(bounds.Left >= visible.Left - 1 && bounds.Right <= visible.Right + 1,
                    $"{context}: {name} {bounds} is cut by {ancestor.GetType().Name} {ancestor.Name} {visible}");
            }
            if (element is TextBlock text && text.TextWrapping == TextWrapping.NoWrap && !string.IsNullOrEmpty(text.Text))
                Assert.True(NaturalWidth(text) <= text.ActualWidth + 1, $"{context}: {name} is truncated to {text.ActualWidth} of {NaturalWidth(text)} px");
        }
    }

    // The handoff heights of the MCP page hold at every width: one-line labels keep the service halves at 162 px.
    private static void AssertConfigurationCards(MainWindow window, int height, string context)
    {
        var form = window.Configuration!;
        foreach (string name in new[] { "InstallationCard", "ServerPane" })
        {
            double actual = ((FrameworkElement)form.FindName(name)).ActualHeight;
            Assert.True(Math.Abs(actual - 162) < 1, $"{context}: {name} is {actual} px high, not 162");
        }
        double client = ((FrameworkElement)form.FindName("ClientPane")).ActualHeight;
        if (height >= 780) Assert.True(client > 300, $"{context}: the client card is {client} px high");
    }

    // Titles, form labels, fields and command buttons. List rows are data with tooltips and may trim.
    private static bool IsPrimary(FrameworkElement element)
    {
        if (Inside<ListBoxItem>(element, null) || Inside<ComboBox>(element, null) && element is not ComboBox) return false;
        if (element is TextBlock text)
            return AutomationProperties.GetHeadingLevel(text) != AutomationHeadingLevel.None || text.Name == "PageTitle"
                || Inside<ButtonBase>(text, null) || Inside<Label>(text, null);
        return element is ButtonBase or TextBox or PasswordBox or ComboBox or Label;
    }

    private static double NaturalWidth(FrameworkElement element)
    {
        if (element is not TextBlock text) return element.ActualWidth;
        var probe = new TextBlock
        {
            Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
            FontStyle = text.FontStyle, FontStretch = text.FontStretch, Padding = text.Padding,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(text));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static string Describe(FrameworkElement element)
    {
        string label = element switch
        {
            TextBlock text => text.Text,
            ContentControl { Content: string content } => content,
            _ => AutomationProperties.GetName(element),
        };
        return $"{element.GetType().Name} {element.Name} '{label}'";
    }

    private static Rect Bounds(FrameworkElement element, Visual host) => element.TransformToAncestor(host).TransformBounds(new Rect(element.RenderSize));

    // The host is detached from a window, so IsVisible stays false: walk the Visibility chain instead.
    private static bool Shown(DependencyObject element, DependencyObject host)
    {
        for (var node = element; node != null && node != host; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static bool Inside<T>(DependencyObject element, DependencyObject? root) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(element); node != null && node != root; node = VisualTreeHelper.GetParent(node))
            if (node is T) return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
