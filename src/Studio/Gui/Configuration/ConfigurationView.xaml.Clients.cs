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
        private void OnClientGuidanceKey(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)) return;
            TiaOpenness.Gui.Controls.WorkbenchMessageBox
                .Show(Window, (string)ClientSelection.ToolTip, Loc.Current["Config.ClientInstructionsCaption"], MessageBoxButton.OK, MessageBoxImage
                    .Information);
            e.Handled = true;
        }
        internal bool PreviewClientSelection(ClientProfile profile)
        {
            if (ConfirmClientSelection != null) return ConfirmClientSelection(profile);
            string message = Loc.Current["Pages.ClientWritePreview"] + "\n\n" + profile.DisplayName + "\n" + profile.Path
                + "\n" + Loc.Current.T("Pages.ClientBackupPreview", profile.Path)
                + "\n\n" + Loc.Current["Config.Transport"] + ": " + (Remote ? "HTTP" : "stdio")
                + "\n" + ServiceEndpoint + "\n" + Loc.Current["Calls.SecretPlaceholder"];
            return WorkbenchMessageBox
                .Show(Window, message, Loc.Current["Pages.ClientEnable"], MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
        }
        internal bool SetClientSelected(ClientProfile profile, bool selected)
        {
            if (ClientChoices.SelectedItems.Contains(profile) == selected) return true;
            if (selected && !PreviewClientSelection(profile)) return false;
            if (selected) ClientChoices.SelectedItems.Add(profile); else ClientChoices.SelectedItems.Remove(profile);
            return true;
        }
        private void OnClientSwitch(object sender, RoutedEventArgs e)
        {
            if (updatingClientSwitch || e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton toggle
                || toggle.DataContext is not ClientProfile profile) return;
            // Selection bindings also raise Checked while the collection is still updating.
            if (ItemsControl.ContainerFromElement(ClientChoices, toggle) is ListBoxItem item
                && toggle.IsChecked == item.IsSelected) return;
            updatingClientSwitch = true;
            try
            {
                SetClientSelected(profile, toggle.IsChecked == true);
                toggle.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
            }
            finally { updatingClientSwitch = false; }
        }
        private void OnClientPreviewMouse(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(ClientChoices, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item?.DataContext is not ClientProfile profile) return;
            SetClientSelected(profile, !item.IsSelected);
            item.Focus();
            e.Handled = true;
        }
        private void OnClientPreviewKey(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key is not (System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter)) return;
            var item = ItemsControl.ContainerFromElement(ClientChoices, System.Windows.Input.Keyboard.FocusedElement as DependencyObject) as ListBoxItem;
            if (item?.DataContext is not ClientProfile profile) return;
            SetClientSelected(profile, !item.IsSelected);
            e.Handled = true;
        }
        private void UpdateLink()
        {
            bool remote = Remote;
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("LinkClient").Text = selected.Count == 0 ? Loc.Current["Config.NoSelection"] : Loc.Current.T("Config.Selected", selected.Count);
            Find<TextBlock>("LinkClient").ToolTip = string.Join(Loc.Current["Config.ListSeparator"], selected.Select(x => x.DisplayName));
            Find<TextBlock>("LinkServer").Text = Loc.Current.T("Common.McpPortal", TiaVersionCatalog.Get(SelectedVersion).DisplayName);
            bool running = DisplayRunning;
            string address = Text("ServerAddress");
            Find<TextBlock>("LinkEndpoint").Text = !remote ? Loc.Current["Config.LocalAddress"] : address.Length == 0 ? Loc.Current["Config.NoAddress"]
                : address + ":" + Text("ServerPort");
            Find<TextBlock>("LinkState").Text = Loc.Current[!remote ? "Config.Local" : running ? "Config.Running" : "Config.Idle"];
            ServiceStatusText.Text = LinkState.Text;
            ServiceStateChip.Tag = running ? TiaOpenness.Gui.ViewModels.FeatureTone.Accent : TiaOpenness.Gui.ViewModels.FeatureTone.Muted;
            StartServer.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            StopServer.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            UpdateSteps();
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }
        private void UpdateInstructions()
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("ClientSelection").Text = Loc.Current
                .T("Mcp.ClientCounts", selected.Count, ClientChoices.Items.Cast<ClientProfile>().Count(x => x.Detected));
            WriteTargets.Text = Loc.Current["Mcp.WillWrite"] + " " + string.Join("; ", selected.Select(x => x.DisplayName + " → " + x.Path));
            clientInstructions = selected.Count == 0 ? Loc.Current["Config.ChooseClients"] :
                string
                    .Join("\n", selected
                        .Select(x => Loc.Current
                            .T(x.Detected ? "Config.ClientInstructionsDetected" : "Config.ClientInstructionsNotDetected", x.Name, x.Hint, x.Evidence)));
            Find<TextBlock>("ClientSelection").ToolTip = clientInstructions;
            UpdateLink();
        }
        // 宿主机上没有服务端配置，用上次填写的连接信息补齐同一组字段。
        private void LoadClient()
        {
            string path = Path.Combine(ConfigCore.StateDirectory, "client.json");
            if (!File.Exists(path)) return;
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(path));
            ConfigCore.Prefix(settings.Address, settings.Port);
            Find<TextBox>("ServerAddress").Text = settings.Address; Find<TextBox>("ServerPort").Text = settings.Port.ToString();
        }
        // 一页两侧，所以保存也是两侧：客户端侧的连接信息总能保存；服务端配置只在本机确实装了
        // TIA 和引擎时才写，宿主机上校验失败不算错误，只记一行说明。
        private void OnSaveBoth()
        {
            int port = int.Parse(Text("ServerPort"), System.Globalization.CultureInfo.InvariantCulture); string ip = Text("ServerAddress");
            ConfigCore.Prefix(ip, port);
            ConfigCore
                .AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings {
                    Address = ip, Port = port });
            Append(Loc.Current["Config.ClientConnectionSaved"]);
            try { ConfigCore.AtomicJson(StatePath, Settings()); Append(Loc.Current["Config.ServerSaved"]); }
            catch (Exception ex) { Append(Loc.Current.T("Config.ClientOnlySaved", ex.GetBaseException().Message)); }
            SetStatus("Config.Saved");
        }

        private void SaveClients(bool remote)
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException(Loc.Current["Config.SelectClientsFirst"]);
            string engine = null, ip = null; int port = 0;
            if (remote) {
                ip = Text("ServerAddress"); port = int.Parse(Text("ServerPort"), System.Globalization.CultureInfo.InvariantCulture);
                ConfigCore.Prefix(ip, port); engine = ConfigCore.Engine(root, SelectedVersion);
            }
            else { engine = ConfigCore.Engine(root, SelectedVersion); ConfigCore.ValidateTia(Text("TiaPath"), SelectedVersion); }
            // DeepSeek / 智谱 / Grok are brand cards over the same OpenCode file: write it once, name every brand.
            var targets = selected.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Profile = g.First(), Names = string.Join(" / ", g.Select(x => x.Name)),
                    Change = ClientProfiles.PrepareSave(g.First(), remote, ip, port, engine, SelectedVersion, Text("TiaPath")) }).ToList();
            string message = Loc.Current["Config.WriteClientsPrompt"] + string.Join("\n", targets.Select(x => x.Names + "\n" + x.Profile.Path
                + "\n" + Loc.Current.T("Pages.ClientBackupPreview", x.Profile.Path)));
            if (remote) message += "\n\n" + Loc.Current["Config.RelayMode"];
            if (targets.Any(x => x.Change.RequiresMigration))
                message += "\n\n" + Loc.Current["Config.MigrateClientsPrompt"] + "\n" + string
                    .Join("\n", targets.Where(x => x.Change.RequiresMigration).Select(x => x.Profile.Path + " → " + x.Change.TargetCommand));
            message += Loc.Current["Config.WriteClientsContinue"];
            if (TiaOpenness.Gui.Controls.WorkbenchMessageBox
                    .Show(Window, message, Loc.Current["Config.Write"], MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return;
            int saved = 0; var errors = new List<string>();
            foreach (var target in targets)
            {
                try
                {
                    target.Change.Apply(true); saved++; Append(Loc.Current.T("Config.ClientSaved", target.Names, target.Profile.Path));
                    if (target.Change.BackupPath != null) Append(Loc.Current.T("Config.ClientBackupSaved", target.Change.BackupPath));
                }
                catch (Exception ex) { errors.Add(Loc.Current.T("Config.ClientSaveError", target.Names, ex.Message)); }
            }
            if (remote && saved > 0) ConfigCore
                .AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings {
                    Address = ip, Port = port });
            SetStatus("Config.ClientsConfigured", saved);
            Append(Loc.Current.T("Config.RestartClients", remote ? "tia-portal-vm" : "tia-portal"));
            if (errors.Count > 0) throw new InvalidOperationException(Loc.Current.T("Config.SomeClientsNotSaved", string.Join("\n", errors)));
        }

        internal void RepairRemoteClients()
        {
            Guard(() => {
                Find<RadioButton>("RemoteNav").IsChecked = true;
                if (Text("ServerAddress").Length == 0)
                    throw new InvalidOperationException(Loc.Current["Config.RelayNeedsConnection"]);
                var choices = Find<ListBox>("ClientChoices");
                choices.SelectedItems.Clear();
                string endpoint = ConfigCore.Prefix(Text("ServerAddress"), int.Parse(Text("ServerPort"), System.Globalization.CultureInfo.InvariantCulture))
                    + "mcp";
                foreach (ClientProfile profile in choices.Items)
                    if (ClientProfiles.HasRemoteEntry(profile, endpoint)) choices.SelectedItems.Add(profile);
                SaveClients(true);
            });
        }
        private async Task OnTestClient()
        {
            if (busy) return;
            try
            {
                string ip = Text("ServerAddress"); int port = int
                    .Parse(Text("ServerPort"), System.Globalization.CultureInfo.InvariantCulture);
                SetBusy(true); SetStatus("Config.Testing");
                string result = await Task.Run(() => ConfigCore.TestRemote(ip, port));
                lastTestResult = result; lastTestFailed = false; UpdateLastTest();
                Append(result); SetStatus("Config.ConnectionOk");
            }
            catch (Exception ex) { lastTestResult = null; lastTestFailed = true; UpdateLastTest(); Report(ex); }
            finally { SetBusy(false); }
        }
    }
}
