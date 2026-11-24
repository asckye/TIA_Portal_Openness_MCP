using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        foreach (var (key, button) in new[] { ("D", OpenLog), ("O", OpenExports), ("G", SettingsGenerateButton) })
        {
            button.Loaded += (_, _) => AccessKeyManager.Register(key, button);
            button.Unloaded += (_, _) => AccessKeyManager.Unregister(key, button);
        }
        ShowSection("Appearance");
        SupportedReleases.Text = string.Join(" · ", TiaMcp.Versioning.TiaVersionCatalog.Runnable.Select(release => release.DisplayName));
        IsVisibleChanged += (_, _) => RefreshVisibleSection();
        VersionText.Text = MainViewModel.AppVersion;
        RuntimeText.Text = $".NET {Environment.Version.Major} WPF";
        VersionText.ToolTip = Loc.Current.T("Common.WorkbenchVersion", MainViewModel.AppVersion);
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
            if (ServiceSection.IsSelected) RefreshVisibleSection();
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
        ExportsPath.Text = Services.DiagnosticBundleService.OutputDirectory(DataLocations.Current.WorkbenchDataDirectory);
        DataPath.Text = DataLocations.Current.WorkbenchDataDirectory;
    }

    internal void ShowSection(string section)
    {
        SectionList.SelectedItem = section switch { "Service" => ServiceSection, "AI" => AiSection, "About" => AboutSection, _ => AppearanceSection };
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not ListBoxItem item) return;
        string section = (string)item.Tag;
        AppearancePanel.Visibility = section == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        ServicePanel.Visibility = section == "Service" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = section == "About" ? Visibility.Visible : Visibility.Collapsed;
        AiPanel.Visibility = section == "AI" ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(section switch
            { "About" => "Settings.AboutTitle", "Service" => "Settings.StorageTitle", "AI" => "Settings.SafetyTitle", _ => "Settings.AppearanceTitle" }));
        SectionSubtitle.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(section switch
            { "About" => "Settings.AboutSubtitle", "Service" => "Settings.StorageSubtitle",
              "AI" => "Settings.SafetySubtitle", _ => "Settings.AppearanceSubtitle" }));
        DetailScroll.ScrollToTop();
        RefreshVisibleSection();
    }

    private void RefreshVisibleSection()
    {
        if (!IsVisible) return;
        if (AiSection.IsSelected) _ = LoadSafetyAsync();
        else if (ServiceSection.IsSelected && _features?.Audit is Services.AuditLogService audit) _ = audit.RefreshAsync();
    }

    internal void SetUpdateAvailability(bool hasInstalledVersion, bool sourceCheckout)
    {
        CheckUpdate.IsEnabled = hasInstalledVersion && !sourceCheckout;
        SourceCheckoutHint.Visibility = sourceCheckout ? Visibility.Visible : Visibility.Collapsed;
        UpdateStateItem.Visibility = sourceCheckout ? Visibility.Collapsed : Visibility.Visible;
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
        TimeoutCustom.IsChecked = _approvals.TimeoutSeconds is not (60 or 120 or 300);
        CustomTimeout.Text = _approvals.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Plan5.IsChecked = _approvals.OnlinePlanLifetimeSeconds == 300;
        Plan15.IsChecked = _approvals.OnlinePlanLifetimeSeconds == 900;
        Plan30.IsChecked = _approvals.OnlinePlanLifetimeSeconds == 1800;
        PlanCustom.IsChecked = _approvals.OnlinePlanLifetimeSeconds is not (300 or 900 or 1800);
        OnlinePlanLifetime.Text = _approvals.OnlinePlanLifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void OnPolicySwitchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || _approvals == null) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        bool target = !_approvals.Enabled;
        await SaveApproval(() => _approvals.Enabled = target);
        if (AiPanel.IsKeyboardFocusWithin) (_approvals.Enabled ? ApprovalOn : ApprovalOff).Focus();
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

    private async void OnPlanPreset(object sender, RoutedEventArgs e)
    {
        int seconds = int.Parse((string)((RadioButton)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
        if (_approvals != null) await SaveApproval(() => _approvals.OnlinePlanLifetimeSeconds = seconds);
    }

    private async void OnSaveTimeout(object sender, RoutedEventArgs e)
    {
        if (_approvals == null) return;
        if (!int.TryParse(CustomTimeout.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds)
            || seconds is < 1 or > 3600) { ShowSafetyError("Settings.TimeoutInvalid"); return; }
        await SaveApproval(() => _approvals.TimeoutSeconds = seconds);
    }

    internal async Task SaveApproval(Action action, ToastKind kind = ToastKind.Success, string message = "Toast.Saved")
    {
        try { await Task.Run(action); NotifySettings(kind, LocalizedText.Key(message)); }
        catch (Exception ex) {
            NotifySettings(ToastKind.Error, LocalizedText.Literal(ex.Message));
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
        if (App.Settings.Save() is { } error)
        {
            App.Settings.WorkbenchControlEnabled = false;
            ControlEnabled.IsChecked = false;
            NotifySettings(ToastKind.Error, LocalizedText.Literal(error));
        }
        ControlEnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) _features?.Copy(path);
    }

    private void OnOpenPath(object sender, RoutedEventArgs e)
    {
        string path = sender == OpenConfig ? ConfigPath.Text : sender == OpenLog ? LogPath.Text : sender == OpenExports ? ExportsPath.Text : DataPath.Text;
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(Loc.Current.T("Settings.PathMissing", path));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            NotifySettings(ToastKind.Error, LocalizedText.Literal(ex.Message));
        }
    }
}
