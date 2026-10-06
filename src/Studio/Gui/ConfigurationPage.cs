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
            return TiaOpenness.Shared.BundleLayout.RequireWorkbenchRoot(start);
        }

    internal void ShowConfiguration(bool loadExisting = true, string? bundleRoot = null)
    {
        EnsureConfiguration(loadExisting, bundleRoot);
        Navigate("Mcp");
    }

    internal void EnsureConfiguration(bool loadExisting = true, string? bundleRoot = null)
    {
        if (Configuration == null)
        {
            var page = new ConfigurationView(this, bundleRoot ?? FindBundleRoot(AppContext.BaseDirectory), loadExisting && _loadExistingConfiguration, _preview);
            Configuration = page;
            page.SelectedReleaseKey = _model.SelectedReleaseKey;
            page.SetReleaseEnabled(_model.CanSelectRelease);
            page.CanUpdate = () => _model.CanSelectRelease;
            page.ServiceStateChanged += OnServiceStateChanged;
            page.ReleaseChanged += OnConfigurationReleaseChanged;
            ConfigurationHost.Content = page;
            _model.PropertyChanged += OnDesktopModelChanged;
            UpdateMcpStatus();
        }
    }

    internal void ShowEngineering() => Navigate("Engineering");

    private void OnConfigurationReleaseChanged(object? sender, EventArgs e)
    {
        if (Configuration != null) _model.SelectedReleaseKey = Configuration.SelectedReleaseKey;
        UpdateShell();
    }

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
    private void OnServiceStateChanged(object? sender, EventArgs e)
    {
        _model.ServiceOwnsRelease = Configuration?.LocksRelease == true;
        UpdateMcpStatus();
    }
    private void OnMcpLanguageChanged(object? sender, EventArgs e) => UpdateMcpStatus();
    /// <summary>The MCP service state stays visible on both pages, the way a service host shows its engine state.</summary>
    private void UpdateMcpStatus()
    {
        var state = Configuration?.ServiceStateKey ?? "Config.Idle";
        var version = TiaMcp.Versioning.TiaVersionCatalog.Get(_model.SelectedReleaseKey).DisplayName;
        var endpoint = Configuration?.ServiceEndpoint ?? Loc.Current["Config.NoAddress"];
        var status = state == "Config.Local" ? "stdio" : Loc.Current[state];
        McpStatusText.Text = Loc.Current.T("Shell.Status", status, version, endpoint) + (HasProject ? " · " + _model.Session.ProjectName : "");
        if (Approvals != null) McpStatusText.Text += " · " + Loc.Current[Approvals.Enabled ? "Approval.EnabledStatus" : "Shell.ApprovalOff"];
        McpStatusText.ToolTip = McpStatusText.Text;
        McpStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state == "Config.Running" ? "Ui.Accent" : "Ui.StatusIdle");
    }
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
            Configuration.ReleaseChanged -= OnConfigurationReleaseChanged;
            Configuration.Dispose();
        }
    }
}
