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

        // Check for updates, then hand over to the existing updater with this window closed.
        private void ShowInstalledVersion()
        {
            Find<TextBlock>("PackageText").Text = TiaOpenness.Gui.Views.SettingsView.ReadPackageName(root);
            if (preview != null)
            {
                SetLocalizedText("UpdateInstalledItem", TextBlock.TextProperty, "Settings.EngineVersion", TiaOpenness.Gui.ViewModels.MainViewModel.AppVersion
                        .TrimStart('v'));
                SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.NotChecked");
                return;
            }
            string installed = UpdateCheck.Installed(root);
            if (installed == null) {
                SetLocalizedText("UpdateInstalledItem", TextBlock.TextProperty, "Config.EngineOutsideBundle"); Find<Button>("CheckUpdate").IsEnabled = false;
                return;
            }
            SetLocalizedText("UpdateInstalledItem", TextBlock.TextProperty, "Settings.EngineVersion", installed);
            if (UpdateCheck.IsSourceRepository(root))
            {
                SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.SourceRepositoryUpdate");
                Find<Button>("CheckUpdate").IsEnabled = false;
            }
        }
        private async Task OnCheckUpdate(bool explicitRequest)
        {
            if (UpdateCheck.IsSourceRepository(root))
            {
                SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.SourceRepositoryUpdate");
                return;
            }
            string installed = UpdateCheck.Installed(root);
            if (installed == null) return;
            var check = Find<Button>("CheckUpdate");
            check.IsEnabled = false; SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.CheckingUpdate");
            try
            {
                var info = await Task.Run(() => UpdateCheck.Latest(installed, UpdateCheck.Repository));
                latest = info;
                if (info.UpdateAvailable)
                {
                    var size = info.ZipSizeText.Length > 0 ? LocalizedText.Key("Config.UpdateSize", info.ZipSizeText) : LocalizedText.Empty;
                    SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.UpdateAvailable", info.Latest, size);
                    Find<Button>("RunUpdate").Visibility = Visibility.Visible;
                    Find<Button>("RunUpdate").IsEnabled = !UpdateCheck.IsSourceRepository(root);
                    Append(Loc.Current.T("Config.UpdateAvailableLog", installed, info.Latest, size, info.ReleaseUrl));
                }
                else
                {
                    SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.UpToDate", info.Tag); Find<Button>("RunUpdate").Visibility = Visibility
                        .Collapsed;
                    Find<Button>("RunUpdate").IsEnabled = false;
                    Append(Loc.Current.T("Config.UpToDateLog", installed, info.Source));
                }
            }
            catch (Exception ex)
            {
                SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.CannotCheckUpdate", ex.GetBaseException().Message);
                if (explicitRequest) Append(Loc.Current.T("Config.UpdateCheckFailed", ex.GetBaseException().Message));
            }
            finally { check.IsEnabled = true; }
        }
        private void OnRunUpdate()
        {
            if (CanUpdate != null && !CanUpdate()) throw new InvalidOperationException(Loc.Current["Config.FinishOperationsBeforeUpdate"]);
            if (busy) return;
            if (UpdateCheck.IsSourceRepository(root)) throw new InvalidOperationException(Loc.Current["Config.CannotUpdateSource"]);
            if (server != null && !server.HasExited) throw new InvalidOperationException(Loc.Current["Config.StopServiceBeforeUpdate"]);
            var running = UpdateCheck.RunningEngines(root);
            if (running.Count > 0)
                throw new InvalidOperationException(Loc.Current.T("Config.EnginesStillRunning", string.Join(Loc.Current["Config.ReasonSeparator"], running)));
            string updater = UpdateCheck.UpdaterPath(root);
            if (!File.Exists(updater)) throw new InvalidOperationException(Loc.Current.T("Config.UpdaterMissing", updater));
            string target = latest != null && latest.UpdateAvailable ? latest.Latest : Loc.Current["Config.LatestVersion"];
            if (TiaOpenness.Gui.Controls.WorkbenchMessageBox
                    .Show(Window, Loc.Current.T("Config.RunUpdatePrompt", root, target), Loc.Current["Config.RunUpdateCaption"], MessageBoxButton.OKCancel,
                        MessageBoxImage.Question) != MessageBoxResult
                    .OK)
                return;
            Process.Start(UpdateCheck.Launch(root, Process.GetCurrentProcess().Id));
            Append(Loc.Current["Config.UpdaterStarted"]);
            Window.Close();
        }
    }
}
