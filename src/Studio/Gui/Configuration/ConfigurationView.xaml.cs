extern alias PlcRendering;
using System.Collections.Concurrent;
using System.Collections.Specialized;
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
using System.Windows.Automation;
using TiaOpenness.Gui;
using TiaOpenness.Gui.ViewModels;
using TiaOpenness.Gui.Views;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TiaMcp.Versioning;
using TiaOpenness.Gui.Localization;
using TiaOpenness.Gui.Controls;
using TiaOpenness.Gui.Services;
using TiaOpenness.Shared;
using ToolUsageCatalog = PlcRendering::TiaOpenness.Shared.ToolUsageCatalog;

namespace TiaMcpConfigurator
{
    public sealed partial class ConfigurationView : UserControl, IDisposable
    {
        public Window Window { get; private set; }
        internal ClientsView Clients { get; } = new();
        private readonly string root;
        public event EventHandler ServiceStateChanged;
        public event EventHandler ReleaseChanged;
        private readonly ConfigurationPreview? preview;
        private string clientInstructions = "";
        private string activityLog = "";
        internal string ActivityLogText => activityLog;
        private bool DisplayRunning => preview?.Running ?? HasRunningServer;
        public bool HasRunningServer { get { return server != null && !server.HasExited; } }
        public bool LocksRelease { get { return busy || HasRunningServer; } }
        public string ServiceStateKey { get { return !Remote ? "Config.Local" : DisplayRunning ? "Config.Running" : "Config.Idle"; } }
        public string ServiceEndpoint
        {
            get
            {
                string address = Text("ServerAddress");
                return !Remote ? Loc.Current["Config.LocalAddress"] : address.Length == 0 ? Loc.Current["Config.NoAddress"]
                    : "http://" + address + ":" + Text("ServerPort") + "/mcp";
            }
        }
        public string SelectedReleaseKey
        {
            get { return SelectedVersion; }
            set { Find<ComboBox>("Version").SelectedValue = TiaVersionCatalog.RequireRunnable(value).Key; }
        }
        public void SetReleaseEnabled(bool enabled) { Find<ComboBox>("Version").IsEnabled = enabled && !LocksRelease; }
        public Func<bool> CanUpdate { get; set; }
        private Process server;
        private int listeningPort;
        private UpdateInfo latest;   // last successful update check
        private int logEntries;
        private readonly ConcurrentQueue<string> pendingLog = new();
        private int logQueued;
        private bool tiaDetected;
        private string lastTestResult;
        private bool lastTestFailed;
        private bool busy, closing;
        private bool updatingClientSwitch;
        private T Find<T>(string name) where T : FrameworkElement {
            return (T)(FindName(name) ?? Window.FindName(name) ?? ((TiaOpenness.Gui.Views.SettingsView)Window.FindName("SettingsContent")).FindName(name)); }
        private string Text(string name) { return Find<TextBox>(name).Text.Trim(); }
        private string SelectedVersion
        {
            get
            {
                var selected = Find<ComboBox>("Version").SelectedItem as TiaVersionDescriptor;
                if (selected == null) throw new InvalidOperationException(Loc.Current["Config.SelectRelease"]);
                return TiaVersionCatalog.RequireRunnable(selected.Key).Key;
            }
        }
        private string StatePath { get { return Path.Combine(ConfigCore.StateDirectory, "http-v" + SelectedVersion + ".json"); } }
        // Both transports use the same configuration page.
        private bool Remote { get { return Find<RadioButton>("RemoteNav").IsChecked == true; } }

        public ConfigurationView(Window owner, string bundleRoot, bool loadExisting = true)
            : this(owner, bundleRoot, loadExisting, null) { }

