using System;
using System.Collections.Generic;
using System.IO;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;

namespace TiaOpenness.Gui.Settings;

/// <summary>
/// Preferences remembered between runs: language, theme, brand accent and rendering effects.
/// Operators can inspect or remove the key=value file with a text editor. Missing
/// or unreadable preferences fall back to defaults without a schema migration.
/// </summary>
public sealed class UiSettings
{
    internal static TiaOpenness.Shared.JournalRetention LoadRetention(string path) => TiaOpenness.Shared.JournalRetention.Load(path);
    internal static void SaveRetention(string path, int fileSizeMb, int copies) => new TiaOpenness.Shared.JournalRetention(fileSizeMb, copies).Save(path);
    private const string LanguageKey = "language";
    private const string ThemeKey = "theme";
    private const string AccentKey = "accent";

    public AppLanguage Language { get; set; } = Loc.FromSystem();

    public AppTheme Theme { get; set; } = AppTheme.Light;
    public AppAccent Accent { get; set; } = AppAccent.Cobalt;
    public LowEffectsMode LowEffects { get; set; } = LowEffectsMode.Auto;
    public bool WorkbenchControlEnabled { get; set; } = true;

    public static string FilePath { get; } = TiaOpenness.Shared.DataLocations.Current.UiFilePath;

    public static UiSettings Load() => Load(FilePath);

    /// <summary>Tests pass an isolated path to exercise the round trip outside the user profile.</summary>
    public static UiSettings Load(string path)
    {
        var settings = new UiSettings();

        try
        {
            if (!File.Exists(path)) return settings;

            foreach (var line in File.ReadAllLines(path))
            {
                var split = line.IndexOf('=');
                if (split <= 0) continue;

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                if (key == LanguageKey && Enum.TryParse<AppLanguage>(value, ignoreCase: true, out var language))
                {
                    settings.Language = language;
                }
                else if (key == "lowEffects" && Enum.TryParse<LowEffectsMode>(value, true, out var effects) && Enum.IsDefined(effects))
                {
                    settings.LowEffects = effects;
                }
                else if (key == "workbenchControlEnabled" && bool.TryParse(value, out var controlEnabled))
                {
                    settings.WorkbenchControlEnabled = controlEnabled;
                }
                else if (key == ThemeKey)
                {
                    if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase)) settings.Theme = AppTheme.Light;
                    else if (Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme) && Enum.IsDefined(theme)) settings.Theme = theme;
                }
                else if (key == AccentKey && Enum.TryParse<AppAccent>(value, ignoreCase: true, out var accent) && Enum.IsDefined(accent))
                {
                    settings.Accent = accent;
                }
            }
        }
        catch (Exception) /* swallow(ui): unreadable preferences leave defaults or already parsed values available so the workbench can start */
        {
            // A preference file that cannot be read is not a reason to refuse to start.
        }

        return settings;
    }

    public void Save() => Save(FilePath);

    public void Save(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllLines(path, new List<string>
            {
                $"{LanguageKey}={Language}",
                $"{ThemeKey}={Theme}",
                $"{AccentKey}={Accent}",
                $"lowEffects={LowEffects}",
                $"workbenchControlEnabled={WorkbenchControlEnabled}",
            });
        }
        catch (Exception) /* swallow(ui): failure to persist language and theme preferences must not prevent the window from closing */
        {
            // Losing a preference is a smaller failure than refusing to close the window.
        }
    }
}
