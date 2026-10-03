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
        // A background failure that reaches the dispatcher would otherwise kill the app
        // silently while a TIA session is still open.
        DispatcherUnhandledException += OnUnhandledException;

        Settings = UiSettings.Load();
        ApplyCommandLineOverrides(e.Args);

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

        base.OnStartup(e);
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
        catch (Exception)
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
