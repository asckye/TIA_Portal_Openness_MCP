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

namespace TiaMcpConfigurator
{
    public sealed partial class ConfigurationView : UserControl, IDisposable
    {
        public Window Window { get; private set; }
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
                return !Remote ? Loc.Current["Config.LocalAddress"] : address.Length == 0 ? Loc.Current["Config.NoAddress"] : address + ":" + Text("ServerPort");
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
        private UpdateInfo latest;   // last successful update check
        private string runningKey;
        private int logEntries;
        private bool tiaDetected;
        private string lastTestResult;
        private bool lastTestFailed;
        private bool busy, closing;
        private T Find<T>(string name) where T : FrameworkElement { return (T)(FindName(name) ?? Window.FindName(name) ?? ((TiaOpenness.Gui.Views.SettingsView)Window.FindName("SettingsContent")).FindName(name)); }
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
        private string Secret() { return Find<CheckBox>("ShowKey").IsChecked == true ? Text("KeyVisible") : Find<PasswordBox>("Key").Password; }
        private void SetSecret(string value) { Find<TextBox>("KeyVisible").Text = value; Find<PasswordBox>("Key").Password = value; }

        public ConfigurationView(Window owner, string bundleRoot, bool loadExisting = true)
            : this(owner, bundleRoot, loadExisting, null) { }

