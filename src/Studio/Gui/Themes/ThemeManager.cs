using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace TiaOpenness.Gui.Themes;

public enum LowEffectsMode { Auto, On, Off }

public enum AppTheme
{
    Light,
    Dark,
}

public enum AppAccent
{
    Cobalt,
    Petrol,
}

/// <summary>
/// Owns which palette is loaded (Light/Dark × Precision Cobalt / Siemens TIA Petrol,
/// plus automatic OS High Contrast). Swapping MergedDictionaries[0] repaints the live
/// window immediately without restarting or dropping open sessions.
/// </summary>
public sealed class ThemeManager : INotifyPropertyChanged
{
    // DynamicResource bindings follow this slot across palette changes and retain the live window state.
    private const int PaletteSlot = 0;

    // Absolute pack URIs work in hosts that have no Application.ResourceAssembly.
    // Reading the assembly name here also keeps palette lookup valid after an executable rename.
    internal static readonly string PackPrefix =
        $"pack://application:,,,/{typeof(ThemeManager).Assembly.GetName().Name};component/Themes/";

    private static readonly Uri LightCobaltPalette = new(PackPrefix + "Palette.Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkCobaltPalette = new(PackPrefix + "Palette.Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightPetrolPalette = new(PackPrefix + "Palette.Light.Petrol.xaml", UriKind.Absolute);
    private static readonly Uri DarkPetrolPalette = new(PackPrefix + "Palette.Dark.Petrol.xaml", UriKind.Absolute);
    private static readonly Uri HighContrastPalette = new(PackPrefix + "Palette.HighContrast.xaml", UriKind.Absolute);

    private bool? _highContrastOverride;
    public bool EffectivelyHighContrast => _highContrastOverride ?? SystemParameters.HighContrast;
    public Color SelectionColor => SystemColors.HighlightColor;
    public Color SelectionTextColor => SystemColors.HighlightTextColor;
    internal bool? HighContrastOverride { get => _highContrastOverride; set { _highContrastOverride = value; Apply(); } }

    private AppTheme _theme = AppTheme.Light;
    private AppAccent _accent = AppAccent.Cobalt;
    private LowEffectsMode _lowEffects;
    private bool _virtualMachine;
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
            foreach (var name in new[] { nameof(Theme), nameof(IsLight), nameof(IsDark) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public AppAccent Accent
    {
        get => _accent;
        set
        {
            if (_accent == value) return;
            _accent = value;
            Apply();
            foreach (var name in new[] { nameof(Accent), nameof(IsCobalt), nameof(IsPetrol) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
    // Primer has no visual effects. Keep the persisted low-effects setting readable.
    private void ApplyEffects() { }
    private void OnRenderingChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
    { ApplyEffects(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseLowEffects))); }));
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.IsRemoteSession)) OnRenderingChanged(sender, EventArgs.Empty);
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
    }
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

    // The selectors bind directly to these flags. Selecting another segment handles deselection.
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

    public bool IsCobalt
    {
        get => _accent == AppAccent.Cobalt;
        set { if (value) Accent = AppAccent.Cobalt; }
    }

    public bool IsPetrol
    {
        get => _accent == AppAccent.Petrol;
        set { if (value) Accent = AppAccent.Petrol; }
    }

    public bool EffectivelyDark => _theme == AppTheme.Dark;

    /// <summary>Called after App.xaml resources load. Track OS high contrast and rendering changes while the window is open.</summary>
    public void Initialize(AppTheme theme, LowEffectsMode lowEffects = LowEffectsMode.Auto, AppAccent accent = AppAccent.Cobalt)
    {
        _theme = theme;
        _accent = accent;
        _lowEffects = lowEffects;
        _virtualMachine = DetectVirtualMachine();
        RenderCapability.TierChanged -= OnRenderingChanged; RenderCapability.TierChanged += OnRenderingChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged; SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;

        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception) /* swallow(ui): unavailable system events leave High Contrast tracking inactive without blocking startup */
        {
        }

        Apply();
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color
            or Microsoft.Win32.UserPreferenceCategory.Accessibility or Microsoft.Win32.UserPreferenceCategory.VisualStyle)) return;
        if (!EffectivelyHighContrast) return;
        // The notification arrives on a system thread.
        Application.Current?.Dispatcher.Invoke(Apply);
    }

    internal static Uri PaletteUri(AppTheme theme, AppAccent accent) => (theme, accent) switch
    {
        (AppTheme.Dark, AppAccent.Petrol) => DarkPetrolPalette,
        (AppTheme.Dark, _) => DarkCobaltPalette,
        (_, AppAccent.Petrol) => LightPetrolPalette,
        _ => LightCobaltPalette,
    };

    private void Apply()
    {
        var application = Application.Current;
        if (application is null) return;

        var wanted = EffectivelyHighContrast ? HighContrastPalette : PaletteUri(_theme, _accent);
        var merged = application.Resources.MergedDictionaries;

        var palette = new ResourceDictionary { Source = wanted };
        if (merged.Count > PaletteSlot) merged[PaletteSlot] = palette;
        else merged.Insert(PaletteSlot, palette);
        ApplyEffects();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectivelyHighContrast)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionColor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionTextColor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectivelyDark)));
    }
}
