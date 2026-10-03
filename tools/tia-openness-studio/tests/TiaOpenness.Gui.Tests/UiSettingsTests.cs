using System;
using System.IO;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Settings;
using TiaOpenness.Gui.Themes;
using Xunit;

namespace TiaOpenness.Gui.Tests;

/// <summary>
/// The preference file is read before the window exists, so it has to survive being absent,
/// truncated or edited by hand without stopping the app from starting. Every one of these
/// cases falls back to the system default rather than throwing.
/// </summary>
public class UiSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "tia-ui-settings-" + Guid.NewGuid().ToString("N"), "ui.settings");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Round_trips_both_choices()
    {
        new UiSettings { Language = AppLanguage.Chinese, Theme = AppTheme.Dark }.Save(_path);

        var loaded = UiSettings.Load(_path);

        Assert.Equal(AppLanguage.Chinese, loaded.Language);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
    }

    [Fact]
    public void Save_creates_the_folder_it_needs()
    {
        new UiSettings().Save(_path);

        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void A_missing_file_gives_the_system_defaults()
    {
        var loaded = UiSettings.Load(_path);

        Assert.Equal(Loc.FromSystem(), loaded.Language);
        Assert.Equal(AppTheme.Auto, loaded.Theme);
    }

    [Fact]
    public void Junk_lines_are_skipped_and_the_good_ones_still_read()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path,
        [
            "this line has no separator",
            "=orphaned value",
            "unknown=setting",
            "theme=Dark",
        ]);

        var loaded = UiSettings.Load(_path);

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(Loc.FromSystem(), loaded.Language);
    }

    [Fact]
    public void An_unrecognised_value_leaves_that_setting_at_its_default()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path, ["language=Klingon", "theme=Neon"]);

        var loaded = UiSettings.Load(_path);

        Assert.Equal(Loc.FromSystem(), loaded.Language);
        Assert.Equal(AppTheme.Auto, loaded.Theme);
    }

    [Fact]
    public void Values_are_read_case_insensitively_so_the_file_can_be_edited_by_hand()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path, ["language=chinese", "theme=  light  "]);

        var loaded = UiSettings.Load(_path);

        Assert.Equal(AppLanguage.Chinese, loaded.Language);
        Assert.Equal(AppTheme.Light, loaded.Theme);
    }

    /// <summary>
    /// A path that cannot be written - a directory where the file should be - must not stop the
    /// window from closing.
    /// </summary>
    [Fact]
    public void An_unwritable_path_is_swallowed_rather_than_thrown()
    {
        Directory.CreateDirectory(_path);

        new UiSettings().Save(_path);
    }

    [Fact]
    public void The_real_settings_file_lives_under_the_local_profile()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, UiSettings.FilePath, StringComparison.OrdinalIgnoreCase);
    }
}
