using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    internal IApprovalService Approvals { get; private set; } = null!;
    private IDiagnosticBundleService _diagnostics = null!;
    private string _page = "Engineering";
    private string? _drawer;
    private string? _toastKey;
    private bool HasProject => _model.Session.IsConnected && !string.IsNullOrEmpty(_model.Session.ProjectName);

    private void InitializeShell(IApprovalService? approvals, IDiagnosticBundleService? diagnostics)
    {
        Approvals = approvals ?? new ApprovalServiceStub();
        _diagnostics = diagnostics ?? new DiagnosticBundleServiceStub();
        SettingsContent.Initialize(Approvals);
        SettingsContent.DisableApprovalRequested += OnDisableApprovalRequested;
        OperationsContent.EnvironmentRequested += OnEnvironmentRequested;
        Approvals.PropertyChanged += OnApprovalChanged;
        _model.Session.PropertyChanged += OnShellSessionChanged;
        _model.Engineering.PropertyChanged += OnShellSessionChanged;
        Loc.Current.LanguageChanged += OnShellLanguageChanged;
        UpdateShell();
        Navigate("Engineering");
    }

    private void DisposeShell()
    {
        SettingsContent.Dispose();
        SettingsContent.DisableApprovalRequested -= OnDisableApprovalRequested;
        OperationsContent.EnvironmentRequested -= OnEnvironmentRequested;
        Approvals.PropertyChanged -= OnApprovalChanged;
        _model.Session.PropertyChanged -= OnShellSessionChanged;
        _model.Engineering.PropertyChanged -= OnShellSessionChanged;
        Loc.Current.LanguageChanged -= OnShellLanguageChanged;
    }

    private void OnApprovalChanged(object? sender, PropertyChangedEventArgs e) => UpdateShell();
    private void OnEnvironmentRequested(object? sender, EventArgs e) => NavigateGuarded("Environment");
    private void OnShellSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!HasProject && _page is "Blocks" or "VersionControl") Navigate("Engineering");
        UpdateShell();
        UpdateMcpStatus();
    }
    private void OnShellLanguageChanged(object? sender, EventArgs e) => UpdateShell();
    private void UpdateShell()
    {
        PendingBadge.Content = Loc.Current.T("Shell.Pending", Approvals.PendingCount);
        PendingBadge.Visibility = Approvals.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApprovalOffPill.Visibility = Approvals.Enabled ? Visibility.Collapsed : Visibility.Visible;
        RailBlocks.IsEnabled = RailVersionControl.IsEnabled = HasProject;
        RailVersion.Text = ViewModels.MainViewModel.AppVersion.TrimStart('v') + " · " + TiaMcp.Versioning.TiaVersionCatalog.Get(_model.SelectedReleaseKey).DisplayName;
        if (_drawer != null) DrawerTitle.Text = Loc.Current[_drawer == "Settings" ? "Shell.Settings" : "Shell.PendingTitle"];
        if (_page is "Calls" or "Audit" or "Environment") UpdateEmptyPage();
        if (_toastKey != null) ToastMessage.Text = Loc.Current[_toastKey];
        ToastTitle.Text = Loc.Current["Shell.Diagnostics"];
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateEmptyPage()
    {
        EmptyPageTitle.Text = Loc.Current["Shell." + _page];
        EmptyPageMessage.Text = Loc.Current["Shell." + _page + "Empty"];
    }

    internal void Navigate(string page)
    {
        if (page is "Blocks" or "VersionControl" && !HasProject) return;
        if (page == "Mcp") EnsureConfiguration();
        _page = page;
        OperationsContent.HideOptions();
        BlocksContent.HideOptions();
        VersionControlContent.HideOptions();
        OperationsContent.Visibility = page == "Engineering" ? Visibility.Visible : Visibility.Collapsed;
        BlocksContent.Visibility = page == "Blocks" ? Visibility.Visible : Visibility.Collapsed;
        EngineeringSideContent.Visibility = page is "Engineering" or "Blocks" ? Visibility.Visible : Visibility.Collapsed;
        VersionControlContent.Visibility = page == "VersionControl" ? Visibility.Visible : Visibility.Collapsed;
        LogContent.Visibility = page == "Log" ? Visibility.Visible : Visibility.Collapsed;
        _model.IsConfigurationPage = page == "Mcp";
        ConfigurationHost.Visibility = page == "Mcp" ? Visibility.Visible : Visibility.Collapsed;
        EngineeringPage.Visibility = page is "Engineering" or "Blocks" or "VersionControl" or "Log" ? Visibility.Visible : Visibility.Collapsed;
        EmptyPage.Visibility = page is "Calls" or "Audit" or "Environment" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "VersionControl") _model.IsVcTab = true;
        else if (page == "Log") _model.IsLogTab = true;
        else if (page is "Engineering" or "Blocks") _model.IsBlocksTab = true;
        ((RadioButton)FindName("Rail" + page)).IsChecked = true;
        UpdateShell();
    }

    private void OnRailClick(object sender, RoutedEventArgs e) => NavigateGuarded((string)((RadioButton)sender).Tag);
    private void OnNavigateExecuted(object sender, ExecutedRoutedEventArgs e) => NavigateGuarded((string)e.Parameter);
    private void OnCanNavigate(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = e.Parameter is not ("Blocks" or "VersionControl") || HasProject;
    private void NavigateGuarded(string page)
    {
        try { Navigate(page); }
        catch (Exception ex) { Controls.GlassMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    internal void OpenSettings()
    {
        EnsureConfiguration();
        SettingsContent.LoadPaths();
        OpenDrawer("Settings");
    }

    private void OpenDrawer(string name)
    {
        _drawer = name;
        DrawerPanel.Width = name == "Settings" ? 400 : 440;
        SettingsContent.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        ApprovalsContent.Visibility = name == "Approvals" ? Visibility.Visible : Visibility.Collapsed;
        DrawerOverlay.Visibility = Visibility.Visible;
        SettingsButton.SetResourceReference(Control.BackgroundProperty, name == "Settings" ? "Ui.Accent" : "Ui.ControlBackground");
        SettingsButton.SetResourceReference(Control.ForegroundProperty, name == "Settings" ? "Ui.OnAccent" : "Ui.Label");
        UpdateShell();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_drawer == "Settings") { CloseDrawer(); return; }
        try { OpenSettings(); }
        catch (Exception ex) { Controls.GlassMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void OnApprovals(object sender, RoutedEventArgs e) => OpenDrawer("Approvals");
    private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseDrawer();
    private void OnDrawerBackdrop(object sender, MouseButtonEventArgs e) => CloseDrawer();
    private void CloseDrawer()
    {
        _drawer = null;
        DrawerOverlay.Visibility = Visibility.Collapsed;
        SettingsButton.SetResourceReference(Control.BackgroundProperty, "Ui.ControlBackground");
        SettingsButton.SetResourceReference(Control.ForegroundProperty, "Ui.Label");
    }
    private void OnDisableApprovalRequested(object? sender, EventArgs e) => ConfirmOverlay.Visibility = Visibility.Visible;
    private void OnCancelApprovalOff(object sender, RoutedEventArgs e) { ConfirmOverlay.Visibility = Visibility.Collapsed; SettingsContent.UpdateApproval(); }
    private void OnConfirmApprovalOff(object sender, RoutedEventArgs e) { Approvals.Enabled = false; ConfirmOverlay.Visibility = Visibility.Collapsed; }
    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        _toastKey = _diagnostics.Export();
        UpdateShell();
        Toast.Visibility = Visibility.Visible;
    }
    private void OnDismissToast(object sender, RoutedEventArgs e) { Toast.Visibility = Visibility.Collapsed; e.Handled = true; }
    private void OnToastClick(object sender, MouseButtonEventArgs e) { Toast.Visibility = Visibility.Collapsed; OpenDrawer("Approvals"); }
}
