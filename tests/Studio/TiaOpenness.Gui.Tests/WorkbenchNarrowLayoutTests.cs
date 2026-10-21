using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TiaMcpConfigurator;
using TiaOpenness.Contracts.Models;
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

    [Fact]
    public void Sider_selects_only_the_current_page_after_leaving_version_control()
    {
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            wpf.RunWithLanguage(language, () =>
            {
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = theme;
                var window = new MainWindow(ProjectPageTestSupport.Model(release: "21"), false,
                    FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
                try
                {
                    var host = WorkbenchRenderTests.CreateHost(window);
                    var sider = (FrameworkElement)window.FindName("SiderMenu");
                    void Selected(string page)
                    {
                        Settle(host, 1200, 780);
                        var selected = Assert.Single(Descendants<RadioButton>(sider),
                            item => Shown(item, sider) && item.IsChecked == true);
                        Assert.Equal("Rail" + (page == "Blocks" ? "Engineering" : page), selected.Name);
                    }
                    foreach (string page in new[] { "Mcp", "Engineering", "Blocks", "Calls", "Audit", "Environment", "Log" })
                    {
                        window.Navigate("VersionControl"); Selected("VersionControl");
                        window.Navigate(page); Selected(page);

                        UnifiedDesktopTests.ClickControl((RadioButton)window.FindName("RailVersionControl"));
                        Selected("VersionControl");
                        UnifiedDesktopTests.ClickControl((RadioButton)window.FindName("Rail" + page));
                        Selected(page);

                        var shortcut = window.InputBindings.OfType<KeyBinding>().Single(binding => Equals(binding.CommandParameter, page));
                        WorkbenchCommands.Navigate.Execute("VersionControl", host); Selected("VersionControl");
                        Assert.Equal(ModifierKeys.Control, shortcut.Modifiers);
                        Assert.Same(WorkbenchCommands.Navigate, shortcut.Command);
                        WorkbenchCommands.Navigate.Execute(shortcut.CommandParameter, host); Selected(page);

                        window.Navigate("VersionControl");
                        window.OpenSettings();
                        var drawer = (FrameworkElement)window.FindName("DrawerPanel");
                        UnifiedDesktopTests.ClickControl(Descendants<Button>(drawer)
                            .Single(button => AutomationProperties.GetName(button) == Loc.Current["Access.CloseDrawer"]));
                        Selected("VersionControl");
                        window.Navigate(page); Selected(page);
                    }
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
            });
    }

    [Theory]
    [InlineData(640, 640)]
    [InlineData(1024, 640)]
    [InlineData(1200, 640)]
    [InlineData(1200, 780)]
    public void Environment_calls_and_version_control_have_one_vertical_scrollbar(int width, int height)
    {
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            wpf.RunWithLanguage(language, () =>
            {
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = theme;
                var model = ProjectPageTestSupport.Model(release: "21");
                var journal = new FeaturePageFixtures.Journal();
                var call = journal.Calls[0];
                journal.Replace(Enumerable.Range(0, 80).Select(i => call with
                    { RequestId = "layout-call-" + i, JournalKey = "layout-call-" + i }).ToArray());
                var environment = new FeaturePageFixtures.Environment();
                environment.Replace(Enumerable.Range(0, 8).SelectMany(_ => environment.Groups).ToArray());
                for (int i = 0; i < 80; i++)
                {
                    model.VersionControl.VcStatusItems.Add(new MappedObjectInfo
                        { Name = "Layout_" + i, FilePath = "Layout_" + i + ".xml", CompareState = VcCompareState.Unequal });
                    model.VersionControl.VcDiffLines.Add(new DiffLine { Text = "+ layout row " + i, Kind = DiffLineKind.Added });
                }
                var window = new MainWindow(model, false, FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
                try
                {
                    window.ConfigureFeaturePages(journal, new FeaturePageFixtures.Audit(), environment,
                        new FeaturePageFixtures.Notification(), () => FeaturePageFixtures.Now);
                    var host = WorkbenchRenderTests.CreateHost(window, width, height);
                    foreach (string page in new[] { "Environment", "Calls", "VersionControl" })
                    {
                        window.Navigate(page);
                        // Exercise the initial layout and both directions across the stacking breakpoint.
                        foreach (int layoutWidth in new[] { width, width == 1200 ? 640 : 1200, width })
                        {
                            host.Width = layoutWidth;
                            Settle(host, layoutWidth, height);
                            var root = PageRoot(window, page);
                            string context = $"{page} {layoutWidth}x{height} {language} {theme}";
                            var bar = Assert.Single(Descendants<ScrollBar>(root), scrollbar =>
                                scrollbar.Orientation == Orientation.Vertical && Shown(scrollbar, host)
                                && scrollbar.ActualWidth > 0 && scrollbar.ActualHeight > 0);
                            var scroll = Descendants<ScrollViewer>(root).Single(viewer =>
                                viewer.ComputedVerticalScrollBarVisibility == Visibility.Visible && Shown(viewer, host));
                            Assert.True(scroll.ScrollableHeight > 0, context);
                            Assert.True(bar.ActualWidth > 0, context);
                            Assert.NotEqual(KeyboardNavigationMode.None, KeyboardNavigation.GetTabNavigation(scroll));
                            foreach (var inner in Descendants<ScrollViewer>(scroll).Where(viewer => Shown(viewer, host)))
                                Assert.True(inner.ScrollableHeight < 0.5, context + ": a list still has its own vertical viewport");
                            ScrollBar.ScrollToTopCommand.Execute(null, scroll);
                            Settle(host, layoutWidth, height);
                            ScrollBar.PageDownCommand.Execute(null, scroll);
                            Settle(host, layoutWidth, height);
                            Assert.True(scroll.VerticalOffset > 0, context + ": keyboard PageDown did not scroll");
                            ScrollBar.ScrollToBottomCommand.Execute(null, scroll);
                            Settle(host, layoutWidth, height);
                            Assert.InRange(scroll.ScrollableHeight - scroll.VerticalOffset, 0, 1);
                        }
                    }
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
            });
    }

    [Theory]
    [InlineData(640)]
    [InlineData(1024)]
    [InlineData(1200)]
    public void Log_page_at_640_px_height_has_no_binding_errors(int width)
    {
        foreach (var language in new[] { AppLanguage.Chinese, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            wpf.RunWithLanguage(language, () =>
            {
                using var trace = new BindingPathTests.BindingTrace();
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = theme;
                var model = ProjectPageTestSupport.Model(results: true, release: "21");
                var window = new MainWindow(model, false, FeaturePageFixtures.Approvals(), new FeaturePageFixtures.Diagnostics());
                try
                {
                    var host = WorkbenchRenderTests.CreateHost(window, width, 640);
                    window.Navigate("VersionControl"); Settle(host, width, 640);
                    window.Navigate("Log"); Settle(host, width, 640);
                    var list = (ListBox)ProjectPageTestSupport.Find(window, "LogList");
                    Assert.NotEmpty(Descendants<ListBoxItem>(list));
                    foreach (int count in new[] { 80, 1, 80 })
                    {
                        for (int i = 0; i < count; i++) model.Activity.Append("Layout log row " + i);
                        Settle(host, width, 640);
                        var scroll = Descendants<ScrollViewer>(list).Single();
                        scroll.ScrollToHome(); Settle(host, width, 640);
                        scroll.ScrollToEnd(); Settle(host, width, 640);
                        model.Activity.ClearLog(); Settle(host, width, 640);
                    }
                    window.Navigate("Engineering"); Settle(host, width, 640);
                    model.Activity.Append("Log reopened");
                    window.Navigate("Log"); Settle(host, width, 640);
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; WpfContext.Drain(); }
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            });
    }

    [Fact]
    public void English_client_names_use_two_lines_at_1200_px_and_keep_equal_card_heights()
    {
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            wpf.RunWithLanguage(AppLanguage.English, () =>
            {
                var previous = ThemeManager.Current.Theme;
                ThemeManager.Current.Theme = theme;
                string[] names = ["Claude Code", "Codex", "Gemini CLI", "Qwen", "Kimi", "Yuanbao", "DeepSeek", "GLM", "Grok",
                    "Qwen Agent", "Cursor", "VS Code · Copilot", "A deliberately long client name that needs more than two lines"];
                var clients = names.Select((name, i) => new ClientProfile("layout-" + i, name,
                    @"C:\fixture\" + i + ".json", "layout fixture") { Detected = true }).ToList();
                var preview = new ConfigurationPreview("21", @"C:\fixture\Portal V21", "127.0.0.1", false,
                    false, true, "", "", clients);
                var window = new MainWindow(ProjectPageTestSupport.Model(), false, FeaturePageFixtures.Approvals(),
                    new FeaturePageFixtures.Diagnostics(), preview);
                try
                {
                    window.ShowConfiguration(false);
                    var host = WorkbenchRenderTests.CreateHost(window);
                    var list = (ListBox)window.Configuration!.FindName("ClientChoices");
                    Settle(host, 1200, 780);
                    var rows = clients.Select((client, i) =>
                        Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(i))).ToArray();
                    int wrapped = 0;
                    foreach (var row in rows)
                    {
                        var name = Descendants<TextBlock>(row).Single(text => text.Name == "ClientName");
                        Assert.Equal(((ClientProfile)row.DataContext).DisplayName, name.Text);
                        Assert.Equal(TextWrapping.Wrap, name.TextWrapping);
                        Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
                        Assert.Equal(36, name.MaxHeight);
                        // TextPointer also visits formatted lines hidden by trimming: measure the visible line boxes.
                        Assert.InRange(name.ActualHeight, name.LineHeight, name.MaxHeight);
                        int lines = (int)Math.Round(name.ActualHeight / name.LineHeight);
                        Assert.InRange(lines, 1, 2);
                        if (name.Text == "VS Code · Copilot") Assert.Equal(2, lines);
                        if (lines == 2) wrapped++;
                        var content = Assert.IsType<Grid>(VisualTreeHelper.GetParent(name));
                        // Three em leaves a usable word-sized name column beside the English detection badge.
                        Assert.InRange(name.ActualWidth, name.FontSize * 3, row.ActualWidth);
                        Assert.InRange(content.ColumnDefinitions[0].ActualWidth,
                            name.FontSize * 3 + name.Margin.Left + name.Margin.Right, content.ActualWidth);
                        Assert.Equal(((ClientProfile)row.DataContext).Tooltip, content.ToolTip);
                        var tile = Assert.IsType<Border>(row.Template.FindName("Tile", row));
                        Assert.True(tile.ActualHeight >= name.ActualHeight + tile.BorderThickness.Top + tile.BorderThickness.Bottom);
                    }
                    Assert.True(wrapped > 0, "The 1200 px fixture must actually render a two-line name.");
                    foreach (var group in rows.GroupBy(row => Math.Round(row.TranslatePoint(new Point(), list).Y)))
                        Assert.InRange(group.Max(row => row.ActualHeight) - group.Min(row => row.ActualHeight), 0, 0.5);
                }
                finally { window.Close(); ThemeManager.Current.Theme = previous; }
            });
    }

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
