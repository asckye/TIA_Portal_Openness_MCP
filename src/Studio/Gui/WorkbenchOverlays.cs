using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
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
        var palette = new CommandPalette(new CommandPaletteViewModel(PaletteItems()), CloseCommandPalette);
        OverlayHost.Children.Add(palette);
        OverlayHost.Visibility = Visibility.Visible;
        _paletteFocus = new ModalPane(palette, palette.SearchBox, CloseCommandPalette);
        QueueControlSnapshot();
    }

    private List<CommandPaletteItem> PaletteItems()
    {
        var items = new List<CommandPaletteItem>();
        void Page(string id, string title, string detail)
        {
            if (PageUnavailable(id) == null) items.Add(new(title, detail, () => Navigate(id)));
        }

        foreach (var rail in RailItems)
        {
            if ((string)rail.Tag == "Agent")
            {
                Page("Approvals", Loc.Current["Shell.ApprovalsPill"], Loc.Current["Rail.Agent"]);
                Page("Calls", Loc.Current["Shell.Calls"], Loc.Current["Rail.Agent"]);
            }
            else if (rail == SettingsButton) Page("Settings", Loc.Current["Palette.Settings"], AutomationProperties.GetAcceleratorKey(rail));
            else Page((string)rail.Tag, (string)rail.Content, System.Windows.Automation.AutomationProperties.GetAcceleratorKey(rail));
        }
        Page("VersionControl", Loc.Current["Shell.VersionControl"], Loc.Current["Rail.Project"]);

        var engineering = _model.Engineering;
        bool LoadedPlc() => HasProject && PageUnavailable("Engineering") == null && engineering.SelectedDevice is { Category: "Plc" } device
            && engineering.LoadedDeviceId == device.Id;
        if (LoadedPlc())
        {
            foreach (var row in engineering.Blocks)
                items.Add(new(row.Name, row.Path == row.Name ? engineering.SelectedDevice!.Name : row.Path, () =>
                {
                    if (!LoadedPlc() || !engineering.Blocks.Contains(row)) return;
                    Navigate("Blocks");
                    engineering.BlockFilter = "";
                    BlocksContent.LocateControlBlock(row);
                }));
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

    private void CloseCommandPalette()
    {
        OverlayHost.Visibility = Visibility.Collapsed;
        _paletteFocus?.Dispose();
        _paletteFocus = null;
        OverlayHost.Children.Clear();
        QueueControlSnapshot();
    }

    partial void ToggleSessionPopover()
    {
        if (_sessionPopover != null) { CloseSessionPopover(); return; }
        if (ShellModalOpen) return;
        var popover = new SessionPopover(_model, NavigateFromSession, () => CloseSessionPopover());
        _sessionPopover = popover;
        popover.AiSettings.IsEnabled = PageUnavailable("Settings") == null;
        popover.SwitchVersion.IsEnabled = PageUnavailable("Mcp") == null;
        popover.Margin = new Thickness(12, 0, 0, Root.ActualHeight - SessionStatusButton.TranslatePoint(new Point(), Root).Y + 4);
        Root.Children.Add(popover);
        PreviewMouseDown += OnSessionOutsideClick;
        Deactivated += OnSessionDeactivated;
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnSessionKey), true);
        popover.LostKeyboardFocus += OnSessionFocusLeft;
        popover.Focusable = true;
        popover.Focus();
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
