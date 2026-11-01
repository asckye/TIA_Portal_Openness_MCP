using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;

namespace TiaMcpConfigurator;

public sealed partial class ConfigurationView
{
    private readonly DispatcherTimer hostTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private HostLifecycleService? hosts;
    private bool refreshingHosts;
    private void InitializeHosts(bool enabled)
    {
        HostList.SelectionChanged += (_, _) => UpdateHostActions();
        ClickAsync("RefreshHosts", RefreshHostListAsync);
        ClickAsync("StopHost", () => ChangeHost(false));
        ClickAsync("RestartHost", () => ChangeHost(true));
        if (!enabled) return;
        hosts = new HostLifecycleService(DataLocations.Current.WorkbenchDataDirectory, TiaOpenness.Gui.ViewModels.MainViewModel.AppVersion);
        hostTimer.Tick += RefreshHostsTick;
        hostTimer.Start();
        _ = RefreshHostListAsync();
    }
    private async void RefreshHostsTick(object? sender, EventArgs args)
    {
        try { await RefreshHostListAsync(); }
        catch (Exception error) { Report(error); }
    }
    private async Task RefreshHostListAsync()
    {
        if (hosts == null || refreshingHosts || closing) return;
        refreshingHosts = true;
        try
        {
            var selected = HostList.SelectedItem as VisibleHost;
            var found = await Task.Run(hosts.Discover);
            if (closing) return;
            HostList.ItemsSource = found;
            HostList.SelectedItem = selected == null ? null : found.FirstOrDefault(host => host.Pid == selected.Pid && host.StartTicks == selected.StartTicks);
            UpdateHostActions();
        }
        finally { refreshingHosts = false; }
    }
    private void UpdateHostActions()
    {
        var host = HostList.SelectedItem as VisibleHost;
        StopHost.IsEnabled = !busy && host != null;
        RestartHost.IsEnabled = !busy && host?.Transport == "http";
        HostDetail.Text = host == null ? Loc.Current["Hosts.Hint"]
            : (host.OlderVersion ? Loc.Current["Hosts.Older"] + "\n" : "")
                + (host.WorkbenchStarted ? Loc.Current["Hosts.Owned"] : Loc.Current["Hosts.External"])
                + (host.Transport == "stdio" ? "\n" + Loc.Current["Hosts.Stdio"] : "");
    }
    private async Task ChangeHost(bool restart)
    {
        if (HostList.SelectedItem is not VisibleHost host) return;
        // Make a restart concrete and validate it before stopping a running process.
        System.Diagnostics.ProcessStartInfo? start = null;
        string restartEndpoint = "";
        if (restart)
        {
            if (host.Transport != "http") throw new InvalidOperationException(Loc.Current["Hosts.Stdio"]);
            string path = Path.Combine(ConfigCore.StateDirectory, "http-v" + host.ReleaseKey + ".json");
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(path));
            if (settings == null) throw new InvalidDataException(Loc.Current["Hosts.ConfigMismatch"]);
            string prefix = ConfigCore.Prefix(settings.Address, settings.Port);
            restartEndpoint = prefix;
            if (settings.EffectiveReleaseKey != host.ReleaseKey || host.HttpPrefix != null && prefix != host.HttpPrefix)
                throw new InvalidOperationException(Loc.Current["Hosts.ConfigMismatch"]);
            start = new(ConfigCore.Engine(root, host.ReleaseKey), ConfigCore.Arguments(settings, ConfigCore.Unprotect(settings.ProtectedKey), root))
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            start.Environment["TIA_MCP_DATA_DIRECTORY"] = DataLocations.Current.WorkbenchDataDirectory;
            start.Environment["TIA_MCP_WORKBENCH_STARTED"] = "1";
        }
        string prompt = Loc.Current.T("Hosts.Confirm", host.Display);
        if (start != null) prompt += "\n\n" + Loc.Current.T("Hosts.RestartTarget", restartEndpoint, start.FileName);
        if (WorkbenchMessageBox.Show(Window, prompt,
            Loc.Current[restart ? "Hosts.Restart" : "Hosts.Stop"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        SetBusy(true);
        try
        {
            if (start != null)
            {
                using var process = await HostLifecycleService.Restart(host, start);
            }
            else await HostLifecycleService.Stop(host);
            await RefreshHostListAsync();
        }
        finally { SetBusy(false); UpdateHostActions(); }
    }
}
