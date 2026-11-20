using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TiaMcp.Versioning;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui.Controls;

public partial class SessionPopover : UserControl
{
    private readonly MainViewModel _model;
    private readonly Action<string> _navigate;
    private readonly Action _close;

    internal SessionPopover(MainViewModel model, Action<string> navigate, Action close)
    {
        _model = model;
        _navigate = navigate;
        _close = close;
        InitializeComponent();
        SetHeading(model.Panel?.Attached == true);
        DataContext = model;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _model.Session.PropertyChanged += OnChanged;
        _model.Engineering.PropertyChanged += OnChanged;
        if (_model.Panel != null) _model.Panel.PropertyChanged += OnChanged;
        Loc.Current.LanguageChanged += OnLanguageChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _model.Session.PropertyChanged -= OnChanged;
        _model.Engineering.PropertyChanged -= OnChanged;
        if (_model.Panel != null) _model.Panel.PropertyChanged -= OnChanged;
        Loc.Current.LanguageChanged -= OnLanguageChanged;
    }

    // The popover's accessible name follows its visible heading; set in code because an ElementName binding on the root
    // element cannot see its own children.
    private void SetHeading(bool shared)
    {
        SessionHeading.Text = shared ? Loc.Current["Overlay.SharedSession"] : Loc.Current["Overlay.Session"];
        System.Windows.Automation.AutomationProperties.SetName(this, SessionHeading.Text);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();
    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var session = _model.Session;
        var panel = _model.Panel;
        var release = TiaVersionCatalog.Get(session.SelectedReleaseKey);
        SetHeading(panel?.Attached == true);
        // Both the bridge and the shared worker use the release's Openness owner apartment.
        Release.Text = session.IsConnected ? release.DisplayName + " · " + release.OwnerApartment : "—";
        ConnectedTag.Visibility = session.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        string projectName = Path.GetFileName(session.OpenProjectFile) is { Length: > 0 } file ? file : session.ProjectName;
        Project.Text = session.IsConnected && projectName.Length > 0 ? projectName : "—";
        var device = _model.Engineering.SelectedDevice;
        Plc.Text = "—";
        if (session.IsConnected && device is { Category: "Plc" })
        {
            Plc.Text = string.IsNullOrEmpty(device.DisplayName) ? device.Name : device.DisplayName;
            if (!string.IsNullOrEmpty(device.TypeName)) Plc.Text += " · " + device.TypeName;
        }
        Client.Text = panel is { Attached: true, ClientStatus.Length: > 0 } ? panel.ClientStatus : "—";
    }

    private void OnAiSettings(object sender, RoutedEventArgs e) => _navigate("Settings");
    private void OnSwitchVersion(object sender, RoutedEventArgs e) => _navigate("Mcp");
    private void OnDisconnect(object sender, RoutedEventArgs e) => _close();
}
