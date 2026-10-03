using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TiaMcpConfigurator;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow
{
    internal ConfigurationView? Configuration { get; private set; }

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
        EnsureConfiguration(loadExisting, bundleRoot);
        OptionsOverlay.Visibility = Visibility.Collapsed;
        _model.IsConfigurationPage = true;
    }

    internal void EnsureConfiguration(bool loadExisting = true, string? bundleRoot = null)
    {
        if (Configuration == null)
        {
            var page = new ConfigurationView(this, bundleRoot ?? FindBundleRoot(AppContext.BaseDirectory), loadExisting && _loadExistingConfiguration);
            Configuration = page;
            page.SelectedReleaseKey = _model.SelectedReleaseKey;
            page.SetReleaseEnabled(_model.CanSelectRelease);
            page.CanUpdate = () => _model.CanSelectRelease;
            page.ServiceStateChanged += OnServiceStateChanged;
            ConfigurationHost.Content = page;
            _model.PropertyChanged += OnDesktopModelChanged;
        }
    }

    internal void ShowEngineering() => _model.IsConfigurationPage = false;

    private void OnPageExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == Controls.WorkbenchCommands.Engineering) { ShowEngineering(); return; }
        try { ShowConfiguration(); }
        catch (Exception ex)
        {
            ShowEngineering();
            TiaOpenness.Gui.Controls.GlassMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void OnHelpOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != HelpMenu) return;
        try { EnsureConfiguration(); }
        catch (Exception ex)
        {
            TiaOpenness.Gui.Controls.GlassMessageBox.Show(this, ex.Message, Loc.Current["Dialog.Error.Caption"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void OnServiceStateChanged(object? sender, EventArgs e) => _model.ServiceOwnsRelease = Configuration?.LocksRelease == true;
    private void OnDesktopModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Configuration == null) return;
        if (e.PropertyName == nameof(MainViewModel.SelectedReleaseKey)) Configuration.SelectedReleaseKey = _model.SelectedReleaseKey;
        if (e.PropertyName == nameof(MainViewModel.CanSelectRelease)) Configuration.SetReleaseEnabled(_model.CanSelectRelease);
    }
    private void DisposeConfiguration()
    {
        _model.PropertyChanged -= OnDesktopModelChanged;
        if (Configuration != null)
        {
            Configuration.ServiceStateChanged -= OnServiceStateChanged;
            Configuration.Dispose();
        }
    }
}
