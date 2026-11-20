using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly bool _loadExistingConfiguration;
    private readonly TiaMcpConfigurator.ConfigurationPreview? _preview;

    public MainWindow() : this(new MainViewModel(MainViewModel.OfflineDemoRequested(System.Environment.GetCommandLineArgs()))) { }

    internal MainWindow(MainViewModel model, bool loadExistingConfiguration = true,
        Services.Stubs.IApprovalService? approvals = null, Services.Stubs.IDiagnosticBundleService? diagnostics = null,
        TiaMcpConfigurator.ConfigurationPreview? preview = null, Func<TimeSpan>? toastClock = null)
    {
        _toasts = new Services.ToastQueue(toastClock ?? (() => System.Diagnostics.Stopwatch.GetElapsedTime(0)));
        _preview = preview;
        _model = model;
        _loadExistingConfiguration = loadExistingConfiguration;
        _model.SelectedReleaseKey = preview?.ReleaseKey ?? InitialReleaseKey(System.Environment.GetCommandLineArgs());
        InitializeComponent();
        DataContext = _model;
        Root.Tag = new Controls.ResultPresentation(_model);
        InitializeToast();
        InitializeShell(approvals, diagnostics);
        UpdateMcpStatus();
        Localization.Loc.Current.LanguageChanged += OnMcpLanguageChanged;
        Closing += (_, e) =>
        {
            if (!_model.Busy) return;
            e.Cancel = true;
            ShowToast(ToastKind.Warning, Localization.LocalizedText.Key("Shell.BusyClose", _model.Activity.Status),
                "busy", title: Localization.LocalizedText.Key("Shell.BusyTitle"));
        };
        Loaded += async (_, _) => {
            if (!_loadExistingConfiguration) return;
            await _model.ApplyStartupAsync(System.Environment.GetCommandLineArgs());
            var args = System.Environment.GetCommandLineArgs();
            if (_model.OfflineDemo)
            {
                int tab = System.Array.IndexOf(args, "--tab");
                Navigate(tab >= 0 && tab + 1 < args.Length ? args[tab + 1].ToLowerInvariant() switch
                    { "calls" => "Calls", "audit" => "Audit", "log" => "Log", _ => "Environment" } : "Environment");
                return;
            }
            if (System.Array.IndexOf(args, "--tab") >= 0) Navigate(_model.IsVcTab ? "VersionControl" : _model.IsLogTab ? "Log" : "Engineering");
            else ShowConfiguration();
        };
    }

    private bool _closePending;
    private bool _closeReady;

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Let the configuration helper and every other close veto run before releasing any resources.
        base.OnClosing(e);
        if (_closeReady || e.Cancel) return;
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        IsEnabled = false;
        try
        {
            Localization.Loc.Current.LanguageChanged -= OnMcpLanguageChanged;
            DisposeToast();
            DisposeShell();
            DisposeConfiguration();
            BlocksContent.Dispose();
            ((Controls.ResultPresentation)Root.Tag).Dispose();
            await _model.DisposeAsync();
        }
        catch (Exception error)
        {
            App.ReportUiException(error);
        }
        finally
        {
            _closeReady = true;
            // Post a fresh close after the current Closing event has returned.
            Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this)).AddHook(CaptionHitTest);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTracking
    {
        public uint Size, Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref MouseTracking tracking);

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private bool OverMaximizeButton(IntPtr lParam)
    {
        long coordinates = lParam.ToInt64();
        var screen = new Point((short)coordinates, (short)(coordinates >> 16));
        return MaximizeWindowButton.IsEnabled && !ShellModalOpen
            && new Rect(MaximizeWindowButton.RenderSize).Contains(MaximizeWindowButton.PointFromScreen(screen));
    }

    private void TrackMaximizePointer(IntPtr hwnd)
    {
        if (MaximizeWindowButton.Tag != null) return;
        const uint leave = 0x2, nonClient = 0x10;
        var tracking = new MouseTracking { Size = (uint)Marshal.SizeOf<MouseTracking>(), Flags = leave | nonClient, Window = hwnd };
        TrackMouseEvent(ref tracking);
        MaximizeWindowButton.Tag = "MouseOver";
    }

    private IntPtr CaptionHitTest(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int hitMaxButton = 9, ncHitTest = 0x0084, ncMouseMove = 0x00A0, ncMouseLeave = 0x02A2;
        const int ncButtonDown = 0x00A1, ncButtonUp = 0x00A2, ncButtonDoubleClick = 0x00A3, cancelMode = 0x001F;
        switch (message)
        {
            case ncHitTest when OverMaximizeButton(lParam):
                handled = true;
                return hitMaxButton;
            case ncMouseMove when wParam == hitMaxButton:
                if (OverMaximizeButton(lParam)) TrackMaximizePointer(hwnd);
                else MaximizeWindowButton.Tag = null;
                handled = true;
                // Windows still receives the hover messages that drive the Snap Layouts flyout.
                return DefWindowProcW(hwnd, message, wParam, lParam);
            case ncMouseLeave when MaximizeWindowButton.Tag != null:
                MaximizeWindowButton.Tag = null;
                handled = true;
                return DefWindowProcW(hwnd, message, wParam, lParam);
            case ncMouseMove:
            case cancelMode:
                if (MaximizeWindowButton.Tag != null) MaximizeWindowButton.Tag = null;
                break;
            case ncButtonDown or ncButtonDoubleClick when wParam == hitMaxButton:
                handled = true;
                if (OverMaximizeButton(lParam))
                {
                    TrackMaximizePointer(hwnd);
                    MaximizeWindowButton.Tag = "Pressed";
                }
                break;
            case ncButtonUp when wParam == hitMaxButton:
                handled = true;
                if (!OverMaximizeButton(lParam)) MaximizeWindowButton.Tag = null;
                else if (MaximizeWindowButton.Tag is "Pressed")
                {
                    MaximizeWindowButton.Tag = "MouseOver";
                    MaximizeWindowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, MaximizeWindowButton));
                }
                break;
        }
        return IntPtr.Zero;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState
        .Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private bool ShellModalOpen => DrawerOverlay.Visibility == Visibility.Visible || OverlayHost.Visibility == Visibility.Visible
        || ApprovalsContent.ConfirmationSheet.Visibility == Visibility.Visible
        || OperationsContent.OptionsOpen || BlocksContent.OptionsOpen || VersionControlContent.OptionsOpen;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool modal = ShellModalOpen;
        if (modal && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && e.Key is Key.O or Key.S or Key.B or Key.K or Key.OemComma or >= Key.D1 and <= Key.D7) e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape || ShellModalOpen) return;
        if (_leftPaneFlyoutOpen) CloseLeftPaneFlyout();
        else if (_toasts.Visible != null) _toasts.Dismiss();
        else return;
        e.Handled = true;
    }
    private void OnOpenExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (_model.OfflineDemo) return;
        Navigate("Engineering");
        OperationsContent.ShowProjectOptions();
    }
}
