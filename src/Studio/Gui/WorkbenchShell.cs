using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Windows.Threading;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    internal IApprovalService Approvals { get; private set; } = null!;
    private IDiagnosticBundleService _diagnostics = null!;
    private string _page = "Engineering";
    private string? _drawer;
    private bool _shellQueued;
    private Controls.ModalPane? _drawerFocus, _confirmFocus;
    private bool HasProject => _model.Session.IsConnected && !string.IsNullOrEmpty(_model.Session.ProjectName);

    private void InitializeShell(IApprovalService? approvals, IDiagnosticBundleService? diagnostics)
    {
        Approvals = approvals ?? (_model.OfflineDemo ? new ApprovalServiceStub() : new Services.ApprovalService());
        _diagnostics = diagnostics ?? new Services.DiagnosticBundleService();
        InitializeFeaturePages();
        SettingsContent.Initialize(Approvals);
        SettingsContent.DisableApprovalRequested += OnDisableApprovalRequested;
        EngineeringSideContent.EnvironmentRequested += OnEnvironmentRequested;
        EngineeringSideContent.InspectionRulesRequested += OnInspectionRulesRequested;
        BlocksContent.TransferOptionsRequested += OnTransferOptionsRequested;
        Approvals.PropertyChanged += OnApprovalChanged;
        _model.Session.PropertyChanged += OnShellSessionChanged;
        _model.Engineering.PropertyChanged += OnShellSessionChanged;
        Loc.Current.LanguageChanged += OnShellLanguageChanged;
        Root.SizeChanged += OnShellSizeChanged;
        UpdateLanguage();
        UpdateShell();
        Navigate("Engineering");
        InitializeControl();
    }

    private void DisposeShell()
    {
        _confirmFocus?.Dispose();
        _drawerFocus?.Dispose();
        DisposeControl();
        DisposeFeaturePages();
        SettingsContent.Dispose();
        SettingsContent.DisableApprovalRequested -= OnDisableApprovalRequested;
        EngineeringSideContent.EnvironmentRequested -= OnEnvironmentRequested;
        EngineeringSideContent.InspectionRulesRequested -= OnInspectionRulesRequested;
        BlocksContent.TransferOptionsRequested -= OnTransferOptionsRequested;
        Root.SizeChanged -= OnShellSizeChanged;
        Approvals.PropertyChanged -= OnApprovalChanged;
        (Approvals as IDisposable)?.Dispose();
        _model.Session.PropertyChanged -= OnShellSessionChanged;
        _model.Engineering.PropertyChanged -= OnShellSessionChanged;
        Loc.Current.LanguageChanged -= OnShellLanguageChanged;
    }

    private void OnApprovalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(IApprovalService.Enabled) or nameof(IApprovalService.PendingCount))) return;
        if (_shellQueued) return;
        _shellQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _shellQueued = false; UpdateShell(); }));
    }
    private void OnEnvironmentRequested(object? sender, EventArgs e) => NavigateGuarded("Environment");
    private void OnInspectionRulesRequested(object? sender, EventArgs e) => OperationsContent.ShowControlInspectionRules();
    private void OnTransferOptionsRequested(object? sender, EventArgs e) => OperationsContent.ShowTransferOptions();

    // Ant Sider: collapsed to its icons below the lg breakpoint (992 px) and expanded above it; the trigger
    // overrides that until the width crosses the breakpoint again. The header status gives way below 860 px.
    // Widths are those of the shell content, so a detached render host follows the same rules.
    private bool? _siderCollapsed;
    private bool _belowBreakpoint;
    private double ShellWidth => Root.ActualWidth;
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool below = ShellWidth > 0 && ShellWidth < 992;
        if (below != _belowBreakpoint) { _belowBreakpoint = below; _siderCollapsed = null; }
        UpdateSider();
    }
    private void OnSiderToggle(object sender, RoutedEventArgs e) { _siderCollapsed = !(_siderCollapsed ?? _belowBreakpoint); UpdateSider(); }
    private void UpdateSider()
    {
        bool compact = _siderCollapsed ?? _belowBreakpoint;
        Sider.Width = compact ? 56 : 216;
        Controls.Ux.SetCompact(Sider, compact);
        BrandTitle.Visibility = BrandVersion.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        string toggle = compact ? "Shell.ExpandMenu" : "Shell.CollapseMenu";
        SiderToggle.SetBinding(ContentControl.ContentProperty, TrExtension.CreateBinding(toggle));
        SiderToggle.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, TrExtension.CreateBinding(toggle));
        SiderToggle.SetResourceReference(Controls.Ux.IconProperty, compact ? "Icon.MenuUnfold" : "Icon.MenuFold");
        StatusInformation.Visibility = ShellWidth > 0 && ShellWidth < 860 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnLanguage(object sender, RoutedEventArgs e)
        => Loc.Current.Language = Loc.Current.IsChinese ? AppLanguage.English : AppLanguage.Chinese;
    private void OnShellSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!HasProject && _page is "Blocks" or "VersionControl") Navigate("Engineering");
        UpdateShell();
        UpdateMcpStatus();
        CommandManager.InvalidateRequerySuggested();
    }
    private void OnShellLanguageChanged(object? sender, EventArgs e) { UpdateLanguage(); UpdateShell(); }
    private void UpdateLanguage() => Language = System.Windows.Markup.XmlLanguage.GetLanguage(
        Loc.Current.Language == AppLanguage.Chinese ? "zh-CN" : "en-US");
    private void UpdateShell()
    {
        if (_featuresDisposed) return;
        PendingBadge.Content = Loc.Current.T("Shell.Pending", Approvals.PendingCount);
        System.Windows.Automation.AutomationProperties.SetName(PendingBadge, (string)PendingBadge.Content);
        PendingBadge.Visibility = Approvals.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        var approvalVisibility = Approvals.Enabled ? Visibility.Collapsed : Visibility.Visible;
        if (ApprovalOffPill.Visibility != approvalVisibility && approvalVisibility == Visibility.Visible)
            Controls.AccessibleStatus.Announce(ApprovalOffText);
        ApprovalOffPill.Visibility = approvalVisibility;
        UpdateMcpStatus();
        RailBlocks.IsEnabled = RailVersionControl.IsEnabled = HasProject;
        RailVersionControl.Visibility = _model.VersionControlPageVisible ? Visibility.Visible : Visibility.Collapsed;
        RailMcp.Visibility = RailEngineering.Visibility = _model.OfflineDemo ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Visibility = _model.OfflineDemo ? Visibility.Collapsed : Visibility.Visible;
        RailVersion.Text = ViewModels.MainViewModel.AppVersion.TrimStart('v');
        BlocksContent.Visibility = _page is "Engineering" or "Blocks" && HasProject ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbGroup.Text = Loc.Current[_page switch
        {
            "Engineering" or "Blocks" or "VersionControl" => "Shell.GroupEngineering", "Calls" => "Shell.GroupAi",
            "Audit" or "Environment" or "Log" => "Shell.GroupOperations", _ => "Shell.GroupConnection",
        }];
        CurrentPageTitle.Text = Loc.Current[_page switch
        {
            "Mcp" => "Desktop.Configuration", "VersionControl" => "Shell.VersionControl",
            "Calls" => "Shell.Calls", "Audit" => "Shell.Audit", "Environment" => "Shell.Environment", "Log" => "Tab.Log",
            _ => "Shell.Engineering",
        }];
        if (_drawer != null)
        {
            DrawerTitle.Text = _drawer == "CallDetail" ? Features.SelectedCall?.Tool ?? Loc.Current["Calls.Detail"]
                : Loc.Current[_drawer == "Settings" ? "Shell.Settings" : "Shell.PendingTitle"];
            DrawerTitle.SetResourceReference(TextBlock.FontFamilyProperty, _drawer == "CallDetail" ? "Ui.FontMono" : "Ui.Font");
        }
        Features.Release = _model.SelectedReleaseKey;
        UpdateFeatureToast();

    }

    internal void Navigate(string page)
    {
        if (_model.OfflineDemo && page is not ("Calls" or "Audit" or "Environment" or "Log")) page = "Environment";
        if (page == "VersionControl" && !_model.VersionControlPageVisible) return;
        if (page is "Blocks" or "VersionControl" && !HasProject) return;
        if (page == "Mcp") EnsureConfiguration();
        _page = page;
        Features.SetVisibility(page == "Calls", page == "Audit", _drawer == "Approvals", _drawer == "CallDetail");
        OperationsContent.HideOptions();
        BlocksContent.HideOptions();
        VersionControlContent.HideOptions();
        // 项目 merges the overview and the program blocks: both keys show the same page.
        OperationsContent.Visibility = page is "Engineering" or "Blocks" ? Visibility.Visible : Visibility.Collapsed;
        BlocksContent.Visibility = page is "Engineering" or "Blocks" && HasProject ? Visibility.Visible : Visibility.Collapsed;
        EngineeringSideContent.Visibility = page is "Engineering" or "Blocks" ? Visibility.Visible : Visibility.Collapsed;
        VersionControlContent.Visibility = page == "VersionControl" ? Visibility.Visible : Visibility.Collapsed;
        LogContent.Visibility = page == "Log" ? Visibility.Visible : Visibility.Collapsed;
        _model.IsConfigurationPage = page == "Mcp";
        ConfigurationHost.Visibility = page == "Mcp" ? Visibility.Visible : Visibility.Collapsed;
        EngineeringPage.Visibility = page is "Engineering" or "Blocks" or "VersionControl" or "Log" ? Visibility.Visible : Visibility.Collapsed;
        CallsContent.Visibility = page == "Calls" ? Visibility.Visible : Visibility.Collapsed;
        AuditContent.Visibility = page == "Audit" ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentContent.Visibility = page == "Environment" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "VersionControl") _model.IsVcTab = true;
        else if (page == "Log") _model.IsLogTab = true;
        else if (page is "Engineering" or "Blocks") _model.IsBlocksTab = true;
        QueueControlSnapshot();
        ((RadioButton)FindName("Rail" + (page is "Blocks" or "VersionControl" ? "Engineering" : page))).IsChecked = true;
        if (page is "Engineering" or "Blocks" or "VersionControl")
            ((RadioButton)FindName("Rail" + (page == "Engineering" ? "Overview" : page))).IsChecked = true;
        UpdateShell();
    }

    private void OnRailClick(object sender, RoutedEventArgs e) => NavigateGuarded((string)((RadioButton)sender).Tag);
    private void OnNavigateExecuted(object sender, ExecutedRoutedEventArgs e) => NavigateGuarded((string)e.Parameter);
    private void OnCanNavigate(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = e.Parameter is not ("Blocks" or "VersionControl") || HasProject;
    private void NavigateGuarded(string page)
    {
        try { Navigate(page); }
        catch (Exception ex) { Controls.WorkbenchMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    internal void OpenSettings()
    {
        if (_model.OfflineDemo) return;
        EnsureConfiguration();
        SettingsContent.LoadPaths();
        OpenDrawer("Settings");
    }

    private void OpenDrawer(string name)
    {
        _drawerFocus?.Dispose();
        _drawer = name;
        Features.SetVisibility(_page == "Calls", _page == "Audit", name == "Approvals", name == "CallDetail");
        DrawerCount.Visibility = name == "Approvals" ? Visibility.Visible : Visibility.Collapsed;
        DrawerCallResult.Visibility = name == "CallDetail" ? Visibility.Visible : Visibility.Collapsed;
        DrawerPanel.Width = name switch { "Settings" => 400, "Approvals" => 480, _ => 520 };
        SettingsContent.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        ApprovalsContent.Visibility = name == "Approvals" ? Visibility.Visible : Visibility.Collapsed;
        CallDetailContent.Visibility = name == "CallDetail" ? Visibility.Visible : Visibility.Collapsed;
        DrawerOverlay.Visibility = Visibility.Visible;
        MarkSettingsOpen(name == "Settings");
        UpdateShell();
        _drawerFocus = new Controls.ModalPane(DrawerOverlay, DrawerTitle, CloseDrawer);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_drawer == "Settings") { CloseDrawer(); return; }
        try { OpenSettings(); }
        catch (Exception ex) { Controls.WorkbenchMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void OnApprovals(object sender, RoutedEventArgs e) => OpenDrawer("Approvals");
    private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseDrawer();
    private void OnDrawerBackdrop(object sender, MouseButtonEventArgs e) => CloseDrawer();
    private void CloseDrawer()
    {
        _drawer = null;
        Features.SetVisibility(_page == "Calls", _page == "Audit", false);
        DrawerOverlay.Visibility = Visibility.Collapsed;
        _drawerFocus?.Dispose(); _drawerFocus = null;
        QueueControlSnapshot();
        MarkSettingsOpen(false);
    }
    // While its drawer is open the settings entry is drawn as the selected menu item, through a complete
    // style so its hover and disabled states stay consistent.
    private void MarkSettingsOpen(bool open)
        => SettingsButton.SetResourceReference(StyleProperty, open ? "Primer.MenuButton.Selected" : "Primer.MenuButton");
    private void OnDisableApprovalRequested(object? sender, EventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Visible;
        _confirmFocus?.Dispose();
        _confirmFocus = new Controls.ModalPane(ConfirmOverlay, CancelApprovalOff, CancelApprovalConfirmation);
    }
    private void CancelApprovalConfirmation()
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirmFocus?.Dispose(); _confirmFocus = null;
        SettingsContent.UpdateApproval();
    }
    private void OnCancelApprovalOff(object sender, RoutedEventArgs e) => CancelApprovalConfirmation();
    private async void OnConfirmApprovalOff(object sender, RoutedEventArgs e) { await SetApprovalEnabled(false); CancelApprovalConfirmation(); }
    private async Task SetApprovalEnabled(bool enabled)
    {
        try { await Task.Run(() => Approvals.Enabled = enabled); }
        catch (Exception ex) { Controls.WorkbenchMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error); }
        SettingsContent.UpdateApproval();
    }
    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        Features.Export();
    }
    private void OnDismissToast(object sender, RoutedEventArgs e) { Toast.Visibility = Visibility.Collapsed; e.Handled = true; }
    private void OnToastClick(object sender, RoutedEventArgs e) { Toast.Visibility = Visibility.Collapsed; if (_approvalToast != null) OpenDrawer("Approvals"); else Navigate("Environment"); }
}
