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
        public bool HasRunningServer { get { return server != null && !server.HasExited; } }
        public bool LocksRelease { get { return busy || HasRunningServer; } }
        public string SelectedReleaseKey
        {
            get { return SelectedVersion; }
            set { Find<ComboBox>("Version").SelectedValue = TiaVersionCatalog.RequireRunnable(value).Key; }
        }
        public void SetReleaseEnabled(bool enabled) { Find<ComboBox>("Version").IsEnabled = false; }
        public Func<bool> CanUpdate { get; set; }
        private Process server;
        private UpdateInfo latest;   // last successful update check
        private string runningKey;
        private int logEntries;
        private bool tiaDetected;
        private string lastTestResult;
        private bool lastTestFailed;
        private bool busy, closing;
        private T Find<T>(string name) where T : FrameworkElement { return (T)(FindName(name) ?? Window.FindName(name)); }
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
        {
            Window = owner;
            root = bundleRoot;
            InitializeComponent();
            var versions = Find<ComboBox>("Version");
            versions.ItemsSource = TiaVersionCatalog.Runnable.ToList();
            versions.SelectedValue = "21";
            Resources["ClientColumns"] = Window.Width < 1180 ? 2 : 4;
            SizeChanged += delegate { Resources["ClientColumns"] = ActualWidth < 1180 ? 2 : 4; };
            var choices = Find<ListBox>("ClientChoices");
            var cards = ClientProfiles.All(); choices.ItemsSource = cards;
            int firstDetected = cards.FindIndex(x => x.Detected); choices.SelectedIndex = firstDetected < 0 ? 0 : firstDetected;
            Append(Loc.Current.T("Config.ClientDetection", String.Join(Loc.Current["Config.ClientListSeparator"], cards.Select(x => x.DisplayName + (x.Detected ? " ✓" : " –")))));
            choices.SelectionChanged += delegate { UpdateInstructions(); };
            Find<TextBlock>("ClientSelection").MouseLeftButtonUp += delegate { TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, Find<TextBlock>("ClientInstructions").Text, Loc.Current["Config.ClientInstructionsCaption"], MessageBoxButton.OK, MessageBoxImage.Information); };
            Find<PasswordBox>("Key").PasswordChanged += delegate { UpdateKeyPlaceholder(); };
            Find<TextBox>("KeyVisible").TextChanged += delegate { UpdateKeyPlaceholder(); };
            Find<CheckBox>("ShowKey").Click += delegate {
                bool show = Find<CheckBox>("ShowKey").IsChecked == true;
                if (show) Find<TextBox>("KeyVisible").Text = Find<PasswordBox>("Key").Password;
                else Find<PasswordBox>("Key").Password = Find<TextBox>("KeyVisible").Text;
                Find<TextBox>("KeyVisible").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Find<PasswordBox>("Key").Visibility = show ? Visibility.Collapsed : Visibility.Visible;
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
            MenuClick("CheckUpdate", async delegate { await OnCheckUpdate(true); });
            MenuClick("RunUpdate", OnRunUpdate);
            MenuClick("OpenReleases", delegate { Process.Start(new ProcessStartInfo(latest != null && latest.ReleaseUrl != null ? latest.ReleaseUrl : UpdateCheck.ReleasePageUrl(UpdateCheck.Repository)) { UseShellExecute = true }); });
            MenuClick("ShowClientHelp", delegate { TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, Find<TextBlock>("ClientInstructions").Text, Loc.Current["Config.ClientInstructionsCaption"], MessageBoxButton.OK, MessageBoxImage.Information); });
            MenuClick("OpenProjectPage", delegate { Process.Start(new ProcessStartInfo("https://github.com/" + UpdateCheck.Repository) { UseShellExecute = true }); });
            MenuClick("AboutItem", delegate { TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, Loc.Current.T("Config.AboutDetails", Assembly.GetExecutingAssembly().GetName().Version, UpdateCheck.Installed(root) ?? Loc.Current["Config.UnknownInstalledEngine"], root), Loc.Current["Config.AboutCaption"], MessageBoxButton.OK, MessageBoxImage.Information); });
            ShowInstalledVersion();
            Find<ComboBox>("Version").SelectionChanged += delegate { Guard(delegate { LoadServer(loadExisting); }); UpdateLink(); };
            if (loadExisting)
            {
                Window.Width = Math.Max(Window.MinWidth, Math.Min(Window.Width, SystemParameters.WorkArea.Width - 32));
                Window.Height = Math.Max(Window.MinHeight, Math.Min(Window.Height, SystemParameters.WorkArea.Height - 32));
                Guard(delegate { LoadServer(true); });
                if (Text("ServerAddress").Length == 0) Guard(LoadClient);
                var ignored = OnCheckUpdate(false);   // background; the band reports the outcome, nothing blocks
            }
            else { Find<TextBox>("TiaPath").Text = @"C:\Program Files\Siemens\Automation\Portal V21"; DetectTiaPath(false); }
            UpdateLanguage();
            Loc.Current.LanguageChanged += OnLanguageChanged;
            Append(Loc.Current["Config.Ready"]);
            Window.Closing += OnClosing;
            // The desktop owns the version selector; this field only mirrors its state.
            Find<ComboBox>("Version").IsHitTestVisible = false;
            Find<ComboBox>("Version").Focusable = false;
            Find<ComboBox>("Version").IsEnabled = false;
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
            Find<TextBlock>("DetectionSource").SetResourceReference(TextBlock.ForegroundProperty, tiaDetected ? "Ui.Accent" : "Ui.TertiaryLabel");
        }
        private void UpdateLastTest()
        { Find<TextBlock>("LastTest").Text = Loc.Current.T("Config.LastTest", lastTestResult ?? Loc.Current[lastTestFailed ? "Config.TestFailed" : "Config.TestNotRun"]); }
        private void UpdateLogCount()
        { Find<TextBlock>("LogCount").Text = Loc.Current.T(logEntries == 1 ? "Config.Entry" : "Config.Entries", logEntries); }

        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }
        private void MenuClick(string name, Action action) { Find<MenuItem>(name).Click += delegate { Guard(action); }; }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
        private void SetStatus(string key, params object[] args) { SetLocalizedText("Status", TextBlock.TextProperty, key, args); }
        private void ServiceStatus(bool active)
        {
            Find<System.Windows.Shapes.Ellipse>("StatusDot").SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "Ui.Accent" : "Ui.StatusIdle");
            UpdateLink();
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }
        private void UpdateKeyPlaceholder() { Find<TextBlock>("KeyPlaceholder").Visibility = String.IsNullOrEmpty(Secret()) ? Visibility.Visible : Visibility.Collapsed; }

        // Mode changes only presentation; existing configuration values stay in place.
        private void Mode(bool remote)
        {
            var eyebrow = Find<TextBlock>("PageStep"); eyebrow.Inlines.Clear();
            foreach (char character in Loc.Current["Config.Eyebrow"])
            {
                eyebrow.Inlines.Add(new System.Windows.Documents.Run(character.ToString()));
                eyebrow.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new Border { Width = 1.54 }));
            }
            Find<TextBlock>("PageTitle").Text = Loc.Current[remote ? "Config.RemoteTitle" : "Config.LocalTitle"];
            Find<TextBlock>("PageSubtitle").Text = Loc.Current[remote ? "Config.RemoteSubtitle" : "Config.LocalSubtitle"];
            Find<TextBlock>("ServerCardTitle").Text = Loc.Current[remote ? "Config.HttpService" : "Config.LocalEngine"];
            Find<TextBlock>("ServerCardNote").Text = remote ? "HTTP" : "stdio";
            Find<TextBlock>("Transport").Text = remote ? "HTTP" : "stdio";
            Find<Grid>("AddressRow").Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            Find<Grid>("ServerActions").Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            Find<Border>("LocalNote").Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("LocalActionsNote").Visibility = remote ? Visibility.Collapsed : Visibility.Visible;
            Find<Border>("SecretBand").Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("TestClient").Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
            UpdateLink();
            Find<ScrollViewer>("ContentScroll").ScrollToTop();
        }
        private void UpdateLink()
        {
            bool remote = Remote;
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("LinkClient").Text = selected.Count == 0 ? Loc.Current["Config.NoSelection"] : selected.Count == 1 ? selected[0].DisplayName : Loc.Current.T("Config.Selected", selected.Count);
            Find<TextBlock>("LinkClient").ToolTip = String.Join(Loc.Current["Config.ListSeparator"], selected.Select(x => x.DisplayName));
            Find<TextBlock>("LinkServer").Text = "MCP · TIA Portal " + TiaVersionCatalog.Get(SelectedVersion).DisplayName;
            bool running = server != null && !server.HasExited;
            string address = Text("ServerAddress");
            Find<TextBlock>("LinkEndpoint").Text = !remote ? Loc.Current["Config.LocalAddress"] : address.Length == 0 ? Loc.Current["Config.NoAddress"] : address + ":" + Text("ServerPort");
            Find<TextBlock>("LinkState").Text = Loc.Current[!remote ? "Config.Local" : running ? "Config.Running" : "Config.Idle"];
        }
        private void UpdateInstructions()
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("ClientSelection").Text = Loc.Current.T("Config.Selected", selected.Count);
            Find<TextBlock>("ClientInstructions").Text = selected.Count == 0 ? Loc.Current["Config.ChooseClients"] :
                String.Join("\n", selected.Select(x => Loc.Current.T(x.Detected ? "Config.ClientInstructionsDetected" : "Config.ClientInstructionsNotDetected", x.Name, x.Hint, x.Evidence)));
            Find<TextBlock>("ClientSelection").ToolTip = Find<TextBlock>("ClientInstructions").Text;
            UpdateLink();
        }
        private void Append(string message)
        {
            if (closing) return;
            if (!Window.Dispatcher.CheckAccess()) { Window.Dispatcher.BeginInvoke(new Action<string>(Append), message); return; }
            if (!String.IsNullOrEmpty(runningKey)) message = message.Replace(runningKey, "[redacted]");
            var log = Find<TextBox>("Log"); if (log.Text.Length > 40000) { log.Clear(); logEntries = 0; }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "   " + message + Environment.NewLine); log.ScrollToEnd();
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
                .Select(g => new { Profile = g.First(), Names = String.Join(" / ", g.Select(x => x.Name)) }).ToList();
            string message = Loc.Current["Config.WriteClientsPrompt"] + String.Join("\n", targets.Select(x => x.Names + "\n" + x.Profile.Path));
            message += Loc.Current["Config.WriteClientsContinue"];
            if (TiaOpenness.Gui.Controls.GlassMessageBox.Show(Window, message, Loc.Current["Config.Write"], MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            int saved = 0; var errors = new List<string>();
            foreach (var target in targets)
            {
                try { ClientProfiles.Save(target.Profile, remote, ip, port, secret, engine, SelectedVersion, Text("TiaPath")); saved++; Append(Loc.Current.T("Config.ClientSaved", target.Names, target.Profile.Path)); }
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

        // ---- menu "更新": check against GitHub, then hand over to Update-Engine.ps1 with this window closed.
        private void ShowInstalledVersion()
        {
            string installed = UpdateCheck.Installed(root);
            if (installed == null) { SetLocalizedText("UpdateInstalledItem", MenuItem.HeaderProperty, "Config.EngineOutsideBundle"); Find<MenuItem>("CheckUpdate").IsEnabled = false; return; }
            SetLocalizedText("UpdateInstalledItem", MenuItem.HeaderProperty, "Config.InstalledEngine", installed, UpdateCheck.InstalledPackage(root));
            if (UpdateCheck.IsSourceRepository(root)) SetLocalizedText("UpdateStateItem", MenuItem.HeaderProperty, "Config.SourceRepositoryUpdate");
        }
        private async Task OnCheckUpdate(bool explicitRequest)
        {
            string installed = UpdateCheck.Installed(root);
            if (installed == null) return;
            var check = Find<MenuItem>("CheckUpdate");
            check.IsEnabled = false; SetLocalizedText("UpdateStateItem", MenuItem.HeaderProperty, "Config.CheckingUpdate");
            try
            {
                var info = await Task.Run(() => UpdateCheck.Latest(installed, UpdateCheck.Repository));
                latest = info;
                if (info.UpdateAvailable)
                {
                    var size = info.ZipSizeText.Length > 0 ? LocalizedText.Key("Config.UpdateSize", info.ZipSizeText) : LocalizedText.Empty;
                    SetLocalizedText("UpdateStateItem", MenuItem.HeaderProperty, "Config.UpdateAvailable", info.Latest, size);
                    SetLocalizedText("UpdateMenu", MenuItem.HeaderProperty, "Config.UpdateMenuAvailable", info.Latest);
                    Find<MenuItem>("RunUpdate").IsEnabled = !UpdateCheck.IsSourceRepository(root);
                    Append(Loc.Current.T("Config.UpdateAvailableLog", installed, info.Latest, size, info.ReleaseUrl));
                }
                else
                {
                    SetLocalizedText("UpdateStateItem", MenuItem.HeaderProperty, "Config.UpToDate", info.Tag); SetLocalizedText("UpdateMenu", MenuItem.HeaderProperty, "Config.Update");
                    Find<MenuItem>("RunUpdate").IsEnabled = false;
                    Append(Loc.Current.T("Config.UpToDateLog", installed, info.Source));
                }
            }
            catch (Exception ex)
            {
                SetLocalizedText("UpdateStateItem", MenuItem.HeaderProperty, "Config.CannotCheckUpdate", ex.GetBaseException().Message);
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
            var info = new ProcessStartInfo(ConfigCore.Engine(root, SelectedVersion), ConfigCore.Arguments(settings, runningKey)) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Append(e.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Append(e.Data); };
            process.Exited += delegate {
                Append(Loc.Current.T("Config.ServiceExited", process.ExitCode));
                if (!closing) Window.Dispatcher.BeginInvoke(new Action(delegate { Find<Button>("StartServer").IsEnabled = true; Find<Button>("StopServer").IsEnabled = false; Find<ComboBox>("Version").IsEnabled = true; SetStatus("Config.ServiceStopped"); ServiceStatus(false); }));
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
            if (scrollToBottom) { Find<ScrollViewer>("ContentScroll").ScrollToBottom(); Window.UpdateLayout(); Window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Render); }
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
            if (server != null && server.HasExited) server.Dispose();
        }
    }

}