        internal ConfigurationView(Window owner, string bundleRoot, bool loadExisting, ConfigurationPreview? preview)
        {
            this.preview = preview;
            Window = owner;
            root = preview == null ? TiaOpenness.Shared.BundleLayout.RequireWorkbenchRoot(AppContext.BaseDirectory, bundleRoot) : bundleRoot;
            InitializeComponent();
            var versions = Find<ComboBox>("Version");
            versions.ItemsSource = TiaVersionCatalog.Runnable.ToList();
            versions.SelectedValue = "21";
            var choices = Find<ListBox>("ClientChoices");
            var cards = preview?.Clients ?? ClientProfiles.All(); choices.ItemsSource = cards;
            int firstDetected = cards.FindIndex(x => x.Detected); choices.SelectedIndex = firstDetected < 0 ? 0 : firstDetected;
            Append(Loc.Current.T("Config.ClientDetection", String.Join(Loc.Current["Config.ClientListSeparator"], cards.Select(x => x.DisplayName + (x.Detected ? " ✓" : " –")))));
            choices.SelectionChanged += delegate { UpdateInstructions(); };
            Find<TextBlock>("ClientSelection").MouseLeftButtonUp += delegate { TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, clientInstructions, Loc.Current["Config.ClientInstructionsCaption"], MessageBoxButton.OK, MessageBoxImage.Information); };
            Find<PasswordBox>("Key").PasswordChanged += delegate { UpdateKeyPlaceholder(); };
            Find<TextBox>("KeyVisible").TextChanged += delegate { UpdateKeyPlaceholder(); };
            Find<CheckBox>("ShowKey").Click += delegate {
                bool show = Find<CheckBox>("ShowKey").IsChecked == true;
                if (show) Find<TextBox>("KeyVisible").Text = Find<PasswordBox>("Key").Password;
                else Find<PasswordBox>("Key").Password = Find<TextBox>("KeyVisible").Text;
                Find<TextBox>("KeyVisible").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Find<PasswordBox>("Key").Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                ShowKey.Content = Loc.Current[show ? "Mcp.Hide" : "Config.Show"];
                UpdateKeyPlaceholder();
            };
            Find<RadioButton>("RemoteNav").Checked += delegate { Mode(true); };
            Find<RadioButton>("LocalNav").Checked += delegate { Mode(false); };
            Find<TextBox>("ServerAddress").TextChanged += delegate { UpdateLink(); };
            Find<TextBox>("ServerPort").TextChanged += delegate { UpdateLink(); };
            Click("BrowseTia", delegate {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = Loc.Current.T("Config.BrowseTiaDescription", SelectedVersion), SelectedPath = Text("TiaPath") })
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Find<TextBox>("TiaPath").Text = dialog.SelectedPath;
            });
            Click("DetectTia", delegate { DetectTiaPath(true); });
            Click("GenerateKey", delegate { byte[] bytes = new byte[24]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); SetSecret(Convert.ToBase64String(bytes)); });
            Click("SaveBoth", OnSaveBoth);
            Click("StartServer", OnStartServer);
            Click("StopServer", OnStopServer);
            Click("Network", async delegate { await OnNetwork(); });
            Click("TestClient", async delegate { await OnTestClient(); });
            Click("SaveClient", delegate { SaveClients(Remote); });
            Click("ClearLog", delegate { activityLog = ""; ActivityLog.LogText = ""; logEntries = 0; UpdateLogCount(); });
            Click("CheckUpdate", async delegate { await OnCheckUpdate(true); });
            Click("RunUpdate", OnRunUpdate);
            Click("OpenReleases", delegate { Process.Start(new ProcessStartInfo(latest != null && latest.ReleaseUrl != null ? latest.ReleaseUrl : UpdateCheck.ReleasePageUrl(UpdateCheck.Repository)) { UseShellExecute = true }); });
            Click("OpenProjectPage", delegate { Process.Start(new ProcessStartInfo("https://github.com/" + UpdateCheck.Repository) { UseShellExecute = true }); });
            ShowInstalledVersion();
            Find<ComboBox>("Version").SelectionChanged += delegate { Guard(delegate { LoadServer(loadExisting); }); UpdateLink(); ReleaseChanged?.Invoke(this, EventArgs.Empty); };
            if (loadExisting && preview == null)
            {
                Window.Width = Math.Max(Window.MinWidth, Math.Min(Window.Width, SystemParameters.WorkArea.Width - 32));
                Window.Height = Math.Max(Window.MinHeight, Math.Min(Window.Height, SystemParameters.WorkArea.Height - 32));
                Guard(delegate { LoadServer(true); });
                if (Text("ServerAddress").Length == 0) Guard(LoadClient);
                var ignored = OnCheckUpdate(false);   // background; the band reports the outcome, nothing blocks
            }
            else if (preview == null) { Find<TextBox>("TiaPath").Text = @"C:\Program Files\Siemens\Automation\Portal V21"; DetectTiaPath(false); }
            else
            {
                versions.SelectedValue = preview.ReleaseKey;
                TiaPath.Text = preview.InstallPath;
                ServerAddress.Text = preview.Address;
                tiaDetected = preview.Detected;
                StopServer.IsEnabled = preview.Running;
                SetSecret(preview.Secret);
                LocalNav.IsChecked = preview.Local;
                RemoteNav.IsChecked = !preview.Local;
            }
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
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Current.IsChinese ? "zh-CN" : "en-US");
            Mode(Remote); UpdateInstructions(); UpdateDetectionLabel(); UpdateLastTest(); UpdateLogCount();
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
            Find<TextBlock>("DetectionSource").Text = tiaDetected ? "● " + Loc.Current["Config.Detected"] : Loc.Current["Config.NotDetected"];
            Find<TextBlock>("DetectionSource").SetResourceReference(TextBlock.ForegroundProperty, tiaDetected ? "Ui.Accent" : "Ui.Label");
        }
        private void UpdateLastTest()
        {
            var note = Find<TextBlock>("LastTest");
            note.ToolTip = Loc.Current.T("Config.LastTest", lastTestResult ?? Loc.Current[lastTestFailed ? "Config.TestFailed" : "Config.TestNotRun"]);
            note.Inlines.Clear();
            var label = new System.Windows.Documents.Run(Loc.Current.T("Config.LastTest", "")) { FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Ui.NoteAccent");
            note.Inlines.Add(label);
            note.Inlines.Add(lastTestResult ?? Loc.Current[lastTestFailed ? "Config.TestFailed" : "Mcp.TestNotRun"]);
        }
        private void UpdateLogCount()
        { Find<TextBlock>("LogCount").Text = Loc.Current.T(logEntries == 1 ? "Config.Entry" : "Config.Entries", logEntries); }

        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
        private void SetStatus(string key, params object[] args) { SetLocalizedText("Status", TextBlock.TextProperty, key, args); }
        private void ServiceStatus(bool active)
        {
            Find<System.Windows.Shapes.Ellipse>("StatusDot").SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "Ui.Accent" : "Ui.StatusIdle");
            UpdateLink();
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }
        private void UpdateKeyPlaceholder()
        {
            Find<TextBlock>("KeyPlaceholder").Visibility = String.IsNullOrEmpty(Secret()) ? Visibility.Visible : Visibility.Collapsed;
            Find<PasswordBox>("Key").Tag = String.IsNullOrEmpty(Find<PasswordBox>("Key").Password) ? null : "HasSecret";
        }

        // Mode changes only presentation; existing configuration values stay in place.
        private void Mode(bool remote)
        {
            Find<TextBlock>("PageStep").Text = Loc.Current["Config.Eyebrow"];
            Find<TextBlock>("PageTitle").Text = Loc.Current[remote ? "Config.RemoteTitle" : "Config.LocalTitle"];
            Find<TextBlock>("PageSubtitle").Text = Loc.Current[remote ? "Config.RemoteSubtitle" : "Config.LocalSubtitle"];
            Find<TextBlock>("ServerCardNote").Text = remote ? "HTTP" : "stdio";
            Find<TextBlock>("Transport").Text = remote ? "HTTP" : "stdio";
            ServerPane.Opacity = remote ? 1 : .45;
            AddressRow.IsEnabled = remote;
            ServerActions.IsEnabled = remote;
            SecretField.IsEnabled = GenerateKey.IsEnabled = ShowKey.IsEnabled = SaveBoth.IsEnabled = TestClient.IsEnabled = remote;
            ShowKey.Content = Loc.Current[ShowKey.IsChecked == true ? "Mcp.Hide" : "Config.Show"];
            UpdateLink();
        }
        private void UpdateLink()
        {
            bool remote = Remote;
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("LinkClient").Text = selected.Count == 0 ? Loc.Current["Config.NoSelection"] : Loc.Current.T("Config.Selected", selected.Count);
            Find<TextBlock>("LinkClient").ToolTip = String.Join(Loc.Current["Config.ListSeparator"], selected.Select(x => x.DisplayName));
            Find<TextBlock>("LinkServer").Text = "MCP · TIA Portal " + TiaVersionCatalog.Get(SelectedVersion).DisplayName;
            bool running = DisplayRunning;
            string address = Text("ServerAddress");
            Find<TextBlock>("LinkEndpoint").Text = !remote ? Loc.Current["Config.LocalAddress"] : address.Length == 0 ? Loc.Current["Config.NoAddress"] : address + ":" + Text("ServerPort");
            Find<TextBlock>("LinkState").Text = Loc.Current[!remote ? "Config.Local" : running ? "Config.Running" : "Config.Idle"];
            ServiceStatusText.Text = LinkState.Text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, running ? "Ui.Accent" : "Ui.StatusIdle");
            StartServer.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            StopServer.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }
        private void UpdateInstructions()
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("ClientSelection").Text = Loc.Current.T("Mcp.ClientCounts", selected.Count, ClientChoices.Items.Cast<ClientProfile>().Count(x => x.Detected));
            WriteTargets.Text = Loc.Current["Mcp.WillWrite"] + " " + String.Join("; ", selected.Select(x => x.DisplayName + " → " + x.Path));
            clientInstructions = selected.Count == 0 ? Loc.Current["Config.ChooseClients"] :
                String.Join("\n", selected.Select(x => Loc.Current.T(x.Detected ? "Config.ClientInstructionsDetected" : "Config.ClientInstructionsNotDetected", x.Name, x.Hint, x.Evidence)));
            Find<TextBlock>("ClientSelection").ToolTip = clientInstructions;
            UpdateLink();
        }
        private void Append(string message)
        {
            if (closing) return;
            if (!Window.Dispatcher.CheckAccess()) { Window.Dispatcher.BeginInvoke(new Action<string>(Append), message); return; }
            if (!String.IsNullOrEmpty(runningKey)) message = message.Replace(runningKey, "[redacted]");
            if (activityLog.Length > 40000) { activityLog = ""; logEntries = 0; }
            activityLog += DateTime.Now.ToString("HH:mm:ss") + "   " + message + Environment.NewLine;
            ActivityLog.LogText = activityLog;
            logEntries++; UpdateLogCount();
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
                message = response != null && response.StatusCode == HttpStatusCode.Unauthorized ? Loc.Current["Config.Unauthorized"] :
                    Loc.Current.T("Config.ConnectionFailed", message);
                if (response != null) response.Close();
            }
            string secret = Secret(); if (!String.IsNullOrEmpty(secret)) message = message.Replace(secret, "[redacted]");
            SetStatus("Config.NeedsAttention"); Append(message); TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, message, Loc.Current["Config.NeedsAttention"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // 自动探测安装目录：环境变量 → 注册表 → 默认目录（与引擎同一顺序）。explicit=false 时只在探测成功才覆盖文本框，找不到保持原值不打扰。
        private void DetectTiaPath(bool explicitRequest)
        {
            if (preview != null) return;
            var found = ConfigCore.DetectTia(SelectedVersion);
            tiaDetected = found.Key != null; UpdateDetectionLabel();
            Find<TextBlock>("DetectionSource").ToolTip = found.Value;
            if (found.Key != null) { Find<TextBox>("TiaPath").Text = found.Key; Append(Loc.Current.T("Config.TiaDetected", SelectedVersion, found.Value, found.Key)); }
            else if (explicitRequest) Append(Loc.Current.T("Config.TiaNotDetected", found.Value, SelectedVersion));
        }
        private void LoadServer(bool loadExisting)
        {
            Find<TextBox>("TiaPath").Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Siemens", "Automation", TiaVersionCatalog.Get(SelectedVersion).InstallFolder);
            Find<TextBox>("ServerAddress").Text = ""; Find<TextBox>("ServerPort").Text = "8765"; SetSecret("");
            if (!loadExisting || !File.Exists(StatePath)) { DetectTiaPath(false); return; }
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(StatePath));
            ConfigCore.Prefix(settings.Address, settings.Port);
            if (settings.EffectiveReleaseKey != SelectedVersion) throw new InvalidDataException(Loc.Current["Config.SavedReleaseMismatch"]);
            Find<TextBox>("TiaPath").Text = settings.TiaPath; Find<TextBox>("ServerAddress").Text = settings.Address; Find<TextBox>("ServerPort").Text = settings.Port.ToString();
            SetSecret(ConfigCore.Unprotect(settings.ProtectedKey)); Append(Loc.Current.T("Config.ServerLoaded", SelectedVersion));
        }
        // 宿主机上没有服务端配置，用上次填写的连接信息补齐同一组字段。
        private void LoadClient()
        {
            string path = Path.Combine(ConfigCore.StateDirectory, "client.json");
            if (!File.Exists(path)) return;
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(path));
            ConfigCore.Prefix(settings.Address, settings.Port);
            Find<TextBox>("ServerAddress").Text = settings.Address; Find<TextBox>("ServerPort").Text = settings.Port.ToString(); SetSecret(ConfigCore.Unprotect(settings.ProtectedKey));
        }
        private ServerSettings Settings()
        {
            int port = Int32.Parse(Text("ServerPort")); ConfigCore.Prefix(Text("ServerAddress"), port);
            ConfigCore.Engine(root, SelectedVersion); ConfigCore.ValidateTia(Text("TiaPath"), SelectedVersion);
            return new ServerSettings { Version = TiaVersionCatalog.Get(SelectedVersion).MajorVersion, ReleaseKey = SelectedVersion, Address = Text("ServerAddress"), Port = port, TiaPath = Text("TiaPath"), ProtectedKey = ConfigCore.Protect(Secret()) };
        }
        // 一页两侧，所以保存也是两侧：客户端侧的连接信息总能保存；服务端配置只在本机确实装了
        // TIA 和引擎时才写，宿主机上校验失败不算错误，只记一行说明。
        private void OnSaveBoth()
        {
            int port = Int32.Parse(Text("ServerPort")); string ip = Text("ServerAddress"), secret = Secret();
            ConfigCore.Prefix(ip, port); ConfigCore.ValidateKey(secret);
            ConfigCore.AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings { Address = ip, Port = port, ProtectedKey = ConfigCore.Protect(secret) });
            Append(Loc.Current["Config.ClientConnectionSaved"]);
            try { ConfigCore.AtomicJson(StatePath, Settings()); Append(Loc.Current["Config.ServerSaved"]); }
            catch (Exception ex) { Append(Loc.Current.T("Config.ClientOnlySaved", ex.GetBaseException().Message)); }
            SetStatus("Config.Saved");
        }

        private void SaveClients(bool remote)
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException(Loc.Current["Config.SelectClientsFirst"]);
            string engine = null, ip = null, secret = null; int port = 0;
            if (remote) { ip = Text("ServerAddress"); port = Int32.Parse(Text("ServerPort")); secret = Secret(); ConfigCore.Prefix(ip, port); ConfigCore.ValidateKey(secret); }
            else { engine = ConfigCore.Engine(root, SelectedVersion); ConfigCore.ValidateTia(Text("TiaPath"), SelectedVersion); }
            // DeepSeek / 智谱 / Grok are brand cards over the same OpenCode file: write it once, name every brand.
            var targets = selected.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Profile = g.First(), Names = String.Join(" / ", g.Select(x => x.Name)),
                    Change = ClientProfiles.PrepareSave(g.First(), remote, ip, port, secret, engine, SelectedVersion, Text("TiaPath")) }).ToList();
            string message = Loc.Current["Config.WriteClientsPrompt"] + String.Join("\n", targets.Select(x => x.Names + "\n" + x.Profile.Path));
            if (targets.Any(x => x.Change.RequiresMigration))
                message += "\n\n" + Loc.Current["Config.MigrateClientsPrompt"] + "\n" + String.Join("\n", targets.Where(x => x.Change.RequiresMigration).Select(x => x.Profile.Path + " → " + x.Change.TargetCommand));
            message += Loc.Current["Config.WriteClientsContinue"];
            if (TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, message, Loc.Current["Config.Write"], MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
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
            if (remote && saved > 0) ConfigCore.AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings { Address = ip, Port = port, ProtectedKey = ConfigCore.Protect(secret) });
            SetStatus("Config.ClientsConfigured", saved);
            Append(Loc.Current.T("Config.RestartClients", remote ? "tia-portal-vm" : "tia-portal"));
            if (errors.Count > 0) throw new InvalidOperationException(Loc.Current.T("Config.SomeClientsNotSaved", String.Join("\n", errors)));
        }
        private void SetBusy(bool value)
        {
            busy = value; Find<Grid>("Panes").IsEnabled = !value;
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }

        // Check for updates, then hand over to the existing updater with this window closed.
        private void ShowInstalledVersion()
        {
            Find<TextBlock>("PackageText").Text = TiaOpenness.Gui.Views.SettingsView.ReadPackageName(root);
            if (preview != null)
            {
                SetLocalizedText("UpdateInstalledItem", TextBlock.TextProperty, "Settings.EngineVersion", TiaOpenness.Gui.ViewModels.MainViewModel.AppVersion.TrimStart('v'));
                SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.NotChecked");
                return;
            }
            string installed = UpdateCheck.Installed(root);
            if (installed == null) { SetLocalizedText("UpdateInstalledItem", TextBlock.TextProperty, "Config.EngineOutsideBundle"); Find<Button>("CheckUpdate").IsEnabled = false; return; }
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
                    SetLocalizedText("UpdateStateItem", TextBlock.TextProperty, "Config.UpToDate", info.Tag); Find<Button>("RunUpdate").Visibility = Visibility.Collapsed;
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
            var running = UpdateCheck.RunningEngines();
            if (running.Count > 0) throw new InvalidOperationException(Loc.Current.T("Config.EnginesStillRunning", String.Join(Loc.Current["Config.ReasonSeparator"], running)));
            string updater = UpdateCheck.UpdaterPath(root);
            if (!File.Exists(updater)) throw new InvalidOperationException(Loc.Current.T("Config.UpdaterMissing", updater));
            string target = latest != null && latest.UpdateAvailable ? latest.Latest : Loc.Current["Config.LatestVersion"];
            if (TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, Loc.Current.T("Config.RunUpdatePrompt", root, target), Loc.Current["Config.RunUpdateCaption"], MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Process.Start(UpdateCheck.Launch(root, Process.GetCurrentProcess().Id));
            Append(Loc.Current["Config.UpdaterStarted"]);
            Window.Close();
        }
        private async Task OnTestClient()
        {
            if (busy) return;
            try
            {
                string ip = Text("ServerAddress"), secret = Secret(); int port = Int32.Parse(Text("ServerPort"));
                SetBusy(true); SetStatus("Config.Testing");
                string result = await Task.Run(() => ConfigCore.TestRemote(ip, port, secret));
                lastTestResult = result; lastTestFailed = false; UpdateLastTest();
                Append(result); SetStatus("Config.ConnectionOk");
            }
            catch (Exception ex) { lastTestResult = null; lastTestFailed = true; UpdateLastTest(); Report(ex); }
            finally { SetBusy(false); }
        }
        private async Task OnNetwork()
        {
            if (busy) return;
            try
            {
                string ip = Text("ServerAddress"); int port = Int32.Parse(Text("ServerPort")); ConfigCore.Prefix(ip, port);
                if (TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, Loc.Current["Config.NetworkPrompt"], Loc.Current["Config.Network"], MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
                var args = new[] { "--network", ip, port.ToString(), WindowsIdentity.GetCurrent().User.Value };
                var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, String.Join(" ", args.Select(ConfigCore.Quote))) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
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
            ConfigCore.AtomicJson(StatePath, settings); runningKey = Secret();
            var info = new ProcessStartInfo(ConfigCore.Engine(root, SelectedVersion), ConfigCore.Arguments(settings, runningKey, root)) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
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
                if (!closing) Window.Dispatcher.BeginInvoke(new Action(delegate { if (closing) return; Find<Button>("StartServer").IsEnabled = true; Find<Button>("StopServer").IsEnabled = false; Find<ComboBox>("Version").IsEnabled = true; SetStatus("Config.ServiceStopped"); ServiceStatus(false); }));
            };
            try { if (!process.Start()) throw new InvalidOperationException(Loc.Current["Config.ServiceNotStarted"]); }
            catch { process.Dispose(); throw; }
            server = process;
            Find<Button>("StartServer").IsEnabled = false; Find<Button>("StopServer").IsEnabled = true; Find<ComboBox>("Version").IsEnabled = false;
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); SetStatus("Config.ServiceRunning");
            ServiceStatus(true);
            Append(Loc.Current["Config.CheckListening"]);
        }
        private void OnStopServer()
        {
            if (server == null || server.HasExited) return;
            if (TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, Loc.Current["Config.StopServicePrompt"], Loc.Current["Config.StopServiceCaption"], MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            server.Kill(); server.WaitForExit();
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (e.Cancel) return;
            if (busy) { e.Cancel = true; Append(Loc.Current["Config.WaitBeforeClosing"]); return; }
            try { OnStopServer(); if (server != null && !server.HasExited) { e.Cancel = true; return; } }
            catch (Exception ex) { e.Cancel = true; Report(ex); return; }
            closing = true;
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
            Window.Closing -= OnClosing;
            Loc.Current.LanguageChanged -= OnLanguageChanged;
            closing = true;
            if (server != null && server.HasExited) { server.Dispose(); server = null; }
        }
    }

}
