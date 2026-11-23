using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.ControlChannel;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;
using Xunit;
using static TiaOpenness.Gui.Tests.WorkbenchControlTests;

namespace TiaOpenness.Gui.Tests;

[Collection(WpfCollection.Name)]
public sealed partial class WorkbenchControlOverlayTests(WpfContext wpf)
{
    private static CommandPalette OpenPalette(MainWindow window)
    {
        Assert.True(WorkbenchCommands.CommandPalette.CanExecute(null, window));
        WorkbenchCommands.CommandPalette.Execute(null, window);
        WpfContext.Drain();
        return Assert.IsType<CommandPalette>(Assert.Single(window.OverlayHost.Children.Cast<UIElement>()));
    }

    private static SessionPopover OpenSession(MainWindow window)
    {
        UnifiedDesktopTests.ClickControl(window.SessionStatusButton);
        return Assert.Single(window.Root.Children.OfType<SessionPopover>());
    }

    private static bool KeyDown(UIElement element, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(args);
        return args.Handled;
    }

    [Fact]
    public void Frozen_state_follows_recent_input_and_clears_after_the_operator_is_idle()
    {
        wpf.Run(() =>
        {
            var now = TestNow;
            var (window, _, _) = Window(input: new ControlInputGuard(() => now, mousePosition: () => new Point()));
            try
            {
                KeyDown(window, Key.A);
                WpfContext.Drain();
                string? Reason() => window.ControlSurface.Snapshot.Read(new WorkbenchReadStateArguments()).Workbench!.BlockedBy;
                Assert.Equal("user-active", Reason());
                Assert.Equal("workbench-user-active", window.ApplyControl(Request()).Refusal?.Condition);
                now = now.AddSeconds(2);
                WpfContext.WaitForInput(TimeSpan.FromMilliseconds(200));
                WpfContext.Drain();
                Assert.Null(Reason());
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request()).Status);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Palette_traps_focus_blocks_control_and_restores_its_invoker_on_escape()
    {
        wpf.Run(() =>
        {
            var (window, _, client) = Window();
            try
            {
                Assert.True(window.RailEngineering.Focus());
                var palette = OpenPalette(window);
                Assert.True(AutomationProperties.GetIsDialog(palette));
                Assert.Same(palette.SearchBox, Keyboard.FocusedElement);
                Assert.Equal(0, palette.ResultList.SelectedIndex);
                Assert.True(KeyDown(palette.SearchBox, Key.Down));
                Assert.Equal(1, palette.ResultList.SelectedIndex);
                WpfContext.Drain();
                var current = (ListBoxItem)palette.ResultList.ItemContainerGenerator.ContainerFromIndex(1);
                Assert.True(current.IsSelected);
                Assert.True(((CommandPaletteViewModel)palette.DataContext).ShowKeyboardSelection);
                Assert.Same(current.FindResource("Primer.accent"), current.BorderBrush);
                Assert.True(KeyDown(palette.SearchBox, Key.Up));
                Assert.Equal(0, palette.ResultList.SelectedIndex);
                Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(palette));
                Assert.False(window.RailEngineering.IsEnabled);
                foreach (var direction in new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous })
                    for (int i = 0; i < 5; i++)
                    {
                        Assert.True(((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(direction)));
                        Assert.True(palette.IsKeyboardFocusWithin);
                    }
                Assert.Equal("command-palette", window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Data!.Workbench!.BlockedBy);
                Assert.Equal("workbench-modal-open", window.ApplyControl(Request()).Refusal?.Condition);
                Assert.True(KeyDown((UIElement)Keyboard.FocusedElement, Key.Escape));
                WpfContext.Drain();
                Assert.Equal(Visibility.Collapsed, window.OverlayHost.Visibility);
                Assert.Same(window.RailEngineering, Keyboard.FocusedElement);
                Assert.Null(window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Data!.Workbench!.BlockedBy);
                Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Existing_modals_suppress_the_palette_shortcut()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                void Check(UIElement source)
                {
                    Assert.False(WorkbenchCommands.CommandPalette.CanExecute(null, window));
                    WpfContext.WithControlPressed(() => Assert.True(KeyDown(source, Key.K)));
                    Assert.Equal(Visibility.Collapsed, window.OverlayHost.Visibility);
                }
                window.OperationsContent.ShowProjectOptions();
                Check(window.OperationsContent);
                window.Navigate("Calls");
                window.Features.OpenCall(window.Features.Calls[0]);
                Check(window.DrawerOverlay);
                window.Navigate("Approvals");
                ((ApprovalServiceStub)window.Approvals).Receive(FeaturePageFixtures.Request(CallId) with { Risk = ApprovalRisk.Destructive,
                    Deadline = DateTimeOffset.UtcNow.AddMinutes(2) });
                WpfContext.Drain();
                WpfContext.WaitForDecisionGuard(window.ApprovalsContent);
                UnifiedDesktopTests.ClickControl(DesktopCapture.Descendants<Button>(window.ApprovalsContent).First(b => b.Name == "ApproveRequest"));
                Assert.True(window.ApprovalsContent.ConfirmationSheet.IsVisible);
                Assert.Equal("confirmation-sheet", window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Data!.Workbench!.BlockedBy);
                Check(window.ApprovalsContent.ConfirmationSheet);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_palette_item_only_navigates_locates_or_sets_appearance(bool blockChecked)
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var (window, model, client) = Window();
            var theme = ThemeManager.Current.Theme;
            var accent = ThemeManager.Current.Accent;
            try
            {
                string[] pages = ["Project · Line.ap21", "Agent & Approvals", "MCP Server", "AI Clients", "Environment Check",
                    "Audit Chain", "Runtime Logs", "Settings & Safety", "Approvals", "AI calls", "Version control"];
                string[] appearance = ["Settings.Light", "Settings.Dark", "Settings.AccentCobalt", "Settings.AccentPetrol",
                    "Settings.English", "Settings.Chinese"];
                var commands = new[] { "Main", Loc.Current["Overlay.Rules"], Loc.Current["Overlay.Transfer"] };
                var titles = pages.Concat(commands).Concat(appearance.Select(key => Loc.Current[key])).ToArray();
                var approvals = (ApprovalServiceStub)window.Approvals;
                approvals.Receive(FeaturePageFixtures.Request(CallId) with { Deadline = DateTimeOffset.UtcNow.AddMinutes(2) });
                bool approvalEnabled = approvals.Enabled, controlEnabled = App.Settings.WorkbenchControlEnabled;
                model.Engineering.Blocks[0].Selected = blockChecked;
                model.Engineering.BlockFilter = "unmatched";
                string[] destinations = ["Engineering", "Approvals", "Mcp", "Clients", "Environment", "Audit", "Log",
                    "Settings", "Approvals", "Calls", "VersionControl"];
                for (int i = 0; i < titles.Length; i++)
                {
                    var palette = OpenPalette(window);
                    Assert.Equal(titles.Order(), palette.ResultList.Items.Cast<CommandPaletteItem>().Select(item => item.Title).Order());
                    Assert.Equal(pages.Take(8), palette.ResultList.Items.Cast<CommandPaletteItem>().Take(8).Select(item => item.Title));
                    palette.SearchBox.Text = titles[i];
                    palette.ResultList.SelectedItem = palette.ResultList.Items.Cast<CommandPaletteItem>().Single(item => item.Title == titles[i]);
                    Assert.True(KeyDown(palette.SearchBox, Key.Enter));
                    if (i < pages.Length) Assert.Equal(destinations[i], window.CurrentPage);
                    if (titles[i] == Loc.Current["Overlay.Rules"] || titles[i] == Loc.Current["Overlay.Transfer"])
                    {
                        Assert.Equal("Engineering", window.CurrentPage);
                        Assert.True(window.OperationsContent.OptionsOpen);
                        WpfContext.Drain(); window.UpdateLayout();
                        var options = window.OperationsContent.Options;
                        Assert.True((titles[i] == Loc.Current["Overlay.Rules"] ? options.InspectOptions : options.ExportOptions).IsVisible);
                        options.Hide();
                    }
                    if (titles[i] == "Main")
                    {
                        Assert.Equal("Engineering", window.CurrentPage);
                        Assert.Same(model.Engineering.Blocks[0], window.BlocksContent.ControlFocusedBlock);
                    }
                    Assert.Equal(blockChecked, model.Engineering.Blocks[0].Selected);
                    Assert.Equal(approvalEnabled, approvals.Enabled);
                    Assert.Equal(controlEnabled, App.Settings.WorkbenchControlEnabled);
                    Assert.Equal(ApprovalState.Pending, Assert.Single(approvals.Requests).State);
                    Assert.Empty(client.Calls);
                }
                Assert.Equal(AppTheme.Dark, ThemeManager.Current.Theme);
                Assert.Equal(AppAccent.Petrol, ThemeManager.Current.Accent);
                Assert.Equal(AppLanguage.Chinese, Loc.Current.Language);
            }
            finally { ThemeManager.Current.Theme = theme; ThemeManager.Current.Accent = accent; window.Close(); }
        });
    }

    [Fact]
    public void Palette_keeps_surface_clicks_inside_and_dismisses_on_a_scrim_click()
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                Assert.True(window.CommandPaletteButton.Focus());
                var palette = OpenPalette(window);
                static void Click(UIElement target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Mouse.MouseDownEvent });
                Click(palette.Surface);
                Assert.True(palette.IsVisible);
                Click(palette.Scrim);
                WpfContext.Drain();
                Assert.Equal(Visibility.Collapsed, window.OverlayHost.Visibility);
                Assert.Same(window.CommandPaletteButton, Keyboard.FocusedElement);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Palette_finds_a_block_by_metadata_or_path_and_keeps_the_short_page_names_searchable()
    {
        wpf.RunWithLanguage(AppLanguage.English, () =>
        {
            var (window, model, client) = Window();
            try
            {
                var row = model.Engineering.Blocks[0];
                row.Info.Number = 7;
                row.Info.ProgrammingLanguage = "SCL";
                row.Info.Path = "Program/Main";
                var palette = OpenPalette(window);
                foreach (string query in new[] { "OB 7", "scl", "program/", "PLC_1" })
                {
                    palette.SearchBox.Text = query;
                    var result = Assert.Single(palette.ResultList.Items.Cast<CommandPaletteItem>());
                    Assert.Equal("Main (OB 7 · SCL)", result.Title);
                    Assert.Equal("PLC_1", result.Detail);
                }
                palette.SearchBox.Text = "doctor";
                Assert.Equal("Environment Check", Assert.Single(palette.ResultList.Items.Cast<CommandPaletteItem>()).Title);
                palette.SearchBox.Text = "Ctrl+2";
                Assert.True(KeyDown(palette.SearchBox, Key.Enter));
                Assert.Equal("Calls", window.CurrentPage);
                Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Offline_palette_offers_only_available_pages_and_appearance()
    {
        wpf.Run(() =>
        {
            var window = new MainWindow(new MainViewModel(true), false);
            try
            {
                WpfContext.ShowWindow(window);
                var palette = OpenPalette(window);
                var titles = palette.ResultList.Items.Cast<CommandPaletteItem>().Select(item => item.Title).ToArray();
                Assert.Equal(10, titles.Length);
                foreach (var key in new[] { "Shell.Calls", "Shell.Environment", "Shell.Audit", "Shell.Logs" }) Assert.Contains(Loc.Current[key], titles);
                palette.SearchBox.Text = Guid.NewGuid().ToString();
                Assert.Empty(palette.ResultList.Items);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Session_escape_returns_focus_and_AI_navigation_closes_the_non_modal_flyout()
    {
        wpf.Run(() =>
        {
            var (window, _, client) = Window();
            try
            {
                var popover = OpenSession(window);
                Assert.False(AutomationProperties.GetIsDialog(popover));
                Assert.Contains("Line.ap21", window.SessionStatus.Text);
                Assert.Null(popover.FindName("Project"));
                Assert.Equal(Loc.Current["Overlay.Session"], popover.SessionHeading.Text);
                Assert.Null(popover.FindName("Plc"));
                Assert.Null(popover.FindName("Client"));
                Assert.Null(popover.FindName("AiSettings"));
                Assert.True(KeyDown(popover, Key.Escape));
                Assert.Empty(window.Root.Children.OfType<SessionPopover>());
                Assert.Same(window.SessionStatusButton, Keyboard.FocusedElement);
                client.Devices[0].TypeName = "CPU 1512SP-1 PN";
                Assert.DoesNotContain(DesktopCapture.Descendants<TextBlock>(OpenSession(window)), text => text.Text.Contains("CPU 1512SP-1 PN"));
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request()).Status);
                Assert.Equal("Log", window.CurrentPage);
                Assert.Empty(window.Root.Children.OfType<SessionPopover>());
                Assert.Empty(client.Calls);
                UnifiedDesktopTests.ClickControl(OpenSession(window).SwitchVersion);
                Assert.Equal("Mcp", window.CurrentPage);
                UnifiedDesktopTests.ClickControl(window.ApprovalStatusButton);
                Assert.Equal("Settings", window.CurrentPage);
                Assert.Equal(Visibility.Visible, window.SettingsContent.AiPanel.Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Refused_AI_requests_leave_the_same_session_popover_open()
    {
        wpf.Run(() =>
        {
            var (window, _, client) = Window();
            try
            {
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request(WorkbenchControlOperation.PrefillForm)).Status);
                WpfContext.Drain();
                var popover = OpenSession(window);
                void Refused(WorkbenchControlRequest request, string condition)
                {
                    Assert.Equal(condition, window.ApplyControl(request).Refusal?.Condition);
                    WpfContext.Drain();
                    Assert.Same(popover, Assert.Single(window.Root.Children.OfType<SessionPopover>()));
                }
                Refused(Request(WorkbenchControlOperation.DisplayPage, new WorkbenchDisplayPageArguments { Page = (WorkbenchPage)99 }),
                    "workbench-operation");
                Refused(Request(WorkbenchControlOperation.DisplayBlock,
                    new WorkbenchDisplayBlockArguments { SoftwarePath = "PLC_1", BlockPath = "Missing" }), "block-not-found");
                Refused(Request(WorkbenchControlOperation.PrefillForm, session: "3333333333333333"), "prefill-pending");
                Assert.Empty(client.Calls);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("Engineering", "Project")]
    [InlineData("Blocks", "Export")]
    [InlineData("VersionControl", "Workspace")]
    public void Human_options_remain_open_and_block_AI_until_dismissed(string page, string section)
    {
        wpf.Run(() =>
        {
            var (window, _, _) = Window();
            try
            {
                window.Navigate(page);
                var options = page switch
                {
                    "Engineering" => window.OperationsContent.Options,
                    "Blocks" => window.BlocksContent.Options,
                    _ => window.VersionControlContent.Options,
                };
                string? Blocker() => window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Data!.Workbench!.BlockedBy;
                WpfContext.Drain();
                Assert.Null(Blocker());
                options.Show(section);
                WpfContext.Drain();
                Assert.Equal("options", Blocker());
                Assert.Equal("workbench-modal-open", window.ApplyControl(Request()).Refusal?.Condition);
                Assert.True(options.IsOpen);
                options.Hide();
                WpfContext.Drain();
                Assert.Null(Blocker());
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request()).Status);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(nameof(WorkbenchPrefillForm.InspectionRules))]
    [InlineData(nameof(WorkbenchPrefillForm.ConnectProject))]
    [InlineData(nameof(WorkbenchPrefillForm.ExportBlocks))]
    [InlineData(nameof(WorkbenchPrefillForm.ImportBlocks))]
    public async Task AI_navigation_closes_prefilled_options_but_preserves_a_human_reopening(string form)
    {
        await using var fixture = new McpOperatorFixture("21");
        fixture.BindProject();
        await using var client = fixture.Client();
        await client.AttachAsync(Assert.Single(await client.DiscoverAsync("21")));
        wpf.Run(() =>
        {
            var model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
            var window = new MainWindow(model, false, new ApprovalServiceStub()) { ControlGuard = new(humanActive: () => false) };
            try
            {
                WpfContext.Complete(model.Session.ApplySharedStateAsync(client.State));
                WpfContext.ShowWindow(window);
                var arguments = new WorkbenchPrefillArguments { Form = Enum.Parse<WorkbenchPrefillForm>(form) };
                arguments.Fields = arguments.Form switch
                {
                    WorkbenchPrefillForm.InspectionRules => new WorkbenchInspectionRulesFields { NamePattern = "^FB_" },
                    WorkbenchPrefillForm.ConnectProject => new WorkbenchConnectProjectFields { ProjectPath = Project },
                    WorkbenchPrefillForm.ExportBlocks => new WorkbenchExportBlocksFields { OutputDirectory = fixture.Root, Format = "Source" },
                    _ => new WorkbenchImportBlocksFields { Paths = [Path.Combine(fixture.Root, "Main.xml")], Overwrite = false },
                };
                var request = Request(WorkbenchControlOperation.PrefillForm, arguments);
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(request).Status);
                Assert.True(window.OperationsContent.OptionsOpen);
                WpfContext.Drain();
                string? Blocker() => window.ApplyControl(Request(WorkbenchControlOperation.ReadState)).Data!.Workbench!.BlockedBy;
                Assert.Null(Blocker());
                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(Request()).Status);
                Assert.False(window.OperationsContent.OptionsOpen);
                Assert.Equal("Log", window.CurrentPage);

                Assert.Equal(WorkbenchControlStatus.Done, window.ApplyControl(request).Status);
                window.OperationsContent.HideOptions();
                window.OperationsContent.ShowProjectOptions();
                WpfContext.Drain();
                Assert.Equal("options", Blocker());
                Assert.Equal("workbench-modal-open", window.ApplyControl(Request()).Refusal?.Condition);
                Assert.True(window.OperationsContent.OptionsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("14sp1", "STA", AppLanguage.English)]
    [InlineData("15.1", "STA", AppLanguage.English)]
    [InlineData("16", "STA", AppLanguage.English)]
    [InlineData("17", "STA", AppLanguage.English)]
    [InlineData("18", "STA", AppLanguage.English)]
    [InlineData("19", "STA", AppLanguage.English)]
    [InlineData("20", "MTA", AppLanguage.English)]
    [InlineData("21", "MTA", AppLanguage.Chinese)]
    public async Task Session_shows_unverified_client_and_disconnect_uses_cancel_default(string release, string apartment, AppLanguage language)
    {
        await using var fixture = new McpOperatorFixture(release);
        await fixture.Notify(state => state["binding"] = new System.Text.Json.Nodes.JsonObject { ["processId"] = 42 });
        var client = fixture.Client();
        await client.AttachAsync(Assert.Single(await client.DiscoverAsync(release)));
        wpf.RunWithLanguage(language, () =>
        {
            var dialogs = new DisconnectDialog();
            var model = new MainViewModel(new McpProjectClient(client), dialogs);
            WpfContext.Complete(model.Session.ApplySharedStateAsync(client.State));
            var window = new MainWindow(model, false, new ApprovalServiceStub()) { ControlGuard = new(humanActive: () => false) };
            try
            {
                WpfContext.ShowWindow(window);
                var popover = OpenSession(window);
                Assert.Equal(TiaMcp.Versioning.TiaVersionCatalog.Get(release).DisplayName + " · " + apartment, popover.Release.Text);
                InvokeSessionButton(popover.Discover, model.Panel!.Refresh);
                popover.UpdateLayout();
                var row = Assert.Single(DesktopCapture.Descendants<ListBoxItem>(popover.SessionList));
                var clientLabel = Assert.Single(DesktopCapture.Descendants<TextBlock>(row), text => text.Text.Contains("fixture AI"));
                Assert.Contains(DesktopCapture.Descendants<TextBlock>(row), text => text.IsVisible && text.Text == "IDLE");
                Assert.True(clientLabel.IsVisible);
                Assert.Contains("fixture AI", clientLabel.Text);
                Assert.Contains("(stdio)", clientLabel.Text);
                Assert.Equal(Loc.Current["Overlay.SharedSession"], popover.SessionHeading.Text);
                var marker = language == AppLanguage.Chinese ? "（未验证）" : "(unverified)";
                Assert.Contains(DesktopCapture.Descendants<TextBlock>(row), text => text.IsVisible && text.Text == marker);
                Assert.Contains(marker, AutomationProperties.GetName(row));
                popover.UpdateLayout();
                // XREF-1 removed the cross-reference strings, so match the visible words in both languages.
                static bool Mentions(string? text) => text is not null
                    && (text.Contains("cross reference", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("cross-reference", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("交叉引用", StringComparison.Ordinal));
                Assert.DoesNotContain(DesktopCapture.Descendants<FrameworkElement>(popover).Prepend(popover),
                    element => Mentions(AutomationProperties.GetName(element)) || element switch
                    {
                        TextBlock text => Mentions(text.Text),
                        TextBox field => Mentions(field.Text),
                        AccessText label => Mentions(label.Text),
                        ContentControl control => Mentions(control.Content as string),
                        _ => false,
                    });
                Assert.Empty(DesktopCapture.Descendants<ToggleButton>(popover));
                Assert.Equal(new[] { "Attach", "Create", "Detach", "Disconnect", "Discover", "SwitchVersion" },
                    DesktopCapture.Descendants<Button>(popover).Where(button => button.IsVisible)
                        .Select(button => button.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
                Assert.True(popover.Disconnect.IsEnabled);
                UnifiedDesktopTests.ClickControl(popover.Disconnect);
                Assert.Equal(ConfirmationAction.EndSharedSession, dialogs.Action);
                Assert.True(model.Session.IsConnected);
                Assert.DoesNotContain(fixture.Calls, call => call.Name == "DisconnectPortal");
                Assert.Empty(window.Root.Children.OfType<SessionPopover>());
            }
            finally { window.Close(); }
        });
    }

    private sealed class DisconnectDialog : IDialogService
    {
        internal ConfirmationAction? Action { get; private set; }
        public DialogResult ShowMessage(string text, string caption, DialogButtons buttons, DialogIcon icon, ConfirmationLabels labels)
        {
            Assert.Equal(DialogButtons.OKCancel, buttons);
            Assert.Equal(DialogIcon.Warning, icon);
            var dialog = new WorkbenchMessageBox(text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning, labels);
            try
            {
                WpfContext.ShowWindow(dialog);
                var cancel = Assert.Single(DesktopCapture.Descendants<Button>(dialog), button => button.IsDefault);
                Assert.True(cancel.IsCancel);
                Assert.Equal(Loc.Current["Dialog.Cancel"], cancel.Content);
            }
            finally { dialog.Close(); }
            Action = labels.Action;
            return DialogResult.Cancel;
        }

        public int? PickPortalProcess(IReadOnlyList<PortalProcessInfo> processes) => throw new NotSupportedException();
        public DialogResult ShowMessage(string text, string caption, DialogButtons buttons, DialogIcon icon) => throw new NotSupportedException();
        public string[]? OpenFiles(string title, string filter, bool multiselect = false) => throw new NotSupportedException();
        public string? OpenFolder(string title) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(1024, 640)]
    [InlineData(1366, 860)]
    public async Task Overlays_fit_the_workbench_in_both_languages_and_all_contrast_modes(int width, int height)
    {
        await using var fixture = new McpOperatorFixture("19");
        fixture.BindProject();
        await using var client = fixture.Client();
        await client.AttachAsync(Assert.Single(await client.DiscoverAsync("19")));
        wpf.Run(() =>
        {
            var model = new MainViewModel(new McpProjectClient(client), new FakeDialogService());
            WpfContext.Complete(model.Session.ApplySharedStateAsync(client.State));
            var window = new MainWindow(model, false, new ApprovalServiceStub()) { Width = width, Height = height };
            var themes = ThemeManager.Current;
            var saved = (themes.Theme, themes.HighContrastOverride, Loc.Current.Language);
            string? render = Environment.GetEnvironmentVariable("TIA_WORKBENCH_RENDER_DIR");
            try
            {
                WpfContext.ShowWindow(window);
                window.UpdateLayout();
                foreach (var language in Enum.GetValues<AppLanguage>())
                foreach (var mode in new[] { "light", "dark", "high-contrast" })
                {
                    Loc.Current.Language = language;
                    themes.Theme = mode == "dark" ? AppTheme.Dark : AppTheme.Light;
                    themes.HighContrastOverride = mode == "high-contrast";
                    var palette = OpenPalette(window);
                    Capture((FrameworkElement)palette.ResultList.Parent, "palette");
                    Assert.Equal(!themes.UseLowEffects && !themes.EffectivelyHighContrast, palette.Backdrop.IsVisible);
                    Assert.Equal(490, palette.Surface.ActualWidth);
                    Assert.Equal(56, palette.Surface.TranslatePoint(new Point(), window.Root).Y);
                    Assert.Equal(300, palette.ResultList.ActualHeight);
                    UnifiedDesktopTests.ClickControl(palette.Dismiss);
                    var popover = OpenSession(window);
                    Capture(popover, "session");
                    var corner = popover.TranslatePoint(new Point(0, popover.ActualHeight), window.Root);
                    Assert.Equal(12, corner.X);
                    Assert.Equal(32, window.Root.ActualHeight - corner.Y);
                    Assert.Equal(372, popover.ActualWidth);
                    Assert.Equal(140, popover.SwitchVersion.ActualWidth);
                    Assert.Equal(28, popover.SwitchVersion.ActualHeight);
                    Assert.Equal(28, popover.Disconnect.ActualHeight);
                    var actionsBottom = popover.Disconnect.TranslatePoint(new Point(0, popover.Disconnect.ActualHeight), popover).Y;
                    Assert.Equal(17, popover.ActualHeight - actionsBottom);
                    Assert.InRange(Math.Abs(popover.Detach.ActualWidth - popover.Disconnect.ActualWidth), 0, 1);
                    var peer = new ButtonAutomationPeer(popover.Disconnect);
                    Assert.Equal(Loc.Current["Overlay.Disconnect"], peer.GetName());
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(popover);
                    Assert.InRange(Math.Abs(peer.GetBoundingRectangle().Height - 28 * dpi.DpiScaleY), 0, 1);
                    UnifiedDesktopTests.ClickControl(window.SessionStatusButton);

                    void Capture(FrameworkElement panel, string name)
                    {
                        WpfContext.Drain(); window.UpdateLayout();
                        Rect bounds = panel.TransformToAncestor(window.Root).TransformBounds(new Rect(panel.RenderSize));
                        Assert.True(new Rect(window.Root.RenderSize).Contains(bounds), bounds.ToString());
                        Assert.True(bounds.Width > 0 && bounds.Height > 0);
                        if (string.IsNullOrEmpty(render)) return;
                        string directory = Directory.CreateDirectory(Path.Combine(render, "overlays")).FullName;
                        WorkbenchRenderTests.SaveRender(window.Root, Path.Combine(directory, $"{name}-{language}-{mode}-{width}x{height}.png"), width, height);
                    }
                }
            }
            finally { (themes.Theme, themes.HighContrastOverride, Loc.Current.Language) = saved; window.Close(); }
        });
    }
}
