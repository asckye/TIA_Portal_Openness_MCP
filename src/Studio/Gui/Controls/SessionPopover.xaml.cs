using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TiaMcp.Versioning;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Controls;

public partial class SessionPopover : UserControl
{
    private readonly MainViewModel _model;
    private readonly Action _openMcp;
    private readonly Action _close;
    private readonly Func<string, bool> _installed;

    internal SessionPopover(MainViewModel model, Action openMcp, Action close, Func<string, bool> installed)
    {
        _model = model;
        _openMcp = openMcp;
        _close = close;
        _installed = installed;
        InitializeComponent();
        SetHeading(model.HasMcpPanel);
        DataContext = model;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _model.Session.PropertyChanged += OnChanged;
        _model.Activity.PropertyChanged += OnChanged;
        if (_model.Panel is { } panel)
        {
            panel.PropertyChanged += OnChanged;
            panel.Sessions.CollectionChanged += OnSessionsChanged;
        }
        Loc.Current.LanguageChanged += OnLanguageChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _model.Session.PropertyChanged -= OnChanged;
        _model.Activity.PropertyChanged -= OnChanged;
        if (_model.Panel is { } panel)
        {
            panel.PropertyChanged -= OnChanged;
            panel.Sessions.CollectionChanged -= OnSessionsChanged;
        }
        Loc.Current.LanguageChanged -= OnLanguageChanged;
    }

    // The popover's accessible name follows its visible heading; set in code because an ElementName binding on the root
    // element cannot see its own children.
    private void SetHeading(bool shared)
    {
        SessionHeading.Text = shared ? Loc.Current["Overlay.SharedSession"] : Loc.Current["Overlay.Session"];
        System.Windows.Automation.AutomationProperties.SetName(this, SessionHeading.Text);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        SessionList.Items.Refresh();
        Refresh();
    }
    private void OnSessionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => SessionCount.Text = Loc.Current.T("Overlay.Sessions", _model.Panel!.Sessions.Count);
    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var session = _model.Session;
        var panel = _model.Panel;
        var release = TiaVersionCatalog.Get(session.SelectedReleaseKey);
        if (panel != null && panel.Sessions.Any(row => row.Host.ReleaseKey != release.Key))
        {
            panel.Sessions.Clear();
            panel.Selected = null;
        }
        SetHeading(_model.HasMcpPanel);
        // Both the bridge and the shared worker use the release's Openness owner apartment.
        VersionMenuButton.Visibility = _model.HasMcpPanel && session.CanSelectRelease ? Visibility.Visible : Visibility.Collapsed;
        Release.Text = release.DisplayName + " · " + release.OwnerApartment;
        bool lost = !session.IsConnected && session.OpenProjectFile.Length > 0;
        bool busy = _model.Activity.Busy || panel?.WorkerBusy == true;
        SessionState.Text = session.IsConnected ? (busy ? "BUSY" : "IDLE") : lost ? "CRASHED" : Loc.Current["Shell.Disconnected"];
        ConnectedTag.ToolTip = session.IsConnected ? Loc.Current["Overlay.Connected"] : SessionState.Text;
        ConnectedTag.SetResourceReference(StyleProperty, session.IsConnected ? (busy ? "Primer.Tag.Warn" : "Primer.Tag.Ok") : "Primer.Tag.Err");
        Discovery.Visibility = Attach.Visibility = Create.Visibility = Detach.Visibility = _model.HasMcpPanel ? Visibility.Visible : Visibility.Collapsed;
        SessionCount.Text = Loc.Current.T("Overlay.Sessions", panel?.Sessions.Count ?? 0);
    }

    internal ProjectSessionDetails? Details { get; set; }
    private void OnSessionDetails(object sender, RoutedEventArgs e)
        => SessionDetails.Content = SessionDetails.Content == null ? Details : null;

    private void OnVersionMenu(object sender, RoutedEventArgs e)
    {
        var menu = VersionMenuButton.ContextMenu;
        menu.PlacementTarget = VersionMenuButton;
        menu.IsOpen = true;
    }

    private void OnReleaseMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        foreach (var release in _model.Releases.Where(release => _installed(release.Key)))
        {
            var item = new MenuItem { Header = release.DisplayName + " · " + release.OwnerApartment };
            item.Click += (_, _) =>
            {
                if (!_model.CanSelectRelease || _model.SelectedReleaseKey == release.Key) return;
                _model.SelectedReleaseKey = release.Key;
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = Loc.Current["Overlay.SwitchVersion"] };
        manage.Click += OnSwitchVersion;
        menu.Items.Add(manage);
    }

    private void OnSwitchVersion(object sender, RoutedEventArgs e) => _openMcp();
    private void OnSessionAction(object sender, RoutedEventArgs e) => _close();
}
