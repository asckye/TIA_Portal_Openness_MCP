using System;
using System.ComponentModel;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace TiaOpenness.Gui.Themes;

public enum LowEffectsMode { Auto, On, Off }

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
    private LowEffectsMode _lowEffects;
    private bool _virtualMachine;
    private readonly Dictionary<string, object> _normalShadows = new();
    private static readonly string[] ShadowKeys = ["Glass.AccentShadow", "Glass.PopupShadow", "Glass.DrawerShadow", "Glass.SurfaceShadow", "Glass.RailShadow"];

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

    public LowEffectsMode LowEffects
    {
        get => _lowEffects;
        set
        {
            if (_lowEffects == value) return;
            _lowEffects = value; ApplyEffects();
            foreach (string name in new[] { nameof(LowEffects), nameof(IsEffectsAuto), nameof(IsEffectsOn), nameof(IsEffectsOff), nameof(UseLowEffects) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
    public bool IsEffectsAuto { get => LowEffects == LowEffectsMode.Auto; set { if (value) LowEffects = LowEffectsMode.Auto; } }
    public bool IsEffectsOn { get => LowEffects == LowEffectsMode.On; set { if (value) LowEffects = LowEffectsMode.On; } }
    public bool IsEffectsOff { get => LowEffects == LowEffectsMode.Off; set { if (value) LowEffects = LowEffectsMode.Off; } }
    public bool UseLowEffects => ResolveLowEffects(LowEffects, RenderCapability.Tier >> 16, SystemParameters.IsRemoteSession, _virtualMachine);
    internal static bool ResolveLowEffects(LowEffectsMode mode, int tier, bool remote, bool vm)
        => mode == LowEffectsMode.On || mode == LowEffectsMode.Auto && (tier == 0 || remote || vm);
    private void ApplyEffects()
    {
        var app = Application.Current;
        if (app == null || app.Resources.MergedDictionaries.Count == 0) return;
        var palette = app.Resources.MergedDictionaries[PaletteSlot];
        foreach (string key in ShadowKeys)
        {
            if (!_normalShadows.ContainsKey(key)) _normalShadows[key] = palette[key];
            palette[key] = UseLowEffects ? null : _normalShadows[key];
        }
        app.Resources["Glass.PopupAnimation"] = UseLowEffects ? PopupAnimation.None : PopupAnimation.Fade;
        foreach (string key in new[] { "Ui.CardBackground", "Ui.MenuBackground" })
        {
            var brush = (SolidColorBrush)palette[key];
            var color = brush.Color;
            var background = ((SolidColorBrush)palette["Ui.WindowBackground"]).Color;
            double alpha = color.A / (double)byte.MaxValue * brush.Opacity;
            color.R = (byte)Math.Round(color.R * alpha + background.R * (1 - alpha));
            color.G = (byte)Math.Round(color.G * alpha + background.G * (1 - alpha));
            color.B = (byte)Math.Round(color.B * alpha + background.B * (1 - alpha));
            color.A = byte.MaxValue;
            app.Resources[key] = UseLowEffects ? new SolidColorBrush(color) : brush;
        }
    }
    private void OnRenderingChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
    { ApplyEffects(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseLowEffects))); }));
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.IsRemoteSession)) OnRenderingChanged(sender, EventArgs.Empty); }
    private static bool DetectVirtualMachine()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string identity = Convert.ToString(key?.GetValue("SystemManufacturer")) + " " + Convert.ToString(key?.GetValue("SystemProductName"));
            return new[] { "VMware", "VirtualBox", "Virtual Machine", "QEMU", "KVM", "Xen", "Parallels" }
                .Any(value => identity.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceInformation("VM rendering hint unavailable: " + ex.GetType().Name); return false; }
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
    public void Initialize(AppTheme theme, LowEffectsMode lowEffects = LowEffectsMode.Auto)
    {
        _systemIsDark = ReadSystemIsDark();
        _theme = theme;
        _lowEffects = lowEffects; _virtualMachine = DetectVirtualMachine();
        RenderCapability.TierChanged -= OnRenderingChanged; RenderCapability.TierChanged += OnRenderingChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged; SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;

        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception) /* swallow(ui): unavailable system events leave Auto using its startup appearance sample without blocking initialization */
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
        foreach (string key in ShadowKeys) _normalShadows[key] = palette[key];
        ApplyEffects();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectivelyDark)));
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
        catch (Exception) /* swallow(env-probe): an unreadable Windows appearance setting uses the light-theme fallback */
        {
            return false;
        }
    }
}
