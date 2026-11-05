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
        SettingsContent.Initialize(Approvals, _model.Panel);
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
        Navigate(_model.OfflineDemo ? "Environment" : "Engineering");
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

    // The manual collapse choice lasts until the window crosses the 1200 epx breakpoint.
    // Detached render hosts use the same content width as the window.
    private bool? _siderCollapsed;
    private bool _belowBreakpoint;
    private double ShellWidth => Root.ActualWidth;
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool below = ShellWidth > 0 && ShellWidth < 1200;
        if (below != _belowBreakpoint) { _belowBreakpoint = below; _siderCollapsed = null; }
        UpdateSider();
    }
    private void OnSiderToggle(object sender, RoutedEventArgs e) { _siderCollapsed = !(_siderCollapsed ?? _belowBreakpoint); UpdateSider(); }
    private void UpdateSider()
    {
        bool compact = _siderCollapsed ?? _belowBreakpoint;
        Sider.Width = compact ? 64 : 232;
        Controls.Ux.SetCompact(Sider, compact);
        BrandTitle.Visibility = BrandVersion.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        string toggle = compact ? "Shell.ExpandMenu" : "Shell.CollapseMenu";
        SiderToggle.SetBinding(ContentControl.ContentProperty, TrExtension.CreateBinding(toggle));
        SiderToggle.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, TrExtension.CreateBinding(toggle));
        SiderToggle.SetResourceReference(Controls.Ux.IconProperty, compact ? "Icon.MenuUnfold" : "Icon.MenuFold");

    }
    private void OnShellSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!HasProject && _page == "VersionControl") Navigate("Engineering");
        UpdateShell();
        UpdateMcpStatus();
        CommandManager.InvalidateRequerySuggested();
    }
    private void OnShellLanguageChanged(object? sender, EventArgs e) { UpdateLanguage(); UpdateShell(); }
    private void UpdateLanguage()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Current.Language == AppLanguage.Chinese ? "zh-CN" : "en-US");
        ApprovalOffConfirmationLabel.Text = Loc.Current.T("Settings.ConfirmOffPrompt", ApprovalOffConfirmationWord);
        UpdateApprovalOffConfirmation();
    }
    private void UpdateShell()
    {
        if (_featuresDisposed) return;
        PendingBadge.Content = Approvals.PendingCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        string pending = Loc.Current.T("Shell.Pending", Approvals.PendingCount);
        System.Windows.Automation.AutomationProperties.SetName(PendingBadge, pending);
        System.Windows.Automation.AutomationProperties.SetName(RailApprovals, Loc.Current["Shell.Approvals"] + " · " + pending);
        PendingBadge.Visibility = ApprovalsButton.Visibility = Approvals.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApprovalsButton.Content = PendingBadge.Content;
        System.Windows.Automation.AutomationProperties.SetName(ApprovalsButton, pending);
        ApprovalsButton.ToolTip = pending;
        UpdateMcpStatus();
        ApprovalStatusText.Text = Loc.Current[Approvals.Enabled ? "Shell.ApprovalOnStatus" : "Shell.ApprovalOffStatus"];
        SessionStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, HasProject ? "Primer.ok" : "Primer.textMuted");
        ApprovalStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, Approvals.Enabled ? "Primer.ok" : "Primer.warn");
        foreach (var item in new[] {
                RailApprovals, RailEngineering, RailVersionControl, RailCalls, RailAudit, RailEnvironment, RailLog, RailMcp, SettingsButton })
        {
            LocalizedText? reason = PageUnavailable((string)item.Tag);
            item.IsEnabled = reason == null;
            item.ToolTip = reason == null ? System.Windows.Automation.AutomationProperties.GetName(item) : reason.Value.Resolve();
        }
        RailVersion.Text = ViewModels.MainViewModel.AppVersion.TrimStart('v');
        BlocksContent.Visibility = _page == "Engineering" && HasProject ? Visibility.Visible : Visibility.Collapsed;
        CurrentPageTitle.Text = Loc.Current[_page switch
        {
            "Approvals" => "Shell.Approvals", "Settings" => "Shell.Settings",
            "Mcp" => "Desktop.Configuration", "VersionControl" => "Shell.VersionControl",
            "Calls" => "Shell.Calls", "Audit" => "Shell.Audit", "Environment" => "Shell.Environment", "Log" => "Shell.Logs",
            _ => "Shell.Engineering",
        }];
        Title = CurrentPageTitle.Text + " – " + Loc.Current["App.Title"];
        UpdateSafetyBanner();
        if (_drawer != null)
        {
            DrawerTitle.Text = Features.SelectedCall?.Tool ?? Loc.Current["Calls.Detail"];
            DrawerTitle.SetResourceReference(TextBlock.FontFamilyProperty, _drawer == "CallDetail" ? "Ui.FontMono" : "Ui.Font");
        }
        Features.Release = _model.SelectedReleaseKey;
        UpdateFeatureToast();

    }

    private LocalizedText? PageUnavailable(string page)
    {
        if (_model.OfflineDemo && page is not ("Calls" or "Audit" or "Environment" or "Log"))
            return LocalizedText.Key("Shell.OfflineUnavailable");
        if (page == "VersionControl" && !_model.VersionControlPageVisible)
            return LocalizedText.Key("Shell.VersionControlUnavailable");
        if (page == "VersionControl" && !HasProject) return LocalizedText.Key("Shell.NoProject");
        return null;
    }

    internal void Navigate(string page)
    {
        // Blocks remains a protocol alias for Project; it is not a second navigation target.
        if (page == "Blocks") page = "Engineering";
        if (PageUnavailable(page) is { } reason)
        {
            _approvalToast = null;
            _featureTitle = LocalizedText.Key("Shell.PageUnavailable");
            _featureFeedback = reason;
            UpdateFeatureToast(); Toast.Visibility = Visibility.Visible;
            Controls.AccessibleStatus.Announce(ToastMessage);
            return;
        }
        if (page is "Mcp" or "Settings") EnsureConfiguration();
        if (page == "Settings") SettingsContent.LoadPaths();
        if (_drawer != null) CloseDrawer();
        OperationsContent.HideOptions();
        BlocksContent.HideOptions();
        VersionControlContent.HideOptions();
        _page = page;
        Features.SetVisibility(page == "Calls", page == "Audit", page == "Approvals");
        OperationsContent.Visibility = page == "Engineering" ? Visibility.Visible : Visibility.Collapsed;
        BlocksContent.Visibility = page == "Engineering" && HasProject ? Visibility.Visible : Visibility.Collapsed;
        EngineeringSideContent.Visibility = page == "Engineering" ? Visibility.Visible : Visibility.Collapsed;
        VersionControlContent.Visibility = page == "VersionControl" ? Visibility.Visible : Visibility.Collapsed;
        LogContent.Visibility = page == "Log" ? Visibility.Visible : Visibility.Collapsed;
        _model.IsConfigurationPage = page == "Mcp";
        ConfigurationHost.Visibility = page == "Mcp" ? Visibility.Visible : Visibility.Collapsed;
        EngineeringPage.Visibility = page == "Engineering" ? Visibility.Visible : Visibility.Collapsed;
        CallsContent.Visibility = page == "Calls" ? Visibility.Visible : Visibility.Collapsed;
        AuditContent.Visibility = page == "Audit" ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentContent.Visibility = page == "Environment" ? Visibility.Visible : Visibility.Collapsed;
        ApprovalsContent.Visibility = page == "Approvals" ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "VersionControl") _model.IsVcTab = true;
        else if (page == "Log") _model.IsLogTab = true;
        else if (page == "Engineering") _model.IsBlocksTab = true;
        QueueControlSnapshot();
        ((RadioButton)FindName(page == "Settings" ? "SettingsButton" : "Rail" + page)).IsChecked = true;
        UpdateShell();
        if (IsLoaded) CurrentPageTitle.Focus();
    }

    private void OnRailClick(object sender, RoutedEventArgs e) => NavigateGuarded((string)((RadioButton)sender).Tag);
    private void OnNavigateExecuted(object sender, ExecutedRoutedEventArgs e) => NavigateGuarded((string)e.Parameter);
    // Disabled menu entries still have executable shortcuts, so the reason can be announced in a toast.
    private void OnCanNavigate(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
    private void NavigateGuarded(string page)
    {
        try { Navigate(page); }
        catch (Exception ex) {
            Controls.WorkbenchMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    internal void OpenSettings() => Navigate("Settings");
    private void OnMcpStatus(object sender, RoutedEventArgs e) => NavigateGuarded("Mcp");
    private void OnProjectStatus(object sender, RoutedEventArgs e) => NavigateGuarded("Engineering");
    private void OnApprovalStatus(object sender, RoutedEventArgs e)
    {
        NavigateGuarded("Settings");
        if (_page == "Settings") SettingsContent.ShowSection("AI");
    }

    private void OpenDrawer(string name)
    {
        if (name is "Approvals" or "Settings") { Navigate(name); return; }
        _drawerFocus?.Dispose();
        _drawer = name;
        Features.SetVisibility(_page == "Calls", _page == "Audit", _page == "Approvals", true);
        DrawerCallResult.Visibility = Visibility.Visible;
        DrawerPanel.Width = 520;
        CallDetailContent.Visibility = Visibility.Visible;
        DrawerOverlay.Visibility = Visibility.Visible;
        UpdateShell();
        _drawerFocus = new Controls.ModalPane(DrawerOverlay, DrawerTitle, CloseDrawer);
    }

    private void OnApprovals(object sender, RoutedEventArgs e) => Navigate("Approvals");
    private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseDrawer();
    private void OnDrawerBackdrop(object sender, MouseButtonEventArgs e) => CloseDrawer();
    private void CloseDrawer()
    {
        _drawer = null;
        Features.SetVisibility(_page == "Calls", _page == "Audit", _page == "Approvals");
        DrawerOverlay.Visibility = Visibility.Collapsed;
        _drawerFocus?.Dispose(); _drawerFocus = null;
        QueueControlSnapshot();
    }
    private void OnDisableApprovalRequested(object? sender, EventArgs e)
    {
        ApprovalOffConfirmation.Clear();
        UpdateApprovalOffConfirmation();
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
    private void OnConfirmApprovalKeyDown(object sender, KeyEventArgs e)
    {
        // C6: Enter always remains safe, including when the destructive button has focus.
        if (e.Key == Key.Enter || e.Key == Key.System && e.SystemKey == Key.Enter) e.Handled = true;
    }
    private string ApprovalOffConfirmationWord
    {
        get
        {
            string label = Loc.Current["Settings.TurnOff"];
            // The confirmation word excludes both inline and trailing access-key markers.
            int accessKey = label.IndexOf("(_", StringComparison.Ordinal);
            return (accessKey < 0 ? label : label[..accessKey]).Replace("_", "", StringComparison.Ordinal).Trim();
        }
    }
    private bool IsApprovalOffConfirmed => string.Equals(ApprovalOffConfirmation.Text.Trim(), ApprovalOffConfirmationWord,
        StringComparison.OrdinalIgnoreCase);
    private void UpdateApprovalOffConfirmation() => ConfirmApprovalOff.IsEnabled = IsApprovalOffConfirmed;
    private void OnApprovalOffConfirmationChanged(object sender, TextChangedEventArgs e) => UpdateApprovalOffConfirmation();
    private void OnCancelApprovalOff(object sender, RoutedEventArgs e) => CancelApprovalConfirmation();
    private async void OnConfirmApprovalOff(object sender, RoutedEventArgs e)
    {
        if (ConfirmOverlay.Visibility != Visibility.Visible || !IsApprovalOffConfirmed) return;
        await SetApprovalEnabled(false);
        CancelApprovalConfirmation();
    }
    private async Task SetApprovalEnabled(bool enabled)
    {
        try { await Task.Run(() => Approvals.Enabled = enabled); }
        catch (Exception ex) {
            Controls.WorkbenchMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error); }
        SettingsContent.UpdateApproval();
    }
    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        Features.Export();
    }
    private void OnDismissToast(object sender, RoutedEventArgs e) { Toast.Visibility = Visibility.Collapsed; e.Handled = true; }
    private void OnToastClick(object sender, RoutedEventArgs e) {
        Toast.Visibility = Visibility.Collapsed; if (_approvalToast != null) Navigate("Approvals"); else Navigate("Environment"); }
}
