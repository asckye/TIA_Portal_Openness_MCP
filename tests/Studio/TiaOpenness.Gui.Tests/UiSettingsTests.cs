using System;
using System.IO;
using System.Linq;
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
public sealed class UiSettingsTests : IDisposable
{
    [Fact]
    public void Native_cross_reference_consent_is_not_a_saved_preference()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "nativeCrossReferencesAllowed=true\nlanguage=English\n");
        UiSettings.Load(_path).Save(_path);
        Assert.DoesNotContain("cross", File.ReadAllText(_path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Native_cross_reference_consent_has_no_preference_setter()
    {
        Assert.DoesNotContain(typeof(UiSettings).GetProperties(), property => property.Name.Contains("CrossReference", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(UiSettings).GetMethods(), method => method.Name.Contains("CrossReference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Second_instance_signals_the_original_and_does_not_take_ownership()
    {
        using var activated = new System.Threading.ManualResetEventSlim();
        var scope = "Workbench.Gui.Tests." + Guid.NewGuid().ToString("N");
        using (var first = new WorkbenchInstance(scope, activated.Set))
        using (var second = new WorkbenchInstance(scope, () => throw new InvalidOperationException()))
        {
            Assert.True(first.IsFirst);
            Assert.False(second.IsFirst);
            Assert.True(activated.Wait(TiaMcp.Engine.Tests.Shared.TestDeadlines.Hang));
        }
        using var reopened = new WorkbenchInstance(scope, activated.Set);
        Assert.True(reopened.IsFirst);
    }

    [Fact]
    public void Locked_destination_preserves_previous_preferences_and_reports_failure()
    {
        var settings = new UiSettings { WorkbenchControlEnabled = false };
        Assert.Null(settings.Save(_path));
        var before = File.ReadAllBytes(_path);
        settings.WorkbenchControlEnabled = true;
        using (File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotNull(settings.Save(_path));
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(_path)!));
        using (File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(UiSettings.Load(_path).WorkbenchControlEnabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("workbenchControlEnabled=tr")]
    [InlineData("broken\nworkbenchControlEnabled=True")]
    [InlineData("workbenchControlEnabled=True\nworkbenchControlEnabled=True")]
    public void Damaged_preferences_disable_control(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, content);
        Assert.False(UiSettings.Load(_path).WorkbenchControlEnabled);
    }

    [Fact]
    public void Crash_report_displays_the_same_id_as_the_utc_redacted_log()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var message = App.CrashReport(new IOException("password=private-fixture"), _path);
        var first = File.ReadLines(_path).First().Split(' ');
        Assert.EndsWith("Z", first[0]);
        Assert.True(Guid.TryParseExact(first[1], "N", out _));
        Assert.Contains(first[1], message);
        Assert.DoesNotContain("private-fixture", File.ReadAllText(_path));
    }

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
        new UiSettings { Language = AppLanguage.Chinese, Theme = AppTheme.Dark, Accent = AppAccent.Petrol }.Save(_path);

        var loaded = UiSettings.Load(_path);

        Assert.Equal(AppLanguage.Chinese, loaded.Language);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(AppAccent.Petrol, loaded.Accent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Left_pane_preference_round_trips_and_defaults_open_for_existing_files(bool open)
    {
        new UiSettings { LeftPaneOpen = open }.Save(_path);
        Assert.Equal(open, UiSettings.Load(_path).LeftPaneOpen);
        File.WriteAllText(_path, "language=English");
        Assert.True(UiSettings.Load(_path).LeftPaneOpen);
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
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(AppAccent.Cobalt, loaded.Accent);
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
        File.WriteAllLines(_path, ["language=Klingon", "theme=Neon", "accent=Magenta"]);

        var loaded = UiSettings.Load(_path);

        Assert.Equal(Loc.FromSystem(), loaded.Language);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Equal(AppAccent.Cobalt, loaded.Accent);
    }

    [Fact]
    public void Command_line_overrides_accept_named_palettes_and_ignore_undefined_numbers()
    {
        var settings = new UiSettings();
        App.ApplyCommandLineOverrides(["--theme", "dark", "--accent", "petrol"], settings);
        App.ApplyCommandLineOverrides(["--theme", "99", "--accent", "-1"], settings);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal(AppAccent.Petrol, settings.Accent);
    }

    [Fact]
    public void Legacy_auto_theme_maps_cleanly_to_light()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path, ["theme=Auto"]);

        var loaded = UiSettings.Load(_path);

        Assert.Equal(AppTheme.Light, loaded.Theme);
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
    public void Settings_and_configuration_use_the_shared_data_locations()
    {
        Assert.Equal(TiaOpenness.Shared.DataLocations.Current.UiFilePath, UiSettings.FilePath);
        Assert.Equal(TiaOpenness.Shared.DataLocations.Current.ConfigDirectory, TiaMcpConfigurator.ConfigCore.StateDirectory);
    }

    [Fact]
    public void Core_and_gui_share_the_process_data_root_even_after_environment_changes()
    {
        var first = TiaOpenness.Shared.DataLocations.Current.Root;
        string? previous = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", Path.GetDirectoryName(_path));
            var core = typeof(TiaOpenness.Core.Mock.MockTiaSession).Assembly;
            var type = core.GetType("TiaOpenness.Shared.DataLocations", throwOnError: true)!;
            var locations = type.GetProperty("Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null);
            var root = type.GetProperty("Root", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(locations);
            Assert.Equal(first, root);
        }
        finally { Environment.SetEnvironmentVariable("TIA_MCP_DATA_DIRECTORY", previous); }
    }
}
