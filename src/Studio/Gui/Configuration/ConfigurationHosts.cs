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
        ClickAsync("RefreshHosts", RefreshHostListAsync);
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
            var found = await Task.Run(hosts.Discover);
            if (closing) return;
            if (!HostList.Items.Cast<VisibleHost>().SequenceEqual(found))
            {
                var selected = HostList.SelectedItem as VisibleHost;
                HostList.ItemsSource = found;
                if (selected != null)
                    HostList.SelectedItem = found.FirstOrDefault(host => host.Pid == selected.Pid && host.StartTicks == selected.StartTicks);
            }
            if (server != null && !server.HasExited)
                Find<Button>("StopServer").IsEnabled = !busy && found.FirstOrDefault(host => host.Pid == server.Id)?.CallInProgress != true;
        }
        finally { refreshingHosts = false; }
    }
    private void OnRestartHost(object sender, RoutedEventArgs e) => ReviewHostAction((Button)sender, true);
    private void OnStopHost(object sender, RoutedEventArgs e) => ReviewHostAction((Button)sender, false);

    private async void ReviewHostAction(Button button, bool restart)
    {
        if (busy || button.DataContext is not VisibleHost host) return;
        try { await ChangeHost(host, restart); }
        catch (Exception error) { Report(error); }
    }

    private async Task ChangeHost(VisibleHost host, bool restart)
    {
        if (host.CallInProgress) throw new InvalidOperationException(Loc.Current["Hosts.Busy"]);
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
            start = new(ConfigCore.Engine(root, host.ReleaseKey), ConfigCore.Arguments(settings, root))
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            HostLifecycleService.PrepareWorkbenchHost(start);
        }
        string prompt = Loc.Current.T("Hosts.Confirm", host.Display);
        if (start != null) prompt += "\n\n" + Loc.Current.T("Hosts.RestartTarget", restartEndpoint, start.FileName);
        if (WorkbenchMessageBox.Show(Window, prompt,
            Loc.Current[restart ? "Hosts.Restart" : "Hosts.Stop"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        SetBusy(true);
        try
        {
            Task<bool> ConfirmForce(VisibleHost target) => Window.Dispatcher.InvokeAsync(() => WorkbenchMessageBox.Show(Window,
                Loc.Current.T("Hosts.ForceConfirm", target.Display), Loc.Current["Hosts.Stop"],
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK).Task;
            if (start != null)
            {
                using var process = await HostLifecycleService.Restart(host, start, confirmForce: ConfirmForce);
            }
            else await HostLifecycleService.Stop(host, confirmForce: ConfirmForce);
            if (restart) SetStatus("Hosts.Restarted", host.Display);
            else SetStatus("Hosts.Stopped", host.Display);
            await RefreshHostListAsync();
        }
        finally { SetBusy(false); }
    }
}
