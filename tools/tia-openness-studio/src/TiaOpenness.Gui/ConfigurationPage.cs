using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Themes;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    internal ConfigWindow? Configuration { get; private set; }

    internal static string InitialReleaseKey(string[] args)
    {
        var index = Array.IndexOf(args, "--openness-version");
        var version = index >= 0 && index + 1 < args.Length ? args[index + 1] :
            TiaOpenness.Core.Environment.OpennessLocator.FindAll().FirstOrDefault()?.Version;
        var match = TiaMcp.Versioning.TiaVersionCatalog.Runnable.FirstOrDefault(release => release.Key == version || release.ApiVersion == version);
        if (index >= 0 && (index + 1 >= args.Length || match == null)) throw new ArgumentException("Unsupported or missing --openness-version.");
        return match?.Key ?? "21";
    }

    internal static string FindBundleRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "manifest", "package-manifest.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("The desktop must remain inside the complete TIA MCP bundle.");
    }

    internal void ShowConfiguration(bool loadExisting = true, string? bundleRoot = null)
    {
        if (Configuration == null)
        {
            var page = new ConfigWindow(this, bundleRoot ?? FindBundleRoot(AppContext.BaseDirectory), loadExisting);
            Configuration = page;
            page.SelectedReleaseKey = _model.SelectedReleaseKey;
            page.SetReleaseEnabled(_model.CanSelectRelease);
            page.CanUpdate = () => _model.CanSelectRelease;
            page.ServiceStateChanged += OnServiceStateChanged;
            ConfigurationHost.Content = page.View;
            Loc.Current.LanguageChanged += OnDesktopLanguageChanged;
            ThemeManager.Current.PropertyChanged += OnDesktopThemeChanged;
            _model.PropertyChanged += OnDesktopModelChanged;
            ApplyConfigurationAppearance();
        }
        EngineeringWorkspace.Visibility = Visibility.Collapsed;
        ConfigurationHost.Visibility = Visibility.Visible;
        EngineeringTab.IsChecked = false;
        ConfigurationTab.IsChecked = true;
    }

    internal void ShowEngineering()
    {
        ConfigurationHost.Visibility = Visibility.Collapsed;
        EngineeringWorkspace.Visibility = Visibility.Visible;
        EngineeringTab.IsChecked = true;
        ConfigurationTab.IsChecked = false;
    }

    private void OnConfiguration(object sender, RoutedEventArgs e)
    {
        try { ShowConfiguration(); }
        catch (Exception ex)
        {
            ShowEngineering();
            MessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void OnEngineering(object sender, RoutedEventArgs e) => ShowEngineering();
    private void OnServiceStateChanged(object? sender, EventArgs e) => _model.ServiceOwnsRelease = Configuration?.LocksRelease == true;
    private void OnDesktopModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Configuration == null) return;
        if (e.PropertyName == nameof(MainViewModel.SelectedReleaseKey)) Configuration.SelectedReleaseKey = _model.SelectedReleaseKey;
        if (e.PropertyName == nameof(MainViewModel.CanSelectRelease)) Configuration.SetReleaseEnabled(_model.CanSelectRelease);
    }
    private void OnDesktopLanguageChanged(object? sender, EventArgs e) => ApplyConfigurationAppearance();
    private void OnDesktopThemeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThemeManager.EffectivelyDark)) Configuration?.ApplyTheme(ThemeManager.Current.EffectivelyDark ? "Dark" : "Light");
    }
    private void ApplyConfigurationAppearance()
    {
        Configuration?.ApplyLanguage(Loc.Current.IsChinese ? "zh" : "en");
        Configuration?.ApplyTheme(ThemeManager.Current.EffectivelyDark ? "Dark" : "Light");
    }
    private void DisposeConfiguration()
    {
        Loc.Current.LanguageChanged -= OnDesktopLanguageChanged;
        ThemeManager.Current.PropertyChanged -= OnDesktopThemeChanged;
        _model.PropertyChanged -= OnDesktopModelChanged;
        if (Configuration != null)
        {
            Configuration.ServiceStateChanged -= OnServiceStateChanged;
            Configuration.Dispose();
        }
    }
}
