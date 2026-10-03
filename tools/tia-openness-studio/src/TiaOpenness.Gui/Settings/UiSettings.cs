using System;
using System.Collections.Generic;
using System.IO;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;

namespace TiaOpenness.Gui.Settings;

/// <summary>
/// The two choices worth remembering between runs: language and appearance.
///
/// Deliberately a two-line key=value file rather than JSON or the registry - it has no schema to
/// version, a corrupt or half-written file costs nothing because every read falls back to the
/// system default, and an operator can read or delete it without tooling. Nothing here is worth
/// a dependency.
/// </summary>
public sealed class UiSettings
{
    private const string LanguageKey = "language";
    private const string ThemeKey = "theme";

    public AppLanguage Language { get; set; } = Loc.FromSystem();

    public AppTheme Theme { get; set; } = AppTheme.Auto;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TiaOpennessStudio",
        "ui.settings");

    public static UiSettings Load() => Load(FilePath);

    /// <summary>Overload taking a path so the round trip can be exercised without touching
    /// the real user profile.</summary>
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
                else if (key == ThemeKey && Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme))
                {
                    settings.Theme = theme;
                }
            }
        }
        catch (Exception)
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
            });
        }
        catch (Exception)
        {
            // Losing a preference is a smaller failure than refusing to close the window.
        }
    }
}
