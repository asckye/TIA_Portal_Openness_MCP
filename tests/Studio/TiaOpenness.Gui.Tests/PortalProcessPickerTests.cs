using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using Xunit;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed class PortalProcessPickerTests(WpfContext wpf)
{
    private static PortalProcessInfo[] Processes() =>
    [
        new() { ProcessId = 123, ProjectPath = @"D:\项目\很长的生产线项目路径\Line.ap21", Release = "21.0", UiMode = "WithUserInterface" },
        new() { ProcessId = 456, Release = "21.0", UiMode = "WithoutUserInterface" },
    ];

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    public void Model_lists_every_field_starts_unselected_and_Cancel_returns_no_PID(AppLanguage language)
    {
        wpf.RunWithLanguage(language, () =>
        {
            using var model = new PortalProcessPickerViewModel(Processes());
            Assert.Equal(new[] { 123, 456 }, model.Processes.Select(p => p.ProcessId));
            Assert.Equal(Processes()[0].ProjectPath, model.Processes[0].ProjectPath);
            Assert.Equal("V21", model.Processes[0].Release);
            Assert.Equal(Loc.Current["ProcessPicker.NoProject"], model.Processes[1].ProjectPath);
            Assert.Equal(Loc.Current["ProcessPicker.WithUi"], model.Processes[0].UiMode);
            Assert.Equal(Loc.Current["ProcessPicker.WithoutUi"], model.Processes[1].UiMode);
            Assert.Null(model.SelectedProcess);
            Assert.False(model.CanConnect);
            Assert.False(model.Connect.CanExecute(null));
            model.Connect.Execute(null);
            Assert.Null(model.ResultProcessId);
            model.SelectedProcess = model.Processes[1];
            Assert.True(model.Connect.CanExecute(null));
            Assert.Null(model.ResultProcessId);
            int closes = 0;
            model.CloseRequested += (_, _) => closes++;
            model.Cancel.Execute(null);
            Assert.Null(model.ResultProcessId);
            Assert.Equal(1, closes);
        });
    }

    [Fact]
    public void Model_returns_only_the_confirmed_PID_and_refreshes_row_names_when_language_changes()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            using var model = new PortalProcessPickerViewModel(Processes());
            model.SelectedProcess = model.Processes[1];
            int changes = 0;
            model.Processes[1].PropertyChanged += (_, args) => { if (args.PropertyName == nameof(PortalProcessRow.AccessibleName)) changes++; };
            Loc.Current.Language = AppLanguage.Chinese;
            Assert.Equal(1, changes);
            Assert.Contains("无项目", model.Processes[1].AccessibleName);
            Assert.Equal("无用户界面", model.Processes[1].UiMode);
            model.Connect.Execute(null);
            Assert.Equal(456, model.ResultProcessId);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancelling_connect_or_implicit_connect_does_not_open_a_project_or_retry(bool openProject)
    {
        wpf.Run(() =>
        {
            var processes = Processes();
            var client = new FakeProjectClient { ConnectHandler = _ => Task.FromException<SessionState>(new PortalProcessSelectionRequiredException(processes)) };
            var dialogs = new FakeDialogService();
            using var model = new MainViewModel(client, dialogs);
            model.Session.ProjectPath = openProject ? @"D:\Other.ap21" : "";
            (openProject ? model.Session.OpenProject : model.Session.Connect).Execute(null);
            Assert.False(model.Session.Connect.IsRunning);
            Assert.False(model.Activity.Busy);
            Assert.False(model.Session.IsConnected);
            Assert.Equal(new[] { "Start", "Connect" }, client.Calls.Select(c => c.Name));
            Assert.Same(processes, Assert.Single(dialogs.ProcessRequests));
            Assert.Empty(model.Session.ProjectName);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selection_retries_once_with_the_exact_PID_and_a_refusal_stays_a_refusal(bool denied)
    {
        wpf.Run(() =>
        {
            var client = new FakeProjectClient
            {
                ConnectHandler = pid => pid == null
                    ? Task.FromException<SessionState>(new PortalProcessSelectionRequiredException(Processes()))
                    : denied ? Task.FromException<SessionState>(new InvalidOperationException("Openness firewall denied access"))
                    : Task.FromResult(new SessionState { Connected = true, OpennessVersion = "21.0" }),
            };
            var dialogs = new FakeDialogService { SelectedProcessId = 456 };
            using var model = new MainViewModel(client, dialogs);
            model.Session.Connect.Execute(null);
            Assert.Equal(!denied, model.Session.IsConnected);
            Assert.Single(dialogs.ProcessRequests);
            Assert.Equal(new int?[] { null, 456 }, client.Calls.Where(c => c.Name == "Connect").Select(c => (int?)c.Arguments[1]));
            Assert.DoesNotContain(client.Calls, c => c.Name is "OpenProject" or "Disconnect");
            if (denied) Assert.Contains("Openness firewall denied access", model.Activity.Status);
        });
    }

    public static IEnumerable<object[]> Layouts()
    {
        foreach (var language in new[] { AppLanguage.English, AppLanguage.Chinese })
        foreach (var theme in new[] { "Light", "Dark", "HighContrast" })
        foreach (var size in new[] { new Size(1024, 640), new Size(1366, 860) })
            yield return [language, theme, size.Width, size.Height];
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Sheet_layout_names_focus_styles_and_scrolling_fit_the_workbench(AppLanguage language, string theme, double width, double height)
    {
        wpf.RunWithLanguage(language, () =>
        {
            var manager = ThemeManager.Current;
            var oldTheme = manager.Theme;
            var oldContrast = manager.HighContrastOverride;
            manager.HighContrastOverride = theme == "HighContrast";
            manager.Theme = theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
            using var trace = new BindingPathTests.BindingTrace();
            var rows = Enumerable.Range(0, 40).Select(i => new PortalProcessInfo { ProcessId = 123 + i,
                ProjectPath = Processes()[0].ProjectPath + new string('x', 120), Release = "21.0", UiMode = i % 2 == 0 ? "WithUserInterface" : "WithoutUserInterface" }).ToArray();
            using var model = new PortalProcessPickerViewModel(rows);
            var window = new PortalProcessPicker(model);
            try
            {
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                var sheet = new Border { Child = content, Width = window.Width, Height = window.Height, DataContext = model };
                var host = new Grid { Width = width, Height = height };
                host.Children.Add(sheet);
                host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout(); WpfContext.Drain();
                var list = (ListBox)window.FindName("ProcessList");
                var cancel = (Button)window.FindName("CancelButton");
                var connect = (Button)window.FindName("ConnectButton");
                Assert.Equal(40, list.Items.Count);
                Assert.Null(list.SelectedItem);
                Assert.True(cancel.IsDefault); Assert.True(cancel.IsCancel);
                Assert.False(connect.IsDefault); Assert.False(connect.IsEnabled);
                Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(window));
                Assert.True(AutomationProperties.GetIsDialog(window));
                Assert.False(string.IsNullOrWhiteSpace(new WindowAutomationPeer(window).GetName()));
                Assert.False(string.IsNullOrWhiteSpace(new ListBoxAutomationPeer(list).GetName()));
                Assert.DoesNotContain("_", new ButtonAutomationPeer(cancel).GetName());
                Assert.DoesNotContain("_", new ButtonAutomationPeer(connect).GetName());
                Assert.True(cancel.TranslatePoint(new Point(), host).X + cancel.ActualWidth < connect.TranslatePoint(new Point(), host).X);
                Assert.InRange(cancel.ActualHeight, 32, 44);
                Assert.InRange(connect.TranslatePoint(new Point(0, connect.ActualHeight), host).Y, 0, height);
                var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.Equal(36, first.ActualHeight);
                Assert.NotNull(first.FocusVisualStyle);
                Assert.Equal(model.Processes[0].AccessibleName, UIElementAutomationPeer.CreatePeerForElement(first)!.GetName());
                var pid = Descendants<TextBlock>(first).Single(text => text.Text == "123");
                Assert.Equal(((FontFamily)Application.Current.FindResource("Ui.FontMono")).Source, pid.FontFamily.Source);
                var projectPath = Descendants<TextBlock>(first).Single(text => text.Text == rows[0].ProjectPath);
                Assert.Equal(rows[0].ProjectPath, projectPath.ToolTip);
                Assert.Equal(TextTrimming.CharacterEllipsis, projectPath.TextTrimming);
                var releaseHeader = Descendants<TextBlock>(list).Single(text => text.Text == Loc.Current["ProcessPicker.Release"]);
                var modeHeader = Descendants<TextBlock>(list).Single(text => text.Text == Loc.Current["ProcessPicker.Mode"]);
                var releaseCell = Descendants<TextBlock>(first).Single(text => text.Text == "V21");
                var modeCell = Descendants<TextBlock>(first).Single(text => text.Text == model.Processes[0].UiMode);
                Assert.InRange(Math.Abs(releaseHeader.TranslatePoint(new Point(), list).X - releaseCell.TranslatePoint(new Point(), list).X), 0, 1);
                Assert.InRange(Math.Abs(modeHeader.TranslatePoint(new Point(), list).X - modeCell.TranslatePoint(new Point(), list).X), 0, 1);
                var modeText = new FormattedText(modeCell.Text, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface(modeCell.FontFamily, modeCell.FontStyle, modeCell.FontWeight, modeCell.FontStretch),
                    modeCell.FontSize, modeCell.Foreground, VisualTreeHelper.GetDpi(modeCell).PixelsPerDip);
                Assert.InRange(Math.Round(modeText.WidthIncludingTrailingWhitespace, 3, MidpointRounding.AwayFromZero), 1,
                    Math.Round(modeCell.ActualWidth, 3, MidpointRounding.AwayFromZero));
                list.SelectedItem = model.Processes[^1]; list.ScrollIntoView(list.SelectedItem); host.UpdateLayout(); WpfContext.Drain(); host.UpdateLayout();
                Assert.Equal(model.Processes[^1], model.SelectedProcess);
                Assert.True(connect.IsEnabled);
                var last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(39);
                Assert.InRange(last.TranslatePoint(new Point(), list).Y, 0, list.ActualHeight - last.ActualHeight);
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root != null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
                Assert.NotNull(root);
                var output = Path.Combine(root.FullName, "bin-build/refactor/P8-84-renders");
                Directory.CreateDirectory(output);
                WorkbenchRenderTests.SaveRender(host, Path.Combine(output, $"{language}-{theme}-{width}x{height}.png"), (int)width, (int)height);
                Assert.True(string.IsNullOrWhiteSpace(trace.Text), trace.Text);
            }
            finally { window.Close(); manager.HighContrastOverride = oldContrast; manager.Theme = oldTheme; }
        });
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Escape)]
    public void Initial_focus_is_Cancel_and_Enter_or_Escape_never_connect_even_on_Connect(Key key)
    {
        wpf.Run(() =>
        {
            using var model = new PortalProcessPickerViewModel(Processes());
            var window = new PortalProcessPicker(model);
            try
            {
                WpfContext.ShowWindow(window); window.Activate(); WpfContext.Drain();
                var cancel = (Button)window.FindName("CancelButton");
                var connect = (Button)window.FindName("ConnectButton");
                Assert.Same(cancel, Keyboard.FocusedElement);
                var list = (ListBox)window.FindName("ProcessList");
                list.SelectedIndex = 0;
                var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                first.Focus();
                first.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(first), 0, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.Equal(1, list.SelectedIndex);
                cancel.Focus();
                cancel.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Assert.Same(connect, Keyboard.FocusedElement);
                connect.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
                Assert.Same(cancel, Keyboard.FocusedElement);
                connect.Focus();
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(connect), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                connect.RaiseEvent(args);
                Assert.True(args.Handled);
                Assert.Null(model.ResultProcessId);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Space_on_Connect_returns_the_keyboard_selected_PID()
    {
        wpf.Run(() =>
        {
            using var model = new PortalProcessPickerViewModel(Processes());
            var window = new PortalProcessPicker(model);
            try
            {
                WpfContext.ShowWindow(window); window.Activate(); WpfContext.Drain();
                ((ListBox)window.FindName("ProcessList")).SelectedIndex = 1;
                var connect = (Button)window.FindName("ConnectButton");
                connect.Focus();
                WpfContext.PressSpace(connect);
                Assert.Equal(456, model.ResultProcessId);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("14.0.1.0", "V14 SP1")]
    [InlineData("15.1.0.0", "V15.1")]
    [InlineData("16.0.0.0", "V16")]
    [InlineData("17.0.0.0", "V17")]
    [InlineData("18.0.0.0", "V18")]
    [InlineData("19.0.0.0", "V19")]
    [InlineData("20.0.0.0", "V20")]
    [InlineData("21", "V21")]
    public void Rows_use_the_catalogue_release_names_including_V14_SP1(string release, string expected)
    {
        var row = new PortalProcessRow(new PortalProcessInfo { ProcessId = 123, Release = release });
        Assert.Equal(expected, row.Release);
    }

    [Fact]
    public void Modal_Cancel_returns_no_PID_and_restores_the_owners_keyboard_focus()
    {
        wpf.Run(() =>
        {
            var trigger = new Button { Content = "Fixture" };
            var owner = new Window { Content = trigger, Width = 1024, Height = 640 };
            try
            {
                WpfContext.ShowWindow(owner); owner.Activate(); trigger.Focus(); WpfContext.Drain();
                bool? matchingOwner = null, safeFocus = null;
                owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
                {
                    var picker = Application.Current.Windows.OfType<PortalProcessPicker>().Single();
                    matchingOwner = picker.Owner == owner;
                    var cancel = (Button)picker.FindName("CancelButton");
                    safeFocus = Keyboard.FocusedElement == cancel;
                    UnifiedDesktopTests.ClickControl(cancel);
                }));
                var result = new WpfDialogService().PickPortalProcess(Processes());
                Assert.Null(result);
                Assert.True(matchingOwner);
                Assert.True(safeFocus);
                Assert.Same(trigger, Keyboard.FocusedElement);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public async Task Shared_MCP_session_discovers_modes_and_attaches_only_after_a_choice()
    {
        await using var session = new ProcessSession();
        using var client = new McpProjectClient(session);
        var error = await Assert.ThrowsAsync<PortalProcessSelectionRequiredException>(() => client.ConnectAsync(true));
        Assert.Equal(new[] { "ListPortalProcessProjects", "GetPortalInfo" }, session.Calls);
        Assert.Equal("WithoutUserInterface", error.Processes[1].UiMode);
        Assert.Equal("21", error.Processes[1].Release);
        var state = await client.ConnectAsync(true, 456);
        Assert.True(state.Connected);
        Assert.Equal(456, session.ConnectedPid);
        Assert.Null(state.OpenProject);
        Assert.DoesNotContain("AttachOpenProject", session.Calls);
    }

    [Fact]
    public async Task Shared_MCP_session_refuses_a_stale_choice_without_attaching_another_instance()
    {
        await using var session = new ProcessSession();
        using var client = new McpProjectClient(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(true, 789));
        Assert.Equal(new[] { "ListPortalProcessProjects" }, session.Calls);
        Assert.Null(session.ConnectedPid);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject element) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class ProcessSession : IMcpSessionClient
    {
        public string? SessionKey => "fixture";
        public JsonObject? State => new() { ["releaseKey"] = "21" };
        public List<string> Calls { get; } = [];
        public int? ConnectedPid { get; private set; }
        public JsonObject? ToolSchema(string tool) => null;
        public event EventHandler<JsonObject>? StateChanged { add { } remove { } }
        public event EventHandler? Detached { add { } remove { } }
        public Task<IReadOnlyList<OperatorSession>> DiscoverAsync(string release, CancellationToken token = default) => throw new NotSupportedException();
        public Task AttachAsync(OperatorSession session, CancellationToken token = default) => throw new NotSupportedException();
        public Task CreateAsync(string release, CancellationToken token = default) => throw new NotSupportedException();
        public Task DetachAsync() => Task.CompletedTask;
        public void CancelPending() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<JsonObject> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken token = default)
        {
            Calls.Add(tool);
            JsonObject data;
            switch (tool)
            {
                case "ListPortalProcessProjects": data = new() { ["processes"] = new JsonArray(
                    new JsonObject { ["processId"] = 123, ["projectPath"] = @"D:\Line.ap21" },
                    new JsonObject { ["processId"] = 456, ["projectPath"] = null }) }; break;
                case "GetPortalInfo":
                    Assert.Equal(false, arguments["includeSessions"]); Assert.Equal(false, arguments["includeProducts"]);
                    data = new() { ["evidence"] = new JsonObject { ["processes"] = new JsonArray(
                        new JsonObject { ["id"] = 123, ["mode"] = "WithUserInterface" },
                        new JsonObject { ["id"] = 456, ["mode"] = "WithoutUserInterface" }) } }; break;
                case "ConnectPortal": ConnectedPid = (int)arguments["processId"]!; data = new(); break;
                default: throw new InvalidOperationException("Unexpected tool: " + tool);
            }
            return Task.FromResult(new JsonObject { ["ok"] = true, ["data"] = data });
        }
    }
}
