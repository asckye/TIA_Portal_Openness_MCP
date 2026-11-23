using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Core.Environment;
using System.Windows;
using System.Windows.Threading;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Settings;
using TiaOpenness.Gui.Themes;

namespace TiaOpenness.Gui;

public partial class App : Application
{
    /// <summary>Where the crash log lands, so a field failure can be sent back as one file.</summary>
    public static string CrashLogPath => TiaOpenness.Shared.DataLocations.Current.LogFile("TiaOpenness.crash.log", "studio");

    /// <summary>Language and appearance as they were left last time.</summary>
    public static UiSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // A test host loads the real application resources without launching another window,
        // reading user preferences, or starting configuration/update probes in the background.
        if (System.Reflection.Assembly.GetEntryAssembly() != typeof(App).Assembly)
        {
            base.OnStartup(e);
            return;
        }
        string[] args;
        try { TiaOpenness.Shared.BundleLayout.InitializeWorkbench(AppContext.BaseDirectory, e.Args, out args); }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("TIA_OPENNESS_NETWORK_NO_DIALOG") == "1") Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, Localization.Loc.Current["Common.PortalName"], MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(ex is ArgumentException ? 64 : 70);
            return;
        }
        TiaOpenness.Shared.DataLocations.PruneLogsAtStartup();
        if (args.Length > 0 && args[0] == "--network")
        {
            // The elevated network helper never opens the workbench: no StartupUri is set on this path
            // (WPF on .NET 10 rejects assigning null to it, which used to crash this helper).
            Shutdown(RunNetworkConfiguration(args));
            return;
        }
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
        var instance = new WorkbenchInstance("TiaOpenness.Workbench", () =>
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (MainWindow is not { } window) return;
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            })));
        if (!instance.IsFirst) { instance.Dispose(); Shutdown(); return; }

        Settings = UiSettings.Load();
        ApplyCommandLineOverrides(args, Settings);

        Loc.Current.Language = Settings.Language;
        ThemeManager.Current.Initialize(Settings.Theme, Settings.LowEffects, Settings.Accent);

        // Persist whatever the window is showing when it closes, not at the moment of the click,
        // so a language toggled and then toggled back does not write twice.
        Exit += (_, _) =>
        {
            Settings.Language = Loc.Current.Language;
            Settings.Theme = ThemeManager.Current.Theme;
            Settings.Accent = ThemeManager.Current.Accent;
            Settings.LowEffects = ThemeManager.Current.LowEffects;
            try
            {
                if (Settings.Save() is { } error)
                    MessageBox.Show(error, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
                TaskScheduler.UnobservedTaskException -= OnTaskException;
                instance.Dispose();
            }
        };

        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }

    /// <summary>
    /// <c>--network address port sid</c>, run elevated by the configuration page: reserve the HTTP prefix
    /// and open the firewall. Returns the process exit code. With TIA_OPENNESS_NETWORK_NO_DIALOG=1 the
    /// error goes to stderr instead of a dialog, so the path can be exercised without a desktop.
    /// </summary>
    internal static int RunNetworkConfiguration(string[] args)
    {
        try
        {
            if (args.Length != 4) throw new ArgumentException("Invalid network configuration arguments.");
            TiaMcpConfigurator.ConfigCore.ConfigureNetwork(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args[3]);
            return 0;
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("TIA_OPENNESS_NETWORK_NO_DIALOG") == "1") Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, Localization.Loc.Current["Common.PortalName"], MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>
    /// <c>--lang en|zh</c> and <c>--theme light|dark</c> with <c>--accent cobalt|petrol</c>. Present so a screenshot or a demo
    /// can be pinned to one appearance without touching the saved preference.
    /// </summary>
    internal static void ApplyCommandLineOverrides(string[] args, UiSettings settings)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase))
            {
                settings.Language = args[i + 1].ToLowerInvariant() switch
                {
                    "zh" or "zh-cn" or "chinese" => AppLanguage.Chinese,
                    "en" or "en-us" or "english" => AppLanguage.English,
                    _ => settings.Language,
                };
            }
            else if (string.Equals(args[i], "--theme", StringComparison.OrdinalIgnoreCase)
                     && Enum.TryParse<AppTheme>(args[i + 1], ignoreCase: true, out var theme) && Enum.IsDefined(theme))
            {
                settings.Theme = theme;
            }
            else if (string.Equals(args[i], "--accent", StringComparison.OrdinalIgnoreCase)
                     && Enum.TryParse<AppAccent>(args[i + 1], ignoreCase: true, out var accent) && Enum.IsDefined(accent))
            {
                settings.Accent = accent;
            }
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportUiException(e.Exception);
        e.Handled = true;
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        // The runtime may terminate when this handler returns.
        ReportFatalException(e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
    }

    internal static void ReportFatalException(Exception error, string? path = null, Action<Window, string>? showDialog = null)
    {
        var message = CrashReport(error, path);
        PresentUiError(message, fatal: true, showDialog: showDialog);
    }

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var message = CrashReport(e.Exception);
        e.SetObserved();
        PresentUiError(message);
    }

    internal static void ReportUiException(Exception error) => PresentUiError(CrashReport(error));

    private static readonly object CrashLogLock = new();
    internal static string CrashReport(Exception error, string? path = null)
    {
        string id = Guid.NewGuid().ToString("N");
        var redactor = new DiagnosticRedactor();
        string details;
        try
        {
            path ??= CrashLogPath;
            var entry = $"{DateTime.UtcNow:O} {id}{Environment.NewLine}{redactor.Redact(error.ToString()).Text}{Environment.NewLine}";
            lock (CrashLogLock) File.AppendAllText(path, entry);
            details = Loc.Current.T("Dialog.Error.Details", path);
        }
        catch (Exception logError)
        {
            details = Loc.Current.T("Common.IoFailed", logError.Message);
            TiaOpenness.Shared.DataLocations.ReportLogFailure("TiaOpenness.crash", logError);
        }
        return string.Join(Environment.NewLine, redactor.Redact(error.Message).Text, Loc.Current.T("Dialog.DiagnosticId", id), details);
    }

    private static int presentingCrash, presentingFatalCrash;
    internal static void PresentUiError(string message, bool fatal = false, Action<Window, string>? showDialog = null)
    {
        var app = Current;
        if (app == null || app.Dispatcher.HasShutdownStarted) return;
        if (fatal)
        {
            if (Interlocked.CompareExchange(ref presentingFatalCrash, 1, 0) != 0) return;
        }
        else if (Volatile.Read(ref presentingFatalCrash) != 0 || Interlocked.CompareExchange(ref presentingCrash, 1, 0) != 0) return;
        void Release()
        {
            if (fatal) Interlocked.Exchange(ref presentingFatalCrash, 0);
            else Interlocked.Exchange(ref presentingCrash, 0);
        }
        void Present()
        {
            try
            {
                if (!fatal && Volatile.Read(ref presentingFatalCrash) != 0) return;
                if (!fatal && app.MainWindow is MainWindow workbench && !workbench.ToastHostClosed)
                {
                    workbench.ShowToast(ViewModels.ToastKind.Error, LocalizedText.Literal(message), "dispatcher");
                    return;
                }
                var owner = app.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? app.MainWindow;
                Window? temporaryOwner = null;
                try
                {
                    owner ??= temporaryOwner = new Window { ShowInTaskbar = false };
                    new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
                    if (showDialog != null) showDialog(owner, message);
                    else MessageBox.Show(owner, message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { temporaryOwner?.Close(); }
            }
            finally { Release(); }
        }

        if (app.Dispatcher.CheckAccess()) Present();
        else
        {
            var pending = app.Dispatcher.InvokeAsync(Present, DispatcherPriority.Send);
            if (!fatal) return;
            // Returning from the domain handler lets the runtime terminate even if the UI never dismisses the dialog.
            var finished = ((IAsyncResult)pending.Task).AsyncWaitHandle;
            if (!finished.WaitOne(TimeSpan.FromSeconds(5)) && pending.Abort()) Release();
        }
    }
}
