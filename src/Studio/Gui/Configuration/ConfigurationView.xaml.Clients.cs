using System;
using TiaOpenness.Gui.ViewModels;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TiaMcp.Versioning;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Localization;

namespace TiaMcpConfigurator
{
    public sealed partial class ConfigurationView
    {
        private static readonly JsonSerializerOptions PreviewFormat = new() { WriteIndented = true };
        internal Action<string> CopyClientText { get; set; } = Clipboard.SetText;

        private void InitializeClients()
        {
            // Existing configuration consumers resolve these names on the shared configuration surface.
            // Register the controls themselves so selection, UI Automation and localization all use the same instances.
            foreach (var control in new FrameworkElement[] { Clients.ClientChoices, Clients.ClientSelection, Clients.SaveClient,
                Clients.WriteTargets, Clients.ClientPane, Clients.ClientActionsBand, Clients.RelayNote })
                RegisterName(control.Name, control);

            var choices = Clients.ClientChoices;
            var cards = preview?.Clients ?? ClientProfiles.All();
            choices.ItemsSource = cards;
            int firstDetected = cards.FindIndex(profile => profile.Detected);
            choices.SelectedIndex = firstDetected < 0 ? 0 : firstDetected;
            Append(Loc.Current.T("Config.ClientDetection",
                string.Join(Loc.Current["Config.ClientListSeparator"], cards.Select(profile => profile.DisplayName + (profile.Detected ? " ✓" : " –")))));

            Clients.PreviewTarget.ItemsSource = choices.SelectedItems;
            Clients.PreviewTarget.SelectionChanged += (_, _) => UpdateJsonPreview();
            Clients.PreviewJson.Expanded += (_, _) => UpdateJsonPreview();
            Clients.PreviewJson.Collapsed += (_, _) => UpdateJsonPreview();
            choices.SelectionChanged += (_, _) => UpdateInstructions();
            choices.PreviewMouseLeftButtonDown += OnClientPreviewMouse;
            choices.PreviewKeyDown += OnClientPreviewKey;
            choices.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnClientSwitch));
            choices.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnClientSwitch));
            foreach (var guidance in new[] { Clients.ClientSelection, Clients.DetectedCount })
            {
                guidance.KeyDown += OnClientGuidanceKey;
                guidance.MouseLeftButtonUp += (_, _) => ShowClientGuidance();
            }
            Clients.SelectDetected.Click += (_, _) => Guard(SelectDetectedClients);
            Clients.ClearClients.Click += (_, _) => choices.UnselectAll();
            Clients.SaveClient.Click += (_, _) => Guard(() => SaveClients(Remote));
            Clients.CopyJson.Click += (_, _) => Guard(() => {
                CopyClientText(Clients.JsonText.Text);
                Notify(ToastKind.Success, LocalizedText.Key("Config.FormatCopied", PreviewFormatName), "copy");
            });
        }

        private void SelectDetectedClients()
        {
            var choices = Clients.ClientChoices;
            var detected = choices.Items.Cast<ClientProfile>().Where(profile => profile.Detected).ToList();
            foreach (var profile in detected)
                if (!choices.SelectedItems.Contains(profile) && !PreviewClientSelection(profile)) return;
            // Apply the batch only after all new targets have been reviewed.
            foreach (var profile in choices.Items.Cast<ClientProfile>())
                if (!profile.Detected) choices.SelectedItems.Remove(profile);
            foreach (var profile in detected)
                if (!choices.SelectedItems.Contains(profile)) choices.SelectedItems.Add(profile);
        }

        private string PreviewFormatName => (Clients.PreviewTarget.SelectedItem as ClientProfile)?.Client == "codex" ? "TOML" : "JSON";

        private void UpdateJsonPreview()
        {
            Clients.PreviewJson.Header = Loc.Current.T(Clients.PreviewJson.IsExpanded ? "Config.HideFormat" : "Config.PreviewFormat", PreviewFormatName);
            Clients.CopyJson.Content = Loc.Current.T("Config.CopyFormat", PreviewFormatName);
            Clients.CopyJson.IsEnabled = false;
            if (Clients.PreviewTarget.SelectedItem is not ClientProfile target)
            {
                Clients.JsonText.Text = Loc.Current["Config.SelectClientsFirst"];
                return;
            }
            try
            {
                int port = Remote ? int.Parse(ServerPort.Text.Trim(), CultureInfo.InvariantCulture) : 0;
                var engine = ConfigCore.Engine(root, SelectedVersion);
                string name = ClientProfiles.ServerName(target, Remote);
                string address = ServerAddress.Text.Trim(), path = TiaPath.Text.Trim();
                if (target.Client == "codex")
                    Clients.JsonText.Text = ClientProfiles.MergeToml("", name, Remote, address, port, engine, SelectedVersion, path);
                else
                {
                    var entry = ClientProfiles.Entry(target, Remote, address, port, engine, SelectedVersion, path);
                    var servers = new Dictionary<string, object> { [name] = entry };
                    var document = new Dictionary<string, object> { [ClientProfiles.RootKey(target)] = servers };
                    Clients.JsonText.Text = JsonSerializer.Serialize(document, PreviewFormat);
                }
                Clients.CopyJson.IsEnabled = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            {
                Clients.JsonText.Text = ex is FormatException or OverflowException ? Loc.Current["Config.InvalidEndpoint"] : ex.Message;
            }
        }

        private void OnClientGuidanceKey(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            ShowClientGuidance();
            e.Handled = true;
        }
        private void ShowClientGuidance() => Notify(ToastKind.Info, LocalizedText.Literal(clientInstructions), "client-guidance");
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
            if (Clients.ClientChoices.SelectedItems.Contains(profile) == selected) return true;
            if (selected && !PreviewClientSelection(profile)) return false;
            if (selected) Clients.ClientChoices.SelectedItems.Add(profile); else Clients.ClientChoices.SelectedItems.Remove(profile);
            return true;
        }
        private void OnClientSwitch(object sender, RoutedEventArgs e)
        {
            if (updatingClientSwitch || e.OriginalSource is not ToggleButton toggle
                || toggle.DataContext is not ClientProfile profile) return;
            // Selection bindings also raise Checked while the collection is still updating.
            if (ItemsControl.ContainerFromElement(Clients.ClientChoices, toggle) is ListBoxItem item
                && toggle.IsChecked == item.IsSelected) return;
            updatingClientSwitch = true;
            try
            {
                SetClientSelected(profile, toggle.IsChecked == true);
                toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
            }
            finally { updatingClientSwitch = false; }
        }
        private void OnClientPreviewMouse(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (ItemsControl.ContainerFromElement(Clients.ClientChoices, source) is not ListBoxItem item) return;
            item.Focus();
            // The tile handles a normal button release. Clicks in the gutter must not select an item without its review.
            while (source != null && source != item)
            {
                if (source is ToggleButton) return;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            e.Handled = true;
        }
        private void OnClientPreviewKey(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Space or Key.Enter)) return;
            var item = ItemsControl.ContainerFromElement(Clients.ClientChoices, Keyboard.FocusedElement as DependencyObject) as ListBoxItem;
            if (item?.DataContext is not ClientProfile profile) return;
            SetClientSelected(profile, !item.IsSelected);
            e.Handled = true;
        }
        private void UpdateLink()
        {
            bool remote = Remote;
            var selected = Clients.ClientChoices.SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("LinkClient").Text = selected.Count == 0 ? Loc.Current["Config.NoSelection"] : Loc.Current.T("Config.Selected", selected.Count);
            Find<TextBlock>("LinkClient").ToolTip = string.Join(Loc.Current["Config.ListSeparator"], selected.Select(x => x.DisplayName));
            Find<TextBlock>("LinkServer").Text = Loc.Current.T("Common.McpPortal", TiaVersionCatalog.Get(SelectedVersion).DisplayName);
            bool running = DisplayRunning;
            string address = Text("ServerAddress");
            bool http = remote && address.Length > 0;
            EndpointScheme.Text = http ? "http://" : "";
            EndpointAddress.Text = http ? address + ":" + Text("ServerPort") : ServiceEndpoint;
            EndpointPath.Text = http ? "/mcp" : "";
            Find<TextBlock>("LinkState").Text = Loc.Current[!remote ? "Config.Local" : running ? "Config.Running" : "Config.Idle"];
            ServiceStatusText.Text = LinkState.Text;
            ServiceStateChip.Tag = running ? TiaOpenness.Gui.ViewModels.FeatureTone.Accent : TiaOpenness.Gui.ViewModels.FeatureTone.Muted;
            StartServer.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            StopServer.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            var release = TiaVersionCatalog.Get(SelectedVersion);
            TransportStatus.Text = remote ? "HTTP :" + Text("ServerPort") : Loc.Current.T("Config.LocalStatus", release.DisplayName);
            TransportStatus.ToolTip = ServiceStatusText.Text;
            TransportChip.SetResourceReference(StyleProperty, remote && !running ? "Primer.Tag" : "Primer.Tag.Ok");
            string statusBrush = remote && !running ? "Primer.textFaint" : "Primer.ok";
            TransportDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, statusBrush);
            Clients.TargetDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, statusBrush);
            Clients.TargetEndpoint.Text = remote ? ServiceEndpoint : "STDIO · TIA " + release.DisplayName;
            UpdateJsonPreview();
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }
        private void UpdateInstructions()
        {
            var selected = Clients.ClientChoices.SelectedItems.Cast<ClientProfile>().ToList();
            int detected = Clients.ClientChoices.Items.Cast<ClientProfile>().Count(profile => profile.Detected);
            Clients.DetectedCount.Text = Loc.Current.T("Config.DetectedCount", detected);
            Clients.DetectedDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, detected > 0 ? "Primer.ok" : "Primer.textFaint");
            Clients.ClientSelection.Text = Loc.Current.T("Mcp.ClientCounts", selected.Count, detected);
            Clients.SaveClient.IsEnabled = Clients.ClearClients.IsEnabled = selected.Count > 0;
            Clients.SaveClient.Tag = selected.Count == 0 ? Loc.Current["Config.SelectForWrite"] : Loc.Current.T("Config.WriteCount", selected.Count);
            Clients.SelectDetected.Content = Loc.Current.T("Config.SelectDetected", detected);
            Clients.SelectDetected.IsEnabled = detected > 0;
            if (Clients.PreviewTarget.SelectedIndex < 0 && selected.Count > 0) Clients.PreviewTarget.SelectedIndex = 0;
            Clients.WriteTargets.Text = selected.Count == 0 ? Loc.Current["Config.EmptySelection"] : Loc.Current.T("Config.SelectionBackup", selected.Count);
            Clients.WriteTargets.ToolTip = Loc.Current["Mcp.WillWrite"] + "\n" + string.Join("\n", selected.Select(x => x.DisplayName + " → " + x.Path));
            clientInstructions = selected.Count == 0 ? Loc.Current["Config.ChooseClients"] :
                string
                    .Join("\n", selected
                        .Select(x => Loc.Current
                            .T(x.Detected ? "Config.ClientInstructionsDetected" : "Config.ClientInstructionsNotDetected", x.Name, x.Hint, x.Evidence)));
            Clients.ClientSelection.ToolTip = clientInstructions;
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
        // Save endpoint settings on the client machine even when the service is installed on a different machine.
        private void OnSaveBoth()
        {
            ReadEndpoint(out int port);
            string ip = Text("ServerAddress");
            ConfigCore
                .AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings {
                    Address = ip, Port = port });
            Append(Loc.Current["Config.ClientConnectionSaved"]);
            try { ConfigCore.AtomicJson(StatePath, Settings()); Append(Loc.Current["Config.ServerSaved"]); }
            catch (Exception ex)
            {
                var text = LocalizedText.Key("Config.ClientOnlySaved", ex.GetBaseException().Message);
                Append(text.Resolve()); Notify(ToastKind.Error, text); return;
            }
            SetStatus("Config.Saved");
        }

        private void SaveClients(bool remote)
        {
            var selected = Clients.ClientChoices.SelectedItems.Cast<ClientProfile>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException(Loc.Current["Config.SelectClientsFirst"]);
            string engine = null, ip = null; int port = 0;
            if (remote) {
                ip = Text("ServerAddress"); ReadEndpoint(out port);
                engine = ConfigCore.Engine(root, SelectedVersion);
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
            Append(Loc.Current.T("Config.RestartClients", remote ? "tia-portal-vm" : "tia-portal"));
            if (errors.Count > 0) throw new InvalidOperationException(Loc.Current.T("Config.SomeClientsNotSaved", string.Join("\n", errors)));
            SetStatus("Config.ClientsConfigured", saved);
        }

        internal void RepairRemoteClients()
        {
            Guard(() => {
                Find<RadioButton>("RemoteNav").IsChecked = true;
                if (Text("ServerAddress").Length == 0)
                    throw new InvalidOperationException(Loc.Current["Config.RelayNeedsConnection"]);
                var choices = Clients.ClientChoices;
                choices.SelectedItems.Clear();
                string endpoint = ReadEndpoint(out _) + "mcp";
                foreach (ClientProfile profile in choices.Items)
                    if (ClientProfiles.HasRemoteEntry(profile, endpoint)) choices.SelectedItems.Add(profile);
                WorkbenchCommands.Navigate.Execute("Clients", this);
                SaveClients(true);
            });
        }
        private async Task OnTestClient()
        {
            if (busy) return;
            try
            {
                string ip = Text("ServerAddress"); ReadEndpoint(out int port);
                SetBusy(true); SetStatus("Config.Testing");
                string result = await Task.Run(() => ConfigCore.TestRemote(ip, port));
                lastTestResult = result; lastTestFailed = false; UpdateLastTest();
                Append(result);
                Notify(ToastKind.Success, LocalizedText.Literal(Loc.Current["Config.ConnectionOk"] + " · " + result));
            }
            catch (Exception ex) { lastTestResult = null; lastTestFailed = true; UpdateLastTest(); Report(ex); }
            finally { SetBusy(false); }
        }
    }
}
