using System;
using System.Text;
using System.IO;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;

namespace TiaOpenness.Gui.Settings;

/// <summary>
/// Preferences remembered between runs: language, theme, brand accent and rendering effects.
/// The editable key=value file preserves appearance preferences. Invalid files disable AI control.
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
    public bool LeftPaneOpen { get; set; } = true;
    public bool WorkbenchControlEnabled { get; set; } = true;

    public static string FilePath { get; } = TiaOpenness.Shared.DataLocations.Current.UiFilePath;

    public static UiSettings Load() => Load(FilePath);

    /// <summary>Tests pass an isolated path to exercise the round trip outside the user profile.</summary>
    public static UiSettings Load(string path)
    {
        var settings = new UiSettings();

        try
        {
            bool valid = true;
            bool? control = null;

            foreach (var line in File.ReadAllLines(path, new UTF8Encoding(false, true)))
            {
                var split = line.IndexOf('=');
                if (split <= 0) { if (!string.IsNullOrWhiteSpace(line)) valid = false; continue; }

                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();

                if (key == LanguageKey && Enum.TryParse<AppLanguage>(value, ignoreCase: true, out var language) && Enum.IsDefined(language))
                {
                    settings.Language = language;
                }
                else if (key == "lowEffects" && Enum.TryParse<LowEffectsMode>(value, true, out var effects) && Enum.IsDefined(effects))
                {
                    settings.LowEffects = effects;
                }
                else if (key == "workbenchControlEnabled")
                {
                    if (control.HasValue || !bool.TryParse(value, out var enabled)) valid = false;
                    else control = enabled;
                }
                else if (key == "leftPaneOpen" && bool.TryParse(value, out var leftPaneOpen))
                {
                    settings.LeftPaneOpen = leftPaneOpen;
                }
                else if (key == ThemeKey)
                {
                    if (string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase)) settings.Theme = AppTheme.Light;
                    else if (Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme) && Enum.IsDefined(theme)) settings.Theme = theme;
                    else valid = false;
                }
                else if (key == AccentKey && Enum.TryParse<AppAccent>(value, ignoreCase: true, out var accent) && Enum.IsDefined(accent))
                {
                    settings.Accent = accent;
                }
                else if (key is LanguageKey or ThemeKey or AccentKey or "lowEffects" or "leftPaneOpen") valid = false;
            }
            settings.WorkbenchControlEnabled = valid && control == true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return settings; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            settings.WorkbenchControlEnabled = false;
        }

        return settings;
    }

    public string? Save() => Save(FilePath);

    public string? Save(string path)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                writer.WriteLine($"{LanguageKey}={Language}");
                writer.WriteLine($"{ThemeKey}={Theme}");
                writer.WriteLine($"{AccentKey}={Accent}");
                writer.WriteLine($"leftPaneOpen={LeftPaneOpen}");
                writer.WriteLine($"lowEffects={LowEffects}");
                writer.WriteLine($"workbenchControlEnabled={WorkbenchControlEnabled}");
                writer.Flush();
                file.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Loc.Current.T("Common.IoFailed", error.Message);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Preference temp cleanup failed: {0}", error.Message); }
        }
    }
}
