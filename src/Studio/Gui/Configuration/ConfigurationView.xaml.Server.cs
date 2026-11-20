using System.Collections.Concurrent;
using System.Threading;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TiaMcp.Versioning;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;

namespace TiaMcpConfigurator
{
    public sealed partial class ConfigurationView
    {
        private void LoadServer(bool loadExisting)
        {
            Find<TextBox>("TiaPath").Text = Path
                .Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Siemens", "Automation", TiaVersionCatalog.Get(SelectedVersion)
                    .InstallFolder);
            Find<TextBox>("ServerAddress").Text = ""; Find<TextBox>("ServerPort").Text = "8765";
            if (!loadExisting || !File.Exists(StatePath)) { DetectTiaPath(false); return; }
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(StatePath));
            ConfigCore.Prefix(settings.Address, settings.Port);
            if (settings.EffectiveReleaseKey != SelectedVersion) throw new InvalidDataException(Loc.Current["Config.SavedReleaseMismatch"]);
            Find<TextBox>("TiaPath").Text = settings.TiaPath; Find<TextBox>("ServerAddress").Text = settings.Address; Find<TextBox>("ServerPort").Text =
                settings.Port.ToString();
            Append(Loc.Current.T("Config.ServerLoaded", SelectedVersion));
        }
        private ServerSettings Settings()
        {
            int port = int.Parse(Text("ServerPort"), System.Globalization.CultureInfo.InvariantCulture); ConfigCore.Prefix(Text("ServerAddress"), port);
            ConfigCore.Engine(root, SelectedVersion); ConfigCore.ValidateTia(Text("TiaPath"), SelectedVersion);
            return new ServerSettings {
                Version = TiaVersionCatalog.Get(SelectedVersion).MajorVersion, ReleaseKey = SelectedVersion, Address = Text("ServerAddress"), Port = port,
                    TiaPath = Text("TiaPath")
            };
        }
        private async Task OnNetwork()
        {
            if (busy) return;
            try
            {
                string ip = Text("ServerAddress"); int port = int.Parse(Text("ServerPort"), System.Globalization.CultureInfo.InvariantCulture); ConfigCore
                    .Prefix(ip, port);
                if (TiaOpenness.Gui.Controls.WorkbenchMessageBox
                        .Show(Window, Loc.Current["Config.NetworkPrompt"], Loc.Current["Config.Network"], MessageBoxButton.OKCancel, MessageBoxImage
                            .Information) != MessageBoxResult
                        .OK)
                    return;
                var args = new[] { "--network", ip, port.ToString(), WindowsIdentity.GetCurrent().User.Value };
                var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, string.Join(" ", args.Select(ConfigCore.Quote))) {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                SetBusy(true); SetStatus("Config.ConfiguringNetwork");
                using (var process = Process.Start(info))
                {
                    await Task.Run(() => process.WaitForExit());
                    if (process.ExitCode != 0) throw new InvalidOperationException(Loc.Current["Config.NetworkIncomplete"]);
                }
                SetStatus("Config.NetworkConfigured"); Append(Loc.Current["Config.NetworkReady"]);
            }
            catch (Exception ex) { Report(ex); }
            finally { SetBusy(false); }
        }
        private void OnStartServer()
        {
            if (server != null && !server.HasExited) throw new InvalidOperationException(Loc.Current["Config.ServiceAlreadyStarted"]);
            var settings = Settings(); ConfigCore.CheckListener(ConfigCore.Prefix(settings.Address, settings.Port));
            ConfigCore.AtomicJson(StatePath, settings);
            var info = new ProcessStartInfo(ConfigCore.Engine(root, SelectedVersion), ConfigCore.Arguments(settings, root)) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            HostLifecycleService.PrepareWorkbenchHost(info);
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Append(e.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Append(e.Data); };
            process.Exited += delegate {
                // Closing the window stops the service and then releases the process object; this event and the
                // queued UI update can run after that release, so neither may touch a disposed Process.
                int? exitCode = null;
                try { exitCode = process.ExitCode; }
                catch (InvalidOperationException) /* swallow(teardown): the window already released the service process while closing */ { }
                if (exitCode.HasValue) Append(Loc.Current.T("Config.ServiceExited", exitCode.Value));
                if (!closing) Window.Dispatcher
                    .BeginInvoke(new Action(delegate {
                            if (closing) return; Find<Button>("StartServer").IsEnabled = true; Find<Button>("StopServer").IsEnabled = false;
                            Find<ComboBox>("Version").IsEnabled = true; SetStatus("Config.ServiceStopped"); ServiceStatus(false);
                        }));
            };
            try { if (!process.Start()) throw new InvalidOperationException(Loc.Current["Config.ServiceNotStarted"]); }
            catch { process.Dispose(); throw; }
            server = process;
            Find<Button>("StartServer").IsEnabled = false; Find<Button>("StopServer").IsEnabled = true; Find<ComboBox>("Version").IsEnabled = false;
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); SetStatus("Config.ServiceRunning");
            ServiceStatus(true);
            Append(Loc.Current["Config.CheckListening"]);
        }
        private async Task OnStopServer()
        {
            if (server == null || server.HasExited) return;
            var process = server;
            var identity = new HostLifecycleService(DataLocations.Current.WorkbenchDataDirectory, TiaOpenness.Gui.ViewModels.MainViewModel.AppVersion)
                .Discover().FirstOrDefault(host => host.Pid == process.Id && host.StartTicks == process.StartTime.ToUniversalTime().Ticks)
                ?? new VisibleHost(process.Id, process.StartTime.ToUniversalTime().Ticks, SelectedVersion, process.StartInfo.FileName,
                    root, "http", null, null, true, false);
            if (identity.CallInProgress) throw new InvalidOperationException(Loc.Current["Hosts.Busy"]);
            if (TiaOpenness.Gui.Controls.WorkbenchMessageBox
                    .Show(Window, Loc.Current["Config.StopServicePrompt"], Loc.Current["Config.StopServiceCaption"], MessageBoxButton.OKCancel, MessageBoxImage
                        .Warning) != MessageBoxResult
                    .OK)
                return;
            SetBusy(true);
            try
            {
                Func<VisibleHost, Task>? requestShutdown = identity.PipeName != null ? null : async _ => {
                    await process.StandardInput.WriteLineAsync("tiamcp.host-control/1 shutdown");
                    await process.StandardInput.FlushAsync();
                };
                await HostLifecycleService.Stop(identity, requestShutdown, target => Window.Dispatcher.InvokeAsync(() => WorkbenchMessageBox.Show(Window,
                    Loc.Current.T("Hosts.ForceConfirm", target.Display), Loc.Current["Hosts.Stop"],
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK).Task);
            }
            finally { SetBusy(false); }
        }
        private async void OnClosing(object sender, CancelEventArgs e)
        {
            if (e.Cancel || closing) return;
            if (busy) { e.Cancel = true; Append(Loc.Current["Config.WaitBeforeClosing"]); return; }
            if (server != null && !server.HasExited)
            {
                e.Cancel = true;
                try { await OnStopServer(); if (server != null && !server.HasExited) return; }
                catch (Exception ex) { Report(ex); return; }
                closing = true; Window.Close(); return;
            }
            closing = true;
        }
    }
}
