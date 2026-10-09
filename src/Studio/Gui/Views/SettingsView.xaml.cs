using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Shared;

namespace TiaOpenness.Gui.Views;

public partial class SettingsView : UserControl
{
    private IApprovalService? _approvals;
    internal event EventHandler? DisableApprovalRequested;
    internal event EventHandler? ControlEnabledChanged;

    public SettingsView()
    {
        InitializeComponent();
        VersionText.Text = "TIA Workbench " + MainViewModel.AppVersion.TrimStart('v');
    }

    internal void Initialize(IApprovalService approvals)
    {
        _approvals = approvals;
        approvals.PropertyChanged += OnApprovalChanged;
        UpdateApproval();
        ControlEnabled.IsChecked = App.Settings.WorkbenchControlEnabled;
    }

    internal void LoadPaths()
    {
        ConfigPath.Text = DataLocations.Current.ConfigDirectory;
        LogPath.Text = DataLocations.Current.LogsDirectory;
    }

    internal static string ReadPackageName(string bundleRoot)
    {
        string path = Path.Combine(bundleRoot, "manifest", "package-manifest.json");
        if (!File.Exists(path)) return "";
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && json.RootElement.TryGetProperty("packageName", out var name)
                && name.ValueKind == System.Text.Json.JsonValueKind.String ? name.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Trace.TraceWarning("Unable to read the delivery package name: {0}", ex.Message);
            return "";
        }
    }

    internal void Dispose() { if (_approvals != null) _approvals.PropertyChanged -= OnApprovalChanged; _approvals = null; }
    private void OnApprovalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(IApprovalService.Enabled) or nameof(IApprovalService.TimeoutSeconds))) return;
        if (Dispatcher.CheckAccess()) UpdateApproval();
        else Dispatcher.BeginInvoke(UpdateApproval);
    }
    internal void UpdateApproval()
    {
        if (_approvals == null) return;
        ApprovalOn.IsChecked = _approvals.Enabled;
        ApprovalOff.IsChecked = !_approvals.Enabled;
        Timeout60.IsChecked = _approvals.TimeoutSeconds == 60;
        Timeout120.IsChecked = _approvals.TimeoutSeconds == 120;
        Timeout300.IsChecked = _approvals.TimeoutSeconds == 300;
    }

    private async void OnApprovalClick(object sender, RoutedEventArgs e)
    {
        if (_approvals == null) return;
        if (sender == ApprovalOn) await SaveApproval(() => _approvals.Enabled = true);
        else if (_approvals.Enabled) DisableApprovalRequested?.Invoke(this, EventArgs.Empty);
        UpdateApproval();
    }

    private async void OnTimeoutClick(object sender, RoutedEventArgs e)
    {
        int value = int.Parse((string)((RadioButton)sender).Tag);
        if (_approvals != null) await SaveApproval(() => _approvals.TimeoutSeconds = value);
    }

    private async Task SaveApproval(Action action)
    {
        try { await Task.Run(action); }
        catch (Exception ex) { Controls.WorkbenchMessageBox.Show(Window.GetWindow(this), ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error); }
        UpdateApproval();
    }

    private void OnControlEnabledClick(object sender, RoutedEventArgs e)
    {
        App.Settings.WorkbenchControlEnabled = ControlEnabled.IsChecked == true;
        App.Settings.Save();
        ControlEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenPath(object sender, RoutedEventArgs e)
    {
        string path = sender == OpenConfig ? ConfigPath.Text : LogPath.Text;
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(Loc.Current.T("Settings.PathMissing", path));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Controls.WorkbenchMessageBox.Show(Window.GetWindow(this), ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
