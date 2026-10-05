using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Settings;
using TiaOpenness.Gui.Themes;

namespace TiaOpenness.Gui;

public partial class App : Application
{
    /// <summary>Where the crash log lands, so a field failure can be sent back as one file.</summary>
    public static string CrashLogPath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? Path.GetTempPath(),
        (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "TiaOpenness") + ".crash.log");

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
            else MessageBox.Show(ex.Message, "TIA Portal", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(ex is ArgumentException ? 64 : 70);
            return;
        }
        if (args.Length > 0 && args[0] == "--network")
        {
            // The elevated network helper never opens the workbench: no StartupUri is set on this path
            // (WPF on .NET 10 rejects assigning null to it, which used to crash this helper).
            Shutdown(RunNetworkConfiguration(args));
            return;
        }
        // A background failure that reaches the dispatcher would otherwise kill the app
        // silently while a TIA session is still open.
        DispatcherUnhandledException += OnUnhandledException;

        Settings = UiSettings.Load();
        ApplyCommandLineOverrides(args);

        Loc.Current.Language = Settings.Language;
        ThemeManager.Current.Initialize(Settings.Theme);

        // Persist whatever the window is showing when it closes, not at the moment of the click,
        // so a language toggled and then toggled back does not write twice.
        Exit += (_, _) =>
        {
            Settings.Language = Loc.Current.Language;
            Settings.Theme = ThemeManager.Current.Theme;
            Settings.Save();
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
            TiaMcpConfigurator.ConfigCore.ConfigureNetwork(args[1], int.Parse(args[2]), args[3]);
            return 0;
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("TIA_OPENNESS_NETWORK_NO_DIALOG") == "1") Console.Error.WriteLine(ex.Message);
            else MessageBox.Show(ex.Message, "TIA Portal", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>
    /// <c>--lang en|zh</c> and <c>--theme auto|light|dark</c>. Present so a screenshot or a demo
    /// can be pinned to one appearance without touching the saved preference.
    /// </summary>
    private static void ApplyCommandLineOverrides(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase))
            {
                Settings.Language = args[i + 1].ToLowerInvariant() switch
                {
                    "zh" or "zh-cn" or "chinese" => AppLanguage.Chinese,
                    "en" or "en-us" or "english" => AppLanguage.English,
                    _ => Settings.Language,
                };
            }
            else if (string.Equals(args[i], "--theme", StringComparison.OrdinalIgnoreCase)
                     && Enum.TryParse<AppTheme>(args[i + 1], ignoreCase: true, out var theme))
            {
                Settings.Theme = theme;
            }
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"{DateTimeOffset.Now:O}{System.Environment.NewLine}{e.Exception}{System.Environment.NewLine}{System.Environment.NewLine}");
        }
        catch (Exception) /* swallow(logging-failure): failure to append the crash log must not prevent the original exception dialog */
        {
            // Reporting the original failure matters more than logging it.
        }

        MessageBox.Show(
            e.Exception.Message + System.Environment.NewLine + System.Environment.NewLine +
            Loc.Current.T("Dialog.Error.Details", CrashLogPath),
            Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
