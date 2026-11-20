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
    private FeaturePagesViewModel? _features;
    internal event EventHandler? ControlEnabledChanged;

    public SettingsView()
    {
        InitializeComponent();
        InitializeSafetyEditors();
        ShowSection("Appearance");
        VersionText.Text = Loc.Current.T("Common.WorkbenchVersion", MainViewModel.AppVersion).TrimStart('v');
        LoadPaths();
    }

    internal void Initialize(IApprovalService approvals, McpSessionPanel? session = null)
    {
        _approvals = approvals;
        approvals.PropertyChanged += OnApprovalChanged;
        UpdateApproval();
        ControlEnabled.IsChecked = App.Settings.WorkbenchControlEnabled;
    }

    internal void SetFeatures(FeaturePagesViewModel? features)
    {
        if (_features != null) _features.PropertyChanged -= OnFeatureChanged;
        _features = features;
        StorageTools.DataContext = features;
        if (features != null)
        {
            features.PropertyChanged += OnFeatureChanged;
            UpdateRetention();
        }
    }

    private void OnFeatureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FeaturePagesViewModel.RetentionSize) or nameof(FeaturePagesViewModel.RetentionCopies))
            UpdateRetention();
    }

    private void UpdateRetention()
    {
        if (_features == null) return;
        foreach (var button in new[] { Size5, Size10, Size50 })
            button.IsChecked = int.Parse((string)button.Tag, System.Globalization.CultureInfo.InvariantCulture) == _features.RetentionSize;
        foreach (var button in new[] { Copies3, Copies5, Copies10 })
            button.IsChecked = int.Parse((string)button.Tag, System.Globalization.CultureInfo.InvariantCulture) == _features.RetentionCopies;
    }

    internal void LoadPaths()
    {
        ConfigPath.Text = DataLocations.Current.ConfigDirectory;
        LogPath.Text = DataLocations.Current.LogsDirectory;
        DataPath.Text = DataLocations.Current.WorkbenchDataDirectory;
    }

    internal void ShowSection(string section)
    {
        SectionList.SelectedItem = section switch { "Service" => ServiceSection, "AI" => AiSection, _ => AppearanceSection };
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not ListBoxItem item) return;
        string section = (string)item.Tag;
        AppearancePanel.Visibility = section == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        ServicePanel.Visibility = section == "Service" ? Visibility.Visible : Visibility.Collapsed;
        AiPanel.Visibility = section == "AI" ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(section switch
            { "Service" => "Settings.Engine", "AI" => "Control.Section", _ => "Settings.Appearance" }));
        DetailScroll.ScrollToTop();
        if (section == "AI") _ = LoadSafetyAsync();
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

    internal void Dispose()
    {
        SetFeatures(null);
        if (_approvals != null) _approvals.PropertyChanged -= OnApprovalChanged; _approvals = null;
    }

    private void OnApprovalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(IApprovalService.Enabled) or nameof(IApprovalService.TimeoutSeconds)
            or nameof(IApprovalService.OnlinePlanLifetimeSeconds))) return;
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
        OnlinePlanLifetime.Text = _approvals.OnlinePlanLifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void OnApprovalClick(object sender, RoutedEventArgs e)
    {
        if (_approvals == null) return;
        bool target = sender == ApprovalOn;
        if (_approvals.Enabled != target) await SaveApproval(() => _approvals.Enabled = target);
        else UpdateApproval();
    }

    private async void OnTimeoutClick(object sender, RoutedEventArgs e)
    {
        int value = int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
        if (_approvals != null) await SaveApproval(() => _approvals.TimeoutSeconds = value);
    }

    private async Task SaveApproval(Action action)
    {
        try { await Task.Run(action); }
        catch (Exception ex) {
            Controls.WorkbenchMessageBox
                .Show(Window.GetWindow(this), ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateApproval();
    }

    private void OnSize(object sender, RoutedEventArgs e) => _features?
        .SetRetention(int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture), true);

    private void OnCopies(object sender, RoutedEventArgs e) => _features?
        .SetRetention(int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture), false);

    private void OnRetentionFolder(object sender, RoutedEventArgs e) { if (_features != null) _features.Run(_features.Audit.OpenFolder); }
    private void OnGenerateDiagnostics(object sender, RoutedEventArgs e) => _features?.Export();
    private void OnOpenDiagnosticsFolder(object sender, RoutedEventArgs e) { if (_features != null) _features.Run(_features.Diagnostics.OpenFolder); }

    private void OnControlEnabledClick(object sender, RoutedEventArgs e)
    {
        App.Settings.WorkbenchControlEnabled = ControlEnabled.IsChecked == true;
        App.Settings.Save();
        ControlEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenPath(object sender, RoutedEventArgs e)
    {
        string path = sender == OpenConfig ? ConfigPath.Text : sender == OpenLog ? LogPath.Text : DataPath.Text;
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(Loc.Current.T("Settings.PathMissing", path));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Controls.WorkbenchMessageBox
                .Show(Window.GetWindow(this), ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
