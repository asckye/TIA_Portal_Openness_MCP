using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    private ModalPane? _paletteFocus;
    private SessionPopover? _sessionPopover;

    partial void OpenCommandPalette()
    {
        if (ShellModalOpen) return;
        CloseSessionPopover();
        var invoker = Keyboard.FocusedElement as UIElement;
        void Close() => CloseCommandPalette(invoker);
        var palette = new CommandPalette(new CommandPaletteViewModel(PaletteItems()), Close);
        palette.Scrim.Source = ShellFrame;
        OverlayHost.Children.Add(palette);
        OverlayHost.Visibility = Visibility.Visible;
        _paletteFocus = new ModalPane(palette, palette.SearchBox, Close);
        QueueControlSnapshot();
    }

    private List<CommandPaletteItem> PaletteItems()
    {
        var items = new List<CommandPaletteItem>();
        void Page(string id, string title, string detail, string alias = "")
        {
            if (PageUnavailable(id) == null) items.Add(new(title, detail, () => Navigate(id), alias));
        }

        void RailPage(RadioButton rail, string title)
        {
            Page((string)rail.Tag, title, AutomationProperties.GetAcceleratorKey(rail), (string)rail.Content);
        }
        RailPage(RailEngineering, HasProject ? Loc.Current["Shell.Engineering"] + " · " + ProjectFileName : Loc.Current["Shell.Engineering"]);
        RailPage(RailAgent, Loc.Current["Shell.Approvals"]);
        RailPage(RailMcp, Loc.Current["Desktop.Configuration"]);
        RailPage(RailClients, Loc.Current["Shell.Clients"]);
        RailPage(RailEnvironment, Loc.Current["Shell.Environment"]);
        RailPage(RailAudit, Loc.Current["Shell.Audit"]);
        RailPage(RailLog, Loc.Current["Shell.Logs"]);
        Page("Settings", Loc.Current["Shell.Settings"], AutomationProperties.GetAcceleratorKey(SettingsButton),
            Loc.Current["Palette.Settings"] + " " + SettingsButton.Content);
        Page("Approvals", Loc.Current["Shell.ApprovalsPill"], Loc.Current["Rail.Agent"]);
        Page("Calls", Loc.Current["Shell.Calls"], Loc.Current["Rail.Agent"]);
        Page("VersionControl", Loc.Current["Shell.VersionControl"], Loc.Current["Rail.Project"]);

        var engineering = _model.Engineering;
        bool LoadedPlc() => HasProject && PageUnavailable("Engineering") == null && engineering.SelectedDevice is { Category: "Plc" } device
            && engineering.LoadedDeviceId == device.Id;
        if (LoadedPlc())
        {
            foreach (var row in engineering.Blocks)
            {
                var metadata = new List<string>(2);
                if (row.Info.Number.HasValue) metadata.Add(row.Kind + " " + row.Number);
                if (!string.IsNullOrWhiteSpace(row.Info.ProgrammingLanguage)) metadata.Add(row.Language);
                var title = metadata.Count == 0 ? row.Name : row.Name + " (" + string.Join(" · ", metadata) + ")";
                items.Add(new(title, engineering.SelectedDevice!.Name, () =>
                {
                    if (!LoadedPlc() || !engineering.Blocks.Contains(row)) return;
                    Navigate("Blocks");
                    engineering.BlockFilter = "";
                    BlocksContent.LocateControlBlock(row);
                }, row.Path));
            }
        }

        var themes = ThemeManager.Current;
        items.Add(new(Loc.Current["Settings.Light"], Loc.Current["Settings.Theme"], () => themes.Theme = AppTheme.Light));
        items.Add(new(Loc.Current["Settings.Dark"], Loc.Current["Settings.Theme"], () => themes.Theme = AppTheme.Dark));
        items.Add(new(Loc.Current["Settings.AccentCobalt"], Loc.Current["Settings.Accent"], () => themes.Accent = AppAccent.Cobalt));
        items.Add(new(Loc.Current["Settings.AccentPetrol"], Loc.Current["Settings.Accent"], () => themes.Accent = AppAccent.Petrol));
        items.Add(new(Loc.Current["Settings.English"], Loc.Current["Settings.Language"], () => Loc.Current.Language = AppLanguage.English));
        items.Add(new(Loc.Current["Settings.Chinese"], Loc.Current["Settings.Language"], () => Loc.Current.Language = AppLanguage.Chinese));
        return items;
    }

    private void CloseCommandPalette(UIElement? invoker = null)
    {
        OverlayHost.Visibility = Visibility.Collapsed;
        _paletteFocus?.Dispose();
        _paletteFocus = null;
        OverlayHost.Children.Clear();
        // A command button can still be disabled until WPF requeries it after the modal closes.
        if (invoker != null && !invoker.IsKeyboardFocusWithin)
        {
            CommandManager.InvalidateRequerySuggested();
            Dispatcher.InvokeAsync(() =>
            {
                if (!ShellModalOpen && invoker.IsEnabled && invoker.IsVisible
                    && (Keyboard.FocusedElement == this || (IsActive && !IsKeyboardFocusWithin)))
                    invoker.Focus();
            }, DispatcherPriority.ContextIdle);
        }
        QueueControlSnapshot();
    }

    partial void ToggleSessionPopover()
    {
        if (_sessionPopover != null) { CloseSessionPopover(); return; }
        if (ShellModalOpen) return;
        var popover = new SessionPopover(_model, NavigateFromSession, () => CloseSessionPopover());
        _sessionPopover = popover;
        popover.AiSettings.IsEnabled = PageUnavailable("Settings") == null;
        SetServiceHelp(popover.SwitchVersion, AutomationProperties.GetHelpText(RailMcp));
        popover.SwitchVersion.IsEnabled = PageUnavailable("Mcp") == null;
        Root.Children.Add(popover);
        PreviewMouseDown += OnSessionOutsideClick;
        Deactivated += OnSessionDeactivated;
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnSessionKey), true);
        popover.LostKeyboardFocus += OnSessionFocusLeft;
        popover.Focusable = true;
        popover.Focus();
    }

    private static void SetServiceHelp(Button button, string service)
    {
        button.ToolTip = service;
        AutomationProperties.SetHelpText(button, service);
    }

    private void NavigateFromSession(string page)
    {
        CloseSessionPopover(false);
        Navigate(page);
        if (page == "Settings" && _page == page) SettingsContent.ShowSection("AI");
    }

    private void OnSessionOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (!SessionStatusButton.IsMouseOver && _sessionPopover?.IsMouseOver == false)
            CloseSessionPopover(false);
    }

    private void OnSessionDeactivated(object? sender, EventArgs e) => CloseSessionPopover(false);
    private void OnSessionFocusLeft(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject next && next != SessionStatusButton && next != _sessionPopover
            && _sessionPopover?.IsAncestorOf(next) == false) CloseSessionPopover(false);
    }

    private void OnSessionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseSessionPopover();
    }

    private void CloseSessionPopover(bool restoreFocus = true)
    {
        if (_sessionPopover is not { } popover) return;
        _sessionPopover = null;
        PreviewMouseDown -= OnSessionOutsideClick;
        Deactivated -= OnSessionDeactivated;
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnSessionKey));
        popover.LostKeyboardFocus -= OnSessionFocusLeft;
        Root.Children.Remove(popover);
        if (restoreFocus) SessionStatusButton.Focus();
    }
}