        internal ConfigurationView(Window owner, string bundleRoot, bool loadExisting, ConfigurationPreview? preview)
        {
            this.preview = preview;
            Window = owner;
            root = preview == null ? TiaOpenness.Shared.BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory, bundleRoot) : bundleRoot;
            InitializeComponent();
            ((INotifyCollectionChanged)HostList.Items).CollectionChanged += (_, _) => UpdateHostsCard();
            ConfigurationActivity.Expanded += (_, _) => UpdateLogCount();
            ConfigurationActivity.Collapsed += (_, _) => UpdateLogCount();
            InitializeHosts(loadExisting && preview == null);
            var versions = Find<ComboBox>("Version");
            versions.ItemsSource = TiaVersionCatalog.Runnable.ToList();
            versions.SelectedValue = TiaOpenness.Core.Environment.WorkbenchReleaseSelection.DefaultKey(root);
            InitializeReleaseTiles();
            InitializeClients();
            RemoteNav.Checked += delegate { InstallDetails.IsExpanded = true; Mode(true); };
            LocalNav.Checked += delegate { InstallDetails.IsExpanded = false; Mode(false); };
            ServerAddress.TextChanged += OnEndpointChanged;
            ServerPort.TextChanged += OnEndpointChanged;
            Click("BrowseTia", delegate {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog {
                        Description = Loc.Current.T("Config.BrowseTiaDescription", SelectedVersion), SelectedPath = Text("TiaPath") })
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Find<TextBox>("TiaPath").Text = dialog.SelectedPath;
            });
            Click("DetectTia", delegate { RefreshReleaseDetections(); UpdateReleaseSelection(); DetectTiaPath(true); });
            TiaPath.TextChanged += (_, _) => { UpdateAssemblyStatus(); UpdateJsonPreview(); };
            Click("SaveBoth", OnSaveBoth);
            Click("StartServer", OnStartServer);
            ClickAsync("StopServer", OnStopServer);
            ClickAsync("Network", OnNetwork);
            ClickAsync("TestClient", OnTestClient);
            Click("ClearLog", delegate
            {
                while (pendingLog.TryDequeue(out _)) { }
                activityLog = ""; ActivityLog.LogText = ""; logEntries = 0; UpdateLogCount();
                Notify(ToastKind.Success, LocalizedText.Key("Toast.Cleared"), "configuration-log");
            });
            ClickAsync("CheckUpdate", () => OnCheckUpdate(true));
            Click("RunUpdate", OnRunUpdate);
            Click("OpenReleases", delegate {
                    Process
                        .Start(new ProcessStartInfo(latest != null && latest.ReleaseUrl != null ? latest.ReleaseUrl : UpdateCheck
                                .ReleasePageUrl(UpdateCheck.Repository)) {
                            UseShellExecute = true });
                });
            Click("OpenProjectPage", delegate { Process.Start(new ProcessStartInfo("https://github.com/" + UpdateCheck.Repository) { UseShellExecute = true });
                });
            ShowInstalledVersion();
            Version.SelectionChanged += delegate {
                Guard(delegate { LoadServer(loadExisting && preview == null); });
                UpdateReleaseSelection();
                UpdateLink();
                ReleaseChanged?.Invoke(this, EventArgs.Empty);
            };
            if (loadExisting && preview == null)
            {
                Window.Width = Math.Max(Window.MinWidth, Math.Min(Window.Width, SystemParameters.WorkArea.Width - 32));
                Window.Height = Math.Max(Window.MinHeight, Math.Min(Window.Height, SystemParameters.WorkArea.Height - 32));
                Guard(delegate { LoadServer(true); });
                if (Text("ServerAddress").Length == 0) Guard(LoadClient);
                if (Text("ServerAddress").Length == 0) LocalNav.IsChecked = true;
                var ignored = OnCheckUpdate(false);   // background; the band reports the outcome, nothing blocks
            }
            else if (preview == null) {
                Find<TextBox>("TiaPath").Text = Path
                    .Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "Siemens", "Automation", TiaVersionCatalog
                        .Get(SelectedVersion).InstallFolder);
                DetectTiaPath(false);
            }
            else
            {
                versions.SelectedValue = preview.ReleaseKey;
                TiaPath.Text = preview.InstallPath;
                ServerAddress.Text = preview.Address;
                tiaDetected = preview.Detected;
                listeningPort = int.Parse(ServerPort.Text, System.Globalization.CultureInfo.InvariantCulture);
                StopServer.IsEnabled = preview.Running;
                LocalNav.IsChecked = preview.Local;
                RemoteNav.IsChecked = !preview.Local;
            }
            InstallDetails.IsExpanded = Remote;
            UpdateLanguage();
            Loc.Current.LanguageChanged += OnLanguageChanged;
            Append(Loc.Current["Config.Ready"]);
            Window.Closing += OnClosing;
            if (preview != null)
            {
                activityLog = preview.Log;
                ActivityLog.LogText = activityLog;
                logEntries = activityLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                UpdateLogCount();
            }
        }

        private void OnLanguageChanged(object? sender, EventArgs e) { UpdateLanguage(); }
        private void UpdateLanguage()
        {
            Mode(Remote); UpdateInstructions(); UpdateDetectionLabel(); UpdateReleaseCounts(); UpdateLastTest(); UpdateLogCount();
            foreach (var update in localizedText.Values) update();
        }

        // Retain format arguments for current labels; activity-log history stays as written.
        private readonly Dictionary<string, Action> localizedText = new Dictionary<string, Action>();
        private void SetLocalizedText(string name, DependencyProperty property, string key, params object[] args)
        {
            Action update = delegate { Find<FrameworkElement>(name).SetValue(property, Loc.Current.T(key, args)); };
            localizedText[name] = update;
            update();
        }

        private void UpdateDetectionLabel()
        {
            int count = ReleaseTiles.Children.Cast<RadioButton>().Count(tile => Equals(tile.Tag, true));
            ReleaseSummary.Text = Loc.Current["Config.ReleaseHint"];
            ReleaseSummary.ToolTip = Loc.Current.T(count == 1 ? "Config.OneRelease" : "Config.ReleaseCount", count);
            UpdateAssemblyStatus();
            DetectionSource.Text = Loc.Current[tiaDetected ? "Config.Detected" : "Config.NotDetected"];
            DetectionChip.Tag = tiaDetected ? TiaOpenness.Gui.ViewModels.FeatureTone.Accent : TiaOpenness.Gui.ViewModels.FeatureTone.Muted;
        }

        private static readonly ConcurrentDictionary<string, (int Lite, int Tools)> ReleaseCounts = new();

        private void UpdateReleaseCounts()
        {
            var counts = ReleaseCounts.GetOrAdd(SelectedVersion, static key => {
                var entries = ToolUsageCatalog.ProfileEntries(key);
                return (entries.Count(entry => entry!["profiles"]!.AsArray().Any(profile => (string?)profile == "lite")), entries.Count);
            });
            LiteCount.Text = counts.Lite + " lite";
            ToolCount.Text = counts.Tools + " tools";
            LiteCount.ToolTip = Loc.Current.T("Config.LiteCount", counts.Lite);
            ToolCount.ToolTip = Loc.Current.T("Config.ToolCount", counts.Tools);
        }

        private void UpdateAssemblyStatus()
        {
            var release = (TiaVersionDescriptor)Version.SelectedItem;
            var path = TiaPath.Text.Trim();
            bool verified = preview == null ? release.FindApiDirectory(path) != null :
                preview.Detected && release.Key == preview.ReleaseKey && path.Equals(preview.InstallPath, StringComparison.OrdinalIgnoreCase);
            AssemblyStatus.Text = Loc.Current[verified ? "Config.AssembliesReady" : "Config.AssembliesMissing"];
            AssemblyStatus.SetResourceReference(TextBlock.ForegroundProperty, verified ? "Primer.ok" : "Primer.textFaint");
        }

        private void InitializeReleaseTiles()
        {
            ReleaseTiles.SizeChanged += (_, e) => ReleaseTiles.Columns = e.NewSize.Width >= 640 ? 8 : 4;
            foreach (var release in TiaVersionCatalog.All.Where(release => release.IsRunnable))
            {
                var tile = new RadioButton {
                    Content = release, ContentTemplate = (DataTemplate)FindResource("Configuration.Release"),
                    Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(6, 8, 6, 8), MinHeight = 52
                };
                ContrastSelection.SetActive(tile, false);
                AutomationProperties.SetName(tile, release.DisplayName);
                tile.Checked += (_, _) => Version.SelectedValue = release.Key;
                ReleaseTiles.Children.Add(tile);
            }
            RefreshReleaseDetections();
            UpdateReleaseSelection();
        }

        private void RefreshReleaseDetections()
        {
            foreach (RadioButton tile in ReleaseTiles.Children)
            {
                var release = (TiaVersionDescriptor)tile.Content;
                bool installed = preview == null ? ConfigCore.DetectTia(release.Key).Key != null : preview.Detected && preview.ReleaseKey == release.Key;
                SetTileDetection(tile, installed);
            }
        }

        private static void SetTileDetection(RadioButton tile, bool installed)
        {
            tile.Tag = installed;
            UpdateReleaseTileSurface(tile);
            tile.SetResourceReference(StyleProperty, installed ? "Primer.Tile" : "Primer.Tile.Muted");
            tile.SetBinding(AutomationProperties.HelpTextProperty, TrExtension.CreateBinding(installed ? "Config.Detected" : "Config.NotDetected"));
        }

        private static void UpdateReleaseTileSurface(RadioButton tile)
        {
            bool selected = tile.IsChecked == true;
            tile.BorderThickness = new Thickness(selected ? 2 : 1);
            tile.SetResourceReference(Control.BackgroundProperty, selected || Equals(tile.Tag, true) ? "Primer.card" : "Primer.sidebar");
            tile.SetResourceReference(Control.ForegroundProperty, "Primer.text");
        }

        private RadioButton SelectedReleaseTile => ReleaseTiles.Children.Cast<RadioButton>().Single(tile => Equals(tile.Content, Version.SelectedItem));

        private void UpdateReleaseSelection()
        {
            var tile = SelectedReleaseTile;
            tile.IsChecked = true;
            foreach (RadioButton item in ReleaseTiles.Children) UpdateReleaseTileSurface(item);
            tiaDetected = (bool)tile.Tag;
            UpdateDetectionLabel();
            UpdateReleaseCounts();
        }

        private void UpdateLastTest()
        {
            HealthResult.Text = Loc.Current[lastTestFailed ? "Config.TestFailed" : lastTestResult == null ? "Config.NoProbe" : "Config.ProbeOk"];
            HealthResult.SetResourceReference(TextBlock.ForegroundProperty, lastTestFailed ? "Primer.destructiveText" :
                lastTestResult == null ? "Primer.textFaint" : "Primer.ok");
            TestClient.ToolTip = Loc.Current.T("Config.LastTest", lastTestResult ?? Loc.Current[lastTestFailed ? "Config.TestFailed" : "Config.TestNotRun"]);
            AutomationProperties.SetHelpText(TestClient, Loc.Current.T("Config.LastTest",
                lastTestResult ?? Loc.Current[lastTestFailed ? "Config.TestFailed" : "Mcp.TestNotRun"]));
        }
        private void UpdateLogCount()
        {
            LogCount.Text = Loc.Current.T(logEntries == 1 ? "Config.Entry" : "Config.Entries", logEntries);
            ActivityTitle.Text = Loc.Current.T("Config.ActivityCount", logEntries);
            ActivityDisclosure.Text = Loc.Current[ConfigurationActivity.IsExpanded ? "Config.Collapse" : "Config.Expand"];
        }

        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }
        private void ClickAsync(string name, Func<Task> action) {
            Find<Button>(name).Click += async (_, _) => { try { await action(); } catch (Exception ex) { Report(ex); } }; }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
        internal void SetStatus(string key, params object[] args) => Notify(
            key is "Config.Testing" or "Config.ConfiguringNetwork" ? ToastKind.Info : ToastKind.Success, LocalizedText.Key(key, args));
        private void Notify(ToastKind kind, LocalizedText text, string source = "configuration", LocalizedText title = default)
            => (Window as MainWindow)?.ShowToast(kind, text, source, title: title);
        private void ServiceStatus(bool active)
        {
            UpdateLink();
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }
        // Mode changes only presentation; existing configuration values stay in place.
        private void Mode(bool remote)
        {
            Clients.RelayNote.Text = Loc.Current[remote ? "Config.RelayLabel" : "Mcp.ClientsNote"];
            Clients.RelayNote.ToolTip = remote ? Loc.Current["Config.RelayMode"] : null;
            Clients.RemoteGuidance.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            ModeHead.Margin = new Thickness(0, 0, 0, remote ? 0 : 20);
            foreach (var mode in new[] { LocalNav, RemoteNav })
            {
                mode.SetResourceReference(Control.BackgroundProperty, "Primer.card");
                mode.BorderThickness = new Thickness(mode.IsChecked == true ? 2 : 1);
            }
            ModeTag.Text = Loc.Current[remote ? "Config.RemoteTag" : "Config.LocalTag"];
            Find<TextBlock>("Transport").Text = remote ? "HTTP" : "stdio";
            UpdateHostsCard();
            AddressRow.IsEnabled = remote;
            ServerActions.IsEnabled = remote;
            TestClient.IsEnabled = remote;
            SaveBoth.IsEnabled = remote && !busy;
            UpdateLink();
        }
        private void UpdateHostsCard()
        {
            bool hasHosts = HostList.HasItems;
            ActivityCard.Visibility = HostsCard.Visibility = Remote || hasHosts ? Visibility.Visible : Visibility.Collapsed;
            NoHosts.Visibility = hasHosts ? Visibility.Collapsed : Visibility.Visible;
            HostList.Visibility = hasHosts ? Visibility.Visible : Visibility.Collapsed;
        }

        internal Func<ClientProfile, bool>? ConfirmClientSelection { get; set; }
        private void Append(string message)
        {
            if (closing) return;
            pendingLog.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "   " + message + Environment.NewLine);
            while (pendingLog.Count > 1000) pendingLog.TryDequeue(out _);
            if (Interlocked.Exchange(ref logQueued, 1) == 0)
                Window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushLog));
        }
        private void FlushLog()
        {
            Interlocked.Exchange(ref logQueued, 0);
            if (closing) return;
            var batch = new StringBuilder();
            while (pendingLog.TryDequeue(out var line)) batch.Append(line);
            string message = batch.ToString();
            activityLog += message;
            if (activityLog.Length > 40000)
            {
                int start = activityLog.IndexOf('\n', activityLog.Length - 40000);
                activityLog = start < 0 ? "" : activityLog[(start + 1)..];
            }
            ActivityLog.LogText = activityLog;
            logEntries = activityLog.Count(c => c == '\n'); UpdateLogCount();
        }
        private void OnEndpointChanged(object sender, TextChangedEventArgs e)
        {
            lastTestResult = null;
            lastTestFailed = false;
            UpdateLastTest();
            EndpointError.Text = "";
            EndpointError.Visibility = Visibility.Collapsed;
            UpdateLink();
        }

        private string ReadEndpoint(out int port)
        {
            try
            {
                port = int.Parse(Text("ServerPort"), System.Globalization.CultureInfo.InvariantCulture);
                return ConfigCore.Prefix(Text("ServerAddress"), port);
            }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
            {
                EndpointError.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding("Config.InvalidEndpoint"));
                EndpointError.Visibility = Visibility.Visible;
                throw;
            }
        }

        private void Report(Exception ex)
        {
            string message = ex.GetBaseException().Message;
            var http = ex as HttpListenerException;
            if (http != null) message = Loc.Current.T("Config.HttpListenFailed", http.NativeErrorCode, message);
            var web = ex as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                message = Loc.Current.T("Config.ConnectionFailed", message);
                if (response != null) response.Close();
            }
            Append(message);
            Notify(ToastKind.Error, LocalizedText.Literal(message), title: LocalizedText.Key("Config.NeedsAttention"));
        }
        // 自动探测安装目录：环境变量 → 注册表 → 默认目录（与引擎同一顺序）。explicit=false 时只在探测成功才覆盖文本框，找不到保持原值不打扰。
        private void DetectTiaPath(bool explicitRequest)
        {
            if (preview != null) return;
            PresentDetection(ConfigCore.DetectTia(SelectedVersion), explicitRequest);
        }

        internal void PresentDetection(KeyValuePair<string, string> found, bool explicitRequest)
        {
            tiaDetected = found.Key != null;
            SetTileDetection(SelectedReleaseTile, tiaDetected);
            UpdateDetectionLabel();
            Find<TextBlock>("DetectionSource").ToolTip = found.Value;
            if (found.Key != null) {
                Find<TextBox>("TiaPath").Text = found.Key; Append(Loc.Current.T("Config.TiaDetected", SelectedVersion, found.Value, found.Key)); }
            if (explicitRequest)
            {
                var text = found.Key != null ? LocalizedText.Key("Config.TiaDetected", SelectedVersion, found.Value, found.Key)
                    : LocalizedText.Key("Config.TiaNotDetected", found.Value, SelectedVersion);
                if (found.Key == null) Append(text.Resolve());
                Notify(found.Key == null ? ToastKind.Warning : ToastKind.Success, text, "detect");
            }
        }
        private void SetBusy(bool value)
        {
            busy = value;
            Panes.IsEnabled = Clients.IsEnabled = !value;
            DetectTia.IsEnabled = !value;
            SaveBoth.IsEnabled = !value && Remote;
            SetReleaseEnabled(CanUpdate?.Invoke() ?? true);
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }
        public void CapturePage(string path, int mode, bool scrollToBottom = false)
        {
            Find<RadioButton>(mode == 0 ? "RemoteNav" : "LocalNav").IsChecked = true;
            Window.Show(); Window.UpdateLayout(); Window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Render);
            if (scrollToBottom) ActivityLog.ScrollToEnd();
            // Render() draws the panel at its layout offset, so the bitmap must also cover the margin on
            // both sides — sizing it from ActualWidth alone cut the right edge (and the status pill with it).
            var content = (FrameworkElement)Window.Content;
            int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
            int height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
        public void Dispose()
        {
            hostTimer.Stop(); hostTimer.Tick -= RefreshHostsTick;
            Window.Closing -= OnClosing;
            Loc.Current.LanguageChanged -= OnLanguageChanged;
            closing = true;
            if (server != null && server.HasExited) { server.Dispose(); server = null; }
        }
    }

}
