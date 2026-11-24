using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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
            TiaOpenness.Core.Environment.WorkbenchReleaseSelection.DefaultKey();
        var match = TiaMcp.Versioning.TiaVersionCatalog.Runnable.FirstOrDefault(release => release.Key == version || release.ApiVersion == version);
        if (index >= 0 && (index + 1 >= args.Length || match == null)) throw new ArgumentException("Unsupported or missing --openness-version.");
        return match?.Key ?? TiaOpenness.Core.Environment.WorkbenchReleaseSelection.DefaultKey();
    }

        internal static string FindBundleRoot(string start)
        {
            return TiaOpenness.Shared.BundleLayout.RequireWorkbenchRoot(start);
        }

    internal void ShowConfiguration(bool loadExisting = true, string? bundleRoot = null)
    {
        if (_model.OfflineDemo) { Navigate("Environment"); return; }
        EnsureConfiguration(loadExisting, bundleRoot);
        Navigate("Mcp");
    }

    internal void EnsureConfiguration(bool loadExisting = true, string? bundleRoot = null)
    {
        if (_model.OfflineDemo) return;
        if (Configuration == null)
        {
            var page =
                new ConfigurationView(this, bundleRoot ?? FindBundleRoot(AppContext.BaseDirectory), loadExisting && _loadExistingConfiguration, _preview);
            Configuration = page;
            page.SelectedReleaseKey = _model.SelectedReleaseKey;
            page.SetReleaseEnabled(_model.CanSelectRelease);
            page.CanUpdate = () => _model.CanSelectRelease;
            page.ServiceStateChanged += OnServiceStateChanged;
            page.ReleaseChanged += OnConfigurationReleaseChanged;
            ConfigurationHost.Content = page;
            ClientsHost.Content = page.Clients;
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
            ShowToast(ToastKind.Error, LocalizedText.Literal(ex.Message), "navigation");
        }
    }
    private void OnServiceStateChanged(object? sender, EventArgs e)
    {
        _model.ServiceOwnsRelease = Configuration?.LocksRelease == true;
        UpdateMcpStatus();
    }
    private void OnMcpLanguageChanged(object? sender, EventArgs e) => UpdateMcpStatus();
    /// <summary>The shell shows service and project state; connection details belong to MCP &amp; clients.</summary>
    private string ProjectFileName => Path.GetFileName(_model.Session.OpenProjectFile) is { Length: > 0 } name ? name : _model.Session.ProjectName;

    private void UpdateMcpStatus()
    {
        var state = Configuration?.ServiceStateKey ?? "Config.Idle";
        var release = TiaMcp.Versioning.TiaVersionCatalog.Get(_model.SelectedReleaseKey);
        var status = state == "Config.Local" ? Loc.Current["Pages.ServiceOnDemand"] : Loc.Current[state];
        string service = Loc.Current.T("Shell.McpStatus", status);
        System.Windows.Automation.AutomationProperties.SetHelpText(RailMcp, service);
        if (_sessionPopover != null) SetServiceHelp(_sessionPopover.SwitchVersion, service);
        string prefix = release.DisplayName + " · " + release.OwnerApartment + "   ";
        string file = HasProject ? ProjectFileName : Loc.Current[ConnectionLost ? "Shell.SessionLost" : "Shell.NoProjectFile"];
        string fullText = prefix + file;
        if (SessionStatus.Text != fullText)
        {
            SessionStatus.Text = fullText;
            // Set the complete accessible text first, then format its two runs without replacing the text.
            var run = (Run)SessionStatus.Inlines.FirstInline!;
            var end = run.ContentStart.GetPositionAtOffset(prefix.Length)!;
            new TextRange(run.ContentStart, end).ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
            var label = (Run)SessionStatus.Inlines.FirstInline!;
            var name = (Run)SessionStatus.Inlines.LastInline!;
            label.SetResourceReference(TextElement.ForegroundProperty, ConnectionLost ? "Ant.colorError" : "Primer.text");
            if (ConnectionLost)
            {
                name.SetResourceReference(TextElement.ForegroundProperty, "Ant.colorError");
                SessionStatusButton.SetResourceReference(BackgroundProperty, "Ant.colorErrorBg");
            }
            else SessionStatusButton.ClearValue(BackgroundProperty);
            label.SetResourceReference(TextElement.FontFamilyProperty, "Ui.FontMono");
            name.FontWeight = FontWeights.Normal;
            name.SetResourceReference(TextElement.FontFamilyProperty, "Ui.Font");
            double prefixWidth = MeasureSessionRun(label);
            double fileWidth = MeasureSessionRun(name);
            SessionStatus.Width = prefixWidth + (ConnectionLost ? fileWidth : Math.Min(170, fileWidth));
            SessionStatus.MaxWidth = prefixWidth + (ConnectionLost ? fileWidth : 170);
        }
        SessionStatus.ToolTip = SessionStatus.Text;
    }

    private double MeasureSessionRun(Run run)
    {
        var text = new TextBlock { Text = run.Text, FontFamily = run.FontFamily, FontWeight = run.FontWeight, FontSize = run.FontSize };
        TextOptions.SetTextFormattingMode(text, TextOptions.GetTextFormattingMode(SessionStatus));
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return text.DesiredSize.Width;
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
