using System;
using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly bool _loadExistingConfiguration;
    private readonly TiaMcpConfigurator.ConfigurationPreview? _preview;

    public MainWindow() : this(new MainViewModel(MainViewModel.OfflineDemoRequested(System.Environment.GetCommandLineArgs()))) { }

    internal MainWindow(MainViewModel model, bool loadExistingConfiguration = true, Services.Stubs.IApprovalService? approvals = null, Services.Stubs.IDiagnosticBundleService? diagnostics = null, TiaMcpConfigurator.ConfigurationPreview? preview = null)
    {
        _preview = preview;
        _model = model;
        _loadExistingConfiguration = loadExistingConfiguration;
        _model.SelectedReleaseKey = preview?.ReleaseKey ?? InitialReleaseKey(System.Environment.GetCommandLineArgs());
        InitializeComponent();
        DataContext = _model;
        Root.Tag = new Controls.ResultPresentation(_model);
        InitializeShell(approvals, diagnostics);
        UpdateMcpStatus();
        Localization.Loc.Current.LanguageChanged += OnMcpLanguageChanged;
        Closing += (_, e) => { if (_model.Busy) e.Cancel = true; };
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

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnOpenExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (_model.OfflineDemo) return;
        Navigate("Engineering");
        OperationsContent.ShowProjectOptions();
    }
}
