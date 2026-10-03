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
        private UpdateInfo latest;   // 2.8.0: last successful update check
        private string runningKey;
        private int logEntries;
        private bool tiaDetected;
        private string lastTestResult;
        private bool lastTestFailed;
        private bool busy, closing;
        private T Find<T>(string name) where T : FrameworkElement { return (T)FindName(name); }
        private string Text(string name) { return Find<TextBox>(name).Text.Trim(); }
        private string SelectedVersion
        {
            get
            {
                var selected = Find<ComboBox>("Version").SelectedItem as TiaVersionDescriptor;
                if (selected == null) throw new InvalidOperationException("请选择可运行的 TIA Portal 版本。");
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
            string assembly = Assembly.GetExecutingAssembly().GetName().Name;
            Resources["Ui.Font"] = new FontFamily(new Uri("pack://application:,,,/" + assembly + ";component/"), "./Fonts/#Manrope, Segoe UI Variable, Microsoft YaHei UI");
            Resources["Ui.FontMono"] = new FontFamily(new Uri("pack://application:,,,/" + assembly + ";component/"), "./Fonts/#JetBrains Mono, Consolas");
            ApplyTheme("Auto");
            var versions = Find<ComboBox>("Version");
            versions.ItemsSource = TiaVersionCatalog.Runnable.ToList();
            versions.SelectedValue = "21";
            Resources["ClientColumns"] = Window.Width < 1180 ? 2 : 4;
            SizeChanged += delegate { Resources["ClientColumns"] = ActualWidth < 1180 ? 2 : 4; };
            var choices = Find<ListBox>("ClientChoices");
            var cards = ClientProfiles.All(); choices.ItemsSource = cards;
            int firstDetected = cards.FindIndex(x => x.Detected); choices.SelectedIndex = firstDetected < 0 ? 0 : firstDetected;
            Append("客户端检测：" + String.Join("，", cards.Select(x => x.DisplayName + (x.Detected ? " ✓" : " –"))) + "（✓ = 本机检测到；未检测到的仍可写入）");
            choices.SelectionChanged += delegate { UpdateInstructions(); };
            Find<TextBlock>("ClientSelection").MouseLeftButtonUp += delegate { MessageBox.Show(Window, Find<TextBlock>("ClientInstructions").Text, "客户端使用说明", MessageBoxButton.OK, MessageBoxImage.Information); };
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
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择 Portal V" + SelectedVersion + " 安装目录，不带 Bin", SelectedPath = Text("TiaPath") })
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
            MenuClick("ShowClientHelp", delegate { MessageBox.Show(Window, Find<TextBlock>("ClientInstructions").Text, "客户端使用说明", MessageBoxButton.OK, MessageBoxImage.Information); });
            MenuClick("OpenProjectPage", delegate { Process.Start(new ProcessStartInfo("https://github.com/" + UpdateCheck.Repository) { UseShellExecute = true }); });
            MenuClick("AboutItem", delegate { MessageBox.Show(Window, "TIA Portal · MCP Bridge 配置器 " + Assembly.GetExecutingAssembly().GetName().Version + "\n引擎：" + (UpdateCheck.Installed(root) ?? "未知（不在交付包里）") + "\n目录：" + root + "\n\n更新走菜单“更新 → 更新引擎…”，由 scripts\\operations\\Update-Engine.ps1 在引擎停止后完成。", "关于", MessageBoxButton.OK, MessageBoxImage.Information); });
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
            ApplyLanguage(loadExisting && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en");
            Append("就绪。服务与客户端配置在同一页完成，两端使用同一密钥。");
            Window.Closing += OnClosing;
            // The desktop owns the version selector; this field only mirrors its state.
            Find<ComboBox>("Version").IsHitTestVisible = false;
            Find<ComboBox>("Version").Focusable = false;
            Find<ComboBox>("Version").IsEnabled = false;
        }

        private static ResourceDictionary LoadDictionary(string path)
        {
            return new ResourceDictionary { Source = new Uri("/TiaOpenness;component/" + path, UriKind.Relative) };
        }

        public void ApplyTheme(string theme)
        {
            string resolved = theme;
            if (theme == "Auto")
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    resolved = key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0 ? "Dark" : "Light";
            }
            var palette = LoadDictionary("Themes/Palette." + resolved + ".xaml");
            foreach (object key in palette.Keys) Resources[key] = palette[key];
        }

        private string T(string key) { return (string)Resources["Text." + key]; }
        private string F(string key, object value) { return String.Format(T(key), value); }
        public void ApplyLanguage(string language)
        {
            var strings = LoadDictionary("Configuration/Glass.Strings." + language + ".xaml");
            foreach (object key in strings.Keys) Resources[key] = strings[key];
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(language == "zh" ? "zh-CN" : "en-US");
            Mode(Remote); UpdateInstructions(); UpdateDetectionLabel(); UpdateLastTest(); UpdateLogCount();
        }
        private void UpdateDetectionLabel()
        {
            Find<TextBlock>("DetectionSource").Text = tiaDetected ? "● " + T("Detected") : T("NotDetected");
            Find<TextBlock>("DetectionSource").SetResourceReference(TextBlock.ForegroundProperty, tiaDetected ? "Ui.Accent" : "Ui.TertiaryLabel");
        }
        private void UpdateLastTest()
        { Find<TextBlock>("LastTest").Text = F("LastTest", lastTestResult ?? T(lastTestFailed ? "TestFailed" : "TestNotRun")); }
        private void UpdateLogCount()
        { Find<TextBlock>("LogCount").Text = F(logEntries == 1 ? "Entry" : "Entries", logEntries); }

        private void Click(string name, Action action) { Find<Button>(name).Click += delegate { Guard(action); }; }
        private void MenuClick(string name, Action action) { Find<MenuItem>(name).Click += delegate { Guard(action); }; }
        private void Guard(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
        private void SetStatus(string status) { Find<TextBlock>("Status").Text = status; }
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
            foreach (char character in T("Eyebrow"))
            {
                eyebrow.Inlines.Add(new System.Windows.Documents.Run(character.ToString()));
                eyebrow.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new Border { Width = 1.54 }));
            }
            Find<TextBlock>("PageTitle").Text = T(remote ? "RemoteTitle" : "LocalTitle");
            Find<TextBlock>("PageSubtitle").Text = T(remote ? "RemoteSubtitle" : "LocalSubtitle");
            Find<TextBlock>("ServerCardTitle").Text = T(remote ? "HttpService" : "LocalEngine");
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
            Find<TextBlock>("LinkClient").Text = selected.Count == 0 ? T("NoSelection") : selected.Count == 1 ? selected[0].DisplayName : F("Selected", selected.Count);
            Find<TextBlock>("LinkClient").ToolTip = String.Join("、", selected.Select(x => x.DisplayName));
            Find<TextBlock>("LinkServer").Text = "MCP · TIA Portal " + TiaVersionCatalog.Get(SelectedVersion).DisplayName;
            bool running = server != null && !server.HasExited;
            string address = Text("ServerAddress");
            Find<TextBlock>("LinkEndpoint").Text = !remote ? T("LocalAddress") : address.Length == 0 ? T("NoAddress") : address + ":" + Text("ServerPort");
            Find<TextBlock>("LinkState").Text = T(!remote ? "Local" : running ? "Running" : "Idle");
        }
        private void UpdateInstructions()
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            Find<TextBlock>("ClientSelection").Text = F("Selected", selected.Count);
            Find<TextBlock>("ClientInstructions").Text = selected.Count == 0 ? "选择一个或多个客户端，保存后将自动写入对应配置。" :
                String.Join("\n", selected.Select(x => x.Name + "：" + x.Hint + "（" + (x.Detected ? "已检测到：" : "未检测到：") + x.Evidence + "）"));
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
            if (http != null) message = "HTTP 监听失败（" + http.NativeErrorCode + "）：" + message + "。拒绝访问时点击“网络权限”；端口占用时停止旧 MCP 或更换端口。";
            var web = ex as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                message = response != null && response.StatusCode == HttpStatusCode.Unauthorized ? "鉴权失败（401）：请检查两端连接密钥是否一致。" :
                    "连接失败：" + message + " 请检查虚拟机服务、IP、端口和防火墙。";
                if (response != null) response.Close();
            }
            string secret = Secret(); if (!String.IsNullOrEmpty(secret)) message = message.Replace(secret, "[redacted]");
            SetStatus("需要处理"); Append(message); MessageBox.Show(Window, message, "需要处理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // 自动探测安装目录：环境变量 → 注册表 → 默认目录（与引擎同一顺序）。explicit=false 时只在探测成功才覆盖文本框，找不到保持原值不打扰。
        private void DetectTiaPath(bool explicitRequest)
        {
            var found = ConfigCore.DetectTia(SelectedVersion);
            tiaDetected = found.Key != null; UpdateDetectionLabel();
            Find<TextBlock>("DetectionSource").ToolTip = found.Value;
            if (found.Key != null) { Find<TextBox>("TiaPath").Text = found.Key; Append("已自动检测到 V" + SelectedVersion + " 安装目录（" + found.Value + "）：" + found.Key); }
            else if (explicitRequest) Append("未自动检测到：" + found.Value + "。请用“浏览”手动选择 Portal V" + SelectedVersion + " 安装根目录。");
        }
        private void LoadServer(bool loadExisting)
        {
            Find<TextBox>("TiaPath").Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Siemens", "Automation", TiaVersionCatalog.Get(SelectedVersion).InstallFolder);
            Find<TextBox>("ServerAddress").Text = ""; Find<TextBox>("ServerPort").Text = "8765"; SetSecret("");
            if (!loadExisting || !File.Exists(StatePath)) { DetectTiaPath(false); return; }
            var settings = ConfigCore.Json().Deserialize<ServerSettings>(File.ReadAllText(StatePath));
            ConfigCore.Prefix(settings.Address, settings.Port);
            if (settings.EffectiveReleaseKey != SelectedVersion) throw new InvalidDataException("已保存配置中的 TIA 版本不匹配。");
            Find<TextBox>("TiaPath").Text = settings.TiaPath; Find<TextBox>("ServerAddress").Text = settings.Address; Find<TextBox>("ServerPort").Text = settings.Port.ToString();
            SetSecret(ConfigCore.Unprotect(settings.ProtectedKey)); Append("已载入 V" + SelectedVersion + " 服务配置。");
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
            Append("客户端侧连接信息已按当前 Windows 用户加密保存。");
            try { ConfigCore.AtomicJson(StatePath, Settings()); Append("服务端配置已保存。下一步：网络权限 → 启动服务。"); }
            catch (Exception ex) { Append("本机未通过服务端校验，只保存了客户端侧信息：" + ex.GetBaseException().Message); }
            SetStatus("已保存");
        }

        private void SaveClients(bool remote)
        {
            var selected = Find<ListBox>("ClientChoices").SelectedItems.Cast<ClientProfile>().ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请先选择一个或多个 AI 客户端。");
            string engine = null, ip = null, secret = null; int port = 0;
            if (remote) { ip = Text("ServerAddress"); port = Int32.Parse(Text("ServerPort")); secret = Secret(); ConfigCore.Prefix(ip, port); ConfigCore.ValidateKey(secret); }
            else { engine = ConfigCore.Engine(root, SelectedVersion); ConfigCore.ValidateTia(Text("TiaPath"), SelectedVersion); }
            // DeepSeek / 智谱 / Grok are brand cards over the same OpenCode file: write it once, name every brand.
            var targets = selected.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Profile = g.First(), Names = String.Join(" / ", g.Select(x => x.Name)) }).ToList();
            string message = "请先退出所选客户端，避免配置同时写入。\n将保留其它设置并备份原文件：\n\n" + String.Join("\n", targets.Select(x => x.Names + "\n" + x.Profile.Path));
            message += "\n\n客户端配置按其格式保存密钥，请勿分享文件或备份。继续？";
            if (MessageBox.Show(Window, message, "写入客户端配置", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            int saved = 0; var errors = new List<string>();
            foreach (var target in targets)
            {
                try { ClientProfiles.Save(target.Profile, remote, ip, port, secret, engine, SelectedVersion, Text("TiaPath")); saved++; Append(target.Names + " 已保存：" + target.Profile.Path); }
                catch (Exception ex) { errors.Add(target.Names + "：" + ex.Message); }
            }
            if (remote && saved > 0) ConfigCore.AtomicJson(Path.Combine(ConfigCore.StateDirectory, "client.json"), new ServerSettings { Address = ip, Port = port, ProtectedKey = ConfigCore.Protect(secret) });
            SetStatus("已配置 " + saved + " 个客户端");
            Append("重启已配置的客户端，使用 " + (remote ? "tia-portal-vm" : "tia-portal") + " 读取工程树。不要让多个 AI 同时修改同一工程。");
            if (errors.Count > 0) throw new InvalidOperationException("部分客户端未保存，其它成功项已保留：\n" + String.Join("\n", errors));
        }
        private void SetBusy(bool value)
        {
            busy = value; Find<Grid>("Panes").IsEnabled = !value;
            if (ServiceStateChanged != null) ServiceStateChanged(this, EventArgs.Empty);
        }

        // ---- 2.8.0 menu "更新": check against GitHub, then hand over to Update-Engine.ps1 with this window closed.
        private void ShowInstalledVersion()
        {
            string installed = UpdateCheck.Installed(root);
            var item = Find<MenuItem>("UpdateInstalledItem");
            if (installed == null) { item.Header = "引擎版本未知：这里不是解压后的交付包（没有 manifest\\delivery.json）"; Find<MenuItem>("CheckUpdate").IsEnabled = false; return; }
            item.Header = "引擎 " + installed + "（" + UpdateCheck.InstalledPackage(root) + "）";
            if (UpdateCheck.IsSourceRepository(root)) Find<MenuItem>("UpdateStateItem").Header = "源码仓库（有 .git）：不在这里更新，发布走 scripts\\build\\Release.ps1";
        }
        private async Task OnCheckUpdate(bool explicitRequest)
        {
            string installed = UpdateCheck.Installed(root);
            if (installed == null) return;
            var state = Find<MenuItem>("UpdateStateItem"); var check = Find<MenuItem>("CheckUpdate"); var menu = Find<MenuItem>("UpdateMenu");
            check.IsEnabled = false; state.Header = "正在检查 GitHub 最新版本…";
            try
            {
                var info = await Task.Run(() => UpdateCheck.Latest(installed, UpdateCheck.Repository));
                latest = info;
                if (info.UpdateAvailable)
                {
                    string size = info.ZipSizeText.Length > 0 ? "（" + info.ZipSizeText + "）" : "";
                    state.Header = "可更新到 " + info.Latest + size + " · 先停引擎，再点下面的“更新引擎…”";
                    menu.Header = "更新 · 有新版本 " + info.Latest + "(_U)";
                    Find<MenuItem>("RunUpdate").IsEnabled = !UpdateCheck.IsSourceRepository(root);
                    Append("检查更新：" + installed + " → " + info.Latest + size + "，菜单“更新 → 更新引擎…”执行（" + info.ReleaseUrl + "）");
                }
                else
                {
                    state.Header = "已是最新（" + info.Tag + "）"; menu.Header = "更新(_U)";
                    Find<MenuItem>("RunUpdate").IsEnabled = false;
                    Append("检查更新：" + installed + " 已是最新（" + info.Source + "）");
                }
            }
            catch (Exception ex)
            {
                state.Header = "无法检查：" + ex.GetBaseException().Message;
                if (explicitRequest) Append("检查更新失败：" + ex.GetBaseException().Message);
            }
            finally { check.IsEnabled = true; }
        }
        private void OnRunUpdate()
        {
            if (CanUpdate != null && !CanUpdate()) throw new InvalidOperationException("请先完成工程操作并断开 TIA 会话，再更新软件。");
            if (busy) return;
            if (UpdateCheck.IsSourceRepository(root)) throw new InvalidOperationException("这是源码仓库，不在这里更新。");
            if (server != null && !server.HasExited) throw new InvalidOperationException("先点“停止”结束本窗口启动的 MCP，再更新。");
            var running = UpdateCheck.RunningEngines();
            if (running.Count > 0) throw new InvalidOperationException("引擎仍在运行，更新器会拒绝：" + String.Join("；", running) + "。请先停止它（可能是某个 AI 客户端启动的：关闭那个会话），再更新。");
            string updater = UpdateCheck.UpdaterPath(root);
            if (!File.Exists(updater)) throw new InvalidOperationException("找不到更新器 " + updater + "。请从 GitHub Releases 重新下载完整交付包。");
            string target = latest != null && latest.UpdateAvailable ? latest.Latest : "最新版";
            if (MessageBox.Show(Window, "将关闭本程序，在新的 PowerShell 窗口里把\n" + root + "\n更新到 " + target + "：下载 ZIP 与 .sha256、校验 SHA-256、备份到 .previous、替换 runtime 与 manifest。完成后自动重新打开本程序；失败时该窗口保留错误信息，-Rollback 可换回上一版。\n\n继续？", "更新引擎", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Process.Start(UpdateCheck.Launch(root, Process.GetCurrentProcess().Id));
            Append("更新器已在新窗口启动，本程序即将关闭。");
            Window.Close();
        }
        private async Task OnTestClient()
        {
            if (busy) return;
            try
            {
                string ip = Text("ServerAddress"), secret = Secret(); int port = Int32.Parse(Text("ServerPort"));
                SetBusy(true); SetStatus("正在测试…");
                string result = await Task.Run(() => ConfigCore.TestRemote(ip, port, secret));
                lastTestResult = result; lastTestFailed = false; UpdateLastTest();
                Append(result); SetStatus("连接正常");
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
                if (MessageBox.Show(Window, "为当前用户授权此 HTTP 地址，并放行本地子网到此端口。\n接下来会出现 Windows 管理员权限提示，继续？", "网络权限", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
                var args = new[] { "--network", ip, port.ToString(), WindowsIdentity.GetCurrent().User.Value };
                var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, String.Join(" ", args.Select(ConfigCore.Quote))) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                SetBusy(true); SetStatus("配置网络…");
                using (var process = Process.Start(info))
                {
                    await Task.Run(() => process.WaitForExit());
                    if (process.ExitCode != 0) throw new InvalidOperationException("网络配置未完成，请查看管理员窗口中的错误信息。");
                }
                SetStatus("网络已配置"); Append("网络权限配置完成。现在可以启动服务。");
            }
            catch (Exception ex) { Report(ex); }
            finally { SetBusy(false); }
        }
        private void OnStartServer()
        {
            if (server != null && !server.HasExited) throw new InvalidOperationException("此窗口已经启动 MCP。");
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
                Append("MCP 已退出，退出码：" + process.ExitCode);
                if (!closing) Window.Dispatcher.BeginInvoke(new Action(delegate { Find<Button>("StartServer").IsEnabled = true; Find<Button>("StopServer").IsEnabled = false; Find<ComboBox>("Version").IsEnabled = true; SetStatus("服务已停止"); ServiceStatus(false); }));
            };
            try { if (!process.Start()) throw new InvalidOperationException("MCP 进程未启动。"); }
            catch { process.Dispose(); throw; }
            server = process;
            Find<Button>("StartServer").IsEnabled = false; Find<Button>("StopServer").IsEnabled = true; Find<ComboBox>("Version").IsEnabled = false;
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); SetStatus("服务进程运行中");
            ServiceStatus(true);
            Append("请检查日志中的 listening 提示，并在宿主机测试连接。保持此窗口打开。");
        }
        private void OnStopServer()
        {
            if (server == null || server.HasExited) return;
            if (MessageBox.Show(Window, "将停止本窗口启动的 MCP，中断客户端连接。\n请确认没有正在执行的工程操作。继续？", "停止 MCP", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            server.Kill(); server.WaitForExit();
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (e.Cancel) return;
            if (busy) { e.Cancel = true; Append("正在处理配置，请稍候再关闭。"); return; }
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
            closing = true;
            if (server != null && server.HasExited) server.Dispose();
        }
    }

}
