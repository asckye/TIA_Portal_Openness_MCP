using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services.Stubs;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    internal IApprovalService Approvals { get; private set; } = null!;
    private IDiagnosticBundleService _diagnostics = null!;
    private string _page = "Engineering";
    private string _lastAgentPage = "Calls";
    internal string CurrentPage => _page;
    private string? _drawer;
    private long? _verificationStartHead;
    private long? _verifiedHead;
    private bool DetailDocked => DockedInspector.Visibility == Visibility.Visible;
    private RadioButton[] RailItems => [RailEngineering, RailAgent, RailMcp, RailClients, RailEnvironment, RailAudit, RailLog, SettingsButton];
    partial void OpenCommandPalette();
    partial void ToggleSessionPopover();
    private bool _shellQueued;
    private Controls.ModalPane? _drawerFocus;
    private bool ConnectionLost => !_model.Session.IsConnected && !string.IsNullOrEmpty(_model.Session.OpenProjectFile);
    private bool HasProject => _model.Session.IsConnected && !string.IsNullOrEmpty(_model.Session.ProjectName);

    private void InitializeShell(IApprovalService? approvals, IDiagnosticBundleService? diagnostics)
    {
        foreach (var (scrim, view) in new[] { (ProjectOptionsRailScrim, OperationsContent.Options),
            (BlockOptionsRailScrim, BlocksContent.Options), (WorkspaceOptionsScrim, VersionControlContent.Options) })
        {
            // The workbench covers the full frame. Standalone views use their own page-sized scrim.
            view.LocalScrim.Visibility = Visibility.Collapsed;
            scrim.ForegroundSurface = view.OptionsPanel;
            scrim.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = view.OptionsOverlay });
        }
        ApprovalsContent.LocalScrim.Visibility = Visibility.Collapsed;
        ConfirmationFrameScrim.ForegroundSurface = ApprovalsContent.ConfirmationCard;
        ConfirmationFrameScrim.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = ApprovalsContent.ConfirmationSheet });
        Approvals = approvals ?? (_model.OfflineDemo ? new ApprovalServiceStub() : new Services.ApprovalService());
        _diagnostics = diagnostics ?? new Services.DiagnosticBundleService();
        CallsContent.DataContextChanged += OnFeatureContextChanged;
        InitializeFeaturePages();
        SettingsContent.Initialize(Approvals, _model.Panel);
        ((Grid)SettingsContent.Content).ColumnDefinitions[0].Width = GridLength.Auto;
        if (_model.Panel != null) _model.Panel.PropertyChanged += OnShellSessionChanged;
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
        _drawerFocus?.Dispose();
        DisposeControl();
        DisposeFeaturePages();
        CallsContent.DataContextChanged -= OnFeatureContextChanged;
        SettingsContent.Dispose();
        if (_model.Panel != null) _model.Panel.PropertyChanged -= OnShellSessionChanged;
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
        if (e.PropertyName is not (nameof(IApprovalService.Enabled) or nameof(IApprovalService.PendingCount)
            or nameof(Services.ApprovalService.ChannelAvailable))) return;
        if (_shellQueued) return;
        _shellQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { _shellQueued = false; UpdateShell(); }));
    }
    private void OnEnvironmentRequested(object? sender, EventArgs e) => NavigateGuarded("Environment");
    private void OnInspectionRulesRequested(object? sender, EventArgs e) => OperationsContent.ShowControlInspectionRules();
    private void OnTransferOptionsRequested(object? sender, EventArgs e) => OperationsContent.ShowTransferOptions();

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool paneHadFocus = ProjectTree.IsKeyboardFocusWithin || ApprovalsContent.RequestListPane.IsKeyboardFocusWithin
            || SettingsContent.SectionList.IsKeyboardFocusWithin;
        UpdateLeftPane();
        if (paneHadFocus && !Controls.Ux.GetLeftPaneOpen(Root)) LeftPaneToggle.Focus();
        if ((_drawer == "CallDetail" || DetailDocked) && DetailDocked != CanDockDetail) OpenDrawer("CallDetail");
    }

    private bool _leftPaneFlyoutOpen;
    private bool NarrowContent => Root.ActualWidth > 0 && Root.ActualWidth <= 1024;

    private void UpdateLeftPane()
    {
        LeftPaneToggle.IsEnabled = _page is "Engineering" or "VersionControl" or "Approvals" or "Settings";
        if (!NarrowContent || !LeftPaneToggle.IsEnabled) _leftPaneFlyoutOpen = false;
        Controls.Ux.SetLeftPaneOpen(Root, App.Settings.LeftPaneOpen);
        Controls.Ux.SetNarrow(Root, NarrowContent);
        var secondary = NarrowContent ? Visibility.Collapsed : Visibility.Visible;
        BrandVersion.Visibility = EnvironmentDescription.Visibility = secondary;
        bool hasParticipants = !string.IsNullOrWhiteSpace(ClientStatus.Text) || TiaProcessStatus.Visibility == Visibility.Visible;
        ParticipantStatus.Visibility = !NarrowContent && _model.Panel?.Attached == true && hasParticipants ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLeftPaneToggle(object sender, RoutedEventArgs e)
    {
        App.Settings.LeftPaneOpen = !App.Settings.LeftPaneOpen;
        _leftPaneFlyoutOpen = NarrowContent && App.Settings.LeftPaneOpen;
        UpdateLeftPane();
        if (!_leftPaneFlyoutOpen) return;
        Root.UpdateLayout();
        if (_page == "Settings")
        {
            ((ListBoxItem)SettingsContent.SectionList.SelectedItem).Focus();
            return;
        }
        if (_page == "Approvals") { ApprovalsContent.RequestList.Focus(); return; }
        if (ProjectTree.Content is UIElement content && content.Focus()) return;
        if (!ProjectTree.HasContent || !ProjectTree.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) ProjectTree.Focus();
    }

    private void CloseLeftPaneFlyout()
    {
        _leftPaneFlyoutOpen = false;
        App.Settings.LeftPaneOpen = false;
        UpdateLeftPane();
        LeftPaneToggle.Focus();
    }

    private bool CanDockDetail => _page == "Calls" && Root.ActualWidth - Sider.Width >= 1200;

    private void OnFeatureContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FeaturePagesViewModel old)
        {
            old.PropertyChanged -= OnVerificationChanged;
            old.Audit.PropertyChanged -= OnAuditChanged;
        }
        _verifiedHead = _verificationStartHead = null;
        if (e.NewValue is FeaturePagesViewModel current)
        {
            current.PropertyChanged += OnVerificationChanged;
            current.Audit.PropertyChanged += OnAuditChanged;
            UpdateAuditHead();
            UpdateEnvironmentBanner();
        }
    }

    private void OnVerificationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Features.EnvironmentRows)) UpdateEnvironmentBanner();
        if (e.PropertyName == nameof(Features.Verifying) && Features.Verifying)
        {
            _verificationStartHead = AuditIndex;
            _verifiedHead = null;
        }
        else if (e.PropertyName == nameof(Features.HasVerification))
        {
            if (!Features.HasVerification || Features.VerificationBroken) _verifiedHead = null;
            else if (Features.Verifying && Features.VerificationTone == FeatureTone.Success) _verifiedHead = _verificationStartHead;
        }
        else return;
        UpdateAuditHead();
    }

    private long? AuditIndex => Features.Audit.Events.Max(row => (long?)row.Index);

    private void OnAuditChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(Features.Audit.Events))
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(UpdateAuditHead));
    }

    private void UpdateAuditHead()
    {
        if (_featuresDisposed) return;
        var index = AuditIndex;
        AuditHead.Visibility = index.HasValue ? Visibility.Visible : Visibility.Collapsed;
        string verified = index.HasValue && index == _verifiedHead && Features.HasVerification && !Features.VerificationBroken ? " ✓" : "";
        AuditHead.Text = index.HasValue ? $"Audit #{index}{verified}" : "";
        AuditHead.Tag = index.HasValue ? Loc.Current.T("Shell.AuditHead", index) + verified : "";
    }

    private void OnShellSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == _model.Engineering && e.PropertyName is not (null or "" or nameof(EngineeringViewModel.LastCompile))) return;
        if (!HasProject && _page == "VersionControl") Navigate("Engineering");
        UpdateShell();
        CommandManager.InvalidateRequerySuggested();
    }
    private void OnShellLanguageChanged(object? sender, EventArgs e) { UpdateLanguage(); UpdateShell(); RenderToast(); }
    private void UpdateLanguage()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Current.Language == AppLanguage.Chinese ? "zh-CN" : "en-US");
    }
    private void UpdateShell()
    {
        if (_featuresDisposed) return;
        if (Approvals.PendingCount == 0) _approvalNotification.Hide();
        PendingBadge.Content = Approvals.PendingCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        string pending = Loc.Current.T("Shell.Pending", Approvals.PendingCount);
        string agent = Loc.Current["Rail.Agent"];
        if (Approvals.PendingCount > 0) agent += " · " + pending;
        AutomationProperties.SetName(RailAgent, agent);
        PendingBadge.Visibility = Approvals.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AutomationProperties.GetName(PendingBadge) != pending)
        {
            AutomationProperties.SetName(PendingBadge, pending);
            if (Approvals.PendingCount > 0) Controls.AccessibleStatus.Announce(PendingBadge);
        }
        UpdateMcpStatus();
        bool available = Approvals is not Services.ApprovalService { ChannelAvailable: false };
        ApprovalsContent.DecisionArea.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.AiPanel.IsEnabled = available;
        string mode = Loc.Current[!available ? "Shell.ApprovalUnavailable" : Approvals.Enabled ? "Shell.ApprovalOnStatus" : "Shell.ApprovalOffStatus"];
        if (ApprovalStatusText.Text != mode)
        {
            ApprovalStatusText.Text = mode;
            Controls.AccessibleStatus.Announce(ApprovalStatusText);
        }
        UpdateSessionGlyph();
        ApprovalStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, available && Approvals.Enabled ? "Primer.ok" : "Primer.warn");
        foreach (var item in RailItems)
        {
            var reason = PageUnavailable(ResolvePage((string)item.Tag));
            item.IsEnabled = reason == null;
            item.ToolTip = (reason?.Resolve() ?? AutomationProperties.GetName(item)) + " (" + AutomationProperties.GetAcceleratorKey(item) + ")";
            item.IsTabStop = item.IsChecked == true;
        }
        CommandPaletteButton.ToolTip = Loc.Current["Shell.CommandPalette"] + " (" + AutomationProperties.GetAcceleratorKey(CommandPaletteButton) + ")";
        RailVersion.Text = MainViewModel.AppVersion;
        var result = _model.Engineering.LastCompile;
        CompileStatus.Visibility = result == null ? Visibility.Collapsed : Visibility.Visible;
        if (result != null)
        {
            string glyph = result.ErrorCount > 0 ? "✕" : "✓";
            CompileStatus.Text = $"{glyph} {result.ErrorCount} ! {result.WarningCount}";
            CompileWarningGlyph.PositionStart = CompileStatus.Text.IndexOf('!');
            CompileStatus.Tag = Loc.Current.T("Shell.CompileCounts", result.ErrorCount, result.WarningCount);
            CompileOkGlyph.PositionCount = result.ErrorCount == 0 ? 1 : 0;
            CompileErrorGlyph.PositionCount = result.ErrorCount > 0 ? 1 : 0;
        }
        var panel = _model.Panel;
        ClientStatus.Text = panel?.ClientStatus ?? "";
        TiaProcessStatus.Text = Loc.Current.T("Shell.TiaProcessStatus", _model.Session.ProcessId.ToString());
        TiaProcessStatus.Visibility = _model.Session.IsConnected && _model.Session.ProcessId > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAuditHead();
        UpdateLeftPane();
        BlocksContent.Visibility = _page == "Engineering" && HasProject ? Visibility.Visible : Visibility.Collapsed;
        string crumb = Loc.Current[_page switch
        {
            "Approvals" => "Shell.Approvals", "Settings" => "Shell.Settings",
            "Clients" => "Shell.Clients", "Mcp" => "Desktop.Configuration", "VersionControl" => "Shell.Engineering",
            "Calls" => "Shell.Approvals", "Audit" => "Shell.Audit", "Environment" => "Shell.Environment", "Log" => "Shell.Logs",
            _ => "Shell.Engineering",
        }];
        CurrentPageTitle.Text = HasProject && _page is ("Engineering" or "VersionControl") ? crumb + " · " + ProjectFileName
            : _page == "Engineering" && !_model.Session.IsConnected ? crumb + " · " + Loc.Current["Shell.Disconnected"] : crumb;
        Title = CurrentPageTitle.Text + " – " + Loc.Current["App.Title"];
        UpdateControlHint();
        UpdateEnvironmentBanner();
        if (_drawer != null || DetailDocked)
        {
            DrawerTitle.Text = Features.SelectedCall?.Tool ?? Loc.Current["Calls.Detail"];
            DrawerTitle.SetResourceReference(TextBlock.FontFamilyProperty, "Ui.FontMono");
        }
        Features.Release = _model.SelectedReleaseKey;
    }

    private void UpdateSessionGlyph()
    {
        SessionGlyph.Text = !_model.Session.IsConnected ? "✕" : _model.Busy ? "◌" : "✓";
        SessionGlyph.SetResourceReference(TextBlock.ForegroundProperty,
            !_model.Session.IsConnected ? "Ant.colorError" : _model.Busy ? "Primer.warn" : "Primer.ok");
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

    private string ResolvePage(string page) => page switch
    {
        "Blocks" => "Engineering",
        "Agent" => Approvals.PendingCount > 0 ? "Approvals" : _lastAgentPage,
        _ => page,
    };

    internal void Navigate(string page)
    {
        // Blocks remains a protocol alias for Project; it is not a second navigation target.
        page = ResolvePage(page);
        if (PageUnavailable(page) is { } reason)
        {
            ShowToast(ToastKind.Warning, reason, "navigation", title: LocalizedText.Key("Shell.PageUnavailable"));
            return;
        }
        if (page is "Mcp" or "Clients" or "Settings") EnsureConfiguration();
        if (page == "Settings") SettingsContent.LoadPaths();
        if (_drawer != null || DetailDocked) CloseDrawer();
        OperationsContent.HideOptions();
        BlocksContent.HideOptions();
        VersionControlContent.HideOptions();
        _leftPaneFlyoutOpen = false;
        _page = page;
        if (page is "Calls" or "Approvals") _lastAgentPage = page;
        Features.SetVisibility(page == "Calls", page == "Audit", page == "Approvals");
        OperationsContent.Visibility = page == "Engineering" ? Visibility.Visible : Visibility.Collapsed;
        BlocksContent.Visibility = page == "Engineering" && HasProject ? Visibility.Visible : Visibility.Collapsed;
        EngineeringSideContent.Visibility = page == "Engineering" ? Visibility.Visible : Visibility.Collapsed;
        VersionControlContent.Visibility = page == "VersionControl" ? Visibility.Visible : Visibility.Collapsed;
        LogContent.Visibility = page == "Log" ? Visibility.Visible : Visibility.Collapsed;
        _model.IsConfigurationPage = page == "Mcp";
        ConfigurationHost.Visibility = page == "Mcp" ? Visibility.Visible : Visibility.Collapsed;
        ClientsHost.Visibility = page == "Clients" ? Visibility.Visible : Visibility.Collapsed;
        ProjectOverview.Visibility = page == "Engineering" ? Visibility.Visible : Visibility.Collapsed;
        EngineeringPage.Visibility = page is "Engineering" or "VersionControl" ? Visibility.Visible : Visibility.Collapsed;
        CallsPage.Visibility = CallsContent.Visibility = page == "Calls" ? Visibility.Visible : Visibility.Collapsed;
        AuditContent.Visibility = page == "Audit" ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentContent.Visibility = page == "Environment" ? Visibility.Visible : Visibility.Collapsed;
        ApprovalsContent.Visibility = page == "Approvals" ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "VersionControl") _model.IsVcTab = true;
        else if (page == "Log") _model.IsLogTab = true;
        else if (page == "Engineering") _model.IsBlocksTab = true;
        QueueControlSnapshot();
        var rail = page switch
        {
            "Approvals" or "Calls" => RailAgent,
            "VersionControl" => RailEngineering,
            "Settings" => SettingsButton,
            _ => (RadioButton)FindName("Rail" + page),
        };
        rail.IsChecked = true;
        UpdateShell();
        if (IsLoaded) CurrentPageTitle.Focus();
    }

    private void OnRailKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not RadioButton current || e.Key is not (Key.Up or Key.Left or Key.Down or Key.Right or Key.Home or Key.End)) return;
        var items = RailItems.Where(item => item.IsEnabled).ToArray();
        int direction = e.Key is Key.Up or Key.Left ? -1 : 1;
        var next = e.Key switch
        {
            Key.Home => items[0],
            Key.End => items[^1],
            _ => items[(Array.IndexOf(items, current) + items.Length + direction) % items.Length],
        };
        NavigateGuarded((string)next.Tag);
        next.Focus();
        e.Handled = true;
    }
    private void OnNavigateExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        NavigateGuarded((string)e.Parameter);
        if (e.OriginalSource is RadioButton { GroupName: "Pages" } rail && Equals(rail.CommandParameter, e.Parameter)) rail.Focus();
    }
    // Disabled menu entries still have executable shortcuts, so the reason can be announced in a toast.
    private void OnCanNavigate(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;
    private void NavigateGuarded(string page)
    {
        try { Navigate(page); }
        catch (Exception ex) {
            ShowToast(ToastKind.Error, LocalizedText.Literal(ex.Message), "navigation"); }
    }

    internal void OpenSettings() => Navigate("Settings");
    private void OnCompileStatus(object sender, RoutedEventArgs e)
    {
        NavigateGuarded("Engineering");
        if (_page == "Engineering") BlocksContent.ShowCompileTab();
    }

    private void OnProjectStatus(object sender, RoutedEventArgs e) => ToggleSessionPopover();
    private void OnApprovalStatus(object sender, RoutedEventArgs e)
    {
        NavigateGuarded("Settings");
        if (_page == "Settings") SettingsContent.ShowSection("AI");
    }

    private void OnCanOpenPalette(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = OverlayHost != null && !ShellModalOpen;
    private void OnCommandPalette(object sender, ExecutedRoutedEventArgs e) => OpenCommandPalette();

    private void OpenDrawer(string name)
    {
        if (name is "Approvals" or "Settings") { Navigate(name); return; }
        bool dock = CanDockDetail;
        _drawerFocus?.Dispose();
        _drawerFocus = null;
        _drawer = dock ? null : name;
        var target = dock ? DockedInspector : DrawerOverlay;
        if (DrawerPanel.Parent != target)
        {
            ((Panel)DrawerPanel.Parent).Children.Remove(DrawerPanel);
            target.Children.Add(DrawerPanel);
        }
        DrawerPanel.Width = dock ? 344 : 520;
        DrawerPanel.Margin = dock ? new Thickness(0) : new Thickness(0, 8, 8, 8);
        Features.SetVisibility(_page == "Calls", _page == "Audit", _page == "Approvals", true);
        DrawerCallResult.Visibility = CallDetailContent.Visibility = Visibility.Visible;
        DockedInspector.Visibility = dock ? Visibility.Visible : Visibility.Collapsed;
        DrawerOverlay.Visibility = dock ? Visibility.Collapsed : Visibility.Visible;
        UpdateShell();
        if (!dock) _drawerFocus = new Controls.ModalPane(DrawerOverlay, DrawerTitle, CloseDrawer);
        QueueControlSnapshot();
    }

    private void OnApprovals(object sender, RoutedEventArgs e) => Navigate("Approvals");
    private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseDrawer();
    private void OnDrawerBackdrop(object sender, MouseButtonEventArgs e) => CloseDrawer();
    private void CloseDrawer()
    {
        _drawer = null;
        Features.SetVisibility(_page == "Calls", _page == "Audit", _page == "Approvals");
        DockedInspector.Visibility = DrawerOverlay.Visibility = Visibility.Collapsed;
        _drawerFocus?.Dispose(); _drawerFocus = null;
        QueueControlSnapshot();
    }
    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        Themes.ThemeManager.Current.Theme = Themes.ThemeManager.Current.Theme == Themes.AppTheme.Dark
            ? Themes.AppTheme.Light
            : Themes.AppTheme.Dark;
    }

    private void OnDiagnostics(object sender, RoutedEventArgs e)
    {
        Features.Export();
    }
}
