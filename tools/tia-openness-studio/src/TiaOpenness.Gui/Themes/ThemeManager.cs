using System;
using System.ComponentModel;
using System.Windows;

namespace TiaOpenness.Gui.Themes;

public enum AppTheme
{
    /// <summary>Follow the Windows light/dark setting, and keep following it as it changes.</summary>
    Auto,
    Light,
    Dark,
}

/// <summary>
/// Owns which palette is loaded.
///
/// The palette lives at a fixed slot in <see cref="Application.Resources"/>, and switching
/// appearance swaps that one dictionary. Everything downstream refers to palette keys by
/// DynamicResource, so the swap repaints the live window - no restart, and no window rebuild
/// that would lose the log, the selection or the open session.
/// </summary>
public sealed class ThemeManager : INotifyPropertyChanged
{
    /// <summary>Index of the palette inside Application.Resources.MergedDictionaries.</summary>
    private const int PaletteSlot = 0;

    // Absolute pack URIs, naming the assembly. A relative one would be resolved against
    // Application.ResourceAssembly, which is only populated when the app is started through its
    // generated entry point - so the palette would fail to load in any other host.
    //
    // The name is read from the assembly rather than written out, because a hardcoded one turns
    // renaming the executable into a runtime failure that only shows up when a palette loads.
    internal static readonly string PackPrefix =
        $"pack://application:,,,/{typeof(ThemeManager).Assembly.GetName().Name};component/Themes/";

    private static readonly Uri LightPalette = new(PackPrefix + "Palette.Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkPalette = new(PackPrefix + "Palette.Dark.xaml", UriKind.Absolute);

    private AppTheme _theme = AppTheme.Auto;
    private bool _systemIsDark;

    private ThemeManager() { }

    public static ThemeManager Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            Apply();
            foreach (var name in new[] { nameof(Theme), nameof(IsAuto), nameof(IsLight), nameof(IsDark) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    // Three one-way-ish flags so the appearance segmented control can bind without a converter.
    // Setting one to false does nothing: a segment is deselected by another being selected.
    public bool IsAuto
    {
        get => _theme == AppTheme.Auto;
        set { if (value) Theme = AppTheme.Auto; }
    }

    public bool IsLight
    {
        get => _theme == AppTheme.Light;
        set { if (value) Theme = AppTheme.Light; }
    }

    public bool IsDark
    {
        get => _theme == AppTheme.Dark;
        set { if (value) Theme = AppTheme.Dark; }
    }

    /// <summary>Whether the effective appearance - after resolving Auto - is dark.</summary>
    public bool EffectivelyDark => _theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => _systemIsDark,
    };

    /// <summary>
    /// Called once at startup, after App.xaml's dictionaries exist. Subscribes to the Windows
    /// appearance setting so Auto keeps tracking it rather than sampling it once.
    /// </summary>
    public void Initialize(AppTheme theme)
    {
        _systemIsDark = ReadSystemIsDark();
        _theme = theme;

        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception)
        {
            // No system-event hookup means Auto simply stops updating; not worth failing startup.
        }

        Apply();
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;

        var dark = ReadSystemIsDark();
        if (dark == _systemIsDark) return;

        _systemIsDark = dark;
        if (_theme != AppTheme.Auto) return;

        // The notification arrives on a system thread.
        Application.Current?.Dispatcher.Invoke(Apply);
    }

    private void Apply()
    {
        var application = Application.Current;
        if (application is null) return;

        var wanted = EffectivelyDark ? DarkPalette : LightPalette;
        var merged = application.Resources.MergedDictionaries;

        var palette = new ResourceDictionary { Source = wanted };
        if (merged.Count > PaletteSlot) merged[PaletteSlot] = palette;
        else merged.Insert(PaletteSlot, palette);
    }

    /// <summary>
    /// Reads the Windows "choose your default app mode" setting. Anything unreadable - a locked
    /// down registry, a future Windows that moves the value - is treated as light, which is the
    /// Windows default.
    /// </summary>
    private static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
